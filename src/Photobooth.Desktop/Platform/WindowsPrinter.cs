using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Printing;
using PrintJobStatus = Photobooth.Application.Printing.PrintJobStatus;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// In qua máy in Windows đã cài (XPS print path của WPF), không dùng SDK hãng.
/// Thành công chỉ có nghĩa là <b>Windows đã tiếp nhận lệnh in</b> vào hàng đợi — không phải máy đã in xong.
/// </summary>
public sealed class WindowsPrinter : IPrinter
{
    private readonly string _queueName;
    private readonly ILogger _logger;

    public WindowsPrinter(string queueName, ILogger logger)
    {
        _queueName = queueName;
        _logger = logger;
    }

    public string Name => _queueName;

    public bool IsPhysical => true;

    public async Task<PrintSubmission> SubmitAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.ImageFilePath))
        {
            return new PrintSubmission(PrintJobStatus.Failed, "Không tìm thấy file ảnh thành phẩm.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // WPF printing cần luồng STA: chạy trên Dispatcher của ứng dụng.
        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Print(request));
    }

    private PrintSubmission Print(PrintRequest request)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server) ?? throw new InvalidOperationException("Không tìm thấy máy in đã chọn.");
            queue.Refresh();
            if (queue.IsOffline || queue.IsInError || queue.IsPaperJammed || queue.IsOutOfPaper || queue.IsDoorOpened || queue.IsNotAvailable)
            {
                return new PrintSubmission(PrintJobStatus.Failed, "Máy in đang ngoại tuyến hoặc báo lỗi (hết giấy, kẹt giấy...).");
            }

            var landscape = request.WidthPixels > request.HeightPixels;
            var ticket = (queue.UserPrintTicket ?? queue.DefaultPrintTicket).Clone();
            ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            var capabilities = queue.GetPrintCapabilities(ticket);

            // Driver hỗ trợ đủ số bản → để driver nhân bản; ngược lại gửi N trang (tránh in thiếu/thừa).
            var driverCopies = capabilities.MaxCopyCount is { } max && max >= request.Copies;
            ticket.CopyCount = driverCopies ? request.Copies : 1;
            var pageCount = driverCopies ? 1 : request.Copies;

            // Kích thước trang (đơn vị 1/96 inch); mặc định theo kích thước ảnh ở DPI thực.
            var pageWidth = ticket.PageMediaSize?.Width ?? request.WidthPixels * 96.0 / Math.Max(1, request.Dpi);
            var pageHeight = ticket.PageMediaSize?.Height ?? request.HeightPixels * 96.0 / Math.Max(1, request.Dpi);
            if (landscape != (pageWidth > pageHeight))
            {
                (pageWidth, pageHeight) = (pageHeight, pageWidth);
            }

            var area = capabilities.PageImageableArea;
            var left = area?.OriginWidth ?? 0;
            var top = area?.OriginHeight ?? 0;
            var width = Math.Min(area?.ExtentWidth ?? pageWidth, pageWidth - left);
            var height = Math.Min(area?.ExtentHeight ?? pageHeight, pageHeight - top);
            if (landscape && area is not null && area.ExtentWidth < area.ExtentHeight)
            {
                (left, top, width, height) = (top, left, Math.Min(area.ExtentHeight, pageWidth - top), Math.Min(area.ExtentWidth, pageHeight - left));
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(request.ImageFilePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var document = new FixedDocument();
            document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
            for (var i = 0; i < pageCount; i++)
            {
                var page = new FixedPage { Width = pageWidth, Height = pageHeight };
                var image = new Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.Uniform, Width = width, Height = height };
                FixedPage.SetLeft(image, left);
                FixedPage.SetTop(image, top);
                page.Children.Add(image);
                document.Pages.Add(new PageContent { Child = page });
            }

            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(document, ticket);
            _logger.LogInformation("Windows đã tiếp nhận lệnh in {PrintJobId} ({Copies} bản)", request.PrintJobId, request.Copies);
            return new PrintSubmission(PrintJobStatus.Submitted);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogError(ex, "Gửi lệnh in tới máy in Windows thất bại");
            return new PrintSubmission(PrintJobStatus.Failed, "Không gửi được lệnh in tới máy in Windows.");
        }
    }

    private PrintQueue? FindQueue(LocalPrintServer server) =>
        server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .FirstOrDefault(q => string.Equals(q.FullName, _queueName, StringComparison.OrdinalIgnoreCase));
}
