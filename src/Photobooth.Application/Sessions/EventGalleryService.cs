using Photobooth.Application.Admin;
using Photobooth.Application.Composition;

namespace Photobooth.Application.Sessions;

/// <summary>
/// Thư viện ảnh thành phẩm, lọc theo sự kiện (mặc định sự kiện hiện tại). CHỈ dành cho chế độ quản trị:
/// khách (Kiosk) không được liệt kê hay mở phiên của người khác. Lọc chỉ để xem — không đổi sự kiện Kiosk đang lưu ảnh.
/// </summary>
public sealed class EventGalleryService
{
    private readonly ISessionArchive _archive;
    private readonly AdminAccessService _admin;
    private readonly PhotoSessionService _sessions;
    private readonly IImagePreviewRenderer _previews;

    public EventGalleryService(ISessionArchive archive, AdminAccessService admin, PhotoSessionService sessions, IImagePreviewRenderer previews)
    {
        _archive = archive;
        _admin = admin;
        _sessions = sessions;
        _previews = previews;
    }

    /// <summary>Ảnh thu nhỏ của phiên bản mới nhất (dùng file .thumb.jpg nếu có, không tải ảnh gốc).</summary>
    /// <returns>null nếu phiên chưa có thành phẩm hoặc file bị thiếu.</returns>
    public async Task<byte[]?> GetThumbnailAsync(ArchivedSessionSummary summary, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _admin.EnsureAdmin();
        var path = summary.LatestVersion is { } latest
            ? latest.ThumbnailPath is { } thumbnail && File.Exists(thumbnail) ? thumbnail : latest.FilePath
            : null;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return await _previews.RenderPreviewAsync(path, mirror: false, maxWidthPixels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Giá trị bộ lọc: xem phiên của mọi sự kiện truy cập được.</summary>
    public const string AllEvents = "*";

    /// <summary>Các sự kiện cho bộ lọc (đã từng mở trên máy này).</summary>
    /// <exception cref="UnauthorizedAccessException">Không ở chế độ quản trị.</exception>
    public IReadOnlyList<Events.KnownEvent> ListEvents()
    {
        _admin.EnsureAdmin();
        return _archive.ListKnownEvents();
    }

    /// <param name="eventFilter">null = sự kiện hiện tại; <see cref="AllEvents"/> = mọi sự kiện truy cập được; còn lại = mã sự kiện.</param>
    /// <exception cref="UnauthorizedAccessException">Không ở chế độ quản trị.</exception>
    public async Task<IReadOnlyList<ArchivedSessionSummary>> ListAsync(string? eventFilter = null, CancellationToken cancellationToken = default)
    {
        _admin.EnsureAdmin();
        if (eventFilter != AllEvents)
        {
            return await _archive.ListSessionsAsync(eventFilter, cancellationToken).ConfigureAwait(false);
        }

        var all = new List<ArchivedSessionSummary>();
        foreach (var known in _archive.ListKnownEvents().Where(e => e.IsAvailable))
        {
            try
            {
                all.AddRange(await _archive.ListSessionsAsync(known.EventKey, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Events.EventWorkspaceUnavailableException)
            {
                // Một sự kiện lỗi (ổ đĩa vừa rút...) không làm hỏng danh sách của các sự kiện khác.
            }
        }

        return all.OrderByDescending(s => s.CreatedAt).ToArray();
    }

    /// <summary>Mở một phiên đã lưu để xem/chỉnh sửa/in lại.</summary>
    /// <param name="eventKey">Sự kiện chứa phiên (null = sự kiện hiện tại).</param>
    /// <exception cref="UnauthorizedAccessException">Không ở chế độ quản trị.</exception>
    public async Task<ArchivedSession> OpenAsync(string sessionKey, string? eventKey = null, CancellationToken cancellationToken = default)
    {
        _admin.EnsureAdmin();
        var archived = await _archive.LoadSessionAsync(sessionKey, eventKey, cancellationToken).ConfigureAwait(false);
        _admin.EnsureAdmin();
        if (!_sessions.OpenArchivedSession(archived))
        {
            throw new InvalidOperationException("Đang có phiên chụp chưa kết thúc.");
        }

        return archived;
    }

    /// <summary>Đóng phiên đang xem (bỏ chỉnh sửa chưa lưu, không đổi dữ liệu đã lưu).</summary>
    public void Close()
    {
        if (_sessions.Current is { IsArchived: true })
        {
            _sessions.EndCurrentSession();
        }
    }
}
