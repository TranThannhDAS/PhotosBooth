namespace Photobooth.Infrastructure.Persistence;

// Bảng SQLite của Desktop (nguồn dữ liệu chính cho metadata nghiệp vụ). Chỉ lưu metadata và ĐƯỜNG DẪN:
// ảnh gốc, thành phẩm, ảnh thu nhỏ, snapshot template vẫn là file trong Event Workspace.
// Đường dẫn file trong phiên là TƯƠNG ĐỐI với thư mục sự kiện (dùng "/"), để di chuyển nguyên thư mục sự kiện được;
// khi đọc luôn được kiểm tra không vượt ra ngoài thư mục sự kiện.
// Các entity này là chi tiết lưu trữ: domain model (PhotoSession, OutputVersion...) không đổi.

/// <summary>Một sự kiện (Event Workspace) đã mở trên máy.</summary>
public sealed class EventRecord
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Thư mục sự kiện (tuyệt đối). null = thư mục đã được gán cho sự kiện khác (sự kiện này mất thư mục).</summary>
    public string? FolderPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Lần mở gần nhất; sự kiện mở gần nhất được mở lại khi khởi động.</summary>
    public DateTimeOffset LastOpenedAt { get; set; }

    public List<SessionRecord> Sessions { get; set; } = [];
}

/// <summary>Một phiên chụp, thuộc đúng một sự kiện.</summary>
public sealed class SessionRecord
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public EventRecord? Event { get; set; }

    /// <summary>Thư mục phiên, tương đối với thư mục sự kiện (ví dụ "Sessions/20261119-145223-4ae6062a").</summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>inProgress | completed | cancelled.</summary>
    public string Status { get; set; } = "inProgress";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? TemplateId { get; set; }

    public string? TemplateName { get; set; }

    /// <summary>Snapshot template.json của phiên (file, tương đối với thư mục sự kiện).</summary>
    public string? TemplateFile { get; set; }

    public bool Mirror { get; set; }

    public string Filter { get; set; } = "None";

    /// <summary>Ảnh đang chọn cho từng photoIndex (null = chưa chụp).</summary>
    public List<Guid?> SelectedPhotoIds { get; set; } = [];

    /// <summary>Manual Crop hiện tại theo chỉ số ô (JSON nhỏ, null = Auto Crop).</summary>
    public string SlotCrops { get; set; } = "[]";

    public List<PhotoRecord> Photos { get; set; } = [];

    public List<OutputVersionRecord> Versions { get; set; } = [];

    public List<ExportRecord> Exports { get; set; } = [];

    public List<PrintJobRecord> PrintJobs { get; set; } = [];
}

/// <summary>Ảnh gốc đã chụp (bất biến; kể cả ảnh bị thay khi chụp lại).</summary>
public sealed class PhotoRecord
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public int Sequence { get; set; }

    public int Position { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    public int WidthPixels { get; set; }

    public int HeightPixels { get; set; }

    /// <summary>File ảnh gốc, tương đối với thư mục sự kiện.</summary>
    public string FilePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public bool Mirrored { get; set; }
}

/// <summary>Một phiên bản ảnh thành phẩm (final-v{N}.png) — không bao giờ bị ghi đè hay xóa.</summary>
public sealed class OutputVersionRecord
{
    public int Id { get; set; }

    public Guid SessionId { get; set; }

    public int Version { get; set; }

    /// <summary>File thành phẩm, tương đối với thư mục sự kiện.</summary>
    public string FilePath { get; set; } = string.Empty;

    public string? ThumbnailPath { get; set; }

    public int WidthPixels { get; set; }

    public int HeightPixels { get; set; }

    public int Dpi { get; set; }

    public string TemplateId { get; set; } = string.Empty;

    public string Filter { get; set; } = "None";

    public List<Guid> PhotoIds { get; set; } = [];

    public string SlotCrops { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Share Token (duy nhất). Một khi đã ghi thì không bao giờ đổi hay xóa.</summary>
    public string? ShareToken { get; set; }

    public string? ShareUrl { get; set; }

    public DateTimeOffset? ShareCreatedAt { get; set; }

    public CloudUploadRecord? CloudUpload { get; set; }
}

/// <summary>
/// Trạng thái upload ảnh chia sẻ của một phiên bản (1-1). Tách bảng riêng vì do tiến trình upload nền ghi,
/// độc lập với lúc lưu phiên — hai bên không ghi đè cột của nhau.
/// </summary>
public sealed class CloudUploadRecord
{
    public int OutputVersionId { get; set; }

    public OutputVersionRecord? OutputVersion { get; set; }

    /// <summary>pending | uploading | uploaded | failed.</summary>
    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? UploadedAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>File đã xuất ra ngoài sự kiện (đường dẫn tuyệt đối, chỉ để tham khảo).</summary>
public sealed class ExportRecord
{
    public int Id { get; set; }

    public Guid SessionId { get; set; }

    public string Format { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public DateTimeOffset ExportedAt { get; set; }

    public int? Version { get; set; }
}

/// <summary>Lịch sử in.</summary>
public sealed class PrintJobRecord
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public int Version { get; set; }

    /// <summary>File đã in, tương đối với thư mục sự kiện.</summary>
    public string FilePath { get; set; } = string.Empty;

    public int Copies { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public bool IsPhysicalPrinter { get; set; }

    public string Status { get; set; } = "Pending";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? ErrorMessage { get; set; }
}
