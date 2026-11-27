using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Printing;

/// <summary>
/// In kèm mã QR theo vùng QR của template: mỗi phiên bản một QR riêng, tạo ở lần in đầu (nếu chưa có), in lại dùng đúng QR đó;
/// file thành phẩm không chứa QR; khách in lại từ Thư viện sự kiện (giới hạn số bản).
/// </summary>
public sealed class QrPrintTests : IDisposable
{
    private const int QrX = 600;
    private const int QrY = 600;
    private const int QrSize = 180;
    private static readonly SKColor Background = new(0xFF, 0xCC, 0x00);

    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly EventWorkspaceService _workspaces;
    private readonly FakeCameraDevice _camera = new();
    private readonly RecordingPrinter _printer = new(isPhysical: true);
    private readonly TestPrinterProvider _printers;
    private readonly RecordingUploadQueue _uploads = new();

    public QrPrintTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "Sự kiện QR"));
        _printers = new TestPrinterProvider { Current = _printer };
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    private static PhotoTemplate QrTemplate() => TestTemplates.WithPhotoIndexes(0) with
    {
        Id = "test-qr",
        Canvas = new TemplateCanvas { WidthPixels = 800, HeightPixels = 800, Dpi = 100, BackgroundColor = "#FFCC00" },
        Decorations = [new TemplateDecoration { Id = "qr", Type = DecorationKind.QrCode, X = QrX, Y = QrY, Width = QrSize, Height = QrSize }],
    };

    private static ShareLinkFactory CloudOn() =>
        new(new CloudShareOptions { Enabled = true, BaseUrl = "https://share.example.test" });

    private PhotoSessionService CreateService(bool cloud = true, PrintOptions? printOptions = null) =>
        TestSessions.Create(
            _paths,
            _workspaces,
            printers: _printers,
            printOptions: printOptions,
            shareLinks: cloud ? CloudOn() : new ShareLinkFactory(new CloudShareOptions()),
            uploads: _uploads);

    private async Task<PhotoSessionService> ReadyAsync(PhotoTemplate template, bool cloud = true)
    {
        var service = CreateService(cloud);
        Assert.NotNull(service.StartNewSession());
        Assert.True(service.SelectTemplate(template));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        return service;
    }

    private static SKColor PixelAt(string file, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(file);
        return bitmap.GetPixel(x, y);
    }

    private static int DarkPixelsInQrArea(string file)
    {
        using var bitmap = SKBitmap.Decode(file);
        var dark = 0;
        for (var y = QrY; y < QrY + QrSize; y += 2)
        {
            for (var x = QrX; x < QrX + QrSize; x += 2)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 40 && c.Green < 40 && c.Blue < 40)
                {
                    dark++;
                }
            }
        }

        return dark;
    }

    [Fact]
    public async Task FirstPrint_CreatesQrForThatVersion_AndPrintsCopyWithQr_FinalFileHasNoQr()
    {
        var service = await ReadyAsync(QrTemplate());

        var result = await service.RequestPrintAsync(1);

        Assert.Equal(PrintOutcome.Submitted, result.Outcome);
        var v1 = service.Current!.FindVersion(1)!;
        Assert.NotNull(v1.Share);
        var request = Assert.Single(_printer.Requests);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(v1.FilePath)!, "print-v1.png"), request.ImageFilePath);

        // Thành phẩm: vùng QR để trống (thấy nền template), bản in: có QR (nền trắng + chấm đen); ngoài vùng QR giống hệt.
        Assert.Equal(Background, PixelAt(v1.FilePath, QrX + 2, QrY + 2));
        Assert.Equal(0, DarkPixelsInQrArea(v1.FilePath));
        Assert.Equal(SKColors.White, PixelAt(request.ImageFilePath, QrX + 2, QrY + 2));
        Assert.True(DarkPixelsInQrArea(request.ImageFilePath) > 100);
        Assert.Equal(PixelAt(v1.FilePath, 700, 100), PixelAt(request.ImageFilePath, 700, 100));

        // Lịch sử in vẫn trỏ tới phiên bản; ảnh upload lên Cloud là file thành phẩm (không QR).
        Assert.Equal(v1.FilePath, Assert.Single(service.Current.PrintJobs).OutputFilePath);
        var upload = Assert.Single(_uploads.Items);
        Assert.Equal(v1.FilePath, upload.FilePath);
        Assert.Equal(v1.Share!.Token, upload.Link.Token);
    }

    [Fact]
    public async Task Reprint_KeepsSameQr_NewVersionGetsItsOwnQr()
    {
        var service = await ReadyAsync(QrTemplate());

        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(1)).Outcome);
        var tokenV1 = service.Current!.FindVersion(1)!.Share!.Token;
        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(2, version: 1)).Outcome);
        Assert.Equal(tokenV1, service.Current.FindVersion(1)!.Share!.Token);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.Warm));
        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(3)).Outcome);

        var v2 = service.Current.FindVersion(2)!;
        Assert.NotNull(v2.Share);
        Assert.NotEqual(tokenV1, v2.Share!.Token);
        Assert.Equal(tokenV1, service.Current.FindVersion(1)!.Share!.Token);

        var files = _printer.Requests.Select(r => Path.GetFileName(r.ImageFilePath)).ToArray();
        Assert.Equal(["print-v1.png", "print-v1.png", "print-v2.png"], files);
    }

    [Fact]
    public async Task CompletedSession_PrintUsesQrShownOnDoneScreen()
    {
        var service = await ReadyAsync(QrTemplate());
        Assert.True(await service.CompleteSessionAsync());
        var shown = service.Current!.CompletedOutput!.Share!;

        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(1, version: 1)).Outcome);

        Assert.Equal(shown, service.Current.FindVersion(1)!.Share);
        Assert.EndsWith("print-v1.png", Assert.Single(_printer.Requests).ImageFilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateWithoutQrArea_PrintsFinalFile_AndCreatesNoLink()
    {
        var service = await ReadyAsync(TestTemplates.WithPhotoIndexes(0));

        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(1)).Outcome);

        var v1 = service.Current!.FindVersion(1)!;
        Assert.Null(v1.Share);
        Assert.Equal(v1.FilePath, Assert.Single(_printer.Requests).ImageFilePath);
        Assert.Empty(_uploads.Items);
    }

    [Fact]
    public async Task QrArea_ButCloudNotConfigured_PrintsWithoutQr()
    {
        var service = await ReadyAsync(QrTemplate(), cloud: false);

        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(1)).Outcome);

        var v1 = service.Current!.FindVersion(1)!;
        Assert.Null(v1.Share);
        Assert.Equal(v1.FilePath, Assert.Single(_printer.Requests).ImageFilePath);
    }

    [Fact]
    public async Task PrintImageFailure_DoesNotPrintWithoutQr_AndRecordsNoJob()
    {
        var service = TestSessions.Create(_paths, _workspaces, printers: _printers, shareLinks: CloudOn(), uploads: _uploads, printImages: new FailingPrintImageRenderer());
        Assert.NotNull(service.StartNewSession());
        Assert.True(service.SelectTemplate(QrTemplate()));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());

        var result = await service.RequestPrintAsync(1);

        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Empty(_printer.Requests);
        Assert.Empty(service.Current!.PrintJobs);
    }

    [Fact]
    public async Task ClientGallery_FirstReprint_CreatesQrForVersion_PersistsJob_AndLimitsCopies()
    {
        // Phiên hoàn tất khi chưa cấu hình Cloud → chưa có QR.
        var kiosk = await ReadyAsync(QrTemplate(), cloud: false);
        Assert.True(await kiosk.CompleteSessionAsync());
        var sessionKey = Path.GetFileName(kiosk.Current!.Directory!);
        kiosk.EndCurrentSession();
        await kiosk.FlushAsync();

        // Sau đó Cloud được cấu hình; khách in lại từ Thư viện sự kiện.
        var sessions = CreateService(cloud: true);
        var archive = TestSessions.CreateArchive(_paths, _workspaces);
        var gallery = new ClientEventGalleryService(archive, new SkiaImagePreviewRenderer(_paths), sessions);

        Assert.True(gallery.CanPrint);
        Assert.Equal(PrintOptions.DefaultClientMaxCopies, gallery.MaxClientCopies);
        Assert.Equal(PrintOutcome.InvalidCopies, (await gallery.PrintVersionAsync(sessionKey, 1, PrintOptions.DefaultClientMaxCopies + 1)).Outcome);

        var first = await gallery.PrintVersionAsync(sessionKey, 1, 2);
        Assert.Equal(PrintOutcome.Submitted, first.Outcome);

        var stored = (await archive.LoadSessionAsync(sessionKey)).Session;
        var token = stored.FindVersion(1)!.Share!.Token;
        Assert.Equal(2, Assert.Single(stored.PrintJobs).Copies);
        Assert.EndsWith("print-v1.png", Assert.Single(_printer.Requests).ImageFilePath, StringComparison.Ordinal);

        // In lại lần nữa: cùng QR, thêm một Print Job; phiên hiện tại của Kiosk không bị đổi.
        Assert.Equal(PrintOutcome.Submitted, (await gallery.PrintVersionAsync(sessionKey, 1, 1)).Outcome);
        stored = (await archive.LoadSessionAsync(sessionKey)).Session;
        Assert.Equal(token, stored.FindVersion(1)!.Share!.Token);
        Assert.Equal(2, stored.PrintJobs.Count);
        Assert.Null(sessions.Current);

        var detail = await gallery.GetSessionDetailAsync(sessionKey);
        Assert.Equal(token, Assert.Single(detail.Versions).Share!.Token);
        Assert.Equal(2, detail.PrintJobs.Count);
    }

    [Fact]
    public async Task ClientGallery_UnclearPreviousJob_RequiresStaff()
    {
        var failing = new RecordingPrinter(isPhysical: true, result: PrintJobStatus.Failed);
        _printers.Current = failing;
        var kiosk = await ReadyAsync(QrTemplate());
        Assert.True(await kiosk.CompleteSessionAsync());
        Assert.Equal(PrintOutcome.Failed, (await kiosk.RequestPrintAsync(1, version: 1)).Outcome);
        var sessionKey = Path.GetFileName(kiosk.Current!.Directory!);
        kiosk.EndCurrentSession();
        await kiosk.FlushAsync();

        _printers.Current = _printer;
        var gallery = new ClientEventGalleryService(TestSessions.CreateArchive(_paths, _workspaces), new SkiaImagePreviewRenderer(_paths), CreateService());

        var result = await gallery.PrintVersionAsync(sessionKey, 1, 1);

        Assert.Equal(PrintOutcome.ConfirmationRequired, result.Outcome);
        Assert.Empty(_printer.Requests);
    }

    [Fact]
    public void ClientMaxCopies_MustNotExceedMaxCopies()
    {
        Assert.Equal(2, new PrintOptions().ClientMaxCopies);
        new PrintOptions { MaxCopies = 3, ClientMaxCopies = 3 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrintOptions { MaxCopies = 3, ClientMaxCopies = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrintOptions { ClientMaxCopies = 0 }.Validate());
    }

    [Fact]
    public async Task Renderer_ReusesExistingPrintFile_AndNeverTouchesSource()
    {
        var source = Path.Combine(_temp.Path, "final.png");
        File.WriteAllBytes(source, TestImages.CreateQuadrants(800, 800, SKEncodedImageFormat.Png));
        var before = File.ReadAllBytes(source);
        var destination = Path.Combine(_temp.Path, "print.png");
        var renderer = new SkiaPrintImageRenderer();
        var placement = new PrintQrPlacement(QrX, QrY, QrSize, 800, 800);

        await renderer.RenderWithQrAsync(source, destination, placement, "https://share.example.test/s/abc");
        var first = File.ReadAllBytes(destination);
        await renderer.RenderWithQrAsync(source, destination, placement, "https://share.example.test/s/abc");

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(first, File.ReadAllBytes(destination));
        Assert.True(DarkPixelsInQrArea(destination) > 100);
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class FailingPrintImageRenderer : IPrintImageRenderer
    {
        public Task RenderWithQrAsync(string sourceImagePath, string destinationPath, PrintQrPlacement placement, string url, CancellationToken cancellationToken = default) =>
            throw new IOException("Ổ đĩa đầy.");
    }
}
