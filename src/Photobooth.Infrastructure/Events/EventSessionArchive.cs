using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Composition;
using Photobooth.Application.Events;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;
using SkiaSharp;

namespace Photobooth.Infrastructure.Events;

/// <summary>
/// Lưu phiên chụp trong Event Workspace hiện tại:
/// <code>
/// {Sự kiện}/Sessions/{yyyyMMdd-HHmmss-xxxxxxxx}/
///   session.json        metadata (đường dẫn tương đối)
///   Template/           snapshot template.json + asset đã dùng
///   Originals/          ảnh gốc (bất biến)
///   Outputs/            final-v{N}.png (+ .thumb.jpg) — không bao giờ ghi đè
///   Drafts/             bản ghép tạm khi đang chỉnh (được dọn khi phiên kết thúc)
/// </code>
/// </summary>
public sealed class EventSessionArchive : ISessionArchive
{
    public const string SessionFileName = "session.json";
    public const string TemplateFolderName = "Template";
    public const string OutputsFolderName = "Outputs";
    private const int ThumbnailWidth = 360;
    private const int ThumbnailJpegQuality = 85;

    private static readonly JsonSerializerOptions TemplateWriteOptions = new(JsonTemplateCatalog.SerializerOptions)
    {
        WriteIndented = true,
        Encoder = AppStateStore.SerializerOptions.Encoder,
    };

    private readonly EventWorkspaceService _workspaces;
    private readonly PhotoboothStoragePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventSessionArchive> _logger;

