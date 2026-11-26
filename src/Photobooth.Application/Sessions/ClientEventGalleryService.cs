using Photobooth.Application.Composition;
using Photobooth.Application.Events;
using Photobooth.Application.Export;
using Photobooth.Application.Printing;
using Photobooth.Application.Sharing;

namespace Photobooth.Application.Sessions;

/// <summary>Thông tin tối thiểu của sự kiện hiện tại cho thư viện chế độ khách (không chứa đường dẫn hệ thống).</summary>
public sealed record ClientEventInfo(string EventKey, string Name);

/// <summary>Tóm tắt một phiên trong sự kiện hiện tại để hiển thị ở danh sách thư viện chế độ khách.</summary>
public sealed record ClientSessionSummary(
    string SessionKey,
    Guid SessionId,
    string EventKey,
    string EventName,
    DateTimeOffset CreatedAt,
    string? TemplateName,
    int PhotoCount,
    int? LatestVersionNumber,
    int VersionCount,
    int PrintJobCount,
    bool HasThumbnail,
    string? Problem);

/// <summary>Kết quả liệt kê các phiên của sự kiện hiện tại cho chế độ khách.</summary>
public sealed record ClientGalleryListResult(
    ClientEventInfo Event,
    IReadOnlyList<ClientSessionSummary> Sessions);

/// <summary>Metadata ảnh gốc trong chi tiết phiên (chỉ đọc, không chứa đường dẫn tuyệt đối).</summary>
public sealed record ClientSessionPhoto(
    Guid PhotoId,
    int Sequence,
    int Position,
    DateTimeOffset CapturedAt,
    int WidthPixels,
    int HeightPixels,
    bool Mirrored,
    bool IsSelectedInCurrentSlots,
    bool IsFileAvailable);

/// <summary>Metadata phiên bản thành phẩm trong chi tiết phiên (chỉ đọc, không chứa đường dẫn tuyệt đối).</summary>
public sealed record ClientSessionOutputVersion(
    int Version,
    int WidthPixels,
    int HeightPixels,
    int Dpi,
    ColorFilter Filter,
    DateTimeOffset CreatedAt,
    bool IsFileAvailable,
    ShareLink? Share);

/// <summary>Bản ghi xuất file ở dạng chỉ đọc cho chế độ khách (không chứa đường dẫn ổ đĩa).</summary>
public sealed record ClientSessionExport(
    ExportFormat Format,
    DateTimeOffset ExportedAt,
    int? Version);

/// <summary>Bản ghi lịch sử in ở dạng chỉ đọc cho chế độ khách (không chứa đường dẫn ổ đĩa).</summary>
public sealed record ClientSessionPrintJob(
    int OutputVersion,
    int Copies,
    string PrinterName,
    bool IsPhysicalPrinter,
    PrintJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage);

/// <summary>Chi tiết một phiên trong sự kiện hiện tại (chỉ đọc, không chứa đường dẫn hệ thống).</summary>
public sealed record ClientSessionDetail(
    string SessionKey,
    Guid SessionId,
    string EventKey,
    string EventName,
    DateTimeOffset CreatedAt,
    PhotoSessionState State,
    string? TemplateName,
    int RequiredPhotoCount,
    int CapturedPhotoCount,
    ColorFilter Filter,
    IReadOnlyList<ClientSessionOutputVersion> Versions,
    IReadOnlyList<ClientSessionPhoto> Photos,
    IReadOnlyList<ClientSessionExport> Exports,
    IReadOnlyList<ClientSessionPrintJob> PrintJobs,
    string? Notice);

/// <summary>
/// Thư viện ảnh CHỈ ĐỌC cho chế độ khách (Client Mode), giới hạn chặt trong sự kiện đang phục vụ.
/// Không nhận mã sự kiện hay đường dẫn tùy ý từ giao diện, không mở khóa Admin và không tác động đến
/// phiên đang hoạt động trong <see cref="PhotoSessionService"/>.
/// </summary>
public sealed class ClientEventGalleryService
{
    private readonly ISessionArchive _archive;
    private readonly IImagePreviewRenderer _previews;

