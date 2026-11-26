using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>Nhóm 2: lấy nét, chất lượng/tỉ lệ ảnh, tự tắt nguồn, chi tiết Picture Style (SDK giả).</summary>
public sealed class CanonGroup2SettingsTests : IDisposable
{
    private const uint Portrait = 0x82;
    private const uint Standard = 0x81;
    private const uint LargeFineJpeg = 0x0013ff0f;
    private const uint MiddleFineJpeg = 0x0113ff0f;
    private const uint RawPlusLargeFineJpeg = 0x00640f13;
    private const uint HeifLargeFine = 0x0080ff0f;

    private static readonly DateTimeOffset Now = new(2026, 11, 26, 23, 0, 0, TimeSpan.FromHours(7));

    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;

    public CanonGroup2SettingsTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);
        _api.Properties[EDSDK.PropID_AEMode] = 3;
        _api.Properties[EDSDK.PropID_PictureStyle] = Standard;
        _api.AllowedValues[EDSDK.PropID_PictureStyle] = [Standard, Portrait];
        _api.PictureStyleDescs[Standard] = new EDSDK.EdsPictureStyleDesc { contrast = 0, sharpness = 3, saturation = 0, colorTone = 0, filterEffect = 7 };
        _api.PictureStyleDescs[Portrait] = new EDSDK.EdsPictureStyleDesc { contrast = 0, sharpness = 2, saturation = 0, colorTone = 0 };
        _api.Properties[EDSDK.PropID_Evf_AFMode] = 1;
        _api.AllowedValues[EDSDK.PropID_Evf_AFMode] = [1, 2];
        _api.Properties[EDSDK.PropID_ImageQuality] = LargeFineJpeg;
        _api.AllowedValues[EDSDK.PropID_ImageQuality] = [LargeFineJpeg, MiddleFineJpeg, RawPlusLargeFineJpeg, HeifLargeFine];
        _api.Properties[EDSDK.PropID_Aspect] = 0;
        _api.AllowedValues[EDSDK.PropID_Aspect] = [0, 1, 2, 7];
        _api.Properties[EDSDK.PropID_AutoPowerOffSetting] = 60;
        _api.AllowedValues[EDSDK.PropID_AutoPowerOffSetting] = [0, 30, 60, 120];
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Connect_UnlocksPrivatePropertiesBeforeOpeningSession()
    {
        await using var device = await StartedDeviceAsync();

        Assert.Equal(1, _api.UnlockCalls);
        Assert.True(_api.UnlockedBeforeOpen);
    }

    [Fact]
    public async Task ImageQuality_OffersOnlyJpeg()
    {
        await using var device = await StartedDeviceAsync();

        var quality = Find(await device.ReadSettingsAsync(), CanonSetting.ImageQuality);

        Assert.Equal([LargeFineJpeg, MiddleFineJpeg], quality.Options.Select(o => o.Code));
        Assert.Equal("JPEG Lớn – Tinh (L Fine)", quality.Current!.Label);
    }

    [Fact]
    public async Task ImageQuality_CurrentRaw_IsLabelledAndNotSavedInPreset()
    {
        _api.Properties[EDSDK.PropID_ImageQuality] = RawPlusLargeFineJpeg;
        await using var device = await StartedDeviceAsync();

        var states = await device.ReadSettingsAsync();
        var preset = CanonPreset.FromCurrent(Guid.NewGuid(), "Thử", Now, states);

        Assert.Contains("không dùng được", Find(states, CanonSetting.ImageQuality).Current!.Label);
        Assert.DoesNotContain(preset.Values, v => v.Setting == CanonSetting.ImageQuality);
    }

    [Fact]
    public async Task AutoPowerOff_LabelsAndCanBeDisabled()
    {
        await using var device = await StartedDeviceAsync();

        var state = Find(await device.ReadSettingsAsync(), CanonSetting.AutoPowerOff);
        await device.SetSettingAsync(CanonSetting.AutoPowerOff, 0);

        Assert.Equal("1 phút", state.Current!.Label);
        Assert.Equal(["Không tự tắt", "30 giây", "1 phút", "2 phút"], state.Options.Select(o => o.Label));
        Assert.Equal(0u, _api.Properties[EDSDK.PropID_AutoPowerOffSetting]);
    }

    [Fact]
    public async Task LiveViewAfMethod_CanSwitchToFaceTracking()
    {
        await using var device = await StartedDeviceAsync();

        await device.SetSettingAsync(CanonSetting.LiveViewAfMethod, 2);

        Assert.Equal("Khuôn mặt + bám theo", Find(await device.ReadSettingsAsync(), CanonSetting.LiveViewAfMethod).Current!.Label);
    }

    [Fact]
    public async Task AspectRatio_Labels()
    {
        await using var device = await StartedDeviceAsync();

        var aspect = Find(await device.ReadSettingsAsync(), CanonSetting.AspectRatio);

        Assert.Equal(["3:2 (toàn khung)", "1:1", "4:3", "16:9"], aspect.Options.Select(o => o.Label));
    }

    [Fact]
    public async Task PictureStyleDetails_ReadForCurrentStyle_WithRanges()
    {
        await using var device = await StartedDeviceAsync();

        var states = await device.ReadSettingsAsync();
        var sharpness = Find(states, CanonSetting.PictureStyleSharpness);
        var saturation = Find(states, CanonSetting.PictureStyleSaturation);

        Assert.Equal("3", sharpness.Current!.Label);
        Assert.Equal(8, sharpness.Options.Count);
        Assert.Equal("-4", saturation.Options[0].Label);
        Assert.Equal("+4", saturation.Options[^1].Label);
    }

    [Fact]
    public async Task PictureStyleDetails_NotSupported_AreOmitted()
    {
        _api.PictureStyleDescs.Clear();
        await using var device = await StartedDeviceAsync();

        var states = await device.ReadSettingsAsync();

        Assert.DoesNotContain(states, s => CanonSettingCatalog.IsPictureStyleDetail(s.Setting));
        Assert.Contains(states, s => s.Setting == CanonSetting.PictureStyle);
    }

    [Fact]
    public async Task SetPictureStyleDetail_ChangesOnlyThatField()
    {
        await using var device = await StartedDeviceAsync();

        await device.SetSettingAsync(CanonSetting.PictureStyleSaturation, unchecked((uint)2));

        var desc = _api.PictureStyleDescs[Standard];
        Assert.Equal(2, desc.saturation);
        Assert.Equal(3u, desc.sharpness);
        Assert.Equal(7u, desc.filterEffect);
        Assert.Equal("+2", Find(await device.ReadSettingsAsync(), CanonSetting.PictureStyleSaturation).Current!.Label);
    }

    [Fact]
    public async Task ApplyPreset_SetsPictureStyleBeforeItsDetails()
    {
        await using var device = await StartedDeviceAsync();
        var preset = new CanonPreset(Guid.NewGuid(), "Chân dung tươi", Now, 3,
        [
            new(CanonSetting.PictureStyleSaturation, unchecked((uint)1), "+1"),
            new(CanonSetting.PictureStyle, Portrait, "Chân dung"),
        ]);

        var result = await device.ApplyPresetAsync(preset);

        Assert.True(result.FullyApplied);
        Assert.Equal(Portrait, _api.Properties[EDSDK.PropID_PictureStyle]);

        // Chi tiết phải rơi vào Chân dung (kiểu mới), không phải Tiêu chuẩn.
        Assert.Equal(1, _api.PictureStyleDescs[Portrait].saturation);
        Assert.Equal(0, _api.PictureStyleDescs[Standard].saturation);
    }

    [Fact]
    public void Catalog_EverySettingHasGroupNameAndProperty()
    {
        foreach (var setting in CanonSettingCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(CanonSettingCatalog.Name(setting)));
            Assert.Contains(CanonSettingCatalog.Group(setting), CanonSettingCatalog.Groups);
            Assert.NotEqual(0u, CanonSettingCatalog.PropertyId(setting));
        }
    }

    [Fact]
    public async Task AddMissingBundled_AddsOnlyNewOnes_WithoutOverwriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "pb-canon-bundled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundledDir = Path.Combine(root, "bundled");
            var bundledWriter = new CanonPresetStore(bundledDir, null, NullLogger<CanonPresetStore>.Instance);
            var first = new CanonPreset(Guid.NewGuid(), "Có từ đầu", Now, 3, []);
            await bundledWriter.SaveAsync(first);

            var store = new CanonPresetStore(Path.Combine(root, "library"), bundledDir, NullLogger<CanonPresetStore>.Instance);
            await store.ListAsync();
            await store.SaveAsync(first with { Name = "Đã sửa trên máy" });

            // Bản build mới mang thêm một bộ mẫu.
            var added = new CanonPreset(Guid.NewGuid(), "Đèn LED trắng", Now, 3, []);
            await bundledWriter.SaveAsync(added);

            var result = await store.AddMissingBundledAsync();
            var list = await store.ListAsync();

            Assert.Equal(added.Id, Assert.Single(result).Id);
            Assert.Contains(list, p => p.Name == "Đã sửa trên máy");
            Assert.Contains(list, p => p.Name == "Đèn LED trắng");
            Assert.Empty(await store.AddMissingBundledAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CanonSettingState Find(IReadOnlyList<CanonSettingState> settings, CanonSetting setting) =>
        Assert.Single(settings, s => s.Setting == setting);

    private async Task<CanonCameraDevice> StartedDeviceAsync()
    {
        var device = new CanonCameraDevice(
            _host,
            new CanonCameraOptions { ConnectRetryDelay = TimeSpan.FromMilliseconds(5), BusyRetryDelay = TimeSpan.FromMilliseconds(1) },
            NullLogger<CanonCameraDevice>.Instance);
        Assert.True((await device.StartAsync()).Succeeded);
        return device;
    }
}
