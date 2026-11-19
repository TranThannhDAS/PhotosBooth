using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Templates;

namespace Photobooth.Application.Sessions;

/// <summary>Metadata của một ảnh gốc đã được lưu thành công.</summary>
/// <param name="Sequence">Thứ tự lần chụp trong phiên (tăng dần, kể cả ảnh chụp lại).</param>
/// <param name="Position">Vị trí ảnh (PhotoIndex của template) mà lần chụp này dành cho.</param>
/// <param name="Mirrored">
/// Preview đang lật gương khi chụp. File gốc luôn là khung hình thô của camera;
/// cờ này cho biết ảnh hiển thị/thành phẩm phải được lật để giống preview.
/// </param>
public sealed record CapturedPhoto(
    Guid PhotoId,
    Guid SessionId,
    int Sequence,
    int Position,
    DateTimeOffset CapturedAt,
    int WidthPixels,
    int HeightPixels,
    string OriginalFilePath,
    long SizeBytes,
    bool Mirrored);

/// <summary>Ảnh thành phẩm đã ghép; truy nguyên được template, bộ lọc và các ảnh đã dùng.</summary>
public sealed record ComposedResult(
    string FilePath,
    int WidthPixels,
    int HeightPixels,
    string TemplateId,
    ColorFilter Filter,
    IReadOnlyList<Guid> PhotoIds,
    DateTimeOffset ComposedAt);

public sealed record ExportedFile(ExportFormat Format, string FilePath, DateTimeOffset ExportedAt);

/// <summary>
/// Ảnh chụp nhanh (bất biến) của phiên hiện tại. Mọi thay đổi do <see cref="PhotoSessionService"/> thực hiện.
/// </summary>
/// <param name="Photos">Lịch sử mọi ảnh gốc đã lưu (kể cả ảnh đã bị thay khi chụp lại).</param>
/// <param name="Slots">Ảnh đang được chọn cho từng vị trí (null = chưa chụp).</param>
/// <param name="PendingPositions">Các vị trí sẽ được chụp ở lượt chụp kế tiếp, theo thứ tự.</param>
public sealed record PhotoSession(
    Guid SessionId,
    DateTimeOffset CreatedAt,
    PhotoSessionState State,
    PhotoTemplate? Template,
    IReadOnlyList<CapturedPhoto> Photos,
    IReadOnlyList<CapturedPhoto?> Slots,
    IReadOnlyList<int> PendingPositions,
    bool Mirror,
    ComposedResult? Composed,
    bool LastCompositionFailed,
    IReadOnlyList<ExportedFile> Exports)
{
    public bool IsEnded => State is PhotoSessionState.Completed or PhotoSessionState.Cancelled;

    public int RequiredPhotoCount => Slots.Count;

    public int CapturedCount => Slots.Count(p => p is not null);

    public bool AllSlotsFilled => Slots.Count > 0 && Slots.All(p => p is not null);

    /// <summary>Đang ở lượt chụp lại: các vị trí chờ chụp đều đã có ảnh cũ.</summary>
    public bool IsRetake => PendingPositions.Count > 0 && PendingPositions.All(p => Slots[p] is not null);

    public static PhotoSession Create(Guid sessionId, DateTimeOffset createdAt, int photoCount, bool mirror) =>
        new(sessionId, createdAt, PhotoSessionState.Created, null, [], new CapturedPhoto?[photoCount], [], mirror, null, false, []);
}
