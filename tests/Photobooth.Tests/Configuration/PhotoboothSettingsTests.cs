using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Infrastructure.Configuration;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Configuration;

public sealed class PhotoboothSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsFile => Path.Combine(_temp.Path, "settings.json");

    [Fact]
    public void Load_MissingFile_UsesDefaults()
    {
        var settings = PhotoboothSettings.Load(SettingsFile, NullLogger.Instance);

        Assert.Equal(10, settings.Capture.MaxPhotosPerSession);
        Assert.Equal(3, settings.Capture.CountdownSeconds);
        Assert.True(settings.Capture.MirrorByDefault);
        Assert.Equal(92, settings.Export.JpegQuality);
    }

    [Fact]
    public void Load_ValidFile_AppliesValues()
    {
        File.WriteAllText(SettingsFile, """{ "capture": { "maxPhotosPerSession": 6, "countdownSeconds": 5, "mirrorByDefault": false }, "export": { "jpegQuality": 80 } }""");

        var settings = PhotoboothSettings.Load(SettingsFile, NullLogger.Instance);

        Assert.Equal((6, 5, false, 80), (settings.Capture.MaxPhotosPerSession, settings.Capture.CountdownSeconds, settings.Capture.MirrorByDefault, settings.Export.JpegQuality));
    }

    [Fact]
    public void Load_LegacyPhotosPerSession_IsIgnored_PhotoCountComesFromTemplate()
    {
        File.WriteAllText(SettingsFile, """{ "capture": { "photosPerSession": 3, "countdownSeconds": 5 } }""");

        var settings = PhotoboothSettings.Load(SettingsFile, NullLogger.Instance);

        Assert.Equal((10, 5),(settings.Capture.MaxPhotosPerSession, settings.Capture.CountdownSeconds));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{ "capture": { "countdownSeconds": 0 } }""")]
    [InlineData("""{ "export": { "jpegQuality": 150 } }""")]
    [InlineData("""{ "export": { "defaultFolder": "relative\\folder" } }""")]
    public void Load_InvalidFile_FallsBackToDefaults(string content)
    {
        File.WriteAllText(SettingsFile, content);

        var settings = PhotoboothSettings.Load(SettingsFile, NullLogger.Instance);

        Assert.Equal(3, settings.Capture.CountdownSeconds);
        Assert.Equal(92, settings.Export.JpegQuality);
    }
}
