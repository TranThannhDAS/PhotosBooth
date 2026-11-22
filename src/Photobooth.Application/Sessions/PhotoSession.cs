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
    DateTimeOffset ComposedAt,
    IReadOnlyList<SlotCrop?> SlotCrops);

public sealed record ExportedFile(ExportFormat Format, string FilePath, DateTimeOffset ExportedAt)
{
    /// <summary>Phiên bản thành phẩm đã xuất (null với dữ liệu cũ).</summary>
    public int? Version { get; init; }
}

/// <summary>
/// Ảnh chụp nhanh (bất biến) của phiên hiện tại. Mọi thay đổi do <see cref="PhotoSessionService"/> thực hiện.
/// </summary>
/// <param name="Photos">Lịch sử mọi ảnh gốc đã lưu (kể cả ảnh đã bị thay khi chụp lại).</param>
/// <param name="Slots">
/// Ảnh đang được chọn cho từng ảnh nguồn (<c>photoIndex</c>; null = chưa chụp). Số phần tử là số ảnh cần chụp,
/// được chốt khi chọn template và rỗng khi chưa chọn.
/// </param>
/// <param name="PendingPositions">Các vị trí sẽ được chụp ở lượt chụp kế tiếp, theo thứ tự.</param>
/// <param name="SlotCrops">
/// Manual Crop đã xác nhận theo chỉ số ô của template (null = Auto Crop). Số phần tử = số ô, chốt khi chọn template.
/// Là dữ liệu phiên: không ghi vào template.json, không sửa ảnh gốc.
/// </param>
/// <param name="Filter">Bộ lọc màu khách đã chọn, dùng lại khi ghép lại (sau chụp lại/chỉnh vùng cắt).</param>
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
    IReadOnlyList<ExportedFile> Exports,
    IReadOnlyList<SlotCrop?> SlotCrops,
    ColorFilter Filter)
{
    public bool IsEnded => State is PhotoSessionState.Completed or PhotoSessionState.Cancelled;

    /// <summary>Số ảnh nguồn cần chụp của phiên (snapshot từ template lúc chọn; 0 khi chưa chọn).</summary>
    public int RequiredPhotoCount => Slots.Count;

    public int CapturedCount => Slots.Count(p => p is not null);

    public bool AllSlotsFilled => Slots.Count > 0 && Slots.All(p => p is not null);

    /// <summary>Đang ở lượt chụp lại: các vị trí chờ chụp đều đã có ảnh cũ.</summary>
    public bool IsRetake => PendingPositions.Count > 0 && PendingPositions.All(p => Slots[p] is not null);

    // ------------------------------------------------------------ Lưu trữ theo Event Workspace

    /// <summary>Thư mục phiên trong Event Workspace (chốt khi tạo phiên; đổi sự kiện không ảnh hưởng phiên này).</summary>
    public string? Directory { get; init; }

    public Guid? EventId { get; init; }

    /// <summary>Các phiên bản thành phẩm đã xác nhận, tăng dần. Không phiên bản nào bị ghi đè.</summary>
    public IReadOnlyList<OutputVersion> Versions { get; init; } = [];

    public IReadOnlyList<Printing.PrintJob> PrintJobs { get; init; } = [];

    /// <summary>Phiên đã lưu được quản trị viên mở lại (không phải phiên Kiosk đang chụp).</summary>
    public bool IsArchived { get; init; }

    /// <summary>Còn đủ dữ liệu nguồn (template + ảnh gốc) để chỉnh sửa và ghép lại.</summary>
    public bool CanEdit { get; init; } = true;

    /// <summary>Phiên bản đang làm gốc khi chỉnh sửa lại; "Hủy" quay về đúng phiên bản này.</summary>
    public int? EditBaseVersion { get; init; }

    /// <summary>Lần ghi session.json gần nhất thất bại (ổ đĩa đầy, mất quyền...).</summary>
    public bool PersistenceFailed { get; init; }

    public OutputVersion? LatestVersion => Versions.Count == 0 ? null : Versions[^1];

    public OutputVersion? FindVersion(int version) => Versions.FirstOrDefault(v => v.Version == version);

    /// <summary>Thành phẩm đang hiển thị khác phiên bản gốc → có chỉnh sửa chưa lưu.</summary>
    public bool HasUnsavedEdits =>
        IsArchived && (Composed is null || EditBaseVersion is not { } baseVersion || FindVersion(baseVersion) is not { } v || !v.Matches(Composed));

    /// <summary>Phiên mới chưa có template, nên chưa xác định số ảnh.</summary>
    public static PhotoSession Create(Guid sessionId, DateTimeOffset createdAt, bool mirror) =>
        new(sessionId, createdAt, PhotoSessionState.Created, null, [], [], [], mirror, null, false, [], [], ColorFilter.None);
}
