using System.Globalization;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Export;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Storage;
using SkiaSharp;

namespace Photobooth.Infrastructure.Export;

/// <summary>
/// Xuất ảnh thành phẩm (PNG) ra JPEG/PNG/PDF. Mọi định dạng sinh ra từ cùng một ảnh thành phẩm;
/// PDF có kích thước trang vật lý theo canvas + DPI của template (1200×1800 px @300 DPI = 288×432 pt).
/// Không ghi đè: nếu trùng tên sẽ thêm hậu tố _2, _3...
/// </summary>
public sealed class ComposedPhotoExporter : IComposedPhotoExporter
{
    internal const int MaxNameAttempts = 100;

    // Mã lỗi Win32 (phần thấp của HResult) dùng để phân loại IOException.
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;

    private readonly ExportOptions _options;
    private readonly ILogger<ComposedPhotoExporter> _logger;

    public ComposedPhotoExporter(ExportOptions options, ILogger<ComposedPhotoExporter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = logger;
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsValidDirectory(request.DestinationDirectory) || !IsValidBaseName(request.BaseFileName))
        {
            return ExportResult.Failure(ExportError.InvalidPath);
        }

        if (!Directory.Exists(request.DestinationDirectory))
        {
            return ExportResult.Failure(ExportError.DirectoryNotFound);
        }

        if (!File.Exists(request.ComposedFilePath))
        {
            return ExportResult.Failure(ExportError.SourceMissing);
        }

        byte[] content;
        try
        {
            content = await Task.Run(() => Encode(request), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExportResult.Failure(ExportError.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Không mã hóa được ảnh thành phẩm sang {Format}", request.Format);
            return ExportResult.Failure(ExportError.EncodingFailed);
        }

        var extension = ExportFormats.GetExtension(request.Format);
        var directory = Path.GetFullPath(request.DestinationDirectory);
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var fileName = attempt == 1
                ? request.BaseFileName + extension
                : string.Create(CultureInfo.InvariantCulture, $"{request.BaseFileName}_{attempt}{extension}");
            var destination = Path.Combine(directory, fileName);
            if (File.Exists(destination))
            {
                continue;
            }

            try
            {
                await SafeFileWriter.WriteAsync(
                        destination,
                        (stream, token) => stream.WriteAsync(content, token).AsTask(),
                        overwrite: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                return ExportResult.Success(destination);
            }
            catch (OperationCanceledException)
            {
                return ExportResult.Failure(ExportError.Cancelled);
            }
            catch (IOException ex) when (ClassifyIoError(ex) == ExportError.AlreadyExists)
            {
                // File vừa được tạo bởi tiến trình khác giữa lúc kiểm tra và ghi: thử tên kế tiếp.
                continue;
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogWarning(ex, "Thư mục đích không tồn tại khi xuất");
                return ExportResult.Failure(ExportError.DirectoryNotFound);
            }
            catch (PathTooLongException ex)
            {
                _logger.LogWarning(ex, "Đường dẫn đích quá dài");
                return ExportResult.Failure(ExportError.InvalidPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Không có quyền ghi vào vị trí đích");
                return ExportResult.Failure(ExportError.AccessDenied);
            }
            catch (IOException ex)
            {
                var error = ClassifyIoError(ex);
                _logger.LogWarning(ex, "Lỗi I/O khi xuất {Format}: {ExportError}", request.Format, error);
                return ExportResult.Failure(error);
            }
        }

        return ExportResult.Failure(ExportError.AlreadyExists);
    }

    internal static ExportError ClassifyIoError(IOException exception) =>
        (exception.HResult & 0xFFFF) switch
        {
            ErrorDiskFull or ErrorHandleDiskFull => ExportError.DiskFull,
            ErrorFileExists or ErrorAlreadyExists => ExportError.AlreadyExists,
            _ => ExportError.IoError,
        };

    private byte[] Encode(ExportRequest request)
    {
        if (request.Format == ExportFormat.Png)
        {
            // Ảnh thành phẩm đã là PNG: sao chép nguyên vẹn, không mã hóa lại.
            return File.ReadAllBytes(request.ComposedFilePath);
        }

        var jpeg = EncodeJpeg(request.ComposedFilePath);
        return request.Format == ExportFormat.Pdf ? CreatePdf(jpeg, request.Canvas) : jpeg;
    }

    private byte[] EncodeJpeg(string composedFilePath)
    {
        using var bitmap = SKBitmap.Decode(composedFilePath)
            ?? throw new IOException("Ảnh thành phẩm bị hỏng hoặc không đúng định dạng.");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, _options.JpegQuality)
            ?? throw new InvalidOperationException("Không mã hóa được JPEG.");
        return data.ToArray();
    }

    /// <summary>PDF một trang, kích thước trang = kích thước vật lý của canvas; ảnh phủ kín trang.</summary>
    private static byte[] CreatePdf(byte[] jpeg, TemplateCanvas canvas)
    {
        var pageWidth = (float)canvas.PageWidthPoints;
        var pageHeight = (float)canvas.PageHeightPoints;

        using var image = SKImage.FromEncodedData(jpeg)
            ?? throw new InvalidOperationException("Không đọc được ảnh JPEG để tạo PDF.");
        using var output = new MemoryStream();

        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = "Photobooth";
        metadata.Creator = "Photobooth Mini";
        metadata.Producer = "Photobooth Mini";
        metadata.RasterDpi = canvas.Dpi;

        using (var document = SKDocument.CreatePdf(output, metadata)
            ?? throw new InvalidOperationException("Không tạo được tài liệu PDF."))
        {
            var page = document.BeginPage(pageWidth, pageHeight);
            page.DrawImage(image, SKRect.Create(pageWidth, pageHeight), new SKSamplingOptions(SKFilterMode.Linear));
            document.EndPage();
            document.Close();
        }

        return output.ToArray();
    }

    private static bool IsValidDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            return false;
        }

        try
        {
            _ = Path.GetFullPath(directory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsValidBaseName(string? baseName) =>
        !string.IsNullOrWhiteSpace(baseName)
        && baseName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && baseName != "."
        && baseName != "..";
}
