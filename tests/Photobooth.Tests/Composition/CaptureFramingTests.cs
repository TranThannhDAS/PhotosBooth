using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Composition;

/// <summary>Camera Framing Overlay: vùng khung hình camera sẽ nằm trong ô ảnh (cùng quy tắc với Photo Composer).</summary>
public sealed class CaptureFramingTests : IDisposable
{
    private const int Precision = 6;
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static TemplateSlot Slot(int width, int height, double focalX = 0.5, double focalY = 0.5, ImageFit fit = ImageFit.Cover, int photoIndex = 0) =>
        new() { PhotoIndex = photoIndex, X = 0, Y = 0, Width = width, Height = height, Fit = fit, FocalPoint = new FocalPoint { X = focalX, Y = focalY } };

    private static PhotoTemplate Template(params TemplateSlot[] slots) => new()
    {
        Id = "framing",
        Name = "Khung",
        Canvas = new TemplateCanvas { WidthPixels = 2000, HeightPixels = 2000, Dpi = 300, BackgroundColor = "#FFFFFF" },
        Slots = slots,
    };

    private static void AssertGuide(FramingGuide? guide, double x, double y, double width, double height)
    {
        Assert.NotNull(guide);
        Assert.False(guide.ShowsWholeImage);
        Assert.Equal(x, guide.X, Precision);
        Assert.Equal(y, guide.Y, Precision);
        Assert.Equal(width, guide.Width, Precision);
        Assert.Equal(height, guide.Height, Precision);
    }

    [Fact]
    public void Camera3x2_Slot3x2_KeepsWholeImage()
    {
        // Canon JPEG 3:2 khớp đúng tỷ lệ ô 3:2 → không cắt phần nào.
        AssertGuide(CaptureFraming.Compute(Template(Slot(1200, 800)), 0, 6000, 4000, false),
            0, 0, 1, 1);
    }

    [Fact]
    public void Camera3x2_PortraitSlot3x4_KeepsCenteredHalfWidth()
    {
        // Nguồn 6000x4000, ô dọc 3:4 → chỉ giữ vùng 3000x4000 ở giữa.
        // Không được đánh dấu 6000px chiều ngang là ảnh thành phẩm.
        AssertGuide(CaptureFraming.Compute(Template(Slot(900, 1200)), 0, 6000, 4000, false),
            0.25, 0, 0.5, 1);
    }

    [Fact]
    public void Camera16x9_Slot4x3_KeepsCentered1440x1080() =>
        // Vùng cơ sở 1440×1080 giữa ảnh 1920×1080.
        AssertGuide(CaptureFraming.Compute(Template(Slot(1080, 810)), 0, 1920, 1080, mirrored: false), 240.0 / 1920, 0, 1440.0 / 1920, 1);

    [Fact]
    public void Camera16x9_Slot1x1() =>
        AssertGuide(CaptureFraming.Compute(Template(Slot(1000, 1000)), 0, 1920, 1080, false), 420.0 / 1920, 0, 1080.0 / 1920, 1);

    [Fact]
    public void Camera4x3_Slot4x3_KeepsWholeFrame() =>
        AssertGuide(CaptureFraming.Compute(Template(Slot(1080, 810)), 0, 1280, 960, false), 0, 0, 1, 1);

    [Fact]
    public void Camera16x9_Slot3x4() =>
        AssertGuide(CaptureFraming.Compute(Template(Slot(810, 1080)), 0, 1920, 1080, false), 555.0 / 1920, 0, 810.0 / 1920, 1);

    [Fact]
    public void FocalY_HasNoEffect_WithoutVerticalSlack_ButMovesWindowWhenThereIs()
    {
        // 16:9 → ô 4:3: không có phần dư theo chiều dọc, focal y = 0.4 không đổi gì.
        AssertGuide(CaptureFraming.Compute(Template(Slot(1080, 810, 0.5, 0.4)), 0, 1920, 1080, false), 240.0 / 1920, 0, 0.75, 1);

        // 4:3 → ô 16:9: vùng 1280×720, tâm y = 0.4 × 960 = 384 → y = 24.
        AssertGuide(CaptureFraming.Compute(Template(Slot(1920, 1080, 0.5, 0.4)), 0, 1280, 960, false), 0, 24.0 / 960, 1, 0.75);
    }

    [Fact]
    public void FocalX_Quarter_MovesWindowLeft_AndClampsInsideFrame()
    {
        // Ô 3:4: vùng 810 px, tâm x = 480 → x = 75.
        AssertGuide(CaptureFraming.Compute(Template(Slot(810, 1080, 0.25)), 0, 1920, 1080, false), 75.0 / 1920, 0, 810.0 / 1920, 1);

        // Ô 4:3: tâm 480 − 720 < 0 → kẹp sát mép trái, không dịch khi không còn phần dư.
        AssertGuide(CaptureFraming.Compute(Template(Slot(1080, 810, 0.25)), 0, 1920, 1080, false), 0, 0, 0.75, 1);
    }

