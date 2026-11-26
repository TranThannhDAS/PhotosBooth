using Microsoft.Extensions.Logging;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Events;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sharing;
using Photobooth.Application.Templates;

namespace Photobooth.Application.Sessions;

/// <summary>
/// Điều phối phiên chụp: làm chủ trạng thái, chọn template, chụp nhiều ảnh liên tiếp (có đếm ngược),
/// chụp lại từng ảnh/toàn bộ, ghép ảnh và xuất file. Ảnh gốc luôn được lưu trước khi tính là thành công.
/// </summary>
/// <remarks>
/// Đăng ký singleton để trạng thái phiên tồn tại độc lập với vòng đời Razor Component và WebView.
/// Camera được truyền vào từng lần chụp vì adapter camera có thể gắn với vòng đời UI.
/// Mỗi phiên thuộc Event Workspace tại thời điểm tạo và được lưu lâu dài qua <see cref="ISessionArchive"/>
/// (xem PhotoSessionService.Archive.cs: phiên bản thành phẩm, mở lại, in).
/// </remarks>
public sealed partial class PhotoSessionService
{
    private static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);

    /// <summary>Khoảng dừng ngắn sau mỗi ảnh để khách thấy phản hồi "đã chụp" trước ảnh kế tiếp.</summary>
    private static readonly TimeSpan SavedFeedbackDelay = TimeSpan.FromMilliseconds(800);

    private readonly IOriginalPhotoStore _originalStore;
    private readonly IPhotoComposer _composer;
    private readonly IImagePreviewRenderer _previewRenderer;
    private readonly IComposedPhotoExporter _exporter;
    private readonly ISessionArchive _archive;
    private readonly IPrinterProvider _printers;
    private readonly PrintOptions _printOptions;
    private readonly ShareLinkFactory _shareLinks;
    private readonly ICloudUploadQueue _uploads;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PhotoSessionService> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private PhotoSession? _current;
    private PhotoSessionState _preComposeState = PhotoSessionState.Reviewing;
    private CancellationTokenSource? _composeCts;
    private long _composeGeneration;

    public PhotoSessionService(
        IOriginalPhotoStore originalStore,
        IPhotoComposer composer,
        IImagePreviewRenderer previewRenderer,
        IComposedPhotoExporter exporter,
        ISessionArchive archive,
        IPrinterProvider printers,
        CaptureOptions options,
        PrintOptions printOptions,
        ShareLinkFactory shareLinks,
        ICloudUploadQueue uploads,
        TimeProvider timeProvider,
        ILogger<PhotoSessionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(printOptions);
        options.Validate();
        printOptions.Validate();

        _originalStore = originalStore;
        _composer = composer;
        _previewRenderer = previewRenderer;
        _exporter = exporter;
        _archive = archive;
        _printers = printers;
        _printOptions = printOptions;
        _shareLinks = shareLinks;
        _uploads = uploads;
        _timeProvider = timeProvider;
        _logger = logger;
        Options = options;
    }

    /// <summary>Phát sinh sau mỗi thay đổi trạng thái phiên (có thể từ luồng nền).</summary>
    public event EventHandler? SessionChanged;

    public CaptureOptions Options { get; }

    public PrintOptions PrintOptions => _printOptions;

    /// <summary>Máy in đang cấu hình (null = chưa cấu hình, không thể in).</summary>
    public IPrinter? Printer => _printers.Current;

    /// <summary>Đang có phiên Kiosk chưa kết thúc (không được đổi Event Workspace lúc này).</summary>
    public bool HasActiveSession => Current is { IsEnded: false, IsArchived: false };

    public PhotoSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    // ---------------------------------------------------------------- Vòng đời phiên

    /// <summary>
    /// Kết thúc phiên đang có (nếu còn) và tạo phiên mới trong Event Workspace hiện tại.
    /// Trả về null nếu chưa có Event Workspace hợp lệ — không bao giờ lưu sang thư mục khác thay thế.
    /// </summary>
    public PhotoSession? StartNewSession()
    {
        var sessionId = Guid.NewGuid();
        var createdAt = _timeProvider.GetUtcNow();
        SessionLocation location;
        try
        {
            location = _archive.CreateSessionLocation(sessionId, createdAt);
        }
        catch (EventWorkspaceUnavailableException ex)
        {
            _logger.LogError(ex, "Không tạo được phiên chụp: Event Workspace không khả dụng");
            return null;
        }

        PhotoSession created;
        PhotoSession? ended;
        lock (_gate)
        {
            ended = EndCurrentLocked();
            created = PhotoSession.Create(sessionId, createdAt, Options.MirrorByDefault) with
            {
                Directory = location.Directory,
                EventId = location.EventId,
            };
            _current = created;
        }

        AfterSessionEnded(ended);
        _logger.LogInformation("Bắt đầu phiên chụp {SessionId}", created.SessionId);
        RaiseSessionChanged();
        return created;
    }

    /// <summary>
    /// Hủy/kết thúc phiên Kiosk hiện tại. Không xóa ảnh: Completed nếu đã có thành phẩm được lưu/xuất, ngược lại Cancelled.
    /// Với phiên đã lưu được mở lại (Admin), chỉ đóng lại — không đổi dữ liệu đã lưu.
    /// </summary>
    public void EndCurrentSession()
    {
        PhotoSession? ended;
        lock (_gate)
        {
            ended = EndCurrentLocked();
        }

        if (ended is not null)
        {
            AfterSessionEnded(ended);
            RaiseSessionChanged();
        }
    }

    /// <summary>
    /// Hoàn tất phiên Kiosk: xác nhận ảnh thành phẩm hiện tại thành một phiên bản lưu trong Event Workspace
    /// (dùng lại phiên bản đã có nếu không đổi gì), gắn link chia sẻ QR (nếu Cloud được cấu hình — không cần Internet),
    /// rồi chuyển sang Completed. Upload lên Cloud chạy nền sau đó, không chặn việc hoàn tất.
    /// </summary>
    public async Task<bool> CompleteSessionAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { State: PhotoSessionState.Ready, IsArchived: false })
        {
            return false;
        }

        if (await ConfirmOutputAsync(cancellationToken).ConfigureAwait(false) is not { } output)
        {
            return false;
        }

        // Chia sẻ là phần bổ sung: không tạo được link (chưa cấu hình/ghi metadata lỗi) vẫn hoàn tất phiên bình thường.
        await EnsureShareLinkAsync(output.Version, cancellationToken).ConfigureAwait(false);

        var completed = TryTransition(
            s => s.State == PhotoSessionState.Ready && !s.IsArchived && s.Versions.Count > 0,
            s => s with { State = PhotoSessionState.Completed, CompletedVersion = output.Version });

        if (completed)
        {
            _logger.LogInformation("Hoàn tất phiên {SessionId}", Current?.SessionId);
            await PersistCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (Current is { } session)
            {
                await DeleteDraftsSafelyAsync(session).ConfigureAwait(false);
            }
        }

        return completed;
    }

    // ---------------------------------------------------------------- Template

    /// <summary>
    /// Template cần số ảnh nằm trong giới hạn an toàn <see cref="CaptureOptions.MaxPhotosPerSession"/>.
    /// Số ảnh do template quyết định (BR-T01); đây chỉ là giới hạn tài nguyên.
    /// </summary>
    public bool IsTemplateSupported(PhotoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.RequiredPhotoCount is > 0 && template.RequiredPhotoCount <= Options.MaxPhotosPerSession;
    }

    /// <summary>
    /// Template đang được một phiên chụp CHƯA KẾT THÚC (không phải phiên đã lưu đang xem lại) sử dụng.
    /// Template Manager không cho xóa/lưu đè template trong trường hợp này.
    /// </summary>
    public bool IsTemplateInActiveSession(string templateId)
    {
        var session = Current;
        return session is { IsArchived: false, IsEnded: false } && session.Template?.Id == templateId;
    }

    /// <summary>
    /// Chọn (hoặc đổi) template trước khi chụp ảnh đầu tiên. Phiên lưu snapshot template và chốt số ảnh
    /// cần chụp = <see cref="PhotoTemplate.RequiredPhotoCount"/> (BR-T04). Không đổi template khi đã có ảnh.
    /// </summary>
    public bool SelectTemplate(PhotoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!IsTemplateSupported(template) || !TemplateValidator.Validate(template).IsValid)
        {
            return false;
        }

        // Sao chép danh sách để phiên không bị ảnh hưởng nếu đối tượng template gốc bị thay đổi sau này.
        var snapshot = template with { Slots = [.. template.Slots], Decorations = [.. template.Decorations] };
        var photoCount = snapshot.RequiredPhotoCount;

        var selected = TryTransition(
            s => s.State is PhotoSessionState.Created or PhotoSessionState.Previewing && s.Photos.Count == 0,
            s => s with
            {
                Template = snapshot,
                Slots = new CapturedPhoto?[photoCount],
                SlotCrops = new SlotCrop?[snapshot.Slots.Count],
                PendingPositions = Enumerable.Range(0, photoCount).ToArray(),
                Composed = null,
            });

        if (selected)
        {
            _logger.LogInformation("Chọn template {TemplateId} ({PhotoCount} ảnh)", template.Id, photoCount);
        }

        return selected;
    }

    /// <summary>Quay lại chọn template; chỉ được phép khi chưa chụp ảnh nào.</summary>
    public bool ReturnToTemplateSelection() =>
        TryTransition(
            s => s.State is PhotoSessionState.Created or PhotoSessionState.Previewing && s.Photos.Count == 0,
            s => s with { State = PhotoSessionState.Created, Template = null, Slots = [], SlotCrops = [], PendingPositions = [] });

    // ---------------------------------------------------------------- Camera / chụp

    /// <summary>Báo camera đã sẵn sàng. Cần đã chọn template.</summary>
    public bool MarkPreviewReady() =>
        TryTransition(
            s => (s.State == PhotoSessionState.Created && s.Template is not null) || s.State == PhotoSessionState.Previewing,
            s => s with { State = PhotoSessionState.Previewing });

    /// <summary>Bật/tắt chế độ gương cho các ảnh chụp tiếp theo (ảnh đã chụp giữ nguyên hướng lúc chụp).</summary>
    public bool SetMirror(bool mirror) =>
        TryTransition(
            s => s.State is PhotoSessionState.Created or PhotoSessionState.Previewing,
            s => s with { Mirror = mirror });

    /// <summary>
    /// Chụp lần lượt các vị trí đang chờ (<see cref="PhotoSession.PendingPositions"/>), mỗi ảnh có đếm ngược.
    /// Chỉ một lượt chụp chạy tại một thời điểm. Ảnh đã lưu trước khi lượt bị dừng/hủy vẫn được giữ.
    /// </summary>
    public async Task<CaptureSequenceResult> CaptureSequenceAsync(
        ICameraDevice camera,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Guid sessionId;
        int[] positions;
        int photoCount;
        bool mirror;
        lock (_gate)
        {
            if (_current is null)
            {
                return CaptureSequenceResult.Failed(0, CaptureFailureReason.NotReady);
            }

            if (_current.State == PhotoSessionState.Capturing)
            {
                return CaptureSequenceResult.Failed(0, CaptureFailureReason.Busy);
            }

            if (_current.State != PhotoSessionState.Previewing || _current.Template is null || _current.PendingPositions.Count == 0)
            {
                return CaptureSequenceResult.Failed(0, CaptureFailureReason.NotReady);
            }

            _current = _current with { State = PhotoSessionState.Capturing };
            sessionId = _current.SessionId;
            positions = [.. _current.PendingPositions];
            photoCount = _current.RequiredPhotoCount;
            mirror = _current.Mirror;
        }

        RaiseSessionChanged();
        _logger.LogInformation("Bắt đầu lượt chụp {ShotCount} ảnh ở phiên {SessionId}", positions.Length, sessionId);

        var captured = 0;
        try
        {
            for (var shot = 0; shot < positions.Length; shot++)
            {
                var position = positions[shot];
                CaptureProgress Progress(CaptureStage stage, int seconds) =>
                    new(position, photoCount, shot + 1, positions.Length, stage, seconds);

                for (var remaining = Options.CountdownSeconds; remaining > 0; remaining--)
                {
                    progress?.Report(Progress(CaptureStage.Countdown, remaining));
                    await Task.Delay(CountdownTick, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(Progress(CaptureStage.Shooting, 0));

                var photo = await CaptureOneAsync(camera, sessionId, position, mirror, cancellationToken).ConfigureAwait(false);
                if (photo is null)
                {
                    return CaptureSequenceResult.Failed(captured, CaptureFailureReason.InvalidImage);
                }

                if (!AssignPhoto(photo))
                {
                    // Phiên đã bị thay thế/kết thúc trong lúc chụp; file gốc vẫn còn trên ổ đĩa.
                    return CaptureSequenceResult.Failed(captured, CaptureFailureReason.Cancelled);
                }

                // Ghi metadata ngay sau mỗi ảnh để mở lại được phiên kể cả khi ứng dụng dừng đột ngột.
                await PersistCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                captured++;
                progress?.Report(Progress(CaptureStage.Saved, 0));

                if (shot < positions.Length - 1)
                {
                    await Task.Delay(SavedFeedbackDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }

            return CaptureSequenceResult.Completed(captured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Lượt chụp ở phiên {SessionId} bị hủy sau {Captured} ảnh", sessionId, captured);
            return CaptureSequenceResult.Failed(captured, CaptureFailureReason.Cancelled);
        }
        catch (CameraException ex)
        {
            _logger.LogWarning(ex, "Camera không chụp được ảnh ({CameraError}) ở phiên {SessionId}", ex.Error, sessionId);
            return CaptureSequenceResult.Failed(captured, CaptureFailureReason.CameraFailed, ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Không ghi được ảnh gốc ở phiên {SessionId}", sessionId);
            return CaptureSequenceResult.Failed(captured, CaptureFailureReason.StorageFailed);
        }
        finally
        {
            FinishCaptureSequence(sessionId);
        }
    }

    /// <summary>Từ màn xem lại/thành phẩm quay về chụp lại một vị trí (hoặc toàn bộ nếu null).</summary>
    public bool BeginRetake(int? position)
    {
        var retake = TryTransition(
            s => s.State is PhotoSessionState.Reviewing or PhotoSessionState.Ready
                 && !s.IsArchived // phiên mở lại trong Admin không có camera: chỉ chỉnh sửa/in lại
                 && (position is null || (position >= 0 && position < s.RequiredPhotoCount)),
            s => s with
            {
                State = PhotoSessionState.Previewing,
                PendingPositions = position is { } p ? [p] : Enumerable.Range(0, s.RequiredPhotoCount).ToArray(),
            });

        if (retake)
        {
            _logger.LogInformation("Chụp lại {Target}", position is null ? "toàn bộ phiên" : $"ảnh #{position + 1}");
        }

        return retake;
    }

    /// <summary>Quay về xem lại: hủy lượt chụp lại đang chờ (giữ ảnh cũ) hoặc rời màn thành phẩm.</summary>
    public bool ReturnToReview() =>
        TryTransition(
            s => (s.State == PhotoSessionState.Previewing && s.AllSlotsFilled) || s.State == PhotoSessionState.Ready,
            s => s with { State = PhotoSessionState.Reviewing, PendingPositions = [] });

    // ---------------------------------------------------------------- Ghép ảnh

    /// <summary>
    /// Ghép ảnh thành phẩm với bộ lọc chỉ định (null = bộ lọc đang chọn của phiên) và Manual Crop đã xác nhận.
    /// Nếu thành phẩm hiện tại đã đúng mọi đầu vào thì dùng lại. Thành phẩm này là nguồn duy nhất cho cả
    /// ảnh xem trước và mọi định dạng xuất. Lỗi ghép không làm mất ảnh gốc và đưa phiên về trạng thái trước đó.
    /// </summary>
    public async Task<CompositionOutcome> ComposeAsync(ColorFilter? filter = null, CancellationToken cancellationToken = default)
    {
        PhotoSessionState previousState;
        CompositionRequest? request;
        Guid[] photoIds;
        string templateId;
        ColorFilter effectiveFilter;
        SlotCrop?[] crops;
        long generation;
        CancellationTokenSource? previousCts = null;
        CancellationTokenSource? linkedCts = null;
        lock (_gate)
        {
            if (_current is null)
            {
                return CompositionOutcome.NotReady;
            }

            effectiveFilter = filter ?? _current.Filter;
            if (_current.State == PhotoSessionState.Composing)
            {
                // Đang ghép cùng bộ lọc: báo bận để chống bấm trùng.
                // Nếu người dùng chuyển sang bộ lọc khác: hủy lượt ghép cũ và ghép bộ lọc mới nhất.
                if (filter is null || effectiveFilter == _current.Filter)
                {
                    return CompositionOutcome.Busy;
                }
            }
            else if (_current.State is not (PhotoSessionState.Reviewing or PhotoSessionState.Ready))
            {
                return CompositionOutcome.NotReady;
            }

            if (_current.Template is null
                || !_current.CanEdit
                || !_current.AllSlotsFilled)
            {
                return CompositionOutcome.NotReady;
            }

            var photos = _current.Slots.Select(p => p!).ToArray();
            photoIds = photos.Select(p => p.PhotoId).ToArray();
            templateId = _current.Template.Id;
            if (_current.State != PhotoSessionState.Composing)
            {
                _preComposeState = _current.State;
            }

            previousState = _preComposeState;
            crops = [.. _current.SlotCrops];

            var canReuse = _current.State != PhotoSessionState.Composing
                && _current.Composed is { } existing
                && existing.Filter == effectiveFilter
                && existing.TemplateId == templateId
                && existing.PhotoIds.SequenceEqual(photoIds)
                && existing.SlotCrops.SequenceEqual(crops);

            generation = ++_composeGeneration;
            previousCts = _composeCts;

            if (canReuse)
            {
                _composeCts = null;
                request = null;
                _current = _current with { State = PhotoSessionState.Ready, Filter = effectiveFilter };
            }
            else
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _composeCts = linkedCts;
                request = new CompositionRequest(
                    _current.SessionId,
                    _current.Template,
                    photos.Select(p => new CompositionPhoto(p.OriginalFilePath, p.Mirrored)).ToArray(),
                    effectiveFilter,
                    TimeZoneInfo.ConvertTime(_current.CreatedAt, _timeProvider.LocalTimeZone),
                    crops);
                _current = _current with { State = PhotoSessionState.Composing, Composed = null, LastCompositionFailed = false, Filter = effectiveFilter };
            }
        }

        if (previousCts is not null)
        {
            try
            {
                previousCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previousCts.Dispose();
        }

        RaiseSessionChanged();
        if (request is null)
        {
            return CompositionOutcome.Succeeded;
        }

        _logger.LogInformation(
            "Ghép ảnh phiên {SessionId}, template {TemplateId}, bộ lọc {Filter}, {CropCount} ô chỉnh tay",
            request.SessionId, templateId, effectiveFilter, crops.Count(c => c is not null));

        try
        {
            var image = await _composer.ComposeAsync(request, linkedCts!.Token).ConfigureAwait(false);
            var result = new ComposedResult(image.FilePath, image.WidthPixels, image.HeightPixels, templateId, effectiveFilter, photoIds, _timeProvider.GetUtcNow(), crops);
            var applied = false;
            Mutate(s =>
            {
                if (s.SessionId != request.SessionId || s.State != PhotoSessionState.Composing || _composeGeneration != generation)
                {
                    return null;
                }

                applied = true;
                return s with { State = PhotoSessionState.Ready, Composed = result };
            });

            if (!applied)
            {
                return CompositionOutcome.Failed;
            }

            _logger.LogInformation("Đã ghép ảnh {Width}x{Height} cho phiên {SessionId}", image.WidthPixels, image.HeightPixels, request.SessionId);
            return CompositionOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (linkedCts!.IsCancellationRequested)
        {
            RestoreAfterComposition(request.SessionId, generation, previousState, failed: false);
            return CompositionOutcome.Failed;
        }
        catch (Exception ex) when (ex is CompositionException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Ghép ảnh thất bại ở phiên {SessionId}", request.SessionId);
            RestoreAfterComposition(request.SessionId, generation, previousState, failed: true);
            return CompositionOutcome.Failed;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_composeCts, linkedCts))
                {
                    _composeCts = null;
                }
            }

            linkedCts?.Dispose();
        }
    }

    // ---------------------------------------------------------------- Manual Crop

    /// <summary>
    /// Xác nhận Manual Crop cho ô <paramref name="slotIndex"/> (chỉ số trong <c>Template.Slots</c>, không phải photoIndex)
    /// rồi ghép lại. <paramref name="crop"/> = null hoặc trùng Auto Crop → khôi phục Auto Crop cho ô đó.
    /// Chỉ ô <c>cover</c> được chỉnh. Dữ liệu chỉ nằm trong phiên; template và ảnh gốc không đổi.
    /// Thành phẩm cũ bị bỏ ngay khi crop đổi để không bao giờ xem/xuất nhầm kết quả cũ.
    /// </summary>
    /// <returns><see cref="CompositionOutcome.NotReady"/> nếu trạng thái/ô/dữ liệu crop không hợp lệ (không thay đổi gì).</returns>
    public Task<CompositionOutcome> ApplySlotCropAsync(int slotIndex, SlotCrop? crop, CancellationToken cancellationToken = default)
    {
        var accepted = false;
        Mutate(s =>
        {
            if (s.State is not (PhotoSessionState.Reviewing or PhotoSessionState.Ready)
                || s.Template is null
                || !s.CanEdit
                || slotIndex < 0
                || slotIndex >= s.SlotCrops.Count)
            {
                return null;
            }

            var slot = s.Template.Slots[slotIndex];
            if (crop is not null && (!SlotCrop.IsSupported(slot) || !crop.IsValid))
            {
                return null;
            }

            accepted = true;
            var normalized = crop == SlotCrop.AutoFor(slot) ? null : crop;
            if (s.SlotCrops[slotIndex] == normalized)
            {
                return null;
            }

            var crops = s.SlotCrops.ToArray();
            crops[slotIndex] = normalized;
            return s with { SlotCrops = crops, Composed = null };
        });

        if (!accepted)
        {
            _logger.LogWarning("Không áp dụng được chỉnh vùng cắt cho ô #{SlotIndex}", slotIndex);
            return Task.FromResult(CompositionOutcome.NotReady);
        }

        _logger.LogInformation("Chỉnh vùng cắt ô #{SlotIndex}: {Mode}", slotIndex, crop is null ? "Auto Crop" : "thủ công");
        return ComposeAsync(null, cancellationToken);
    }

    /// <summary>Khôi phục Auto Crop cho mọi ô rồi ghép lại.</summary>
    public Task<CompositionOutcome> ResetAllSlotCropsAsync(CancellationToken cancellationToken = default)
    {
        var accepted = false;
        Mutate(s =>
        {
            if (s.State is not (PhotoSessionState.Reviewing or PhotoSessionState.Ready) || s.Template is null || !s.CanEdit)
            {
                return null;
            }

            accepted = true;
            return s.SlotCrops.All(c => c is null)
                ? null
                : s with { SlotCrops = new SlotCrop?[s.SlotCrops.Count], Composed = null };
        });

        return accepted ? ComposeAsync(null, cancellationToken) : Task.FromResult(CompositionOutcome.NotReady);
    }

    // ---------------------------------------------------------------- Xuất file

    /// <summary>
    /// Xuất ảnh thành phẩm ra các định dạng yêu cầu vào thư mục chỉ định. Mỗi định dạng thành công/thất bại
    /// độc lập; lỗi không ảnh hưởng ảnh gốc, ảnh thành phẩm hay các file đã xuất.
    /// <paramref name="version"/> = null: xác nhận thành phẩm đang xem thành một phiên bản (hoặc dùng lại phiên bản trùng)
    /// rồi xuất đúng file phiên bản đó; ngược lại xuất đúng phiên bản đã lưu được chỉ định.
    /// </summary>
    public async Task<IReadOnlyList<FormatExportResult>> ExportAsync(
        IReadOnlyCollection<ExportFormat> formats,
        string destinationDirectory,
        CancellationToken cancellationToken = default,
        int? version = null)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var requested = formats.Distinct().Order().ToArray();

        if (!await _exportGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return requested.Select(f => new FormatExportResult(f, ExportResult.Failure(ExportError.Busy))).ToArray();
        }

        try
        {
            var sessionId = Current?.SessionId ?? Guid.Empty;
            var output = version is { } requestedVersion
                ? Current?.FindVersion(requestedVersion)
                : await ConfirmOutputAsync(cancellationToken).ConfigureAwait(false);

            if (output is null || !File.Exists(output.FilePath))
            {
                return requested.Select(f => new FormatExportResult(f, ExportResult.Failure(ExportError.NothingToSave))).ToArray();
            }

            var canvas = new TemplateCanvas { WidthPixels = output.WidthPixels, HeightPixels = output.HeightPixels, Dpi = output.Dpi };

            var baseName = PhotoFileNaming.SuggestBaseFileName(_timeProvider.GetUtcNow(), _timeProvider.LocalTimeZone);
            var results = new List<FormatExportResult>(requested.Length);
            foreach (var format in requested)
            {
                ExportResult result;
                if (cancellationToken.IsCancellationRequested)
                {
                    result = ExportResult.Failure(ExportError.Cancelled);
                }
                else
                {
                    result = await _exporter
                        .ExportAsync(new ExportRequest(output.FilePath, format, destinationDirectory, baseName, canvas), cancellationToken)
                        .ConfigureAwait(false);
                }

                results.Add(new FormatExportResult(format, result));
                if (result is { Succeeded: true, FilePath: { } path })
                {
                    var exported = new ExportedFile(format, path, _timeProvider.GetUtcNow()) { Version = output.Version };
                    Mutate(s => s.SessionId == sessionId ? s with { Exports = [.. s.Exports, exported] } : null);
                    _logger.LogInformation("Đã xuất {Format} cho phiên {SessionId}", format, sessionId);
                }
                else
                {
                    _logger.LogWarning("Xuất {Format} thất bại ở phiên {SessionId}: {ExportError}", format, sessionId, result.Error);
                }
            }

            await PersistCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            return results;
        }
        finally
        {
            _exportGate.Release();
        }
    }

    // ---------------------------------------------------------------- Ảnh xem trước

    /// <summary>Ảnh xem trước của vị trí <paramref name="position"/>, đã lật đúng hướng như lúc chụp.</summary>
    public async Task<byte[]?> GetSlotPreviewAsync(int position, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        var slots = Current?.Slots;
        if (slots is null || position < 0 || position >= slots.Count || slots[position] is not { } photo)
        {
            return null;
        }

        return await _previewRenderer
            .RenderPreviewAsync(photo.OriginalFilePath, photo.Mirrored, maxWidthPixels, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Xem mọi ảnh gốc đã chụp trong phiên hiện tại, bao gồm ảnh cũ đã bị thay khi chụp lại.
    /// Tìm bằng PhotoId trong lịch sử phiên; không nhận đường dẫn file từ giao diện.
    /// </summary>
    public async Task<byte[]?> GetCapturedPhotoPreviewAsync(Guid photoId, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        var photo = Current?.Photos.FirstOrDefault(p => p.PhotoId == photoId);
        if (photo is null)
        {
            return null;
        }

        return await _previewRenderer
            .RenderPreviewAsync(photo.OriginalFilePath, photo.Mirrored, maxWidthPixels, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Ảnh xem trước của thành phẩm (đã có hướng đúng, không lật thêm).</summary>
    public async Task<byte[]?> GetComposedPreviewAsync(int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        if (Current?.Composed is not { } composed)
        {
            return null;
        }

        return await _previewRenderer
            .RenderPreviewAsync(composed.FilePath, mirror: false, maxWidthPixels, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ảnh xem trước thu nhỏ cho bộ lọc <paramref name="filter"/>, tạo từ ảnh chụp đầu tiên đang chọn của phiên
    /// bằng đúng thuật toán màu của Photo Composer.
    /// </summary>
    public async Task<byte[]?> GetFilterPreviewAsync(ColorFilter filter, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        var photo = Current?.Slots.FirstOrDefault(p => p is not null);
        if (photo is null)
        {
            return null;
        }

        return await _previewRenderer
            .RenderPreviewAsync(photo.OriginalFilePath, photo.Mirrored, maxWidthPixels, filter, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Nội bộ

    private async Task<CapturedPhoto?> CaptureOneAsync(
        ICameraDevice camera, Guid sessionId, int position, bool mirror, CancellationToken cancellationToken)
    {
        var frame = await camera.CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
        var capturedAt = _timeProvider.GetUtcNow();

        if (frame.Data.Length == 0
            || frame.Data.LongLength > Options.MaxFrameBytes
            || !JpegImageInfo.TryReadDimensions(frame.Data, out var width, out var height))
        {
            _logger.LogWarning("Khung hình không hợp lệ ở phiên {SessionId} ({SizeBytes} byte)", sessionId, frame.Data.LongLength);
            return null;
        }

        int sequence;
        lock (_gate)
        {
            sequence = (_current?.SessionId == sessionId ? _current.Photos.Count : 0) + 1;
        }

        var photoId = Guid.NewGuid();

        // Đã có khung hình: luôn ghi trọn ảnh gốc, không hủy giữa chừng kể cả khi người dùng rời màn hình.
        var stored = await _originalStore
            .SaveOriginalAsync(sessionId, photoId, sequence, frame.Data, CancellationToken.None)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Đã lưu ảnh gốc {PhotoId} (lần chụp #{Sequence}, vị trí {Position}, {Width}x{Height}, gương={Mirrored}) phiên {SessionId}",
            photoId, sequence, position + 1, width, height, mirror, sessionId);

        return new CapturedPhoto(photoId, sessionId, sequence, position, capturedAt, width, height, stored.FilePath, stored.SizeBytes, mirror);
    }

    /// <summary>Gán ảnh mới cho vị trí; ảnh cũ ở vị trí đó vẫn còn trong lịch sử và trên ổ đĩa.</summary>
    private bool AssignPhoto(CapturedPhoto photo)
    {
        var assigned = false;
        Mutate(s =>
        {
            if (s.SessionId != photo.SessionId || s.IsEnded)
            {
                _logger.LogWarning("Ảnh {PhotoId} hoàn tất sau khi phiên {SessionId} không còn hoạt động", photo.PhotoId, photo.SessionId);
                return null;
            }

            var slots = s.Slots.ToArray();
            slots[photo.Position] = photo;
            assigned = true;

            // Ảnh mới thay ảnh cũ: vùng cắt thủ công chỉnh cho ảnh cũ không còn ý nghĩa → các ô dùng ảnh này về Auto Crop.
            var crops = s.SlotCrops.ToArray();
            for (var i = 0; i < crops.Length && s.Template is not null; i++)
            {
                if (s.Template.Slots[i].PhotoIndex == photo.Position)
                {
                    crops[i] = null;
                }
            }

            return s with
            {
                Slots = slots,
                SlotCrops = crops,
                Photos = [.. s.Photos, photo],
                PendingPositions = s.PendingPositions.Where(p => p != photo.Position).ToArray(),

                // Ảnh được chọn đã đổi: thành phẩm cũ không còn đúng, phải ghép lại.
                Composed = null,
            };
        });
        return assigned;
    }

    private void FinishCaptureSequence(Guid sessionId) =>
        Mutate(s => s.SessionId == sessionId && s.State == PhotoSessionState.Capturing
            ? s with
            {
                State = s.AllSlotsFilled && s.PendingPositions.Count == 0 ? PhotoSessionState.Reviewing : PhotoSessionState.Previewing,
            }
            : null);

    private void RestoreAfterComposition(Guid sessionId, long generation, PhotoSessionState previousState, bool failed) =>
        Mutate(s => s.SessionId == sessionId && s.State == PhotoSessionState.Composing && _composeGeneration == generation
            ? s with { State = previousState, LastCompositionFailed = failed }
            : null);

    private bool TryTransition(Func<PhotoSession, bool> canTransition, Func<PhotoSession, PhotoSession> transition)
    {
        var changed = false;
        var allowed = false;
        lock (_gate)
        {
            if (_current is not null && canTransition(_current))
            {
                allowed = true;
                var next = transition(_current);
                changed = !ReferenceEquals(next, _current) && next != _current;
                _current = next;
            }
        }

        if (changed)
        {
            RaiseSessionChanged();
        }

        return allowed;
    }

    /// <summary>Thay đổi phiên hiện tại dưới khóa; hàm trả về null nghĩa là không đổi.</summary>
    private void Mutate(Func<PhotoSession, PhotoSession?> mutation)
    {
        var changed = false;
        lock (_gate)
        {
            if (_current is not null && mutation(_current) is { } next)
            {
                changed = next != _current;
                _current = next;
            }
        }

        if (changed)
        {
            RaiseSessionChanged();
        }
    }

    /// <summary>Kết thúc phiên hiện tại dưới khóa; trả về ảnh chụp phiên đã kết thúc (null nếu không có gì thay đổi).</summary>
    private PhotoSession? EndCurrentLocked()
    {
        if (_current is null)
        {
            return null;
        }

        if (_current.IsArchived)
        {
            // Phiên mở lại để xem/chỉnh: chỉ đóng, dữ liệu đã lưu giữ nguyên.
            var closed = _current;
            _current = null;
            _logger.LogInformation("Đóng phiên đã lưu {SessionId}", closed.SessionId);
            return closed;
        }

        if (_current.IsEnded)
        {
            return null;
        }

        var endState = _current.Exports.Count > 0 || _current.Versions.Count > 0 ? PhotoSessionState.Completed : PhotoSessionState.Cancelled;
        _logger.LogInformation("Kết thúc phiên {SessionId} với trạng thái {State}", _current.SessionId, endState);
        _current = _current with { State = endState };
        return _current;
    }

    private void RaiseSessionChanged()
    {
        try
        {
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Lỗi của subscriber (UI) không được làm hỏng trạng thái nghiệp vụ.
            _logger.LogError(ex, "Lỗi khi xử lý sự kiện SessionChanged");
        }
    }
}
