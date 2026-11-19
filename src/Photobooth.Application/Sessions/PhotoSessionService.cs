using Microsoft.Extensions.Logging;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Templates;

namespace Photobooth.Application.Sessions;

/// <summary>
/// Điều phối phiên chụp: làm chủ trạng thái, chọn template, chụp nhiều ảnh liên tiếp (có đếm ngược),
/// chụp lại từng ảnh/toàn bộ, ghép ảnh và xuất file. Ảnh gốc luôn được lưu trước khi tính là thành công.
/// </summary>
/// <remarks>
/// Đăng ký singleton để trạng thái phiên tồn tại độc lập với vòng đời Razor Component và WebView.
/// Camera được truyền vào từng lần chụp vì adapter camera có thể gắn với vòng đời UI.
/// </remarks>
public sealed class PhotoSessionService
{
    private static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);

    /// <summary>Khoảng dừng ngắn sau mỗi ảnh để khách thấy phản hồi "đã chụp" trước ảnh kế tiếp.</summary>
    private static readonly TimeSpan SavedFeedbackDelay = TimeSpan.FromMilliseconds(800);

    private readonly IOriginalPhotoStore _originalStore;
    private readonly IPhotoComposer _composer;
    private readonly IImagePreviewRenderer _previewRenderer;
    private readonly IComposedPhotoExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PhotoSessionService> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private PhotoSession? _current;

    public PhotoSessionService(
        IOriginalPhotoStore originalStore,
        IPhotoComposer composer,
        IImagePreviewRenderer previewRenderer,
        IComposedPhotoExporter exporter,
        CaptureOptions options,
        TimeProvider timeProvider,
        ILogger<PhotoSessionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _originalStore = originalStore;
        _composer = composer;
        _previewRenderer = previewRenderer;
        _exporter = exporter;
        _timeProvider = timeProvider;
        _logger = logger;
        Options = options;
    }

    /// <summary>Phát sinh sau mỗi thay đổi trạng thái phiên (có thể từ luồng nền).</summary>
    public event EventHandler? SessionChanged;

    public CaptureOptions Options { get; }

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

    /// <summary>Kết thúc phiên đang có (nếu còn) và tạo phiên mới hoàn toàn.</summary>
    public PhotoSession StartNewSession()
    {
        PhotoSession created;
        lock (_gate)
        {
            EndCurrentLocked();
            created = PhotoSession.Create(Guid.NewGuid(), _timeProvider.GetUtcNow(), Options.PhotosPerSession, Options.MirrorByDefault);
            _current = created;
        }

        _logger.LogInformation("Bắt đầu phiên chụp {SessionId} ({PhotoCount} ảnh)", created.SessionId, created.RequiredPhotoCount);
        RaiseSessionChanged();
        return created;
    }

    /// <summary>
    /// Hủy/kết thúc phiên hiện tại. Không xóa ảnh: Completed nếu đã xuất file, ngược lại Cancelled.
    /// </summary>
    public void EndCurrentSession()
    {
        bool changed;
        lock (_gate)
        {
            changed = EndCurrentLocked();
        }

        if (changed)
        {
            RaiseSessionChanged();
        }
    }

    /// <summary>Hoàn tất phiên sau khi đã xuất ít nhất một file thành công.</summary>
    public bool CompleteSession()
    {
        var completed = TryTransition(
            s => s.State == PhotoSessionState.Ready && s.Exports.Count > 0,
            s => s with { State = PhotoSessionState.Completed });

        if (completed)
        {
            _logger.LogInformation("Hoàn tất phiên {SessionId}", Current?.SessionId);
        }

        return completed;
    }

    // ---------------------------------------------------------------- Template

    /// <summary>Template chỉ dùng được khi số ảnh cần chụp khớp cấu hình số ảnh mỗi phiên.</summary>
    public bool IsTemplateCompatible(PhotoTemplate template) =>
        template.RequiredPhotoCount == Options.PhotosPerSession;

    /// <summary>
    /// Chọn template trước khi chụp. Giả định Giai đoạn 2: không đổi template sau khi đã có ảnh
    /// (quy tắc chính thức CHƯA CHỐT).
    /// </summary>
    public bool SelectTemplate(PhotoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!IsTemplateCompatible(template) || !TemplateValidator.Validate(template).IsValid)
        {
            return false;
        }

        var selected = TryTransition(
            s => s.State is PhotoSessionState.Created or PhotoSessionState.Previewing && s.Photos.Count == 0,
            s => s with
            {
                Template = template,
                PendingPositions = Enumerable.Range(0, s.RequiredPhotoCount).ToArray(),
            });

        if (selected)
        {
            _logger.LogInformation("Chọn template {TemplateId}", template.Id);
        }

        return selected;
    }

    /// <summary>Quay lại chọn template; chỉ được phép khi chưa chụp ảnh nào.</summary>
    public bool ReturnToTemplateSelection() =>
        TryTransition(
            s => s.State is PhotoSessionState.Created or PhotoSessionState.Previewing && s.Photos.Count == 0,
            s => s with { State = PhotoSessionState.Created, Template = null, PendingPositions = [] });

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
    /// Ghép ảnh thành phẩm với bộ lọc chỉ định. Nếu thành phẩm hiện tại đã đúng đầu vào thì dùng lại.
    /// Lỗi ghép không làm mất ảnh gốc và đưa phiên về trạng thái trước đó.
    /// </summary>
    public async Task<CompositionOutcome> ComposeAsync(ColorFilter filter, CancellationToken cancellationToken = default)
    {
        PhotoSessionState previousState;
        CompositionRequest? request;
        Guid[] photoIds;
        string templateId;
        lock (_gate)
        {
            if (_current is null)
            {
                return CompositionOutcome.NotReady;
            }

            if (_current.State == PhotoSessionState.Composing)
            {
                return CompositionOutcome.Busy;
            }

            if (_current.State is not (PhotoSessionState.Reviewing or PhotoSessionState.Ready)
                || _current.Template is null
                || !_current.AllSlotsFilled)
            {
                return CompositionOutcome.NotReady;
            }

            var photos = _current.Slots.Select(p => p!).ToArray();
            photoIds = photos.Select(p => p.PhotoId).ToArray();
            templateId = _current.Template.Id;
            previousState = _current.State;

            var canReuse = _current.Composed is { } existing
                && existing.Filter == filter
                && existing.TemplateId == templateId
                && existing.PhotoIds.SequenceEqual(photoIds);

            if (canReuse)
            {
                request = null;
                _current = _current with { State = PhotoSessionState.Ready };
            }
            else
            {
                request = new CompositionRequest(
                    _current.SessionId,
                    _current.Template,
                    photos.Select(p => new CompositionPhoto(p.OriginalFilePath, p.Mirrored)).ToArray(),
                    filter,
                    TimeZoneInfo.ConvertTime(_current.CreatedAt, _timeProvider.LocalTimeZone));
                _current = _current with { State = PhotoSessionState.Composing, LastCompositionFailed = false };
            }
        }

        RaiseSessionChanged();
        if (request is null)
        {
            return CompositionOutcome.Succeeded;
        }

        _logger.LogInformation("Ghép ảnh phiên {SessionId}, template {TemplateId}, bộ lọc {Filter}", request.SessionId, templateId, filter);

        try
        {
            var image = await _composer.ComposeAsync(request, cancellationToken).ConfigureAwait(false);
            var result = new ComposedResult(image.FilePath, image.WidthPixels, image.HeightPixels, templateId, filter, photoIds, _timeProvider.GetUtcNow());
            Mutate(s => s.SessionId == request.SessionId && s.State == PhotoSessionState.Composing
                ? s with { State = PhotoSessionState.Ready, Composed = result }
                : null);
            _logger.LogInformation("Đã ghép ảnh {Width}x{Height} cho phiên {SessionId}", image.WidthPixels, image.HeightPixels, request.SessionId);
            return CompositionOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreAfterComposition(request.SessionId, previousState, failed: false);
            return CompositionOutcome.Failed;
        }
        catch (Exception ex) when (ex is CompositionException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Ghép ảnh thất bại ở phiên {SessionId}", request.SessionId);
            RestoreAfterComposition(request.SessionId, previousState, failed: true);
            return CompositionOutcome.Failed;
        }
    }

    // ---------------------------------------------------------------- Xuất file

    /// <summary>
    /// Xuất ảnh thành phẩm ra các định dạng yêu cầu vào thư mục chỉ định. Mỗi định dạng thành công/thất bại
    /// độc lập; lỗi không ảnh hưởng ảnh gốc, ảnh thành phẩm hay các file đã xuất.
    /// </summary>
    public async Task<IReadOnlyList<FormatExportResult>> ExportAsync(
        IReadOnlyCollection<ExportFormat> formats,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var requested = formats.Distinct().Order().ToArray();

        if (!await _exportGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return requested.Select(f => new FormatExportResult(f, ExportResult.Failure(ExportError.Busy))).ToArray();
        }

        try
        {
            Guid sessionId;
            ComposedResult? composed;
            TemplateCanvas? canvas;
            lock (_gate)
            {
                sessionId = _current?.SessionId ?? Guid.Empty;
                composed = _current is { State: PhotoSessionState.Ready } ? _current.Composed : null;
                canvas = _current?.Template?.Canvas;
            }

            if (composed is null || canvas is null)
            {
                return requested.Select(f => new FormatExportResult(f, ExportResult.Failure(ExportError.NothingToSave))).ToArray();
            }

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
                        .ExportAsync(new ExportRequest(composed.FilePath, format, destinationDirectory, baseName, canvas), cancellationToken)
                        .ConfigureAwait(false);
                }

                results.Add(new FormatExportResult(format, result));
                if (result is { Succeeded: true, FilePath: { } path })
                {
                    var exported = new ExportedFile(format, path, _timeProvider.GetUtcNow());
                    Mutate(s => s.SessionId == sessionId ? s with { Exports = [.. s.Exports, exported] } : null);
                    _logger.LogInformation("Đã xuất {Format} cho phiên {SessionId}", format, sessionId);
                }
                else
                {
                    _logger.LogWarning("Xuất {Format} thất bại ở phiên {SessionId}: {ExportError}", format, sessionId, result.Error);
                }
            }

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
            return s with
            {
                Slots = slots,
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

    private void RestoreAfterComposition(Guid sessionId, PhotoSessionState previousState, bool failed) =>
        Mutate(s => s.SessionId == sessionId && s.State == PhotoSessionState.Composing
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

    private bool EndCurrentLocked()
    {
        if (_current is null || _current.IsEnded)
        {
            return false;
        }

        var endState = _current.Exports.Count > 0 ? PhotoSessionState.Completed : PhotoSessionState.Cancelled;
        _logger.LogInformation("Kết thúc phiên {SessionId} với trạng thái {State}", _current.SessionId, endState);
        _current = _current with { State = endState };
        return true;
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