    public ClientEventGalleryService(ISessionArchive archive, IImagePreviewRenderer previews)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(previews);
        _archive = archive;
        _previews = previews;
    }

    /// <summary>Sự kiện đang phục vụ (null nếu chưa chọn hoặc không truy cập được).</summary>
    public ClientEventInfo? GetCurrentEvent()
    {
        var current = TryGetCurrentKnownEvent();
        return current is null ? null : new ClientEventInfo(current.EventKey, current.Name);
    }

    /// <summary>Liệt kê toàn bộ các phiên thuộc sự kiện hiện tại (mới nhất trước, không tải ảnh gốc).</summary>
    /// <exception cref="EventWorkspaceUnavailableException">Chưa có sự kiện đang phục vụ hoặc sự kiện đổi trong lúc đọc.</exception>
    public async Task<ClientGalleryListResult> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var current = RequireCurrentKnownEvent();
        var eventKey = current.EventKey;

        var raw = await _archive.ListSessionsAsync(eventKey, cancellationToken).ConfigureAwait(false);
        EnsureStillCurrent(eventKey);

        var items = raw
            .Where(s => IsSafeSessionKey(s.SessionKey)
                && (s.EventKey is null || string.Equals(s.EventKey, eventKey, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s =>
            {
                var latest = s.LatestVersion;
                var hasThumbnail = latest is not null
                    && ((latest.ThumbnailPath is { } thumb && File.Exists(thumb))
                        || (!string.IsNullOrWhiteSpace(latest.FilePath) && File.Exists(latest.FilePath)));
                return new ClientSessionSummary(
                    s.SessionKey,
                    s.SessionId,
                    eventKey,
                    current.Name,
                    s.CreatedAt,
                    s.TemplateName,
                    s.PhotoCount,
                    latest?.Version,
                    s.VersionCount,
                    s.PrintJobCount,
                    hasThumbnail,
                    s.Problem);
            })
            .ToArray();

        return new ClientGalleryListResult(new ClientEventInfo(eventKey, current.Name), items);
    }

    /// <summary>Đọc chi tiết chỉ-xem của một phiên thuộc sự kiện hiện tại (bao gồm mọi ảnh gốc kể cả ảnh chụp lại và các phiên bản thành phẩm).</summary>
    /// <exception cref="DirectoryNotFoundException">Mã phiên không hợp lệ hoặc không thuộc sự kiện hiện tại.</exception>
    /// <exception cref="EventWorkspaceUnavailableException">Chưa có sự kiện đang phục vụ hoặc sự kiện đổi trong lúc đọc.</exception>
    public async Task<ClientSessionDetail> GetSessionDetailAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        if (!IsSafeSessionKey(sessionKey))
        {
            throw new DirectoryNotFoundException("Mã phiên không hợp lệ.");
        }

        var current = RequireCurrentKnownEvent();
        var eventKey = current.EventKey;

        var archived = await _archive.LoadSessionAsync(sessionKey, eventKey, cancellationToken).ConfigureAwait(false);
        EnsureStillCurrent(eventKey);
        if (!BelongsToEvent(archived, eventKey, sessionKey))
        {
            throw new DirectoryNotFoundException("Không tìm thấy phiên trong sự kiện hiện tại.");
        }

        var session = archived.Session;
        var templateName = session.Template?.Name;
        if (string.IsNullOrWhiteSpace(templateName))
        {
            // Fallback tên khung từ danh sách phiên nếu bản lưu template.json bị thiếu hoặc hỏng.
            var summaries = await _archive.ListSessionsAsync(eventKey, cancellationToken).ConfigureAwait(false);
            EnsureStillCurrent(eventKey);
            templateName = summaries
                .FirstOrDefault(s => string.Equals(s.SessionKey, sessionKey, StringComparison.OrdinalIgnoreCase))
                ?.TemplateName;
        }

        var selectedIds = session.Slots
            .Where(p => p is not null)
            .Select(p => p!.PhotoId)
            .ToHashSet();

        var photos = session.Photos
            .OrderBy(p => p.Sequence)
            .Select(p => new ClientSessionPhoto(
                p.PhotoId,
                p.Sequence,
                p.Position,
                p.CapturedAt,
                p.WidthPixels,
                p.HeightPixels,
                p.Mirrored,
                selectedIds.Contains(p.PhotoId),
                !string.IsNullOrWhiteSpace(p.OriginalFilePath) && File.Exists(p.OriginalFilePath)))
            .ToArray();

        var versions = session.Versions
            .OrderBy(v => v.Version)
            .Select(v => new ClientSessionOutputVersion(
                v.Version,
                v.WidthPixels,
                v.HeightPixels,
                v.Dpi,
                v.Filter,
                v.CreatedAt,
                !string.IsNullOrWhiteSpace(v.FilePath) && File.Exists(v.FilePath),
                v.Share))
            .ToArray();

        var exports = session.Exports
            .Select(e => new ClientSessionExport(e.Format, e.ExportedAt, e.Version))
            .ToArray();

        var printJobs = session.PrintJobs
            .Select(j => new ClientSessionPrintJob(
                j.OutputVersion,
                j.Copies,
                j.PrinterName,
                j.IsPhysicalPrinter,
                j.Status,
                j.CreatedAt,
                j.UpdatedAt,
                j.ErrorMessage))
            .ToArray();

        var notice = BuildNotice(versions, photos);

        return new ClientSessionDetail(
            archived.SessionKey,
            session.SessionId,
            eventKey,
            current.Name,
            session.CreatedAt,
            archived.PersistedState ?? session.State,
            templateName,
            session.RequiredPhotoCount,
            photos.Length,
            session.Filter,
            versions,
            photos,
            exports,
            printJobs,
            notice);
    }

    /// <summary>Ảnh thu nhỏ của phiên bản thành phẩm mới nhất thuộc sự kiện hiện tại (xác minh lại qua archive, không dùng đường dẫn do caller truyền).</summary>
    public Task<byte[]?> GetSessionThumbnailAsync(ClientSessionSummary summary, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);
        if (!IsStillCurrent(summary.EventKey))
        {
            return Task.FromResult<byte[]?>(null);
        }

        return GetSessionThumbnailAsync(summary.SessionKey, maxWidthPixels, cancellationToken);
    }

    /// <summary>Ảnh thu nhỏ của phiên bản thành phẩm mới nhất cho <paramref name="sessionKey"/> trong sự kiện hiện tại.</summary>
    public async Task<byte[]?> GetSessionThumbnailAsync(string sessionKey, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);
        if (!IsSafeSessionKey(sessionKey))
        {
            return null;
        }

        var current = TryGetCurrentKnownEvent();
        if (current is null)
        {
            return null;
        }

        var eventKey = current.EventKey;
        IReadOnlyList<ArchivedSessionSummary> summaries;
        try
        {
            summaries = await _archive.ListSessionsAsync(eventKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EventWorkspaceUnavailableException or DirectoryNotFoundException)
        {
            return null;
        }

        if (!IsStillCurrent(eventKey))
        {
            return null;
        }

        var summary = summaries.FirstOrDefault(s =>
            string.Equals(s.SessionKey, sessionKey, StringComparison.OrdinalIgnoreCase)
            && (s.EventKey is null || string.Equals(s.EventKey, eventKey, StringComparison.OrdinalIgnoreCase)));
        if (summary?.LatestVersion is not { } latest)
        {
            return null;
        }

        var path = latest.ThumbnailPath is { } thumb && File.Exists(thumb)
            ? thumb
            : latest.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var bytes = await _previews.RenderPreviewAsync(path, mirror: false, maxWidthPixels, cancellationToken).ConfigureAwait(false);
        return IsStillCurrent(eventKey) ? bytes : null;
    }

    /// <summary>Xem trước một phiên bản thành phẩm đã xác minh thuộc sự kiện và phiên hiện tại.</summary>
    public async Task<byte[]?> GetVersionPreviewAsync(string sessionKey, int version, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);
        if (version <= 0 || !IsSafeSessionKey(sessionKey))
        {
            return null;
        }

        var current = TryGetCurrentKnownEvent();
        if (current is null)
        {
            return null;
        }

        var eventKey = current.EventKey;
        var archived = await TryLoadInCurrentEventAsync(sessionKey, eventKey, cancellationToken).ConfigureAwait(false);
        if (archived is null)
        {
            return null;
        }

        var output = archived.Session.FindVersion(version);
        if (output is null || string.IsNullOrWhiteSpace(output.FilePath) || !File.Exists(output.FilePath))
        {
            return null;
        }

        var bytes = await _previews.RenderPreviewAsync(output.FilePath, mirror: false, maxWidthPixels, cancellationToken).ConfigureAwait(false);
        return IsStillCurrent(eventKey) ? bytes : null;
    }

    /// <summary>Xem trước một ảnh gốc theo <paramref name="photoId"/> trong phiên thuộc sự kiện hiện tại, tự lật gương theo <c>photo.Mirrored</c>.</summary>
    public async Task<byte[]?> GetOriginalPhotoPreviewAsync(string sessionKey, Guid photoId, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);
        if (photoId == Guid.Empty || !IsSafeSessionKey(sessionKey))
        {
            return null;
        }

        var current = TryGetCurrentKnownEvent();
        if (current is null)
        {
            return null;
        }

        var eventKey = current.EventKey;
        var archived = await TryLoadInCurrentEventAsync(sessionKey, eventKey, cancellationToken).ConfigureAwait(false);
        if (archived is null)
        {
            return null;
        }

        var photo = archived.Session.Photos.FirstOrDefault(p => p.PhotoId == photoId);
        if (photo is null || string.IsNullOrWhiteSpace(photo.OriginalFilePath) || !File.Exists(photo.OriginalFilePath))
        {
            return null;
        }

        var bytes = await _previews.RenderPreviewAsync(photo.OriginalFilePath, mirror: photo.Mirrored, maxWidthPixels, cancellationToken).ConfigureAwait(false);
        return IsStillCurrent(eventKey) ? bytes : null;
    }

    private async Task<ArchivedSession?> TryLoadInCurrentEventAsync(string sessionKey, string eventKey, CancellationToken cancellationToken)
    {
        ArchivedSession archived;
        try
        {
            archived = await _archive.LoadSessionAsync(sessionKey, eventKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidDataException or EventWorkspaceUnavailableException)
        {
            return null;
        }

        if (!IsStillCurrent(eventKey) || !BelongsToEvent(archived, eventKey, sessionKey))
        {
            return null;
        }

        return archived;
    }

    private KnownEvent? TryGetCurrentKnownEvent()
    {
        var current = _archive.ListKnownEvents().SingleOrDefault(e => e.IsCurrent);
        return current is { IsAvailable: true } && !string.IsNullOrWhiteSpace(current.EventKey) ? current : null;
    }

    private KnownEvent RequireCurrentKnownEvent()
    {
        var current = _archive.ListKnownEvents().SingleOrDefault(e => e.IsCurrent);
        if (current is null || !current.IsAvailable || string.IsNullOrWhiteSpace(current.EventKey))
        {
            throw new EventWorkspaceUnavailableException(current?.Problem ?? "Chưa có sự kiện đang phục vụ.");
        }

        return current;
    }

    private bool IsStillCurrent(string expectedEventKey)
    {
        var current = TryGetCurrentKnownEvent();
        return current is not null && string.Equals(current.EventKey, expectedEventKey, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureStillCurrent(string expectedEventKey)
    {
        if (!IsStillCurrent(expectedEventKey))
        {
            throw new EventWorkspaceUnavailableException("Sự kiện đang phục vụ đã thay đổi.");
        }
    }

    private static bool BelongsToEvent(ArchivedSession archived, string eventKey, string sessionKey)
    {
        if (!string.Equals(archived.SessionKey, sessionKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (archived.Session.EventId is { } eventId
            && Guid.TryParseExact(eventKey, "N", out var expectedEventId)
            && eventId != expectedEventId)
        {
            return false;
        }

        return true;
    }

    private static string? BuildNotice(
        IReadOnlyList<ClientSessionOutputVersion> versions,
        IReadOnlyList<ClientSessionPhoto> photos)
    {
        var missingVersions = versions.Count(v => !v.IsFileAvailable);
        var missingPhotos = photos.Count(p => !p.IsFileAvailable);
        if (versions.Count == 0 && missingPhotos == 0)
        {
            return photos.Count > 0
                ? "Phiên này chưa có ảnh thành phẩm; bạn vẫn có thể xem các ảnh gốc đã chụp."
                : "Phiên này chưa có ảnh nào.";
        }

        if (missingVersions > 0 && missingPhotos > 0)
        {
            return "Một số file ảnh thành phẩm và ảnh gốc hiện không có trên đĩa; các ảnh còn lại vẫn xem được.";
        }

        if (missingVersions > 0)
        {
            return "Một số phiên bản thành phẩm bị thiếu file trên đĩa; bạn vẫn có thể xem các phiên bản hoặc ảnh gốc còn lại.";
        }

        if (missingPhotos > 0)
        {
            return "Một số ảnh gốc bị thiếu file trên đĩa; các ảnh còn lại vẫn xem được.";
        }

        return null;
    }

    private static bool IsSafeSessionKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key is not ("." or "..")
        && key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && key.IndexOfAny(['/', '\\', ':']) < 0;
}
