using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>Live View của <see cref="CanonCameraDevice"/> với SDK giả (không thay thế thử trên máy thật).</summary>
public sealed class CanonLiveViewTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;

    public CanonLiveViewTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task StartLiveViewAsync_PublishesFramesWithSizeAndIncreasingSequence()
    {
        await using var device = await StartedDeviceAsync();

        await device.StartLiveViewAsync();
        var first = await device.LiveView.WaitForNewerAsync(0, TestTimeout);
        var second = await device.LiveView.WaitForNewerAsync(first!.Sequence, TestTimeout);

        Assert.True(device.IsLiveViewRunning);
        Assert.Equal((960, 640), (first.WidthPixels, first.HeightPixels));
        Assert.True(second!.Sequence > first.Sequence);
        Assert.Equal(1, _api.StartLiveViewCalls);
    }

    [Fact]
    public async Task LiveView_NotReadyFrames_AreSkippedWithoutStopping()
    {
        for (var i = 0; i < 5; i++)
        {
            _api.LiveViewResults.Enqueue(EDSDK.EDS_ERR_OBJECT_NOTREADY);
        }

        await using var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();

        Assert.NotNull(await device.LiveView.WaitForNewerAsync(0, TestTimeout));
        Assert.True(device.IsLiveViewRunning);
    }

    [Fact]
    public async Task StopLiveViewAsync_StopsLoop_TurnsOffOnCamera_AndClearsFrame()
    {
        await using var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();
        await device.LiveView.WaitForNewerAsync(0, TestTimeout);

        await device.StopLiveViewAsync();
        await device.StopLiveViewAsync();

        Assert.False(device.IsLiveViewRunning);
        Assert.Null(device.LiveView.Latest);
        Assert.Equal(1, _api.StopLiveViewCalls);
        Assert.False(_api.LiveViewOn);
    }

    [Fact]
    public async Task StartLiveViewAsync_NotConnected_ThrowsNotActive()
    {
        await using var device = CreateDevice();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.StartLiveViewAsync());

        Assert.Equal(CameraError.NotActive, ex.Error);
        Assert.Equal(0, _api.StartLiveViewCalls);
    }

    [Fact]
    public async Task StartLiveViewAsync_CameraRefuses_ThrowsAndDoesNotRun()
    {
        _api.StartLiveViewResult = EDSDK.EDS_ERR_INTERNAL_ERROR;
        await using var device = await StartedDeviceAsync();

        await Assert.ThrowsAsync<CameraException>(() => device.StartLiveViewAsync());

        Assert.False(device.IsLiveViewRunning);
    }

    [Fact]
    public async Task LiveView_RepeatedErrors_StopsLoop()
    {
        for (var i = 0; i < 25; i++)
        {
            _api.LiveViewResults.Enqueue(EDSDK.EDS_ERR_INTERNAL_ERROR);
        }

        await using var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();

        await WaitUntilAsync(() => !device.IsLiveViewRunning);
        Assert.Null(device.LiveView.Latest);
    }

    [Fact]
    public async Task CaptureFrameAsync_WhileLiveViewRunning_Succeeds_AndLiveViewContinues()
    {
        await using var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();
        var before = await device.LiveView.WaitForNewerAsync(0, TestTimeout);

        var frame = await device.CaptureFrameAsync().WaitAsync(TestTimeout);
        var after = await device.LiveView.WaitForNewerAsync(before!.Sequence, TestTimeout);

        Assert.Equal(TestJpeg.Create(6000, 4000), frame.Data);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Disconnect_StopsLiveView_AndClearsFrame()
    {
        await using var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();
        await device.LiveView.WaitForNewerAsync(0, TestTimeout);

        _api.RaiseStateEvent(EDSDK.StateEvent_Shutdown);

        await WaitUntilAsync(() => !device.IsLiveViewRunning);
        Assert.Null(device.LiveView.Latest);
    }

    [Fact]
    public async Task StopAsync_AlsoStopsLiveView()
    {
        await using var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();

        await device.StopAsync();

        Assert.False(device.IsLiveViewRunning);
        Assert.Equal(1, _api.StopLiveViewCalls);
        Assert.Equal(1, _api.CloseSessionCalls);
    }

    [Fact]
    public async Task Dispose_Synchronous_StopsLiveViewAndClosesSession()
    {
        // Đường đi khi DI container giải phóng đồng bộ lúc đóng ứng dụng.
        var device = await StartedDeviceAsync();
        await device.StartLiveViewAsync();

        device.Dispose();
        device.Dispose();

        Assert.False(device.IsLiveViewRunning);
        Assert.Equal(1, _api.StopLiveViewCalls);
        Assert.Equal(1, _api.CloseSessionCalls);
        Assert.Equal(1, _api.ReleaseCameraCalls);
    }

    [Fact]
    public async Task FrameBuffer_WaitForNewer_TimesOutWithNull_WhenNoFrame()
    {
        var buffer = new LiveViewFrameBuffer();

        Assert.Null(await buffer.WaitForNewerAsync(0, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void FrameBuffer_RejectsDataThatIsNotJpeg()
    {
        var buffer = new LiveViewFrameBuffer();

        Assert.False(buffer.Publish([1, 2, 3]));
        Assert.Null(buffer.Latest);
    }

    private CanonCameraDevice CreateDevice() =>
        new(_host, new CanonCameraOptions { ConnectRetryDelay = TimeSpan.FromMilliseconds(5), BusyRetryDelay = TimeSpan.FromMilliseconds(1) },
            NullLogger<CanonCameraDevice>.Instance);

    private async Task<CanonCameraDevice> StartedDeviceAsync()
    {
        var device = CreateDevice();
        Assert.True((await device.StartAsync()).Succeeded);
        return device;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Điều kiện không xảy ra trong thời gian chờ.");
            }

            await Task.Delay(10);
        }
    }
}
