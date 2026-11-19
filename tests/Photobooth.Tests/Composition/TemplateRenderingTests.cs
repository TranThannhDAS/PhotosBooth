using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Composition;

/// <summary>
/// Kiểm tra kết quả render của Photo Composer bằng cách đọc màu tại các điểm xác định
/// trên ảnh mẫu cố định (không cần webcam).
/// </summary>
public sealed class TemplateRenderingTests : IDisposable
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Green = new(0, 255, 0);
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(7));

    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly SkiaPhotoComposer _composer;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly string _assetDirectory;

    public TemplateRenderingTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "data"));
        _composer = new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance);
        _assetDirectory = Path.Combine(_temp.Path, "template");
        Directory.CreateDirectory(_assetDirectory);
    }

    public void Dispose() => _temp.Dispose();

    private static PhotoTemplate Template(IReadOnlyList<TemplateSlot> slots, IReadOnlyList<TemplateDecoration>? decorations = null, int width = 200, int height = 200) => new()
    {
        Id = "render-test",
        Name = "Render",
        Canvas = new TemplateCanvas { WidthPixels = width, HeightPixels = height, Dpi = 100, BackgroundColor = "#FFFFFF" },
        Slots = slots,
        Decorations = decorations ?? [],
    };

    private static TemplateSlot Slot(int x = 0, int y = 0, int width = 100, int height = 100, int photoIndex = 0) =>
        new() { PhotoIndex = photoIndex, X = x, Y = y, Width = width, Height = height };

    private string Original(byte[] data)
    {
        var directory = _paths.GetOriginalsDirectory(_sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, data);
        return path;
    }

    private string Asset(string name, byte[] data)
    {
        var path = Path.Combine(_assetDirectory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private async Task<string> ComposeAsync(PhotoTemplate template, IReadOnlyList<CompositionPhoto> photos, DateTimeOffset? timestamp = null)
    {
        var result = await _composer.ComposeAsync(new CompositionRequest(
            _sessionId, template with { AssetDirectory = _assetDirectory }, photos, ColorFilter.None, timestamp ?? Timestamp));
        return result.FilePath;
    }

    private Task<string> ComposeAsync(PhotoTemplate template, byte[] photo, bool mirror = false) =>
        ComposeAsync(template, [new CompositionPhoto(Original(photo), mirror)]);

    private static SKColor Pixel(string file, int x, int y) => TestImages.PixelAt(file, x, y);

    // ------------------------------------------------------------ Fit / focal point

    [Fact]
    public async Task Contain_ShowsWholePhotoCenteredOnSlotBackground()
    {
        // Ảnh 4:3 vào ô 400×100: ảnh vừa 133×100 ở giữa, hai bên là nền xanh lá của ô.
        var slot = Slot(width: 400, height: 100) with { Fit = ImageFit.Contain, BackgroundColor = "#00FF00" };

        var file = await ComposeAsync(Template([slot], width: 400), TestImages.CreateSplitJpeg());

        Assert.True(TestImages.IsClose(Pixel(file, 20, 50), Green, 5));
        Assert.True(TestImages.IsClose(Pixel(file, 380, 50), Green, 5));
        Assert.True(TestImages.IsClose(Pixel(file, 150, 50), TestImages.Left));
        Assert.True(TestImages.IsClose(Pixel(file, 250, 50), TestImages.Right));
        Assert.True(TestImages.IsClose(Pixel(file, 200, 150), SKColors.White, 5));
    }

    [Theory]
    [InlineData(0.0, 50, false)] // cắt sát mép trái → giữa ô là phần đỏ
    [InlineData(1.0, 50, true)]  // cắt sát mép phải → giữa ô là phần xanh
    public async Task Cover_FocalPointSelectsKeptRegion(double focalX, int probeX, bool expectRight)
    {
        var slot = Slot() with { FocalPoint = new FocalPoint { X = focalX, Y = 0.5 } };

        var file = await ComposeAsync(Template([slot]), TestImages.CreateSplitJpeg());

        Assert.True(TestImages.IsClose(Pixel(file, probeX, 50), expectRight ? TestImages.Right : TestImages.Left));
    }

    [Fact]
    public async Task Cover_FocalPointIsInDisplayedOrientation_WhenMirrored()
    {
        // Ảnh thô (đỏ|xanh) lật gương phải cho kết quả giống hệt ảnh (xanh|đỏ) không lật, với cùng focal point.
        var slot = Slot() with { FocalPoint = new FocalPoint { X = 0, Y = 0.5 } };
        var mirrored = await ComposeAsync(Template([slot]), TestImages.CreateSplitJpeg(), mirror: true);
        var preFlipped = await ComposeAsync(Template([slot]), TestImages.CreateSplitJpeg(leftColor: TestImages.Right, rightColor: TestImages.Left));

        foreach (var x in new[] { 10, 60, 90 })
        {
            Assert.True(TestImages.IsClose(Pixel(mirrored, x, 50), Pixel(preFlipped, x, 50), 30), $"x={x}");
        }
    }

    // ------------------------------------------------------------ Bo góc

    [Fact]
    public async Task CornerRadius_ClipsOnlyThatSlot()
    {
        var photo = TestImages.CreateSolid(400, 300, Red, SKEncodedImageFormat.Jpeg);
        var original = Original(photo);
        var template = Template([Slot() with { CornerRadius = 40 }, Slot(x: 100) with { PhotoIndex = 0 }]);

        var file = await ComposeAsync(template, [new CompositionPhoto(original, false)]);

        Assert.True(TestImages.IsClose(Pixel(file, 2, 2), SKColors.White, 5));    // góc bo: nền canvas
        Assert.True(TestImages.IsClose(Pixel(file, 97, 97), SKColors.White, 5));
        Assert.True(TestImages.IsClose(Pixel(file, 50, 50), Red));                 // giữa ô: ảnh
        Assert.True(TestImages.IsClose(Pixel(file, 102, 2), Red));                 // ô bên cạnh không bị bo
    }

    // ------------------------------------------------------------ Layer / zIndex

    [Theory]
    [InlineData(5, true)]
    [InlineData(-5, false)]
    public async Task ZIndex_DecidesWhatIsOnTop(int rectangleZIndex, bool rectangleOnTop)
    {
        var rectangle = new TemplateDecoration { Type = DecorationKind.Rectangle, X = 0, Y = 0, Width = 200, Height = 200, Color = "#00FF00", ZIndex = rectangleZIndex };

        var file = await ComposeAsync(Template([Slot()], [rectangle]), TestImages.CreateSolid(100, 100, Blue, SKEncodedImageFormat.Jpeg));

        Assert.True(TestImages.IsClose(Pixel(file, 50, 50), rectangleOnTop ? Green : Blue, 10));
        Assert.True(TestImages.IsClose(Pixel(file, 150, 150), Green, 5));
    }

    // ------------------------------------------------------------ Ảnh trang trí PNG

    [Fact]
    public async Task PngOverlay_KeepsTransparency_AndBlendsSemiTransparentPixels()
    {
        var asset = Asset("assets/overlay.png", TestImages.CreateHalfTransparentPng(100, 100, new SKColor(0, 0, 255, 128)));
        var assetHash = SHA256.HashData(await File.ReadAllBytesAsync(asset));
        var overlay = new TemplateDecoration { Type = DecorationKind.Image, AssetPath = "assets/overlay.png", X = 0, Y = 0, Width = 100, Height = 100, ZIndex = 10 };

        var file = await ComposeAsync(Template([Slot()], [overlay]), TestImages.CreateSolid(100, 100, Red, SKEncodedImageFormat.Jpeg));

        Assert.True(TestImages.IsClose(Pixel(file, 20, 50), Red, 10));                       // nửa trong suốt: thấy ảnh
        Assert.True(TestImages.IsClose(Pixel(file, 80, 50), new SKColor(127, 0, 128), 20));   // 50% xanh trộn đỏ
        Assert.Equal(assetHash, SHA256.HashData(await File.ReadAllBytesAsync(asset)));        // asset không bị sửa
    }

    [Fact]
    public async Task ImageDecoration_Contain_KeepsAspectRatio()
    {
        Asset("logo.png", TestImages.CreateSolid(100, 50, Green));
        var logo = new TemplateDecoration { Type = DecorationKind.Image, AssetPath = "logo.png", X = 100, Y = 100, Width = 100, Height = 100 };

        var file = await ComposeAsync(Template([Slot()], [logo]), TestImages.CreateSplitJpeg());

        Assert.True(TestImages.IsClose(Pixel(file, 150, 150), Green, 5));          // logo 100×50 ở giữa khung
        Assert.True(TestImages.IsClose(Pixel(file, 150, 110), SKColors.White, 5)); // trên/dưới logo không bị kéo giãn
    }

    [Fact]
    public async Task MissingAsset_ThrowsCompositionException()
    {
        var missing = new TemplateDecoration { Type = DecorationKind.Image, AssetPath = "assets/none.png", Width = 10, Height = 10 };

        var ex = await Assert.ThrowsAsync<CompositionException>(() => ComposeAsync(Template([Slot()], [missing]), TestImages.CreateSplitJpeg()));

        Assert.Contains("assets/none.png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptAsset_ThrowsCompositionException()
    {
        Asset("bad.png", [1, 2, 3]);
        var bad = new TemplateDecoration { Type = DecorationKind.Image, AssetPath = "bad.png", Width = 10, Height = 10 };

        await Assert.ThrowsAsync<CompositionException>(() => ComposeAsync(Template([Slot()], [bad]), TestImages.CreateSplitJpeg()));
    }

    [Fact]
    public void AssetResolver_RejectsPathsOutsideTemplateFolder()
    {
        File.WriteAllBytes(Path.Combine(_temp.Path, "outside.png"), TestImages.CreateSolid(5, 5, Red));

        Assert.False(TemplateAssetResolver.TryResolve(_assetDirectory, "../outside.png", out _, out _));
        Assert.False(TemplateAssetResolver.TryResolve(_assetDirectory, Path.Combine(_temp.Path, "outside.png"), out _, out _));
        Assert.False(TemplateAssetResolver.TryResolve(_assetDirectory, "missing.png", out _, out var error));
        Assert.Contains("missing.png", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ Chữ và placeholder

    [Fact]
    public async Task TextDecoration_DrawsInsideItsBox_AndRespectsZIndex()
    {
        var text = new TemplateDecoration { Type = DecorationKind.Text, Text = "MMMM", X = 0, Y = 120, Width = 200, Height = 60, FontSizePixels = 50, Bold = true, Color = "#000000", ZIndex = 5 };
        var cover = new TemplateDecoration { Type = DecorationKind.Rectangle, X = 0, Y = 110, Width = 200, Height = 90, Color = "#FFFFFF" };

        var visible = await ComposeAsync(Template([Slot()], [text]), TestImages.CreateSplitJpeg());
        var hidden = await ComposeAsync(Template([Slot()], [text, cover with { ZIndex = 10 }]), TestImages.CreateSplitJpeg());

        Assert.True(CountDark(visible, 0, 120, 200, 60) > 200);
        Assert.Equal(0, CountDark(visible, 0, 185, 200, 15));
        Assert.Equal(0, CountDark(hidden, 0, 120, 200, 60));
    }

    [Fact]
    public async Task LongText_IsShrunkToFitBoxWidth()
    {
        var text = new TemplateDecoration { Type = DecorationKind.Text, Text = "Một dòng chữ rất dài vượt khung", X = 50, Y = 120, Width = 100, Height = 60, FontSizePixels = 40, Color = "#000000" };

        var file = await ComposeAsync(Template([Slot()], [text]), TestImages.CreateSplitJpeg());

        Assert.True(CountDark(file, 50, 120, 100, 60) > 0);
        Assert.Equal(0, CountDark(file, 0, 120, 45, 60));
        Assert.Equal(0, CountDark(file, 155, 120, 45, 60));
    }

    [Fact]
    public async Task Placeholders_UseTheTimestampFromRequest_NotTheSystemClock()
    {
        var text = new TemplateDecoration { Type = DecorationKind.Text, Text = "{time}", X = 0, Y = 120, Width = 200, Height = 60, FontSizePixels = 40, Color = "#000000" };
        var template = Template([Slot()], [text]);
        var photo = Original(TestImages.CreateSplitJpeg());

        var tenA = await ComposeAsync(template, [new CompositionPhoto(photo, false)], Timestamp);
        var tenB = await ComposeAsync(template, [new CompositionPhoto(photo, false)], Timestamp);
        var eleven = await ComposeAsync(template, [new CompositionPhoto(photo, false)], Timestamp.AddHours(1));

        Assert.Equal(await File.ReadAllBytesAsync(tenA), await File.ReadAllBytesAsync(tenB));
        Assert.NotEqual(await File.ReadAllBytesAsync(tenA), await File.ReadAllBytesAsync(eleven));
    }

    // ------------------------------------------------------------ Ảnh nguồn

    [Fact]
    public async Task SlotReferencingMissingPhoto_ThrowsWithSlotInfo()
    {
        var template = Template([Slot() with { Id = "a" }, Slot(x: 100, photoIndex: 1) with { Id = "b" }]);

        var ex = await Assert.ThrowsAsync<CompositionException>(() => ComposeAsync(template, TestImages.CreateSplitJpeg()));

        Assert.Contains("(b)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_DoesNotModifyOriginalPhoto()
    {
        var original = Original(TestImages.CreateSplitJpeg());
        var before = SHA256.HashData(await File.ReadAllBytesAsync(original));

        await ComposeAsync(Template([Slot() with { CornerRadius = 30, Fit = ImageFit.Contain }]), [new CompositionPhoto(original, true)]);

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(original)));
    }

    // ------------------------------------------------------------ Template mẫu hoàn chỉnh

    [Fact]
    public async Task ShippedPartyTemplate_LoadsAndRendersOverlay()
    {
        var file = Path.Combine(TestTemplates.ShippedTemplatesDirectory, "party-overlay", "template.json");
        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(file);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Issues));

        var photo = Original(TestImages.CreateSolid(640, 480, Red, SKEncodedImageFormat.Jpeg));
        var photos = Enumerable.Range(0, 4).Select(_ => new CompositionPhoto(photo, true)).ToArray();
        var result = await _composer.ComposeAsync(new CompositionRequest(_sessionId, template!, photos, ColorFilter.None, Timestamp));

        Assert.Equal((1200, 1800), TestImages.SizeOf(await File.ReadAllBytesAsync(result.FilePath)));
        Assert.True(TestImages.IsClose(Pixel(result.FilePath, 330, 580), Red));               // giữa ô ảnh: overlay trong suốt
        Assert.True(TestImages.IsClose(Pixel(result.FilePath, 30, 900), SKColors.White, 10)); // viền trắng của overlay PNG
        Assert.False(TestImages.IsClose(Pixel(result.FilePath, 600, 1000), new SKColor(124, 58, 237), 5)); // nền là ảnh gradient, không phải màu canvas đơn
    }

    private static int CountDark(string file, int x, int y, int width, int height)
    {
        using var bitmap = SKBitmap.Decode(file);
        var count = 0;
        for (var py = y; py < y + height; py++)
        {
            for (var px = x; px < x + width; px++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
