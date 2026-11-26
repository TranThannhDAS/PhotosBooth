using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>Đọc/đặt thông số máy ảnh Canon với SDK giả (mã giá trị lấy theo bảng của Canon).</summary>
public sealed class CanonSettingsTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;

    public CanonSettingsTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);

        // Máy ở chế độ M: chỉnh được ISO, khẩu độ, tốc độ; cân bằng trắng Tự động.
        _api.Properties[EDSDK.PropID_AEMode] = 3;
        _api.Properties[EDSDK.PropID_ISOSpeed] = 0x48;
        _api.AllowedValues[EDSDK.PropID_ISOSpeed] = [0x00, 0x48, 0x50, 0x58];
        _api.Properties[EDSDK.PropID_Av] = 0x28;
        _api.AllowedValues[EDSDK.PropID_Av] = [0x28, 0x30, 0x38];
        _api.Properties[EDSDK.PropID_Tv] = 0x70;
        _api.AllowedValues[EDSDK.PropID_Tv] = [0x68, 0x70, 0x78];
        _api.Properties[EDSDK.PropID_WhiteBalance] = 0;
        _api.AllowedValues[EDSDK.PropID_WhiteBalance] = [0, 1, 9];
        _api.Properties[EDSDK.PropID_ColorTemperature] = 5200;
        _api.Properties[EDSDK.PropID_PictureStyle] = 0x81;
        _api.AllowedValues[EDSDK.PropID_PictureStyle] = [0x81, 0x82];
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ReadSettingsAsync_ReturnsCurrentValuesWithReadableLabels()
    {
        await using var device = await StartedDeviceAsync();

        var settings = await device.ReadSettingsAsync();

        Assert.Equal("M (Chỉnh tay)", Find(settings, CanonSetting.ExposureMode).Current!.Label);
        Assert.Equal("100", Find(settings, CanonSetting.Iso).Current!.Label);
        Assert.Equal("f/4.0", Find(settings, CanonSetting.Aperture).Current!.Label);
        Assert.Equal("1/125", Find(settings, CanonSetting.ShutterSpeed).Current!.Label);
        Assert.Equal("Tự động (giữ không khí)", Find(settings, CanonSetting.WhiteBalance).Current!.Label);
        Assert.Equal("Tiêu chuẩn", Find(settings, CanonSetting.PictureStyle).Current!.Label);
        Assert.Equal(["Tự động", "100", "200", "400"], Find(settings, CanonSetting.Iso).Options.Select(o => o.Label));
    }

    [Fact]
    public async Task ReadSettingsAsync_ExposureModeIsReadOnly_AndSingleOptionIsNotWritable()
    {
        // Chế độ Av: tốc độ do máy tự chọn → máy chỉ trả một giá trị.
        _api.AllowedValues[EDSDK.PropID_Tv] = [0x70];
        await using var device = await StartedDeviceAsync();

        var settings = await device.ReadSettingsAsync();

        Assert.False(Find(settings, CanonSetting.ExposureMode).IsWritable);
        Assert.False(Find(settings, CanonSetting.ShutterSpeed).IsWritable);
        Assert.True(Find(settings, CanonSetting.Iso).IsWritable);
    }

    [Fact]
    public async Task ColorTemperature_WritableOnlyWhenWhiteBalanceIsKelvin_WithDefaultRange()
    {
        await using var device = await StartedDeviceAsync();

        var auto = Find(await device.ReadSettingsAsync(), CanonSetting.ColorTemperature);
        await device.SetSettingAsync(CanonSetting.WhiteBalance, CanonSettingCatalog.WhiteBalanceColorTemperature);
        var kelvin = Find(await device.ReadSettingsAsync(), CanonSetting.ColorTemperature);

        Assert.False(auto.IsWritable);
        Assert.True(kelvin.IsWritable);
        Assert.Equal("5200 K", kelvin.Current!.Label);
        Assert.Equal("2500 K", kelvin.Options[0].Label);
        Assert.Equal("10000 K", kelvin.Options[^1].Label);
    }

    [Fact]
    public async Task ReadSettingsAsync_UnsupportedProperty_IsOmitted()
    {
        _api.Properties.TryRemove(EDSDK.PropID_ColorTemperature, out _);
        await using var device = await StartedDeviceAsync();

        var settings = await device.ReadSettingsAsync();

        Assert.DoesNotContain(settings, s => s.Setting == CanonSetting.ColorTemperature);
        Assert.DoesNotContain(settings, s => s.Setting == CanonSetting.ExposureCompensation);
    }

    [Fact]
    public async Task SetSettingAsync_WritesToCamera()
    {
        await using var device = await StartedDeviceAsync();

        await device.SetSettingAsync(CanonSetting.Iso, 0x58);

        Assert.Equal(0x58u, _api.Properties[EDSDK.PropID_ISOSpeed]);
        Assert.Equal("400", Find(await device.ReadSettingsAsync(), CanonSetting.Iso).Current!.Label);
    }

    [Fact]
    public async Task SetSettingAsync_DeviceBusy_Retries()
    {
        _api.SetPropertyResults.Enqueue(EDSDK.EDS_ERR_DEVICE_BUSY);
        await using var device = await StartedDeviceAsync();

        await device.SetSettingAsync(CanonSetting.Iso, 0x50);

        Assert.Equal(0x50u, _api.Properties[EDSDK.PropID_ISOSpeed]);
    }

    [Fact]
    public async Task SetSettingAsync_CameraRejects_ThrowsWithoutChangingValue()
    {
        _api.SetPropertyResults.Enqueue(EDSDK.EDS_ERR_INVALID_PARAMETER);
        await using var device = await StartedDeviceAsync();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.SetSettingAsync(CanonSetting.Iso, 0x58));

        Assert.Contains("ISO", ex.Message);
        Assert.Equal(0x48u, _api.Properties[EDSDK.PropID_ISOSpeed]);
    }

    [Fact]
    public async Task SetSettingAsync_ExposureMode_IsRejected()
    {
        await using var device = await StartedDeviceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => device.SetSettingAsync(CanonSetting.ExposureMode, 3));
        Assert.Empty(_api.SetCalls);
    }

    [Fact]
    public async Task Settings_NotConnected_ThrowNotActive()
    {
        await using var device = CreateDevice();

        var read = await Assert.ThrowsAsync<CameraException>(() => device.ReadSettingsAsync());
        var write = await Assert.ThrowsAsync<CameraException>(() => device.SetSettingAsync(CanonSetting.Iso, 0x48));

        Assert.Equal(CameraError.NotActive, read.Error);
        Assert.Equal(CameraError.NotActive, write.Error);
    }

    [Fact]
    public async Task SettingsChanged_RaisedWhenCameraReportsRelevantPropertyChange()
    {
        await using var device = await StartedDeviceAsync();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.SettingsChanged += (_, _) => changed.TrySetResult();

        // Xoay vòng chế độ trên máy → máy báo đổi chế độ chụp.
        _api.RaisePropertyChanged(EDSDK.PropID_AEMode);

        await changed.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task SettingsChanged_NotRaisedForUnrelatedProperty()
    {
        await using var device = await StartedDeviceAsync();
        var raised = false;
        device.SettingsChanged += (_, _) => raised = true;

        _api.RaisePropertyChanged(EDSDK.PropID_BatteryLevel);
        await _host.InvokeAsync(_ => 0);
        await Task.Delay(100);

        Assert.False(raised);
    }

    [Theory]
    [InlineData(CanonSetting.ShutterSpeed, 0x10u, "30\"")]
    [InlineData(CanonSetting.ShutterSpeed, 0x0cu, "Bulb")]
    [InlineData(CanonSetting.ExposureCompensation, 0xf8u, "-1")]
    [InlineData(CanonSetting.ExposureCompensation, 0x05u, "+2/3")]
    [InlineData(CanonSetting.Aperture, 0xFFu, "Tự động")]
    [InlineData(CanonSetting.PictureStyle, 0x82u, "Chân dung")]
    [InlineData(CanonSetting.Iso, 0x1234u, "Mã 0x1234")]
    public void Catalog_Labels(CanonSetting setting, uint code, string expected)
    {
        Assert.Equal(expected, CanonSettingCatalog.Label(setting, code));
    }

    private static CanonSettingState Find(IReadOnlyList<CanonSettingState> settings, CanonSetting setting) =>
        Assert.Single(settings, s => s.Setting == setting);

    private CanonCameraDevice CreateDevice() =>
        new(_host, new CanonCameraOptions { ConnectRetryDelay = TimeSpan.FromMilliseconds(5), BusyRetryDelay = TimeSpan.FromMilliseconds(1) },
            NullLogger<CanonCameraDevice>.Instance);

    private async Task<CanonCameraDevice> StartedDeviceAsync()
    {
        var device = CreateDevice();
        Assert.True((await device.StartAsync()).Succeeded);
        return device;
    }
}