    public EventSessionArchive(EventWorkspaceService workspaces, PhotoboothStoragePaths paths, TimeProvider timeProvider, ILogger<EventSessionArchive> logger)
    {
        _workspaces = workspaces;
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

    public async Task SaveAsync(PhotoSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Directory is not { } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string? templateFile = null;
        if (session.Template is { } template)
        {
            templateFile = await EnsureTemplateSnapshotAsync(directory, template, cancellationToken).ConfigureAwait(false);
        }

        var document = ToDocument(session, directory, templateFile);
        await SafeFileWriter.WriteAsync(
                Path.Combine(directory, SessionFileName),
                (stream, token) => JsonSerializer.SerializeAsync(stream, document, AppStateStore.SerializerOptions, token),
                overwrite: true,
                cancellationToken)
            .ConfigureAwait(false);
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

        // Sự kiện hiện tại luôn có trong danh sách (kể cả dữ liệu cũ chưa có danh bạ).
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
        return await Task.Run<IReadOnlyList<ArchivedSessionSummary>>(
            () =>
            {
                if (!Directory.Exists(workspace.SessionsDirectory))
                {
                    return [];
                }

                var result = new List<ArchivedSessionSummary>();
                foreach (var directory in Directory.EnumerateDirectories(workspace.SessionsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = Path.Combine(directory, SessionFileName);
                    if (File.Exists(file))
                    {
                        result.Add(Summarize(directory, file) with { EventKey = key, EventName = workspace.Info.Name });
                    }
                }

                return result.OrderByDescending(s => s.CreatedAt).ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArchivedSession> LoadSessionAsync(string sessionKey, string? eventKey = null, CancellationToken cancellationToken = default)
    {
        var workspace = await ResolveWorkspaceAsync(eventKey, cancellationToken).ConfigureAwait(false);
        if (!IsSafeKey(sessionKey))
        {
            throw new DirectoryNotFoundException("Mã phiên không hợp lệ.");
        }

        var directory = Path.Combine(workspace.SessionsDirectory, sessionKey);
        var file = Path.Combine(directory, SessionFileName);
        if (!File.Exists(file))
        {
            throw new DirectoryNotFoundException("Không tìm thấy phiên trong sự kiện hiện tại.");
        }

        var document = await Task.Run(() => ReadDocument(file), cancellationToken).ConfigureAwait(false);
        _paths.RegisterSessionDirectory(document.SessionId, directory);

        PhotoTemplate? template = null;
        string? reason = null;
        if (document.TemplateFile is { } templateRelative && Resolve(directory, templateRelative) is { } templatePath && File.Exists(templatePath))
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

        var photos = new List<CapturedPhoto>();
        foreach (var photo in document.Photos)
        {
            if (Resolve(directory, photo.File) is { } path)
            {
                photos.Add(new CapturedPhoto(photo.PhotoId, document.SessionId, photo.Sequence, photo.Position, photo.CapturedAt,
                    photo.WidthPixels, photo.HeightPixels, path, photo.SizeBytes, photo.Mirrored));
            }
        }

        var versions = document.Versions
            .Select(v => ToVersion(directory, v))
            .OfType<OutputVersion>()
            .OrderBy(v => v.Version)
            .ToArray();

        // Ảnh nguồn cần cho chỉnh sửa: theo phiên bản mới nhất, hoặc lựa chọn hiện tại nếu chưa có phiên bản.
        var requiredIds = versions.Length > 0 ? versions[^1].PhotoIds.Select(id => (Guid?)id).ToList() : document.SelectedPhotoIds;
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

        var slotCount = template?.Slots.Count ?? document.SlotCrops.Count;
        var crops = Enumerable.Range(0, slotCount)
            .Select(i => i < document.SlotCrops.Count ? document.SlotCrops[i]?.ToCrop() : null)
            .ToArray();

        var session = new PhotoSession(
            document.SessionId,
            document.CreatedAt,
            versions.Length > 0 || reason is not null ? PhotoSessionState.Ready : PhotoSessionState.Reviewing,
            template,
            photos,
            slots,
            [],
            document.Mirror,
            null,
            false,
            document.Exports
                .Select(e => SessionDocumentEnums.ParseFormat(e.Format) is { } format ? new ExportedFile(format, e.FilePath, e.ExportedAt) { Version = e.Version } : null)
                .OfType<ExportedFile>()
                .ToArray(),
            crops,
            SessionDocumentEnums.ParseFilter(document.Filter))
        {
            Directory = directory,
            EventId = document.EventId,
            Versions = versions,
            PrintJobs = document.PrintJobs
                .Select(j => Resolve(directory, j.File) is { } path
                    ? new PrintJob(j.PrintJobId, document.SessionId, j.Version, path, j.Copies, j.PrinterName, j.IsPhysicalPrinter,
                        SessionDocumentEnums.ParsePrintStatus(j.Status), j.CreatedAt, j.UpdatedAt, j.ErrorMessage)
                    : null)
                .OfType<PrintJob>()
                .ToArray(),
            IsArchived = true,
            CanEdit = reason is null,
        };

        return new ArchivedSession(sessionKey, session, reason);
    }

    // ---------------------------------------------------------------- Nội bộ

    /// <summary>Sao chép template (và asset ảnh đang dùng) vào thư mục phiên một lần, để mở lại được kể cả khi thư viện template đổi/xóa.</summary>
    private async Task<string> EnsureTemplateSnapshotAsync(string sessionDirectory, PhotoTemplate template, CancellationToken cancellationToken)
    {
        var snapshotDirectory = Path.Combine(sessionDirectory, TemplateFolderName);
        var templateFile = Path.Combine(snapshotDirectory, JsonTemplateCatalog.TemplateFileName);
        var relative = $"{TemplateFolderName}/{JsonTemplateCatalog.TemplateFileName}";
        if (File.Exists(templateFile))
        {
            return relative;
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
        return relative;
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

    private SessionDocument ToDocument(PhotoSession session, string directory, string? templateFile) => new()
    {
        SessionId = session.SessionId,
        EventId = session.EventId,
        CreatedAt = session.CreatedAt,
        UpdatedAt = _timeProvider.GetUtcNow(),
        Status = session switch
        {
            { State: PhotoSessionState.Completed } => "completed",
            { State: PhotoSessionState.Cancelled } => "cancelled",

            // Phiên mở lại trong Admin: đã có thành phẩm thì vẫn là đã hoàn tất.
            { IsArchived: true } => session.Versions.Count > 0 ? "completed" : "cancelled",
            _ => "inProgress",
        },
        TemplateId = session.Template?.Id,
        TemplateName = session.Template?.Name,
        TemplateFile = templateFile,
        Mirror = session.Mirror,
        Photos = session.Photos
            .Select(p => new PhotoDocument(p.PhotoId, p.Sequence, p.Position, p.CapturedAt, p.WidthPixels, p.HeightPixels,
                Relative(directory, p.OriginalFilePath), p.SizeBytes, p.Mirrored))
            .ToList(),
        SelectedPhotoIds = session.Slots.Select(p => p?.PhotoId).ToList(),
        Filter = session.Filter.ToString(),
        SlotCrops = session.SlotCrops.Select(CropDocument.From).ToList(),
        Versions = session.Versions
            .Select(v => new VersionDocument
            {
                Version = v.Version,
                File = Relative(directory, v.FilePath),
                Thumbnail = v.ThumbnailPath is null ? null : Relative(directory, v.ThumbnailPath),
                WidthPixels = v.WidthPixels,
                HeightPixels = v.HeightPixels,
                Dpi = v.Dpi,
                TemplateId = v.TemplateId,
                Filter = v.Filter.ToString(),
                PhotoIds = [.. v.PhotoIds],
                SlotCrops = v.SlotCrops.Select(CropDocument.From).ToList(),
                CreatedAt = v.CreatedAt,
            })
            .ToList(),
        Exports = session.Exports.Select(e => new ExportDocument(e.Format.ToString(), e.FilePath, e.ExportedAt, e.Version)).ToList(),
        PrintJobs = session.PrintJobs
            .Select(j => new PrintJobDocument(j.PrintJobId, j.OutputVersion, Relative(directory, j.OutputFilePath), j.Copies, j.PrinterName,
                j.IsPhysicalPrinter, j.Status.ToString(), j.CreatedAt, j.UpdatedAt, j.ErrorMessage))
            .ToList(),
    };

    private ArchivedSessionSummary Summarize(string directory, string file)
    {
        var key = Path.GetFileName(directory);
        try
        {
            var document = ReadDocument(file);
            var latest = document.Versions.OrderBy(v => v.Version).Select(v => ToVersion(directory, v)).LastOrDefault();
            string? problem = null;
            if (document.Versions.Count > 0 && (latest is null || !File.Exists(latest.FilePath)))
            {
                problem = "Thiếu file ảnh thành phẩm.";
            }
            else if (document.Versions.Count == 0)
            {
                problem = "Chưa có ảnh thành phẩm.";
            }

            return new ArchivedSessionSummary(key, document.SessionId, document.CreatedAt, document.TemplateName, document.Photos.Count,
                latest, document.Versions.Count, document.PrintJobs.Count, problem);
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Không đọc được session.json của một phiên trong sự kiện");
            return new ArchivedSessionSummary(key, Guid.Empty, Directory.GetCreationTimeUtc(directory), null, 0, null, 0, 0,
                "Không đọc được dữ liệu phiên (session.json).");
        }
    }

    private static OutputVersion? ToVersion(string directory, VersionDocument v) =>
        Resolve(directory, v.File) is { } path
            ? new OutputVersion(v.Version, path, v.WidthPixels, v.HeightPixels, v.Dpi, v.TemplateId, SessionDocumentEnums.ParseFilter(v.Filter),
                v.PhotoIds, v.SlotCrops.Select(c => c?.ToCrop()).ToArray(), v.CreatedAt)
            {
                ThumbnailPath = v.Thumbnail is null ? null : Resolve(directory, v.Thumbnail),
            }
            : null;

    private static SessionDocument ReadDocument(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(file), AppStateStore.SerializerOptions)
                ?? throw new InvalidDataException("session.json rỗng.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException("Không đọc được session.json.", ex);
        }
    }

    private static string Relative(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return TemplateAssetPath.IsSafeRelative(relative) ? relative.Replace('\\', '/') : path;
    }

    /// <summary>Đường dẫn trong metadata phải là tương đối và nằm trong thư mục phiên (không tin dữ liệu file).</summary>
    private static string? Resolve(string directory, string? relative)
    {
        if (!TemplateAssetPath.IsSafeRelative(relative))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative!));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string EventKey(Guid eventId) => eventId.ToString("N");

    /// <summary>null → sự kiện hiện tại; mã khác → sự kiện đã biết (tra danh bạ, KHÔNG nhận đường dẫn).</summary>
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
