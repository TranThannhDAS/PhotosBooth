using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>Trạng thái máy ảnh: % pin, nguồn AC, cảnh báo nhiệt, tên ống kính (SDK giả).</summary>
public sealed class CanonStatusTests : IDisposable
{
    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;

    public CanonStatusTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ReadStatus_BatteryTemperatureAndLens()
    {
        _api.Properties[EDSDK.PropID_BatteryLevel] = 80;
        _api.Properties[EDSDK.PropID_TempStatus] = 0;
        _api.Strings[EDSDK.PropID_LensName] = " EF-M15-45mm f/3.5-6.3 IS STM ";
        await using var device = await StartedDeviceAsync();

        var status = await device.ReadStatusAsync();

        Assert.Equal(80, status.BatteryPercent);
        Assert.False(status.OnAcPower);
        Assert.False(status.IsBatteryLow);
        Assert.Equal(CanonTemperatureStatus.Normal, status.Temperature);
        Assert.False(status.IsOverheating);
        Assert.Equal("EF-M15-45mm f/3.5-6.3 IS STM", status.LensName);
    }

    [Fact]
    public async Task ReadStatus_AcPower()
    {
        _api.Properties[EDSDK.PropID_BatteryLevel] = 0xffffffff;
        await using var device = await StartedDeviceAsync();

        var status = await device.ReadStatusAsync();

        Assert.True(status.OnAcPower);
        Assert.Null(status.BatteryPercent);
        Assert.False(status.IsBatteryLow);
    }

    [Fact]
    public async Task ReadStatus_LowBatteryAndOverheating()
    {
        _api.Properties[EDSDK.PropID_BatteryLevel] = 15;
        _api.Properties[EDSDK.PropID_TempStatus] = 3;
        await using var device = await StartedDeviceAsync();

        var status = await device.ReadStatusAsync();

        Assert.True(status.IsBatteryLow);
        Assert.Equal(CanonTemperatureStatus.LiveViewDisabled, status.Temperature);
        Assert.True(status.IsOverheating);
    }

    [Fact]
    public async Task ReadStatus_NotProvided_IsNullOrUnknown()
    {
        await using var device = await StartedDeviceAsync();

        var status = await device.ReadStatusAsync();

        Assert.Null(status.BatteryPercent);
        Assert.False(status.OnAcPower);
        Assert.Equal(CanonTemperatureStatus.Unknown, status.Temperature);
        Assert.Null(status.LensName);
    }

    [Fact]
    public async Task ReadStatus_NotConnected_ThrowsNotActive()
    {
        await using var device = CreateDevice();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.ReadStatusAsync());

        Assert.Equal(CameraError.NotActive, ex.Error);
    }

    [Fact]
    public async Task StatusChanged_RaisedWhenBatteryChanges_NotSettingsChanged()
    {
        await using var device = await StartedDeviceAsync();
        var statusChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsChanged = false;
        device.StatusChanged += (_, _) => statusChanged.TrySetResult();
        device.SettingsChanged += (_, _) => settingsChanged = true;

        _api.RaisePropertyChanged(EDSDK.PropID_BatteryLevel);

        await statusChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(settingsChanged);
    }

    [Theory]
    [InlineData(0u, CanonTemperatureStatus.Normal)]
    [InlineData(1u, CanonTemperatureStatus.Warning)]
    [InlineData(2u, CanonTemperatureStatus.FrameRateDown)]
    [InlineData(4u, CanonTemperatureStatus.ShutterDisabled)]
    [InlineData(5u, CanonTemperatureStatus.StillQualityWarning)]
    [InlineData(0x00020001u, CanonTemperatureStatus.MovieRestricted)]
    [InlineData(99u, CanonTemperatureStatus.Unknown)]
    public void ParseTemperature_FollowsCanonTable(uint code, CanonTemperatureStatus expected) =>
        Assert.Equal(expected, CanonCameraStatus.ParseTemperature(code));

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
