namespace Photobooth.Application.Printing;

/// <summary>Trạng thái một yêu cầu in.</summary>
public enum PrintJobStatus
{
    /// <summary>Đã ghi nhận, đang/chưa gửi. Nếu còn ở trạng thái này sau khi ứng dụng khởi động lại → KHÔNG RÕ đã in hay chưa.</summary>
    Pending,

    /// <summary>Windows đã tiếp nhận lệnh in (chưa chắc máy đã in xong).</summary>
    Submitted,

    /// <summary>Máy in giả lập (Mock) đã ghi nhận — KHÔNG có bản in vật lý.</summary>
    Simulated,

    /// <summary>Quản trị viên xác nhận đã in xong thực tế.</summary>
    Completed,

    Failed,

    Cancelled,
}

/// <summary>
/// Một yêu cầu in N bản từ MỘT phiên bản thành phẩm đã lưu. Một phiên có thể có nhiều Print Job.
/// </summary>
/// <param name="OutputVersion">Phiên bản thành phẩm được in (final-v{N}).</param>
/// <param name="OutputFilePath">File thành phẩm đã in (đường dẫn tuyệt đối khi đang dùng).</param>
public sealed record PrintJob(
    Guid PrintJobId,
    Guid SessionId,
    int OutputVersion,
    string OutputFilePath,
    int Copies,
    string PrinterName,
    bool IsPhysicalPrinter,
    PrintJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null,
    string? ErrorMessage = null);

/// <summary>Dữ liệu gửi tới máy in: một file ảnh thành phẩm và số bản.</summary>
public sealed record PrintRequest(Guid PrintJobId, string ImageFilePath, int Copies, int WidthPixels, int HeightPixels, int Dpi);

/// <summary>Kết quả gửi lệnh in: chỉ <see cref="PrintJobStatus.Submitted"/>, <see cref="PrintJobStatus.Simulated"/> hoặc <see cref="PrintJobStatus.Failed"/>.</summary>
public sealed record PrintSubmission(PrintJobStatus Status, string? ErrorMessage = null);

/// <summary>Máy in (thật hoặc giả lập). Không được trả về Completed: việc in xong do người xác nhận.</summary>
public interface IPrinter
{
    string Name { get; }

    /// <summary>false với máy in giả lập — kết quả không phải bản in thật.</summary>
    bool IsPhysical { get; }

    Task<PrintSubmission> SubmitAsync(PrintRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Máy in đang được cấu hình; null = chưa cấu hình máy in nào.</summary>
public interface IPrinterProvider
{
    IPrinter? Current { get; }
}

/// <summary>Cấu hình in (appsettings.json của Desktop, mục "print").</summary>
public sealed record PrintOptions
{
    public const int DefaultMaxCopies = 10;

    /// <summary>Trần kỹ thuật cho cấu hình, tránh nhập nhầm số rất lớn.</summary>
    public const int AbsoluteMaxCopies = 50;

    public const int DefaultClientMaxCopies = 2;

    /// <summary>Số bản tối đa cho một lần in.</summary>
    public int MaxCopies { get; init; } = DefaultMaxCopies;

    /// <summary>
    /// Số bản tối đa cho một lần in do KHÁCH tự in lại từ Thư viện sự kiện (Client Mode). Không vượt <see cref="MaxCopies"/>.
    /// </summary>
    public int ClientMaxCopies { get; init; } = DefaultClientMaxCopies;

    public void Validate()
    {
        if (MaxCopies is < 1 or > AbsoluteMaxCopies)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCopies), MaxCopies, $"Số bản in tối đa phải trong khoảng 1..{AbsoluteMaxCopies}.");
        }

        if (ClientMaxCopies < 1 || ClientMaxCopies > MaxCopies)
        {
            throw new ArgumentOutOfRangeException(nameof(ClientMaxCopies), ClientMaxCopies, $"Số bản khách tự in tối đa phải trong khoảng 1..{MaxCopies} (print.maxCopies).");
        }
    }
}

public enum PrintOutcome
{
    /// <summary>Đã gửi tới máy in Windows (chưa phải đã in xong).</summary>
    Submitted,

    /// <summary>Máy in giả lập đã ghi nhận; không có bản in thật.</summary>
    Simulated,

    Failed,

    NotConfigured,

    InvalidCopies,

    /// <summary>Đang xử lý một yêu cầu in khác, hoặc vừa gửi yêu cầu giống hệt (bấm liên tục).</summary>
    Busy,

    NothingToPrint,

    /// <summary>Phiên bản này có lệnh in trước đó không rõ trạng thái/thất bại: cần người dùng xác nhận trước khi in lại.</summary>
    ConfirmationRequired,
}

public sealed record PrintResult(PrintOutcome Outcome, PrintJob? Job = null, string? ErrorMessage = null);

/// <summary>Vị trí vùng QR trên ảnh thành phẩm, theo tọa độ canvas của template (pixel).</summary>
public sealed record PrintQrPlacement(int X, int Y, int Size, int CanvasWidthPixels, int CanvasHeightPixels);

/// <summary>
/// Tạo bản in kèm mã QR: SAO CHÉP ảnh thành phẩm (không sửa file phiên bản) rồi vẽ QR của link chia sẻ vào vùng QR của template.
/// </summary>
public interface IPrintImageRenderer
{
    /// <summary>Ghi bản in hoàn chỉnh vào <paramref name="destinationPath"/> (ghi file tạm rồi đổi tên; đã có file thì giữ nguyên).</summary>
    /// <exception cref="IOException">Không đọc được ảnh thành phẩm hoặc không ghi được bản in.</exception>
    Task RenderWithQrAsync(string sourceImagePath, string destinationPath, PrintQrPlacement placement, string url, CancellationToken cancellationToken = default);
}
