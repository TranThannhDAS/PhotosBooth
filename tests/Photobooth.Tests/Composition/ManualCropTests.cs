using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Composition;

/// <summary>Thuật toán Manual Crop (zoom + focal point) và cách Photo Composer áp dụng theo từng ô.</summary>
public sealed class ManualCropTests : IDisposable
{
    private static readonly SKColor White = new(255, 255, 255);
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly SkiaPhotoComposer _composer;
    private readonly Guid _sessionId = Guid.NewGuid();

    public ManualCropTests()
    {
        _paths = new PhotoboothStoragePaths(_temp.Path);
        _composer = new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------ CropCalculator

    [Fact]
    public void Zoom100_EqualsAutoCropBase()
    {
        Assert.Equal(CropCalculator.Cover(1920, 1080, 500, 500, 0.3, 0.6), CropCalculator.Cover(1920, 1080, 500, 500, 0.3, 0.6, zoom: 1));
        Assert.Equal(new PixelRect(420, 0, 1080, 1080), CropCalculator.Cover(1920, 1080, 500, 500, 0.5, 0.5, zoom: 1));
    }

    [Fact]
    public void Zoom150_ShrinksCropWindowAroundFocalPoint()
    {
        // Vùng cơ sở 1080×1080 → 720×720, tâm vẫn ở giữa ảnh.
        var crop = CropCalculator.Cover(1920, 1080, 500, 500, 0.5, 0.5, zoom: 1.5);

        Assert.Equal(new PixelRect(600, 180, 720, 720), crop);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.25, 120)]
    [InlineData(0.75, 1080)]
    [InlineData(1.0, 1200)]
    public void DraggingLeftRight_MovesWindowAndClampsInsideImage(double focalX, double expectedX)
    {
        var crop = CropCalculator.Cover(1920, 1080, 500, 500, focalX, 0.5, zoom: 1.5);

        Assert.Equal(expectedX, crop.X, precision: 6);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 180)]
    [InlineData(1.0, 360)]
    public void DraggingUpDown_MovesWindowAndClampsInsideImage(double focalY, double expectedY)
    {
        var crop = CropCalculator.Cover(1920, 1080, 500, 500, 0.5, focalY, zoom: 1.5);

        Assert.Equal(expectedY, crop.Y, precision: 6);
    }

    [Theory]
    [InlineData(1920, 1080, 520, 370, 1.0, 0.0, 0.0)]
    [InlineData(1920, 1080, 520, 370, 3.0, 1.0, 1.0)]
    [InlineData(1280, 720, 340, 340, 2.2, 0.9, 0.1)]
    [InlineData(640, 480, 520, 640, 1.7, 0.01, 0.99)]
    [InlineData(480, 640, 1080, 810, 2.5, 0.5, 0.0)]
    public void AnyZoomAndPosition_NoEmptyAreaAndNoDistortion(int sw, int sh, int tw, int th, double zoom, double fx, double fy)
    {
        var crop = CropCalculator.Cover(sw, sh, tw, th, fx, fy, zoom);

        // Không méo: cùng tỉ lệ với ô. Không trống: vùng cắt nằm trọn trong ảnh.
        Assert.Equal((double)tw / th, crop.Width / crop.Height, precision: 6);
        Assert.True(crop.X >= 0 && crop.Y >= 0 && crop.X + crop.Width <= sw + 1e-9 && crop.Y + crop.Height <= sh + 1e-9);
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void ZoomBelow100_IsRejected(double zoom) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CropCalculator.Cover(100, 100, 10, 10, 0.5, 0.5, zoom));

    [Theory]
    [InlineData(1.0, 0.5, 0.5, true)]
    [InlineData(3.0, 0, 1, true)]
    [InlineData(3.01, 0.5, 0.5, false)]
    [InlineData(0.9, 0.5, 0.5, false)]
    [InlineData(1.5, -0.1, 0.5, false)]
    [InlineData(1.5, 0.5, double.PositiveInfinity, false)]
    public void SlotCrop_Validation(double zoom, double fx, double fy, bool valid) =>
        Assert.Equal(valid, new SlotCrop(zoom, fx, fy).IsValid);

    // ------------------------------------------------------------ Photo Composer

    /// <summary>Canvas 220×100 trắng, hai ô 100×100 cùng dùng ảnh 0.</summary>
    private static PhotoTemplate TwoSlotsSharingPhoto(int cornerRadius = 0) => new()
    {
        Id = "crop-test",
        Name = "Kiểm thử crop",
        Canvas = new TemplateCanvas { WidthPixels = 220, HeightPixels = 100, Dpi = 100, BackgroundColor = "#FFFFFF" },
        Slots =
        [
            new TemplateSlot { Id = "a", PhotoIndex = 0, X = 0, Y = 0, Width = 100, Height = 100, CornerRadius = cornerRadius },
            new TemplateSlot { Id = "b", PhotoIndex = 0, X = 120, Y = 0, Width = 100, Height = 100, CornerRadius = cornerRadius },
        ],
    };

    private async Task<string> ComposeAsync(PhotoTemplate template, bool mirror, params SlotCrop?[]? crops)
    {
        var directory = _paths.GetOriginalsDirectory(_sessionId);
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(original, TestImages.CreateQuadrants(400, 400, SKEncodedImageFormat.Png));
        var request = new CompositionRequest(_sessionId, template, [new CompositionPhoto(original, mirror)], ColorFilter.None, DateTimeOffset.Now, crops is { Length: > 0 } ? crops : null);
        return (await _composer.ComposeAsync(request)).FilePath;
    }

    private static void AssertColor(string file, int x, int y, SKColor expected) =>
        Assert.True(TestImages.IsClose(TestImages.PixelAt(file, x, y), expected, tolerance: 12), $"({x},{y}) = {TestImages.PixelAt(file, x, y)}, mong đợi {expected}");

    [Fact]
    public async Task DefaultCrop_RendersExactlyLikeAutoCrop()
    {
        var template = TwoSlotsSharingPhoto();
        var auto = await ComposeAsync(template, false);
        var explicitAuto = await ComposeAsync(template, false, SlotCrop.AutoFor(template.Slots[0]), SlotCrop.AutoFor(template.Slots[1]));
        var noCrops = await ComposeAsync(template, false, null, null);

        using var a = SKBitmap.Decode(auto);
        using var b = SKBitmap.Decode(explicitAuto);
        using var c = SKBitmap.Decode(noCrops);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Equal(a.Bytes, c.Bytes);
    }

    [Fact]
    public async Task TwoSlotsSharingPhoto_CanBeCroppedIndependently()
    {
        // Ô b phóng 200% vào góc phải dưới (vàng); ô a giữ Auto Crop (đủ 4 góc màu).
        var file = await ComposeAsync(TwoSlotsSharingPhoto(), false, null, new SlotCrop(2, 0.75, 0.75));

        AssertColor(file, 10, 10, TestImages.TopLeft);
        AssertColor(file, 90, 10, TestImages.TopRight);
        AssertColor(file, 10, 90, TestImages.BottomLeft);
        AssertColor(file, 90, 90, TestImages.BottomRight);

        AssertColor(file, 130, 10, TestImages.BottomRight);
        AssertColor(file, 210, 90, TestImages.BottomRight);
        AssertColor(file, 110, 50, White);
    }

    [Theory]
    [InlineData(0.25, 0.25, 0)]
    [InlineData(0.75, 0.25, 1)]
    [InlineData(0.25, 0.75, 2)]
    [InlineData(0.75, 0.75, 3)]
    public async Task ZoomedCrop_ShowsQuadrantOfFocalPoint(double fx, double fy, int quadrant)
    {
        SKColor[] colors = [TestImages.TopLeft, TestImages.TopRight, TestImages.BottomLeft, TestImages.BottomRight];

        var file = await ComposeAsync(TwoSlotsSharingPhoto(), false, new SlotCrop(2, fx, fy), null);

        AssertColor(file, 5, 5, colors[quadrant]);
        AssertColor(file, 95, 95, colors[quadrant]);
    }

    [Fact]
    public async Task MirroredPhoto_CropFollowsViewedOrientation_WithoutDoubleFlip()
    {
        // Ảnh hiển thị đã lật: góc trái trên mà khách thấy là góc phải trên của ảnh thô (xanh lá).
        var file = await ComposeAsync(TwoSlotsSharingPhoto(), true, new SlotCrop(2, 0.25, 0.25), null);

        AssertColor(file, 50, 50, TestImages.TopRight);

        // Ô không chỉnh vẫn lật đúng như trước: trái trên = xanh lá, phải trên = đỏ.
        AssertColor(file, 130, 10, TestImages.TopRight);
        AssertColor(file, 210, 10, TestImages.TopLeft);
    }

    [Fact]
    public async Task CroppedSlot_KeepsCornerRadiusClipping()
    {
        var file = await ComposeAsync(TwoSlotsSharingPhoto(cornerRadius: 40), false, new SlotCrop(3, 0.5, 0.5), null);

        AssertColor(file, 1, 1, White);
        AssertColor(file, 50, 50, TestImages.BottomRight);
    }

    [Fact]
    public async Task InvalidCrops_AreRejectedWithClearError()
    {
        var template = TwoSlotsSharingPhoto();

        await Assert.ThrowsAsync<CompositionException>(() => ComposeAsync(template, false, new SlotCrop(1.5, 0.5, 0.5)));
        await Assert.ThrowsAsync<CompositionException>(() => ComposeAsync(template, false, new SlotCrop(9, 0.5, 0.5), null));

        var contain = template with { Slots = [template.Slots[0] with { Fit = ImageFit.Contain }, template.Slots[1]] };
        var ex = await Assert.ThrowsAsync<CompositionException>(() => ComposeAsync(contain, false, new SlotCrop(1.5, 0.5, 0.5), null));
        Assert.Contains("contain", ex.Message, StringComparison.Ordinal);
    }
}
