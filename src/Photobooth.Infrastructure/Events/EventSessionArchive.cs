using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Composition;
using Photobooth.Application.Events;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;
using SkiaSharp;

namespace Photobooth.Infrastructure.Events;

/// <summary>
/// Lưu phiên chụp: FILE trong Event Workspace, METADATA trong SQLite (nguồn dữ liệu chính, không còn session.json).
/// <code>
/// {Sự kiện}/Sessions/{yyyyMMdd-HHmmss-xxxxxxxx}/
///   Template/           snapshot template.json + asset đã dùng (định dạng template của Template Manager)
///   Originals/          ảnh gốc (bất biến)
///   Outputs/            final-v{N}.png (+ .thumb.jpg) — không bao giờ ghi đè
///   Drafts/             bản ghép tạm khi đang chỉnh (được dọn khi phiên kết thúc)
/// </code>
/// Mọi đường dẫn trong database là tương đối với thư mục sự kiện; khi đọc luôn kiểm tra nằm trong thư mục đó.
/// </summary>
public sealed class EventSessionArchive : ISessionArchive
{
    public const string TemplateFolderName = "Template";
    public const string OutputsFolderName = "Outputs";
    private const int ThumbnailWidth = 360;
    private const int ThumbnailJpegQuality = 85;

    private static readonly JsonSerializerOptions TemplateWriteOptions = new(JsonTemplateCatalog.SerializerOptions)
    {
        WriteIndented = true,
        Encoder = AppStateStore.SerializerOptions.Encoder,
    };

    private static readonly JsonSerializerOptions CropOptions = new(JsonSerializerDefaults.Web);

    private readonly EventWorkspaceService _workspaces;
    private readonly IDbContextFactory<PhotoboothDbContext> _db;
    private readonly PhotoboothStoragePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventSessionArchive> _logger;

