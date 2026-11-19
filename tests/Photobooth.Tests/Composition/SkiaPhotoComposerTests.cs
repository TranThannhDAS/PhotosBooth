using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Composition;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Composition;

public sealed class SkiaPhotoComposerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly SkiaPhotoComposer _composer;
    private readonly Guid _sessionId = Guid.NewGuid();

    public SkiaPhotoComposerTests()
    {
        _paths = new PhotoboothStoragePaths(_temp.Path);
        _composer = new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance);
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

    private CompositionRequest Request(bool mirror, ColorFilter filter = ColorFilter.None)
    {
        var original = WriteOriginal(TestImages.CreateSplitJpeg());
        return new CompositionRequest(
            _sessionId,
            TestTemplates.TwoSlots(),
            [new CompositionPhoto(original, mirror), new CompositionPhoto(original, mirror)],
            filter,
            new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(7)));
    }

    [Fact]
    public async Task ComposeAsync_WritesNewPngWithCanvasSizeInOutputs()
    {
        var result = await _composer.ComposeAsync(Request(mirror: false));

        Assert.StartsWith(_paths.GetOutputsDirectory(_sessionId), result.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((400, 300), (result.WidthPixels, result.HeightPixels));
        Assert.Equal((400, 300), TestImages.SizeOf(await File.ReadAllBytesAsync(result.FilePath)));
    }

    [Fact]
    public async Task ComposeAsync_WithoutMirror_KeepsCameraOrientation()
    {
        var result = await _composer.ComposeAsync(Request(mirror: false));

        // Ô #1: x 10..190. Bên trái ô phải là nửa trái ảnh gốc (đỏ).
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 30, 100), TestImages.Left));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 170, 100), TestImages.Right));
    }

    [Fact]
    public async Task ComposeAsync_WithMirror_FlipsPhotoOnlyNotDecorations()
    {
        var result = await _composer.ComposeAsync(Request(mirror: true));

        // Ảnh được lật như gương: bên trái ô giờ là màu xanh.
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 30, 100), TestImages.Right));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 170, 100), TestImages.Left));

        // Khối trang trí ở góc trái dưới không bị lật.
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 30, 270), new SKColor(0, 255, 0), 5));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 370, 270), SKColors.White, 5));
    }

    [Fact]
    public async Task ComposeAsync_CenterCropsWithoutStretching()
    {
        // Ảnh 640×480 (4:3) vào ô vuông 180×180: cắt hai bên, ranh giới đỏ/xanh vẫn ở giữa ô.
        var result = await _composer.ComposeAsync(Request(mirror: false));

        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 95, 100), TestImages.Left));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(result.FilePath, 105, 100), TestImages.Right));
    }

    [Fact]
    public async Task ComposeAsync_GrayscaleFilter_RemovesColorFromPhotos()
    {
        var result = await _composer.ComposeAsync(Request(mirror: false, ColorFilter.Grayscale));

        var pixel = TestImages.PixelAt(result.FilePath, 30, 100);
        Assert.True(Math.Abs(pixel.Red - pixel.Green) <= 3 && Math.Abs(pixel.Green - pixel.Blue) <= 3);
    }

    [Fact]
    public async Task ComposeAsync_DoesNotModifyOriginals()
    {
        var request = Request(mirror: true);
        var before = await File.ReadAllBytesAsync(request.Photos[0].FilePath);

        await _composer.ComposeAsync(request);
        var second = await _composer.ComposeAsync(request);

        Assert.Equal(before, await File.ReadAllBytesAsync(request.Photos[0].FilePath));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(second.FilePath)!, "composed-*.png").Length);
    }

    [Fact]
    public async Task ComposeAsync_WrongPhotoCount_Throws()
    {
        var request = Request(mirror: false) with { Photos = [new CompositionPhoto(WriteOriginal(TestImages.CreateSplitJpeg()), false)] };

        await Assert.ThrowsAsync<CompositionException>(() => _composer.ComposeAsync(request));
    }

    [Fact]
    public async Task ComposeAsync_CorruptOriginal_ThrowsCompositionException()
    {
        var corrupt = WriteOriginal(TestJpeg.CreateHeaderOnly(), "corrupt.jpg");
        var request = Request(mirror: false) with { Photos = [new CompositionPhoto(corrupt, false), new CompositionPhoto(corrupt, false)] };

        await Assert.ThrowsAsync<CompositionException>(() => _composer.ComposeAsync(request));
    }

    [Fact]
    public async Task ComposeAsync_PhotoOutsideDataRoot_IsRejected()
    {
        using var other = new TempDirectory();
        var outside = Path.Combine(other.Path, "x.jpg");
        await File.WriteAllBytesAsync(outside, TestImages.CreateSplitJpeg());
        var request = Request(mirror: false) with { Photos = [new CompositionPhoto(outside, false), new CompositionPhoto(outside, false)] };

        await Assert.ThrowsAsync<CompositionException>(() => _composer.ComposeAsync(request));
    }

    [Fact]
    public async Task RenderTemplatePreviewAsync_ScalesToMaxWidth()
    {
        var jpeg = await _composer.RenderTemplatePreviewAsync(TestTemplates.FourSlots(), 300);

        Assert.Equal((300, 450), TestImages.SizeOf(jpeg));
    }

    [Fact]
    public async Task ShippedTemplates_ComposeAtFourBySixInches()
    {
        var catalog = new Photobooth.Infrastructure.Templates.JsonTemplateCatalog(
            TestTemplates.ShippedTemplatesDirectory, NullLogger<Photobooth.Infrastructure.Templates.JsonTemplateCatalog>.Instance);
        var original = WriteOriginal(TestImages.CreateSplitJpeg(1280, 720));

        foreach (var template in await catalog.GetTemplatesAsync())
        {
            var photos = Enumerable.Range(0, template.RequiredPhotoCount).Select(_ => new CompositionPhoto(original, true)).ToArray();
            var result = await _composer.ComposeAsync(new CompositionRequest(_sessionId, template, photos, ColorFilter.Sepia, DateTimeOffset.Now));

            Assert.Equal((1200, 1800), TestImages.SizeOf(await File.ReadAllBytesAsync(result.FilePath)));
            Assert.Equal((288d, 432d), (template.Canvas.PageWidthPoints, template.Canvas.PageHeightPoints));
        }
    }
}

public sealed class SkiaImagePreviewRendererTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderPreviewAsync_AppliesMirrorExactlyOnce(bool mirror)
    {
        var paths = new PhotoboothStoragePaths(_temp.Path);
        var file = Path.Combine(_temp.Path, "photo.jpg");
        await File.WriteAllBytesAsync(file, TestImages.CreateSplitJpeg());

        var preview = await new SkiaImagePreviewRenderer(paths).RenderPreviewAsync(file, mirror, 320);

        Assert.Equal((320, 240), TestImages.SizeOf(preview));
        var leftPixel = TestImages.PixelAt(preview, 40, 120);
        Assert.True(TestImages.IsClose(leftPixel, mirror ? TestImages.Right : TestImages.Left));
    }

    [Fact]
    public async Task RenderPreviewAsync_OutsideDataRoot_IsRejected()
    {
        using var other = new TempDirectory();
        var outside = Path.Combine(other.Path, "x.jpg");
        await File.WriteAllBytesAsync(outside, TestImages.CreateSplitJpeg());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new SkiaImagePreviewRenderer(new PhotoboothStoragePaths(_temp.Path)).RenderPreviewAsync(outside, false, 100));
    }
}
