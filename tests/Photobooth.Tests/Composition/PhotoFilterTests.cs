using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Admin;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Composition;

public sealed class PhotoFilterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly SkiaPhotoComposer _composer;
    private readonly SkiaImagePreviewRenderer _previewRenderer;
    private readonly Guid _sessionId = Guid.NewGuid();

    public PhotoFilterTests()
    {
        _paths = new PhotoboothStoragePaths(_temp.Path);
        _composer = new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance);
        _previewRenderer = new SkiaImagePreviewRenderer(_paths);
    }

    public void Dispose() => _temp.Dispose();

    private string WriteOriginal(byte[] data, string name = "photo.jpg")
    {
        var directory = _paths.GetOriginalsDirectory(_sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static PhotoTemplate SingleSlotTemplate() => new()
    {
        Id = "filter-test",
        Name = "Kiểm thử bộ lọc",
        Canvas = new TemplateCanvas { WidthPixels = 200, HeightPixels = 200, Dpi = 100, BackgroundColor = "#FFFFFF" },
        Slots =
        [
            new TemplateSlot { Id = "slot-1", PhotoIndex = 0, X = 10, Y = 10, Width = 120, Height = 120 },
        ],
        Decorations =
        [
            new TemplateDecoration { Id = "deco-rect", Type = DecorationKind.Rectangle, X = 150, Y = 150, Width = 40, Height = 40, Color = "#00FF00", ZIndex = 10 },
        ],
    };

    private async Task<SKColor> RenderSolidColorAsync(SKColor inputColor, ColorFilter filter)
    {
        var original = WriteOriginal(TestImages.CreateSolid(120, 120, inputColor, SKEncodedImageFormat.Png), $"{Guid.NewGuid():N}.png");
        var request = new CompositionRequest(
            _sessionId,
            SingleSlotTemplate(),
            [new CompositionPhoto(original, Mirror: false)],
            filter,
            new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(7)));

        var composed = await _composer.ComposeAsync(request);
        return TestImages.PixelAt(composed.FilePath, 70, 70);
    }

    [Fact]
    public async Task NoneFilter_PreservesExactOriginalPhotoColors()
    {
        var input = new SKColor(180, 130, 100);
        var actual = await RenderSolidColorAsync(input, ColorFilter.None);

        Assert.Equal(input, actual);
    }

    [Fact]
    public async Task AllFilters_ProduceDistinctColorSignatures_AndPreserveDecorations()
    {
        var allFilters = Enum.GetValues<ColorFilter>();
        Assert.Equal(12, allFilters.Length);

        var samplePhoto = WriteOriginal(TestImages.CreateQuadrants(240, 240, SKEncodedImageFormat.Png), "quads.png");
        var template = SingleSlotTemplate();
        var signatures = new Dictionary<ColorFilter, (SKColor A, SKColor B, SKColor C, SKColor D)>();

        foreach (var filter in allFilters)
        {
            var composed = await _composer.ComposeAsync(new CompositionRequest(
                _sessionId,
                template,
                [new CompositionPhoto(samplePhoto, Mirror: false)],
                filter,
                DateTimeOffset.Now));

            // Màu nền canvas và khối trang trí màu xanh lá không bao giờ bị ảnh hưởng bởi bộ lọc ảnh.
            Assert.Equal(SKColors.White, TestImages.PixelAt(composed.FilePath, 5, 5));
            Assert.Equal(new SKColor(0, 255, 0), TestImages.PixelAt(composed.FilePath, 170, 170));

            var signature = (
                TestImages.PixelAt(composed.FilePath, 40, 40),
                TestImages.PixelAt(composed.FilePath, 100, 40),
                TestImages.PixelAt(composed.FilePath, 40, 100),
                TestImages.PixelAt(composed.FilePath, 100, 100));

            foreach (var (existingFilter, existingSignature) in signatures)
            {
                Assert.True(
                    signature != existingSignature,
                    $"Bộ lọc {filter} tạo ra màu trùng với {existingFilter}.");
            }

            signatures[filter] = signature;
        }
    }

    [Fact]
    public async Task FilterColorCharacteristics_MatchDesignIntent()
    {
        var neutral = new SKColor(128, 128, 128);
        var darkShadow = new SKColor(25, 25, 25);
        var vividRed = new SKColor(220, 40, 40);

        var baseNeutral = await RenderSolidColorAsync(neutral, ColorFilter.None);
        var warm = await RenderSolidColorAsync(neutral, ColorFilter.Warm);
        var cool = await RenderSolidColorAsync(neutral, ColorFilter.Cool);
        var bright = await RenderSolidColorAsync(neutral, ColorFilter.Bright);
        var pastelShadow = await RenderSolidColorAsync(darkShadow, ColorFilter.Pastel);
        var pastelVivid = await RenderSolidColorAsync(vividRed, ColorFilter.Pastel);
        var softRose = await RenderSolidColorAsync(neutral, ColorFilter.SoftRose);
        var goldenHour = await RenderSolidColorAsync(neutral, ColorFilter.GoldenHour);
        var filmShadow = await RenderSolidColorAsync(darkShadow, ColorFilter.Film);
        var cinematicShadow = await RenderSolidColorAsync(new SKColor(50, 50, 50), ColorFilter.Cinematic);
        var moodyNeutral = await RenderSolidColorAsync(neutral, ColorFilter.Moody);
        var moodyVivid = await RenderSolidColorAsync(vividRed, ColorFilter.Moody);
        var grayscaleVivid = await RenderSolidColorAsync(vividRed, ColorFilter.Grayscale);

        // Warm: Đỏ > Lục > Lam
        Assert.True(warm.Red > warm.Green && warm.Green > warm.Blue);

        // Cool: Lam > Lục > Đỏ
        Assert.True(cool.Blue > cool.Green && cool.Green > cool.Red);

        // Bright: Sáng hơn gốc trên cả 3 kênh và giữ cân bằng trắng trung tính
        Assert.True(bright.Red > baseNeutral.Red && bright.Green > baseNeutral.Green && bright.Blue > baseNeutral.Blue);
        Assert.True(Math.Abs(bright.Red - bright.Green) <= 2 && Math.Abs(bright.Green - bright.Blue) <= 2);

        // Pastel: Nâng sáng vùng tối (mềm bóng đổ) và làm dịu màu đỏ gắt
        Assert.True(pastelShadow.Red > darkShadow.Red + 10 && pastelShadow.Green > darkShadow.Green + 10);
        Assert.True(pastelVivid.Red < vividRed.Red && pastelVivid.Green > vividRed.Green);

        // SoftRose: Tông hồng phấn (Đỏ > Lam > Lục)
        Assert.True(softRose.Red > softRose.Blue && softRose.Blue > softRose.Green);

        // GoldenHour: Tông nắng vàng hổ phách (cả Đỏ và Lục đều tăng, Lam giảm, ấm vàng hơn Warm)
        Assert.True(goldenHour.Red > warm.Red && goldenHour.Green > warm.Green && goldenHour.Blue < warm.Blue);

        // Film: Vùng tối có ánh phim analog, giữ đa sắc (không đơn sắc như Sepia)
        Assert.True(filmShadow.Blue > filmShadow.Red);
        var filmGreen = await RenderSolidColorAsync(new SKColor(40, 180, 60), ColorFilter.Film);
        Assert.True(filmGreen.Green > filmGreen.Red && filmGreen.Green > filmGreen.Blue);

        // Cinematic: Vùng tối ngả Teal (Lam > Lục > Đỏ)
        Assert.True(cinematicShadow.Blue > cinematicShadow.Green && cinematicShadow.Green > cinematicShadow.Red);

        // Moody: Tông trầm hơn gốc, giảm độ bão hòa nhưng vẫn giữ sắc màu (khác Grayscale)
        Assert.True(moodyNeutral.Red < baseNeutral.Red && moodyNeutral.Green < baseNeutral.Green);
        Assert.True(moodyVivid.Red < vividRed.Red && moodyVivid.Red > moodyVivid.Green + 40);
        Assert.True(Math.Abs(grayscaleVivid.Red - grayscaleVivid.Green) <= 2);
    }

    [Fact]
    public async Task SwitchingFilters_AlwaysStartsFromOriginalSource_NeverStacks()
    {
        var workspaceRoot = Path.Combine(_temp.Path, "Event");
        var workspaces = TestSessions.OpenWorkspace(_paths, workspaceRoot);
        var camera = new FakeCameraDevice
        {
            NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType),
        };

        var directService = TestSessions.Create(_paths, workspaces);
        directService.StartNewSession();
        directService.SelectTemplate(TestTemplates.TwoSlots());
        directService.MarkPreviewReady();
        Assert.True((await directService.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await directService.ComposeAsync(ColorFilter.GoldenHour));
        var directBytes = await File.ReadAllBytesAsync(directService.Current!.Composed!.FilePath);
        directService.EndCurrentSession();

        // Chuyển qua hàng loạt bộ lọc trước khi dừng ở GoldenHour
        var multiSwitchService = TestSessions.Create(_paths, workspaces);
        multiSwitchService.StartNewSession();
        multiSwitchService.SelectTemplate(TestTemplates.TwoSlots());
        multiSwitchService.MarkPreviewReady();
        Assert.True((await multiSwitchService.CaptureSequenceAsync(camera, null)).Succeeded);
        var originalHashes = multiSwitchService.Current!.Photos
            .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.OriginalFilePath))))
            .ToArray();

        foreach (var filter in new[]
                 {
                     ColorFilter.Grayscale,
                     ColorFilter.Sepia,
                     ColorFilter.Moody,
                     ColorFilter.Cinematic,
                     ColorFilter.Cool,
                     ColorFilter.Pastel,
                     ColorFilter.GoldenHour,
                 })
        {
            Assert.Equal(CompositionOutcome.Succeeded, await multiSwitchService.ComposeAsync(filter));
        }

        var switchedBytes = await File.ReadAllBytesAsync(multiSwitchService.Current.Composed!.FilePath);
        Assert.Equal(directBytes, switchedBytes);

        // Ảnh gốc không bị chỉnh sửa sau nhiều lần đổi bộ lọc
        var afterHashes = multiSwitchService.Current.Photos
            .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.OriginalFilePath))))
            .ToArray();
        Assert.Equal(originalHashes, afterHashes);
    }

    [Fact]
    public async Task ChangingFilter_PreservesManualCropAndSyncsPreviewThumbnailsAndAllExports()
    {
        var workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "EventSync"));
        var camera = new FakeCameraDevice
        {
            NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType),
        };
        var service = TestSessions.Create(_paths, workspaces);
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 0));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));

        var crop = new SlotCrop(2.0, 0.75, 0.25);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, crop));

        // Đổi sang bộ lọc Cinematic: Manual Crop vẫn giữ nguyên
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.Cinematic));
        var session = service.Current!;
        Assert.Equal(ColorFilter.Cinematic, session.Filter);
        Assert.Equal([null, crop], session.SlotCrops);
        Assert.Equal([null, crop], session.Composed!.SlotCrops);

        var composedPath = session.Composed.FilePath;
        var previewBytes = (await service.GetComposedPreviewAsync(400))!;
        var exportDir = Directory.CreateDirectory(Path.Combine(_temp.Path, "Exports")).FullName;
        var exports = await service.ExportAsync([ExportFormat.Jpeg, ExportFormat.Png, ExportFormat.Pdf], exportDir);

        Assert.All(exports, e => Assert.True(e.Result.Succeeded));
        var pngPath = exports.Single(e => e.Format == ExportFormat.Png).Result.FilePath!;
        var jpegPath = exports.Single(e => e.Format == ExportFormat.Jpeg).Result.FilePath!;
        var pdfPath = exports.Single(e => e.Format == ExportFormat.Pdf).Result.FilePath!;

        Assert.Equal(await File.ReadAllBytesAsync(composedPath), await File.ReadAllBytesAsync(pngPath));
        Assert.True(new FileInfo(pdfPath).Length > 100);

        var expectedSlot0 = TestImages.PixelAt(composedPath, 50, 50);
        var expectedSlot1 = TestImages.PixelAt(composedPath, 240, 50);
        Assert.True(TestImages.IsClose(TestImages.PixelAt(jpegPath, 50, 50), expectedSlot0, 25));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(jpegPath, 240, 50), expectedSlot1, 25));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(previewBytes, 25, 25), expectedSlot0, 25));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(previewBytes, 120, 25), expectedSlot1, 25));

        // Thumbnail bộ lọc phản ánh đúng sự khác biệt màu giữa các bộ lọc
        var thumbNone = (await service.GetFilterPreviewAsync(ColorFilter.None, 160))!;
        var thumbCool = (await service.GetFilterPreviewAsync(ColorFilter.Cool, 160))!;
        var thumbWarm = (await service.GetFilterPreviewAsync(ColorFilter.Warm, 160))!;
        Assert.NotEqual(TestImages.PixelAt(thumbNone, 20, 20), TestImages.PixelAt(thumbCool, 20, 20));
        Assert.NotEqual(TestImages.PixelAt(thumbCool, 20, 20), TestImages.PixelAt(thumbWarm, 20, 20));
    }

    [Fact]
    public async Task EventWorkspace_SavesAndRestoresNewFilters_AcrossReopenReeditPrintAndUnsupportedFallback()
    {
        var workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "EventPersist"));
        var camera = new FakeCameraDevice
        {
            NextFrame = _ => new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType),
        };
        var printer = new RecordingPrinter(isPhysical: false, result: PrintJobStatus.Simulated);
        var printers = new TestPrinterProvider { Current = printer };

        var service = TestSessions.Create(_paths, workspaces, printers: printers);
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.TwoSlots());
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.Pastel));
        Assert.True(await service.CompleteSessionAsync());
        var completed = service.Current!;
        var sessionKey = Path.GetFileName(completed.Directory!);
        var v1File = completed.LatestVersion!.FilePath;
        var v1Bytes = await File.ReadAllBytesAsync(v1File);
        await service.FlushAsync();

        // Mở lại phiên từ ổ đĩa
        var archive = TestSessions.CreateArchive(_paths, workspaces);
        var reopenedService = TestSessions.Create(_paths, workspaces, printers: printers);
        var admin = new AdminAccessService();
        Assert.True(admin.TryUnlock(AdminAccessService.Pin));
        var gallery = new EventGalleryService(archive, admin, reopenedService, _previewRenderer);

        Assert.NotNull(await gallery.OpenAsync(sessionKey));
        Assert.Equal(ColorFilter.Pastel, reopenedService.Current!.Filter);

        // Chỉnh sửa sang bộ lọc Film và lưu thành v2, giữ nguyên v1
        Assert.True(reopenedService.BeginEditFromVersion(1));
        Assert.Equal(CompositionOutcome.Succeeded, await reopenedService.ComposeAsync(ColorFilter.Film));
        var v2 = await reopenedService.SaveEditsAsync();
        Assert.NotNull(v2);
        Assert.Equal(2, v2.Version);
        Assert.Equal(ColorFilter.Film, v2.Filter);
        Assert.Equal(v1Bytes, await File.ReadAllBytesAsync(v1File));
        Assert.NotEqual(v1Bytes, await File.ReadAllBytesAsync(v2.FilePath));

        // In lại phiên bản v2 đã chỉnh sửa
        var printResult = await reopenedService.RequestPrintAsync(copies: 2, version: 2);
        Assert.Equal(PrintOutcome.Simulated, printResult.Outcome);
        Assert.Equal(2, printResult.Job!.OutputVersion);
        Assert.Equal(v2.FilePath, printResult.Job.OutputFilePath);
        await reopenedService.FlushAsync();

        // Database chứa mã Filter không còn được hỗ trợ: đọc ra bộ lọc mặc định an toàn, không lỗi.
        await using (var db = TestDatabase.Open(_paths))
        {
            var v2Row = db.OutputVersions.Single(v => v.SessionId == completed.SessionId && v.Version == 2);
            v2Row.Filter = "UnsupportedFutureFilter999";
            await db.SaveChangesAsync();
        }

        var loadedWithUnknown = await archive.LoadSessionAsync(sessionKey);
        Assert.Equal(ColorFilter.None, loadedWithUnknown.Session.Versions[^1].Filter);
    }

    [Fact]
    public async Task RapidFilterSwitching_CancelsOlderComposition_AndNeverOverwritesLatestFilter()
    {
        var workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "EventRapid"));
        var slowFirstComposer = new ControlledComposer(_composer);
        var service = TestSessions.Create(_paths, workspaces, composer: slowFirstComposer);

        var camera = new FakeCameraDevice();
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.TwoSlots());
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));

        // Giữ lượt ghép Warm chờ trên luồng nền
        slowFirstComposer.HoldFilter = ColorFilter.Warm;
        var warmTask = service.ComposeAsync(ColorFilter.Warm);
        await slowFirstComposer.EnteredHold.Task;

        // Trong lúc Warm chưa xong, người dùng chuyển ngay sang Moody
        var moodyOutcome = await service.ComposeAsync(ColorFilter.Moody);
        slowFirstComposer.ReleaseHold.TrySetResult();
        var warmOutcome = await warmTask;

        Assert.Equal(CompositionOutcome.Succeeded, moodyOutcome);
        Assert.Equal(CompositionOutcome.Failed, warmOutcome);
        Assert.Equal(ColorFilter.Moody, service.Current!.Filter);
        Assert.Equal(ColorFilter.Moody, service.Current.Composed!.Filter);
    }

    private sealed class ControlledComposer(IPhotoComposer inner) : IPhotoComposer
    {
        public ColorFilter? HoldFilter { get; set; }

        public TaskCompletionSource EnteredHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ComposedImage> ComposeAsync(CompositionRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Filter == HoldFilter)
            {
                EnteredHold.TrySetResult();
                await ReleaseHold.Task;
            }

            return await inner.ComposeAsync(request, cancellationToken);
        }

        public Task<byte[]> RenderTemplatePreviewAsync(PhotoTemplate template, int maxWidthPixels, CancellationToken cancellationToken = default) =>
            inner.RenderTemplatePreviewAsync(template, maxWidthPixels, cancellationToken);

        public Task<byte[]> RenderTemplateSampleAsync(PhotoTemplate template, TemplateSampleContent content, int maxWidthPixels, CancellationToken cancellationToken = default) =>
            inner.RenderTemplateSampleAsync(template, content, maxWidthPixels, cancellationToken);
    }
}