    public EventSessionArchive(
        EventWorkspaceService workspaces,
        IDbContextFactory<PhotoboothDbContext> db,
        PhotoboothStoragePaths paths,
        TimeProvider timeProvider,
        ILogger<EventSessionArchive> logger)
    {
        _workspaces = workspaces;
        _db = db;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public SessionLocation CreateSessionLocation(Guid sessionId, DateTimeOffset createdAt)
    {
        var workspace = _workspaces.Current
            ?? throw new EventWorkspaceUnavailableException(_workspaces.Status.Problem ?? "Chưa chọn thư mục sự kiện.");
        if (!Directory.Exists(workspace.RootDirectory))
        {
            throw new EventWorkspaceUnavailableException("Không tìm thấy thư mục sự kiện (có thể đã bị đổi tên, di chuyển hoặc ổ đĩa đã bị rút).");
        }

        // Tên thư mục đọc được và sắp xếp theo thời gian; phần mã phiên bảo đảm duy nhất.
        var local = TimeZoneInfo.ConvertTime(createdAt, _timeProvider.LocalTimeZone);
        var id = sessionId.ToString("N");
        var name = string.Create(CultureInfo.InvariantCulture, $"{local:yyyyMMdd-HHmmss}-{id[..8]}");
        var directory = Path.Combine(workspace.SessionsDirectory, name);
        if (Directory.Exists(directory))
        {
            directory = Path.Combine(workspace.SessionsDirectory, string.Create(CultureInfo.InvariantCulture, $"{local:yyyyMMdd-HHmmss}-{id}"));
        }

        _paths.RegisterSessionDirectory(sessionId, directory);
        return new SessionLocation(directory, workspace.Info.EventId);
    }

    /// <summary>
    /// Ghi metadata phiên vào SQLite trong MỘT transaction (SaveChanges): phiên, ảnh gốc mới, phiên bản, file xuất, lịch sử in.
    /// Chỉ thêm/cập nhật — không xóa ảnh, phiên bản hay lịch sử. Share Token đã lưu không bao giờ bị đổi hoặc xóa.
    /// </summary>
    /// <exception cref="IOException">Không ghi được (database lỗi, thư mục sự kiện không xác định).</exception>
    public async Task SaveAsync(PhotoSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Directory is not { } directory)
        {
            return;
        }

        var eventId = session.EventId ?? throw new IOException("Phiên không gắn với sự kiện nào.");
        var root = _workspaces.GetEventFolder(eventId) ?? throw new IOException("Không xác định được thư mục của sự kiện chứa phiên.");

        Directory.CreateDirectory(directory);
        string? templateFile = null;
        if (session.Template is { } template)
        {
            templateFile = await EnsureTemplateSnapshotAsync(directory, template, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Sessions
                .Include(s => s.Photos)
                .Include(s => s.Versions)
                .Include(s => s.Exports)
                .Include(s => s.PrintJobs)
                .AsSplitQuery()
                .FirstOrDefaultAsync(s => s.Id == session.SessionId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                row = new SessionRecord { Id = session.SessionId, EventId = eventId, CreatedAt = session.CreatedAt };
                db.Sessions.Add(row);
            }
            else if (row.EventId != eventId)
            {
                throw new IOException("Phiên đã thuộc một sự kiện khác.");
            }

            row.DirectoryPath = Relative(root, directory);
            row.UpdatedAt = _timeProvider.GetUtcNow();
            row.Status = StatusOf(session);
            row.TemplateId = session.Template?.Id ?? row.TemplateId;
            row.TemplateName = session.Template?.Name ?? row.TemplateName;
            row.TemplateFile = templateFile is null ? row.TemplateFile : Relative(root, templateFile);
            row.Mirror = session.Mirror;
            row.Filter = session.Filter.ToString();
            row.SelectedPhotoIds = session.Slots.Select(p => p?.PhotoId).ToList();
            row.SlotCrops = SerializeCrops(session.SlotCrops);

            foreach (var photo in session.Photos.Where(p => row.Photos.All(r => r.Id != p.PhotoId)))
            {
                row.Photos.Add(new PhotoRecord
                {
                    Id = photo.PhotoId,
                    SessionId = row.Id,
                    Sequence = photo.Sequence,
                    Position = photo.Position,
                    CapturedAt = photo.CapturedAt,
                    WidthPixels = photo.WidthPixels,
                    HeightPixels = photo.HeightPixels,
                    FilePath = Relative(root, photo.OriginalFilePath),
                    SizeBytes = photo.SizeBytes,
                    Mirrored = photo.Mirrored,
                });
            }

            foreach (var version in session.Versions)
            {
                var record = row.Versions.FirstOrDefault(v => v.Version == version.Version);
                if (record is null)
                {
                    record = new OutputVersionRecord { SessionId = row.Id, Version = version.Version };
                    row.Versions.Add(record);
                }

                record.FilePath = Relative(root, version.FilePath);
                record.ThumbnailPath = version.ThumbnailPath is null ? null : Relative(root, version.ThumbnailPath);
                record.WidthPixels = version.WidthPixels;
                record.HeightPixels = version.HeightPixels;
                record.Dpi = version.Dpi;
                record.TemplateId = version.TemplateId;
                record.Filter = version.Filter.ToString();
                record.PhotoIds = [.. version.PhotoIds];
                record.SlotCrops = SerializeCrops(version.SlotCrops);
                record.CreatedAt = version.CreatedAt;

                // Share Token chỉ được GÁN một lần; ảnh chụp trạng thái cũ (chưa có token) không bao giờ xóa token đã lưu.
                if (record.ShareToken is null && version.Share is { } share)
                {
                    record.ShareToken = share.Token;
                    record.ShareUrl = share.Url;
                    record.ShareCreatedAt = share.CreatedAt;
                }
                else if (record.ShareToken is not null && version.Share is { } other && other.Token != record.ShareToken)
                {
                    _logger.LogWarning("Bỏ qua thay đổi Share Token của phiên {SessionId} v{Version} (token đã lưu không được đổi)", session.SessionId, version.Version);
                }
            }

            foreach (var export in session.Exports.Where(e => !row.Exports.Any(r => r.FilePath == e.FilePath && r.Format == e.Format.ToString() && r.ExportedAt == e.ExportedAt)))
            {
                row.Exports.Add(new ExportRecord
                {
                    SessionId = row.Id,
                    Format = export.Format.ToString(),
                    FilePath = export.FilePath,
                    ExportedAt = export.ExportedAt,
                    Version = export.Version,
                });
            }

            foreach (var job in session.PrintJobs)
            {
                var record = row.PrintJobs.FirstOrDefault(j => j.Id == job.PrintJobId);
                if (record is null)
                {
                    record = new PrintJobRecord { Id = job.PrintJobId, SessionId = row.Id, CreatedAt = job.CreatedAt };
                    row.PrintJobs.Add(record);
                }

                record.Version = job.OutputVersion;
                record.FilePath = Relative(root, job.OutputFilePath);
                record.Copies = job.Copies;
                record.PrinterName = job.PrinterName;
                record.IsPhysicalPrinter = job.IsPhysicalPrinter;
                record.Status = job.Status.ToString();
                record.UpdatedAt = job.UpdatedAt;
                record.ErrorMessage = job.ErrorMessage;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            // PhotoSessionService xử lý IOException: phiên được đánh dấu chưa lưu được, ảnh trên đĩa không bị ảnh hưởng.
            throw new IOException("Không ghi được dữ liệu phiên vào cơ sở dữ liệu.", ex);
        }
    }

    public async Task<OutputVersion> CreateOutputVersionAsync(PhotoSession session, ComposedResult composed, int version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(composed);
        var directory = session.Directory ?? throw new InvalidOperationException("Phiên không thuộc Event Workspace nào.");
        var outputs = Path.Combine(directory, OutputsFolderName);
        Directory.CreateDirectory(outputs);

        // Không ghi đè: nếu đã có file trùng số (dữ liệu sửa tay...), dùng số kế tiếp.
        string file;
        while (true)
        {
            file = Path.Combine(outputs, string.Create(CultureInfo.InvariantCulture, $"final-v{version}.png"));
            if (!File.Exists(file))
            {
                try
                {
                    await SafeFileWriter.WriteAsync(
                            file,
                            async (stream, token) =>
                            {
                                await using var source = new FileStream(composed.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                                await source.CopyToAsync(stream, token).ConfigureAwait(false);
                            },
                            overwrite: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (File.Exists(file))
                {
                    // Bị chiếm số trong lúc ghi: thử số kế tiếp.
                }
            }

            version++;
        }

        var thumbnail = await TryWriteThumbnailAsync(file, cancellationToken).ConfigureAwait(false);
        return new OutputVersion(
            version,
            file,
            composed.WidthPixels,
            composed.HeightPixels,
            session.Template?.Canvas.Dpi ?? 300,
            composed.TemplateId,
            composed.Filter,
            composed.PhotoIds,
            composed.SlotCrops,
            _timeProvider.GetUtcNow())
        {
            ThumbnailPath = thumbnail,
        };
    }

    public Task DeleteDraftsAsync(PhotoSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var drafts = _paths.GetDraftsDirectory(session.SessionId);
        if (session.Directory is null || !Directory.Exists(drafts))
        {
            return Task.CompletedTask;
        }

        return Task.Run(
            () =>
            {
                // Chỉ xóa bản ghép tạm do ứng dụng tạo; giữ file đang hiển thị nếu phiên còn hoạt động.
                var keep = session.IsEnded ? null : session.Composed?.FilePath;
                foreach (var file in Directory.EnumerateFiles(drafts, "composed-*.png"))
                {
                    if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }
            },
            cancellationToken);
    }

    public IReadOnlyList<KnownEvent> ListKnownEvents()
    {
        var current = _workspaces.Current;
        var known = _workspaces.KnownEvents
            .Select(e => e.EventId == current?.Info.EventId
                ? new KnownEvent(EventKey(e.EventId), current.Info.Name, true, e.LastOpenedAt, null)
                : new KnownEvent(EventKey(e.EventId), e.Name, false, e.LastOpenedAt, _workspaces.DescribeProblem(e)))
            .ToList();

        // Sự kiện hiện tại luôn có trong danh sách.
        if (current is not null && known.All(k => !k.IsCurrent))
        {
            known.Insert(0, new KnownEvent(EventKey(current.Info.EventId), current.Info.Name, true, _timeProvider.GetUtcNow(), null));
        }

        return known;
    }

    public async Task<IReadOnlyList<ArchivedSessionSummary>> ListSessionsAsync(string? eventKey = null, CancellationToken cancellationToken = default)
    {
        var workspace = await ResolveWorkspaceAsync(eventKey, cancellationToken).ConfigureAwait(false);
        var key = EventKey(workspace.Info.EventId);
        await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == workspace.Info.EventId)
            .Select(s => new
            {
                s.Id,
                s.DirectoryPath,
                s.CreatedAt,
                s.TemplateName,
                PhotoCount = s.Photos.Count,
                PrintJobCount = s.PrintJobs.Count,
                Versions = s.Versions.ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await Task.Run<IReadOnlyList<ArchivedSessionSummary>>(
            () => rows
                .Select(r =>
                {
                    var latest = r.Versions.OrderBy(v => v.Version).Select(v => ToVersion(workspace.RootDirectory, v)).LastOrDefault();
                    var problem = r.Versions.Count == 0
                        ? "Chưa có ảnh thành phẩm."
                        : latest is null || !File.Exists(latest.FilePath) ? "Thiếu file ảnh thành phẩm." : null;
                    return new ArchivedSessionSummary(SessionKey(r.DirectoryPath), r.Id, r.CreatedAt, r.TemplateName, r.PhotoCount,
                        latest, r.Versions.Count, r.PrintJobCount, problem)
                    {
                        EventKey = key,
                        EventName = workspace.Info.Name,
                    };
                })
                .OrderByDescending(s => s.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArchivedSession> LoadSessionAsync(string sessionKey, string? eventKey = null, CancellationToken cancellationToken = default)
    {
        var workspace = await ResolveWorkspaceAsync(eventKey, cancellationToken).ConfigureAwait(false);
        if (!IsSafeKey(sessionKey))
        {
            throw new DirectoryNotFoundException("Mã phiên không hợp lệ.");
        }

        await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await db.Sessions
            .AsNoTracking()
            .Include(s => s.Photos)
            .Include(s => s.Versions)
            .Include(s => s.Exports)
            .Include(s => s.PrintJobs)
            .AsSplitQuery()
            .Where(s => s.EventId == workspace.Info.EventId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = candidates.FirstOrDefault(s => string.Equals(SessionKey(s.DirectoryPath), sessionKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new DirectoryNotFoundException("Không tìm thấy phiên trong sự kiện.");

        var root = workspace.RootDirectory;
        var directory = Resolve(root, row.DirectoryPath) ?? throw new InvalidDataException("Đường dẫn phiên trong cơ sở dữ liệu không hợp lệ.");
        _paths.RegisterSessionDirectory(row.Id, directory);

        PhotoTemplate? template = null;
        string? reason = null;
        if (Resolve(root, row.TemplateFile) is { } templatePath && File.Exists(templatePath))
        {
            var (loaded, validation) = await Task.Run(() => JsonTemplateCatalog.LoadTemplateFile(templatePath), cancellationToken).ConfigureAwait(false);
            template = loaded;
            if (loaded is null)
            {
                reason = "Bản lưu template của phiên bị lỗi: " + string.Join(" ", validation.Errors.Take(2));
            }
        }
        else
        {
            reason = "Phiên không còn bản lưu template nên không thể ghép lại.";
        }

        var photos = row.Photos
            .OrderBy(p => p.Sequence)
            .Select(p => Resolve(root, p.FilePath) is { } path
                ? new CapturedPhoto(p.Id, row.Id, p.Sequence, p.Position, p.CapturedAt, p.WidthPixels, p.HeightPixels, path, p.SizeBytes, p.Mirrored)
                : null)
            .OfType<CapturedPhoto>()
            .ToList();

        var versions = row.Versions
            .Select(v => ToVersion(root, v))
            .OfType<OutputVersion>()
            .OrderBy(v => v.Version)
            .ToArray();

        // Ảnh nguồn cần cho chỉnh sửa: theo phiên bản mới nhất, hoặc lựa chọn hiện tại nếu chưa có phiên bản.
        var requiredIds = versions.Length > 0 ? versions[^1].PhotoIds.Select(id => (Guid?)id).ToList() : row.SelectedPhotoIds;
        var slots = requiredIds.Select(id => photos.FirstOrDefault(p => p.PhotoId == id)).ToArray();
        if (reason is null)
        {
            if (template is null || slots.Length != template.RequiredPhotoCount || slots.Length == 0)
            {
                reason = "Dữ liệu ảnh nguồn của phiên không khớp template.";
            }
            else if (slots.Any(p => p is null || !File.Exists(p.OriginalFilePath)))
            {
                reason = "Thiếu ảnh gốc của phiên nên không thể chỉnh sửa; vẫn có thể xem, xuất và in lại ảnh đã lưu.";
            }
        }

        var savedCrops = DeserializeCrops(row.SlotCrops);
        var slotCount = template?.Slots.Count ?? savedCrops.Count;
        var crops = Enumerable.Range(0, slotCount).Select(i => i < savedCrops.Count ? savedCrops[i] : null).ToArray();

        var session = new PhotoSession(
            row.Id,
            row.CreatedAt,
            versions.Length > 0 || reason is not null ? PhotoSessionState.Ready : PhotoSessionState.Reviewing,
            template,
            photos,
            slots,
            [],
            row.Mirror,
            null,
            false,
            row.Exports
                .OrderBy(e => e.Id)
                .Select(e => SessionDocumentEnums.ParseFormat(e.Format) is { } format ? new ExportedFile(format, e.FilePath, e.ExportedAt) { Version = e.Version } : null)
                .OfType<ExportedFile>()
                .ToArray(),
            crops,
            SessionDocumentEnums.ParseFilter(row.Filter))
        {
            Directory = directory,
            EventId = row.EventId,
            Versions = versions,
            PrintJobs = row.PrintJobs
                .OrderBy(j => j.CreatedAt.UtcDateTime)
                .Select(j => Resolve(root, j.FilePath) is { } path
                    ? new PrintJob(j.Id, row.Id, j.Version, path, j.Copies, j.PrinterName, j.IsPhysicalPrinter,
                        SessionDocumentEnums.ParsePrintStatus(j.Status), j.CreatedAt, j.UpdatedAt, j.ErrorMessage)
                    : null)
                .OfType<PrintJob>()
                .ToArray(),
            IsArchived = true,
            CanEdit = reason is null,
        };

        return new ArchivedSession(SessionKey(row.DirectoryPath), session, reason)
        {
            PersistedState = row.Status switch
            {
                "completed" => PhotoSessionState.Completed,
                "cancelled" => PhotoSessionState.Cancelled,
                _ => null,
            },
        };
    }

    // ---------------------------------------------------------------- Nội bộ

    /// <summary>Sao chép template (và asset ảnh đang dùng) vào thư mục phiên một lần, để mở lại được kể cả khi thư viện template đổi/xóa.</summary>
    private async Task<string> EnsureTemplateSnapshotAsync(string sessionDirectory, PhotoTemplate template, CancellationToken cancellationToken)
    {
        var snapshotDirectory = Path.Combine(sessionDirectory, TemplateFolderName);
        var templateFile = Path.Combine(snapshotDirectory, JsonTemplateCatalog.TemplateFileName);
        if (File.Exists(templateFile))
        {
            return templateFile;
        }

        Directory.CreateDirectory(snapshotDirectory);
        var sameDirectory = template.AssetDirectory is { } source
            && string.Equals(Path.GetFullPath(source).TrimEnd('\\'), snapshotDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (!sameDirectory)
        {
            foreach (var decoration in template.Decorations.Where(d => d.Type == DecorationKind.Image))
            {
                if (!TemplateAssetResolver.TryResolve(template.AssetDirectory, decoration.AssetPath, out var assetPath, out _))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(snapshotDirectory, decoration.AssetPath!));
                if (!target.StartsWith(snapshotDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await SafeFileWriter.WriteAsync(
                        target,
                        async (stream, token) =>
                        {
                            await using var input = File.OpenRead(assetPath);
                            await input.CopyToAsync(stream, token).ConfigureAwait(false);
                        },
                        overwrite: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await SafeFileWriter.WriteAsync(
                templateFile,
                (stream, token) => JsonSerializer.SerializeAsync(stream, template, TemplateWriteOptions, token),
                overwrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        return templateFile;
    }

    private async Task<string?> TryWriteThumbnailAsync(string imageFile, CancellationToken cancellationToken)
    {
        var thumbnail = Path.ChangeExtension(imageFile, ".thumb.jpg");
        try
        {
            var bytes = await Task.Run(
                    () =>
                    {
                        using var bitmap = SKBitmap.Decode(imageFile) ?? throw new IOException("Không đọc được ảnh thành phẩm.");
                        using var image = SKImage.FromBitmap(bitmap);
                        var scale = Math.Min(1f, ThumbnailWidth / (float)image.Width);
                        var info = new SKImageInfo(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                        using var surface = SKSurface.Create(info) ?? throw new IOException("Không tạo được ảnh thu nhỏ.");
                        surface.Canvas.Clear(SKColors.White);
                        surface.Canvas.DrawImage(image, SKRect.Create(info.Width, info.Height), TemplateRenderer.Sampling);
                        using var snapshot = surface.Snapshot();
                        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, ThumbnailJpegQuality);
                        return data.ToArray();
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await SafeFileWriter.WriteAsync(thumbnail, (stream, token) => stream.WriteAsync(bytes, token).AsTask(), overwrite: true, cancellationToken)
                .ConfigureAwait(false);
            return thumbnail;
        }
        catch (IOException ex)
        {
            // Ảnh thu nhỏ chỉ để hiển thị nhanh: lỗi không làm hỏng phiên bản thành phẩm.
            _logger.LogWarning(ex, "Không tạo được ảnh thu nhỏ cho thành phẩm");
            return null;
        }
    }

    private static string StatusOf(PhotoSession session) => session switch
    {
        { State: PhotoSessionState.Completed } => "completed",
        { State: PhotoSessionState.Cancelled } => "cancelled",

        // Phiên mở lại trong Admin: đã có thành phẩm thì vẫn là đã hoàn tất.
        { IsArchived: true } => session.Versions.Count > 0 ? "completed" : "cancelled",
        _ => "inProgress",
    };

    internal static OutputVersion? ToVersion(string root, OutputVersionRecord v) =>
        Resolve(root, v.FilePath) is { } path
            ? new OutputVersion(v.Version, path, v.WidthPixels, v.HeightPixels, v.Dpi, v.TemplateId, SessionDocumentEnums.ParseFilter(v.Filter),
                v.PhotoIds, DeserializeCrops(v.SlotCrops), v.CreatedAt)
            {
                ThumbnailPath = v.ThumbnailPath is null ? null : Resolve(root, v.ThumbnailPath),
                Share = ToShareLink(v.ShareToken, v.ShareUrl, v.ShareCreatedAt),
            }
            : null;

    /// <summary>Link chia sẻ đọc từ database: token sai định dạng/URL lạ thì bỏ qua (không hiển thị QR từ dữ liệu không tin cậy).</summary>
    internal static ShareLink? ToShareLink(string? token, string? url, DateTimeOffset? createdAt) =>
        ShareLinkFactory.IsValidToken(token)
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
        && parsed.AbsolutePath.EndsWith("/s/" + token, StringComparison.Ordinal)
            ? new ShareLink(token!, url!, createdAt ?? DateTimeOffset.MinValue)
            : null;

    private static string SerializeCrops(IEnumerable<SlotCrop?> crops) =>
        JsonSerializer.Serialize(crops.Select(CropDocument.From).ToList(), CropOptions);

    private static IReadOnlyList<SlotCrop?> DeserializeCrops(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<CropDocument?>>(json ?? "[]", CropOptions) ?? [])
                .Select(c => c?.ToCrop())
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Đường dẫn lưu trong database: tương đối với thư mục sự kiện, dấu "/". Không cho phép file nằm ngoài thư mục sự kiện.</summary>
    internal static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (!TemplateAssetPath.IsSafeRelative(relative))
        {
            throw new IOException("File nằm ngoài thư mục sự kiện.");
        }

        return relative.Replace('\\', '/');
    }

    /// <summary>Dựng đường dẫn tuyệt đối từ đường dẫn tương đối trong database; null nếu không hợp lệ hoặc vượt ra ngoài thư mục sự kiện.</summary>
    internal static string? Resolve(string root, string? relative)
    {
        if (!TemplateAssetPath.IsSafeRelative(relative))
        {
            return null;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, relative!));
        return full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Mã phiên cho giao diện = tên thư mục phiên (không phải đường dẫn).</summary>
    private static string SessionKey(string directoryPath) => directoryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? directoryPath;

    private static string EventKey(Guid eventId) => eventId.ToString("N");

    /// <summary>null → sự kiện hiện tại; mã khác → sự kiện đã biết (tra database, KHÔNG nhận đường dẫn).</summary>
    private async Task<EventWorkspace> ResolveWorkspaceAsync(string? eventKey, CancellationToken cancellationToken)
    {
        if (eventKey is null)
        {
            return _workspaces.Current ?? throw new EventWorkspaceUnavailableException("Chưa chọn thư mục sự kiện.");
        }

        if (!Guid.TryParseExact(eventKey, "N", out var eventId))
        {
            throw new EventWorkspaceUnavailableException("Mã sự kiện không hợp lệ.");
        }

        return await _workspaces.OpenForBrowsingAsync(eventId, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSafeKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key is not ("." or "..")
        && key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && key.IndexOfAny(['/', '\\', ':']) < 0;
}
