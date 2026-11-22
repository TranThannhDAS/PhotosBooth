using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Export;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Sessions;

/// <summary>
/// Photo Preview và Manual Crop ở mức phiên: preview/xuất file dùng chung thành phẩm của Photo Composer
/// và cùng tham số chỉnh vùng cắt; ảnh gốc/template không bị thay đổi.
/// </summary>
public sealed class PreviewAndCropSessionTests : IDisposable
{
    private const int Tolerance = 30;
    private readonly TempDirectory _temp = new();
    private readonly FakeCameraDevice _camera = new();
    private readonly PhotoboothStoragePaths _paths;

    public PreviewAndCropSessionTests()
    {
        _paths = new PhotoboothStoragePaths(_temp.Path);
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    private PhotoSessionService CreateService(bool mirror = false) =>
        TestSessions.Create(
            _paths,
            TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "Event")),
            new CaptureOptions { MirrorByDefault = mirror });

    /// <summary>Phiên đã chụp đủ và ghép xong (Ready) với template cho trước.</summary>
    private async Task<PhotoSessionService> CreateReadyAsync(PhotoTemplate template, bool mirror = false)
    {
        var service = CreateService(mirror);
        service.StartNewSession();
        Assert.True(service.SelectTemplate(template));
        Assert.True(service.MarkPreviewReady());
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);
        return service;
    }

    // Ô thứ i của TestTemplates.WithPhotoIndexes: 180×180 tại (10 + 190i, 10). Ảnh 640×480 → vùng cơ sở 480×480.
    private static (int X, int Y) InSlot(int slot, double u, double v) =>
        (10 + (slot * 190) + (int)(u * 179), 10 + (int)(v * 179));

    private static SKColor Pixel(string file, (int X, int Y) p) => TestImages.PixelAt(file, p.X, p.Y);

    private static SKColor Pixel(byte[] image, int canvasWidth, (int X, int Y) p)
    {
        using var bitmap = SKBitmap.Decode(image);
        var scale = (double)bitmap.Width / canvasWidth;
        return bitmap.GetPixel((int)(p.X * scale), (int)(p.Y * scale));
    }

    // ------------------------------------------------------------ Photo Preview

    [Fact]
    public async Task Preview_KeepsCanvasAspectRatio_AtAnyPreviewSize()
    {
        var service = await CreateReadyAsync(TestTemplates.FourSlots());

        foreach (var width in new[] { 200, 400, 600 })
        {
            var preview = await service.GetComposedPreviewAsync(width);
            var (w, h) = TestImages.SizeOf(preview!);
            Assert.Equal(600.0 / 900, (double)w / h, precision: 2);
        }
    }

    [Fact]
    public async Task Preview_RendersEverySlotFromComposerOutput()
    {
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0, 1, 0));
        var composed = service.Current!.Composed!.FilePath;
        var preview = (await service.GetComposedPreviewAsync(400))!;

        for (var slot = 0; slot < 3; slot++)
        {
            var point = InSlot(slot, 0.1, 0.1);
            Assert.True(TestImages.IsClose(Pixel(preview, 800, point), Pixel(composed, point), Tolerance), $"Ô {slot}");
            Assert.True(TestImages.IsClose(Pixel(composed, point), TestImages.TopLeft, Tolerance), $"Ô {slot}");
        }
    }

    [Fact]
    public async Task Retake_InvalidatesPreview_AndResetsCropOfSlotsUsingThatPhoto()
    {
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0, 1, 0));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, new SlotCrop(2, 0.25, 0.25)));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, new SlotCrop(2, 0.75, 0.75)));
        var before = service.Current!.Composed!.FilePath;

        Assert.True(service.BeginRetake(0));
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);

        var session = service.Current!;
        Assert.Null(session.Composed);
        Assert.Equal([null, new SlotCrop(2, 0.75, 0.75), null], session.SlotCrops);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        Assert.NotEqual(before, service.Current!.Composed!.FilePath);
    }

    // ------------------------------------------------------------ Manual Crop

    [Fact]
    public async Task ApplyCrop_UpdatesPreview_OnlyForThatSlot_AndKeepsFilter()
    {
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0, 0));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.Grayscale));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));
        var before = service.Current!.Composed!;

        // Ô 2 phóng 200% vào góc phải dưới; ô 1 (cùng ảnh nguồn) giữ Auto Crop.
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, new SlotCrop(2, 0.75, 0.75)));

        var after = service.Current!.Composed!;
        Assert.NotEqual(before.FilePath, after.FilePath);
        Assert.Equal([null, new SlotCrop(2, 0.75, 0.75)], after.SlotCrops);
        Assert.Equal(ColorFilter.None, after.Filter);
        Assert.True(TestImages.IsClose(Pixel(after.FilePath, InSlot(0, 0.9, 0.1)), TestImages.TopRight, Tolerance));
        Assert.True(TestImages.IsClose(Pixel(after.FilePath, InSlot(1, 0.1, 0.1)), TestImages.BottomRight, Tolerance));

        var preview = (await service.GetComposedPreviewAsync(400))!;
        Assert.True(TestImages.IsClose(Pixel(preview, 800, InSlot(1, 0.1, 0.1)), TestImages.BottomRight, Tolerance));

        // Áp dụng lại đúng crop đó: dùng lại thành phẩm, không ghép thừa.
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, new SlotCrop(2, 0.75, 0.75)));
        Assert.Equal(after.FilePath, service.Current!.Composed!.FilePath);
    }

    [Fact]
    public async Task MirroredSession_CropMatchesWhatEditorShows()
    {
        // Trình chỉnh hiển thị ảnh xem trước đã lật (GetSlotPreviewAsync); crop tính theo hướng đó.
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0), mirror: true);
        var shown = (await service.GetSlotPreviewAsync(0, 640))!;
        var shownTopLeft = TestImages.PixelAt(shown, 80, 60);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, new SlotCrop(2, 0.25, 0.25)));

        var composedPixel = Pixel(service.Current!.Composed!.FilePath, InSlot(0, 0.5, 0.5));
        Assert.True(TestImages.IsClose(shownTopLeft, TestImages.TopRight, Tolerance));
        Assert.True(TestImages.IsClose(composedPixel, shownTopLeft, Tolerance));
    }

    [Fact]
    public async Task ResetCrop_ForOneSlotOrAll_ReturnsToAutoCrop()
    {
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0, 0));
        var auto = service.Current!.Composed!.FilePath;
        await service.ApplySlotCropAsync(0, new SlotCrop(2, 0.25, 0.25));
        await service.ApplySlotCropAsync(1, new SlotCrop(1.5, 0.75, 0.25));

        // "Khôi phục Auto Crop" trong trình chỉnh = áp dụng null (hoặc đúng giá trị Auto Crop).
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, null));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, SlotCrop.AutoFor(service.Current!.Template!.Slots[1])));
        Assert.All(service.Current!.SlotCrops, Assert.Null);

        await service.ApplySlotCropAsync(1, new SlotCrop(3, 1, 1));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ResetAllSlotCropsAsync());
        Assert.All(service.Current!.SlotCrops, Assert.Null);

        using var expected = SKBitmap.Decode(auto);
        using var actual = SKBitmap.Decode(service.Current!.Composed!.FilePath);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public async Task RejectedCrop_LeavesConfirmedDataAndPreviewUntouched()
    {
        var contain = TestTemplates.WithPhotoIndexes(0, 0);
        contain = contain with { Slots = [contain.Slots[0], contain.Slots[1] with { Fit = ImageFit.Contain }] };
        var service = await CreateReadyAsync(contain);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, new SlotCrop(2, 0.25, 0.25)));
        var confirmed = service.Current!;

        Assert.Equal(CompositionOutcome.NotReady, await service.ApplySlotCropAsync(0, new SlotCrop(5, 0.5, 0.5)));
        Assert.Equal(CompositionOutcome.NotReady, await service.ApplySlotCropAsync(1, new SlotCrop(1.5, 0.5, 0.5)));
        Assert.Equal(CompositionOutcome.NotReady, await service.ApplySlotCropAsync(2, new SlotCrop(1.5, 0.5, 0.5)));
        Assert.Equal(CompositionOutcome.NotReady, await service.ApplySlotCropAsync(-1, null));

        Assert.Same(confirmed, service.Current);
    }

    [Fact]
    public async Task ApplyCrop_BeforeAllPhotosCaptured_IsNotReady()
    {
        var service = CreateService();
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.WithPhotoIndexes(0));
        service.MarkPreviewReady();

        Assert.Equal(CompositionOutcome.NotReady, await service.ApplySlotCropAsync(0, new SlotCrop(2, 0.5, 0.5)));
        Assert.All(service.Current!.SlotCrops, Assert.Null);
    }

    [Fact]
    public async Task CropIsNormalized_PreviewSizeDoesNotChangeResult()
    {
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0));
        await service.ApplySlotCropAsync(0, new SlotCrop(2, 0.75, 0.25));

        var small = (await service.GetComposedPreviewAsync(200))!;
        var large = (await service.GetComposedPreviewAsync(800))!;

        foreach (var point in new[] { InSlot(0, 0.2, 0.2), InSlot(0, 0.8, 0.8) })
        {
            Assert.True(TestImages.IsClose(Pixel(small, 800, point), TestImages.TopRight, Tolerance));
            Assert.True(TestImages.IsClose(Pixel(large, 800, point), TestImages.TopRight, Tolerance));
        }
    }

    // ------------------------------------------------------------ Đồng bộ với xuất file

    [Fact]
    public async Task Export_UsesExactlyTheConfirmedCrop_SameAsPreview()
    {
        var service = await CreateReadyAsync(TestTemplates.WithPhotoIndexes(0, 0));
        await service.ApplySlotCropAsync(1, new SlotCrop(2, 0.25, 0.75));
        var composed = service.Current!.Composed!.FilePath;
        var preview = (await service.GetComposedPreviewAsync(400))!;
        var outDir = Directory.CreateDirectory(Path.Combine(_temp.Path, "out")).FullName;

        var results = await service.ExportAsync([ExportFormat.Jpeg, ExportFormat.Png, ExportFormat.Pdf], outDir);

        Assert.All(results, r => Assert.True(r.Result.Succeeded));
        var png = results.Single(r => r.Format == ExportFormat.Png).Result.FilePath!;
        var jpeg = results.Single(r => r.Format == ExportFormat.Jpeg).Result.FilePath!;
        Assert.Equal(await File.ReadAllBytesAsync(composed), await File.ReadAllBytesAsync(png));

        foreach (var point in new[] { InSlot(0, 0.9, 0.1), InSlot(1, 0.5, 0.5), InSlot(1, 0.1, 0.9) })
        {
            var expected = Pixel(composed, point);
            Assert.True(TestImages.IsClose(Pixel(jpeg, point), expected, Tolerance));
            Assert.True(TestImages.IsClose(Pixel(preview, 800, point), expected, Tolerance));
        }

        Assert.True(TestImages.IsClose(Pixel(jpeg, InSlot(1, 0.5, 0.5)), TestImages.BottomLeft, Tolerance));
    }

    [Fact]
    public async Task ManualCrop_DoesNotModifyOriginalsOrTemplateJson()
    {
        var templatesRoot = Path.Combine(_temp.Path, "Templates");
        var file = TestTemplateFiles.Write(templatesRoot, "crop-json", """
            {
              "id": "crop-json", "name": "Mẫu crop",
              "canvas": { "widthPixels": 400, "heightPixels": 200 },
              "slots": [
                { "id": "a", "photoIndex": 0, "x": 0, "y": 0, "width": 200, "height": 200, "focalPoint": { "x": 0.4, "y": 0.6 } },
                { "id": "b", "photoIndex": 0, "x": 200, "y": 0, "width": 200, "height": 200 }
              ]
            }
            """);
        var jsonBefore = await File.ReadAllBytesAsync(file);
        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(file);
        Assert.True(validation.IsValid);

        var service = await CreateReadyAsync(template!);
        var original = service.Current!.Slots[0]!.OriginalFilePath;
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(original));

        await service.ApplySlotCropAsync(0, new SlotCrop(2.5, 0.1, 0.9));
        await service.ApplySlotCropAsync(1, new SlotCrop(1.2, 0.9, 0.1));
        await service.ExportAsync([ExportFormat.Png], Directory.CreateDirectory(Path.Combine(_temp.Path, "out")).FullName);

        Assert.Equal(jsonBefore, await File.ReadAllBytesAsync(file));
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(original)));
        Assert.Equal(new FocalPoint { X = 0.4, Y = 0.6 }, service.Current!.Template!.Slots[0].FocalPoint);
        Assert.Equal(new FocalPoint { X = 0.4, Y = 0.6 }, template!.Slots[0].FocalPoint);
    }
}
