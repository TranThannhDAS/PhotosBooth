using Photobooth.Application.Composition;

namespace Photobooth.Application.Sessions;

/// <summary>
/// Một phiên bản ảnh thành phẩm đã xác nhận (<c>Outputs/final-v{N}.png</c>). Không bao giờ bị ghi đè;
/// chỉnh sửa lại luôn tạo phiên bản mới.
/// </summary>
/// <param name="SlotCrops">Manual Crop đã dùng — trạng thái chỉnh sửa được khôi phục khi mở lại.</param>
public sealed record OutputVersion(
    int Version,
    string FilePath,
    int WidthPixels,
    int HeightPixels,
    int Dpi,
    string TemplateId,
    ColorFilter Filter,
    IReadOnlyList<Guid> PhotoIds,
    IReadOnlyList<SlotCrop?> SlotCrops,
    DateTimeOffset CreatedAt)
{
    /// <summary>Ảnh thu nhỏ cho Gallery (có thể không có).</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>
    /// Link chia sẻ QR riêng của phiên bản này (null = chưa chia sẻ). Được lưu bền vững TRƯỚC khi hiển thị QR
    /// và không bao giờ đổi; phiên bản mới (chỉnh sửa lại) có token mới, phiên bản đã chia sẻ không bị ghi đè.
    /// </summary>
    public Sharing.ShareLink? Share { get; init; }

    /// <summary>Thành phẩm này được tạo từ đúng các đầu vào hiện tại (không cần phiên bản mới).</summary>
    public bool Matches(ComposedResult composed) =>
        composed.TemplateId == TemplateId
        && composed.Filter == Filter
        && composed.PhotoIds.SequenceEqual(PhotoIds)
        && composed.SlotCrops.SequenceEqual(SlotCrops);
}

/// <summary>Nơi lưu một phiên chụp trong Event Workspace.</summary>
public sealed record SessionLocation(string Directory, Guid EventId);

/// <summary>Tóm tắt một phiên đã lưu cho Gallery (không tải ảnh).</summary>
/// <param name="SessionKey">Tên thư mục phiên trong Event Workspace (định danh khi mở lại).</param>
/// <param name="Problem">Mô tả lỗi dữ liệu nếu có (file thiếu, chưa có thành phẩm...).</param>
public sealed record ArchivedSessionSummary(
    string SessionKey,
    Guid SessionId,
    DateTimeOffset CreatedAt,
    string? TemplateName,
    int PhotoCount,
    OutputVersion? LatestVersion,
    int VersionCount,
    int PrintJobCount,
    string? Problem)
{
    /// <summary>Mã sự kiện chứa phiên (dùng khi mở phiên từ bộ lọc "Tất cả sự kiện").</summary>
    public string? EventKey { get; init; }

    public string? EventName { get; init; }
}

/// <summary>Phiên đã lưu được mở lại. <see cref="PhotoSession.CanEdit"/> cho biết còn đủ dữ liệu nguồn để chỉnh sửa.</summary>
public sealed record ArchivedSession(string SessionKey, PhotoSession Session, string? EditUnavailableReason);

/// <summary>
/// Lưu trữ phiên chụp lâu dài: file trong Event Workspace hiện tại (snapshot template,
/// ảnh gốc, phiên bản thành phẩm) và metadata (phiên, ảnh, phiên bản, xuất, in, chia sẻ) trong database của Infrastructure.
/// Đường dẫn trong metadata là tương đối với thư mục sự kiện.
/// </summary>
public interface ISessionArchive
{
    /// <summary>Cấp thư mục duy nhất cho phiên mới trong sự kiện hiện tại.</summary>
    /// <exception cref="Events.EventWorkspaceUnavailableException">Chưa có/không truy cập được Event Workspace.</exception>
    SessionLocation CreateSessionLocation(Guid sessionId, DateTimeOffset createdAt);

    /// <summary>Ghi metadata phiên (một transaction) và snapshot template (file, lần đầu).</summary>
    Task SaveAsync(PhotoSession session, CancellationToken cancellationToken = default);

    /// <summary>Sao chép thành phẩm nháp thành phiên bản <paramref name="version"/> (không ghi đè) kèm ảnh thu nhỏ.</summary>
    Task<OutputVersion> CreateOutputVersionAsync(PhotoSession session, ComposedResult composed, int version, CancellationToken cancellationToken = default);

    /// <summary>Xóa các file nháp (bản ghép tạm khi đang chỉnh) của phiên; không đụng tới ảnh gốc/phiên bản đã lưu.</summary>
    Task DeleteDraftsAsync(PhotoSession session, CancellationToken cancellationToken = default);

    /// <summary>Các sự kiện đã mở trên máy này (sự kiện hiện tại trước, rồi mới mở gần nhất).</summary>
    IReadOnlyList<Events.KnownEvent> ListKnownEvents();

    /// <summary>
    /// Danh sách phiên của một sự kiện (<paramref name="eventKey"/> = null: sự kiện hiện tại), mới nhất trước.
    /// Chỉ chấp nhận sự kiện đã từng mở trên máy (tra theo mã, không nhận đường dẫn).
    /// </summary>
    Task<IReadOnlyList<ArchivedSessionSummary>> ListSessionsAsync(string? eventKey = null, CancellationToken cancellationToken = default);

    /// <exception cref="InvalidDataException">Metadata phiên không hợp lệ (đường dẫn ngoài thư mục sự kiện...).</exception>
    /// <exception cref="DirectoryNotFoundException">Không có phiên này trong sự kiện.</exception>
    Task<ArchivedSession> LoadSessionAsync(string sessionKey, string? eventKey = null, CancellationToken cancellationToken = default);
}
