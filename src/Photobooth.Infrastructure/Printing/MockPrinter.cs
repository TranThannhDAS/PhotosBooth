using Microsoft.Extensions.Logging;
using Photobooth.Application.Printing;

namespace Photobooth.Infrastructure.Printing;

/// <summary>
/// Máy in giả lập để thử nghiệp vụ khi chưa có phần cứng. Kiểm tra file đầu vào và số bản rồi trả về
/// <see cref="PrintJobStatus.Simulated"/> — KHÔNG bao giờ báo đã gửi/in thật.
/// </summary>
public sealed class MockPrinter : IPrinter
{
    private readonly ILogger<MockPrinter> _logger;

    public MockPrinter(ILogger<MockPrinter> logger)
    {
        _logger = logger;
    }

    public string Name => "Máy in thử nghiệm (giả lập)";

    public bool IsPhysical => false;

    public Task<PrintSubmission> SubmitAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Copies < 1)
        {
            return Task.FromResult(new PrintSubmission(PrintJobStatus.Failed, "Số bản in không hợp lệ."));
        }

        if (!File.Exists(request.ImageFilePath))
        {
            return Task.FromResult(new PrintSubmission(PrintJobStatus.Failed, "Không tìm thấy file ảnh thành phẩm."));
        }

        _logger.LogInformation("Máy in giả lập ghi nhận lệnh {PrintJobId}: {Copies} bản (không in thật)", request.PrintJobId, request.Copies);
        return Task.FromResult(new PrintSubmission(PrintJobStatus.Simulated));
    }
}
