using System.Text.Json;
using System.Text.Json.Serialization;
using Photobooth.Infrastructure.Events;

namespace Photobooth.Tests.Configuration;

public sealed class CaptureGuideSettingsTests
{
    [Fact]
    public void Default_IsDimOutsideWith65PercentAndLabel()
    {
        var settings = new CaptureGuideSettings();

        Assert.Equal(CaptureGuideMode.DimOutside, settings.Mode);
        Assert.Equal(0.65, settings.OutsideOpacity, 6);
        Assert.True(settings.ShowGuideLabel);
    }

    [Fact]
    public void Normalize_InvalidModeOrOpacity_UsesSafeDefaults()
    {
        var settings = new CaptureGuideSettings
        {
            Mode = (CaptureGuideMode)999,
            OutsideOpacity = double.NaN,
            ShowGuideLabel = false,
        };

        var normalized = settings.Normalize();

        Assert.Equal(CaptureGuideMode.DimOutside, normalized.Mode);
        Assert.Equal(0.65, normalized.OutsideOpacity, 6);
        Assert.False(normalized.ShowGuideLabel);
    }

    [Theory]
    [InlineData(-0.1, 0.2)]
    [InlineData(1.5, 0.9)]
    [InlineData(0.7, 0.7)]
    public void Normalize_ClampsOpacity(double source, double expected)
    {
        var settings = new CaptureGuideSettings { OutsideOpacity = source };
        Assert.Equal(expected, settings.Normalize().OutsideOpacity, 6);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeOrUnknownMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureGuideSettings { OutsideOpacity = 1.0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureGuideSettings { Mode = (CaptureGuideMode)100 }.Validate());
    }

    [Fact]
    public void Json_RoundTripsGuideSettings_AndOldStateGetsDefaults()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var state = new AppState
        {
            CaptureGuide = new CaptureGuideSettings
            {
                Mode = CaptureGuideMode.Outline,
                OutsideOpacity = 0.4,
                ShowGuideLabel = false,
            },
        };

        var json = JsonSerializer.Serialize(state, options);
        Assert.Contains("\"captureGuide\"", json);
        Assert.Contains("\"outline\"", json);

        var restored = JsonSerializer.Deserialize<AppState>(json, options);
        Assert.NotNull(restored);
        Assert.Equal(CaptureGuideMode.Outline, restored.CaptureGuide.Mode);
        Assert.Equal(0.4, restored.CaptureGuide.OutsideOpacity, 6);
        Assert.False(restored.CaptureGuide.ShowGuideLabel);

        var oldState = JsonSerializer.Deserialize<AppState>("{\"camera\":{}}", options);
        Assert.NotNull(oldState);
        Assert.Equal(CaptureGuideMode.DimOutside, oldState.CaptureGuide.Mode);
    }
}
