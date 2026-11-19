using Photobooth.Application.Sessions;

namespace Photobooth.Tests.Sessions;

public class PhotoFileNamingTests
{
    [Fact]
    public void SuggestBaseFileName_UsesLocalTime()
    {
        var vietnamTime = TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");

        var name = PhotoFileNaming.SuggestBaseFileName(new DateTimeOffset(2026, 11, 19, 3, 5, 12, TimeSpan.Zero), vietnamTime);

        Assert.Equal("Photobooth_20261119_100512", name);
    }
}