    [Fact]
    public void Mirror_GuideFollowsViewedOrientation_WithoutDoubleFlip()
    {
        var template = Template(Slot(810, 1080, 0.25));

        var plain = CaptureFraming.Compute(template, 0, 1920, 1080, mirrored: false)!;
        var mirrored = CaptureFraming.Compute(template, 0, 1920, 1080, mirrored: true)!;

        // focalPoint.x = 0.25 luôn là phía trái của hình khách đang nhìn.
        AssertGuide(mirrored, plain.X, plain.Y, plain.Width, plain.Height);

        // Trên ảnh thô (chưa lật) mà Composer dùng, vùng đó nằm ở phía đối diện.
        var raw = CropCalculator.CoverSourceRect(1920, 1080, template.Slots[0], null, mirror: true);
        Assert.Equal(1 - mirrored.X - mirrored.Width, raw.X / 1920, Precision);
    }

    [Fact]
    public void ContainSlot_ShowsWholeImage_InsteadOfFakeCrop()
    {
        var guide = CaptureFraming.Compute(Template(Slot(1080, 1440, fit: ImageFit.Contain)), 0, 1920, 1080, false);

        Assert.NotNull(guide);
        Assert.True(guide.ShowsWholeImage);
        Assert.Equal((0d, 0d, 1d, 1d), (guide.X, guide.Y, guide.Width, guide.Height));
    }

    [Fact]
    public void GuideFollowsPhotoIndex()
    {
        var template = Template(Slot(1000, 1000, photoIndex: 0), Slot(810, 1080, photoIndex: 1), Slot(1080, 810, fit: ImageFit.Contain, photoIndex: 2));

        Assert.Equal(0, CaptureFraming.Compute(template, 0, 1920, 1080, false)!.SlotIndex);
        AssertGuide(CaptureFraming.Compute(template, 1, 1920, 1080, false), 555.0 / 1920, 0, 810.0 / 1920, 1);
        Assert.True(CaptureFraming.Compute(template, 2, 1920, 1080, false)!.ShowsWholeImage);
        Assert.Null(CaptureFraming.Compute(template, 3, 1920, 1080, false));
    }

    [Fact]
    public void SharedPhotoIndex_UsesLargestSlot_TieBreaksByDeclarationOrder()
    {
        Assert.Equal(1, CaptureFraming.GuideSlotIndex(Template(Slot(400, 300), Slot(1000, 1000), Slot(500, 500)), 0));
        Assert.Equal(0, CaptureFraming.GuideSlotIndex(Template(Slot(600, 400), Slot(400, 600)), 0));
    }

    [Fact]
    public void InvalidFrameSize_ReturnsNoGuide()
    {
        Assert.Null(CaptureFraming.Compute(Template(Slot(100, 100)), 0, 0, 1080, false));
        Assert.Null(CaptureFraming.Compute(Template(Slot(100, 100)), 0, 1920, -1, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guide_MatchesWhatPhotoComposerRenders(bool mirror)
    {
        // Ảnh 4 góc phần tư 640×480, ô 1:1 với focal x = 0.25.
        var paths = new PhotoboothStoragePaths(_temp.Path);
        var composer = new SkiaPhotoComposer(paths, NullLogger<SkiaPhotoComposer>.Instance);
        var sessionId = Guid.NewGuid();
        var original = Path.Combine(_temp.Path, "frame.png");
        await File.WriteAllBytesAsync(original, TestImages.CreateQuadrants(640, 480, SKEncodedImageFormat.Png));
        var template = new PhotoTemplate
        {
            Id = "framing-check",
            Name = "Đối chiếu",
            Canvas = new TemplateCanvas { WidthPixels = 300, HeightPixels = 300, Dpi = 100, BackgroundColor = "#FFFFFF" },
            Slots = [Slot(300, 300, focalX: 0.25)],
        };

        var guide = CaptureFraming.Compute(template, 0, 640, 480, mirror)!;
        var composed = await composer.ComposeAsync(new CompositionRequest(sessionId, template, [new CompositionPhoto(original, mirror)], ColorFilter.None, DateTimeOffset.Now));

        // Mỗi điểm trong ô thành phẩm phải là đúng điểm tương ứng trong khung hướng dẫn trên hình khách đang nhìn.
        foreach (var u in new[] { 0.1, 0.45, 0.6, 0.9 })
        {
            foreach (var v in new[] { 0.25, 0.75 })
            {
                var viewedX = guide.X + (u * guide.Width);
                var viewedY = guide.Y + (v * guide.Height);
                var rawX = mirror ? 1 - viewedX : viewedX;
                var expected = (rawX < 0.5, viewedY < 0.5) switch
                {
                    (true, true) => TestImages.TopLeft,
                    (false, true) => TestImages.TopRight,
                    (true, false) => TestImages.BottomLeft,
                    _ => TestImages.BottomRight,
                };

                var actual = TestImages.PixelAt(composed.FilePath, (int)(u * 300), (int)(v * 300));
                Assert.True(TestImages.IsClose(actual, expected, 12), $"u={u}, v={v}, mirror={mirror}: {actual} ≠ {expected}");
            }
        }
    }
}
