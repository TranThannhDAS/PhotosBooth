using Photobooth.Application.Photos;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Photos;

public class JpegImageInfoTests
{
    [Theory]
    [InlineData(640, 480)]
    [InlineData(1920, 1080)]
    [InlineData(1, 65535)]
    public void TryReadDimensions_ValidJpeg_ReturnsSize(int width, int height)
    {
        var ok = JpegImageInfo.TryReadDimensions(TestJpeg.CreateHeaderOnly(width, height), out var w, out var h);

        Assert.True(ok);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Fact]
    public void TryReadDimensions_NotJpeg_ReturnsFalse()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.False(JpegImageInfo.TryReadDimensions(png, out _, out _));
    }

    [Fact]
    public void TryReadDimensions_EmptyOrTruncated_ReturnsFalse()
    {
        var jpeg = TestJpeg.CreateHeaderOnly();

        Assert.False(JpegImageInfo.TryReadDimensions([], out _, out _));
        Assert.False(JpegImageInfo.TryReadDimensions(jpeg.AsSpan(0, 24), out _, out _));
    }

    [Fact]
    public void TryReadDimensions_ZeroSize_ReturnsFalse()
    {
        Assert.False(JpegImageInfo.TryReadDimensions(TestJpeg.CreateHeaderOnly(0, 480), out _, out _));
    }
}
