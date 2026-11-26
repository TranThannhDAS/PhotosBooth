using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Camera.Canon.Native;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>
/// Kiểm thử <see cref="CanonCameraDevice"/> trên <see cref="CanonSdkHost"/> thật (luồng STA thật) với SDK giả.
/// Không thay thế thử nghiệm với máy ảnh thật.
/// </summary>
public sealed class CanonCameraDeviceTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;

    public CanonCameraDeviceTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);
    }

    public void Dispose() => _host.Dispose();

    // ---------------------------------------------------------------- Kết nối

    [Fact]
    public async Task StartAsync_CameraConnected_OpensSessionAndSavesToHost()
    {
        await using var device = CreateDevice();

        var result = await device.StartAsync();

        Assert.True(result.Succeeded);
        Assert.True(device.IsActive);
        Assert.Equal("Canon EOS M50m2", device.CameraName);
        Assert.Equal(1, _api.OpenSessionCalls);
        Assert.True(_api.SaveToHostSet);
    }

    [Fact]
    public async Task StartAsync_NoCamera_RetriesThenReturnsNotFound()
    {
        for (var i = 0; i < 3; i++)
        {
            _api.FindCameraResults.Enqueue(EDSDK.EDS_ERR_DEVICE_NOT_FOUND);
        }

        await using var device = CreateDevice(new CanonCameraOptions { ConnectAttempts = 3, ConnectRetryDelay = TimeSpan.FromMilliseconds(5) });

        var result = await device.StartAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(CameraError.NotFound, result.Error);
        Assert.Equal(3, _api.FindCameraCalls);
        Assert.False(device.IsActive);
    }

    [Fact]
    public async Task StartAsync_CameraAppearsOnSecondAttempt_Succeeds()
    {
        // Mô phỏng việc vừa cắm lại cáp: lần đầu máy ảnh chưa xuất hiện.
        _api.FindCameraResults.Enqueue(EDSDK.EDS_ERR_DEVICE_NOT_FOUND);
        await using var device = CreateDevice();

        var result = await device.StartAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, _api.FindCameraCalls);
    }

    [Fact]
    public async Task StartAsync_OpenSessionFailsWithNonRetryableError_ReleasesCameraWithoutRetry()
    {
        _api.OpenSessionResult = EDSDK.EDS_ERR_COMM_PORT_IS_IN_USE;
        await using var device = CreateDevice();

        var result = await device.StartAsync();

        Assert.Equal(CameraError.InUse, result.Error);
        Assert.Equal(1, _api.FindCameraCalls);
        Assert.Equal(1, _api.ReleaseCameraCalls);
    }

    [Fact]
    public async Task StartAsync_SaveToHostFails_ClosesSessionAndReleasesCamera()
    {
        _api.SaveToHostResult = EDSDK.EDS_ERR_INTERNAL_ERROR;
        await using var device = CreateDevice();

        var result = await device.StartAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(1, _api.CloseSessionCalls);
        Assert.Equal(1, _api.ReleaseCameraCalls);
    }

    [Fact]
    public async Task StartAsync_SdkLibraryMissing_ReturnsNotSupported()
    {
        _api.InitializeException = new DllNotFoundException("EDSDK.dll");
        await using var device = CreateDevice();

        var result = await device.StartAsync();

        Assert.Equal(CameraError.NotSupported, result.Error);
        Assert.Equal(0, _api.FindCameraCalls);
    }

    [Fact]
    public async Task StopAsync_ClosesSessionReleasesCamera_AndIsIdempotent()
    {
        await using var device = CreateDevice();
        await device.StartAsync();

        await device.StopAsync();
        await device.StopAsync();

        Assert.False(device.IsActive);
        Assert.Equal(1, _api.CloseSessionCalls);
        Assert.Equal(1, _api.ReleaseCameraCalls);
    }

    // ---------------------------------------------------------------- Chụp

    [Fact]
    public async Task CaptureFrameAsync_Success_ReturnsOriginalJpeg_PressesThenReleasesShutter_AndReleasesItem()
    {
        await using var device = await StartedDeviceAsync();

        var frame = await device.CaptureFrameAsync().WaitAsync(TestTimeout);

        Assert.Equal("image/jpeg", frame.ContentType);
        Assert.Equal(TestJpeg.Create(6000, 4000), frame.Data);
        Assert.Equal([ShutterButtonState.Completely, ShutterButtonState.Off], _api.ShutterPresses);
        var item = Assert.Single(_api.DownloadedItems);
        Assert.Contains(item, _api.ReleasedItems);
        Assert.Empty(_api.DiscardedItems);
    }

    [Fact]
    public async Task CaptureFrameAsync_AutofocusFails_ThrowsCaptureFailed_AndStillReleasesShutter()
    {
        _api.ShutterResults.Enqueue(EDSDK.EDS_ERR_TAKE_PICTURE_AF_NG);
        await using var device = await StartedDeviceAsync();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.CaptureFrameAsync().WaitAsync(TestTimeout));

        Assert.Equal(CameraError.CaptureFailed, ex.Error);
        Assert.Contains("nét", ex.Message);
        Assert.Equal([ShutterButtonState.Completely, ShutterButtonState.Off], _api.ShutterPresses);
        Assert.True(device.IsActive);
    }

    [Fact]
    public async Task CaptureFrameAsync_DeviceBusy_RetriesShutter()
    {
        _api.ShutterResults.Enqueue(EDSDK.EDS_ERR_DEVICE_BUSY);
        _api.ShutterResults.Enqueue(EDSDK.EDS_ERR_DEVICE_BUSY);
        await using var device = await StartedDeviceAsync();

        var frame = await device.CaptureFrameAsync().WaitAsync(TestTimeout);

        Assert.NotEmpty(frame.Data);
        Assert.Equal(3, _api.ShutterPresses.Count(s => s == ShutterButtonState.Completely));
    }

    [Fact]
    public async Task CaptureFrameAsync_NoImageArrives_TimesOut_AndLateImageIsDiscarded()
    {
        _api.FilesPerShot = [];
        await using var device = await StartedDeviceAsync(new CanonCameraOptions { TransferTimeout = TimeSpan.FromMilliseconds(200) });

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.CaptureFrameAsync().WaitAsync(TestTimeout));
        Assert.Equal(CameraError.CaptureFailed, ex.Error);

        var lateItem = _api.RaiseTransfer("IMG_0001.JPG", TestJpeg.Create(10, 10));
        await WaitUntilAsync(() => _api.DiscardedItems.Contains(lateItem));
        Assert.Empty(_api.DownloadedItems);
    }

    [Fact]
    public async Task CaptureFrameAsync_RawPlusJpeg_DiscardsRaw_ReturnsJpeg()
    {
        _api.FilesPerShot = [("IMG_0001.CR3", [1, 2, 3]), ("IMG_0001.JPG", TestJpeg.Create(6000, 4000))];
        await using var device = await StartedDeviceAsync();

        var frame = await device.CaptureFrameAsync().WaitAsync(TestTimeout);

        Assert.Equal(TestJpeg.Create(6000, 4000), frame.Data);
        await WaitUntilAsync(() => _api.DiscardedItems.Count == 1);
        Assert.Single(_api.DownloadedItems);
    }

    [Fact]
    public async Task CaptureFrameAsync_DataIsNotJpeg_ThrowsCaptureFailed()
    {
        _api.FilesPerShot = [("IMG_0001.JPG", [0x00, 0x01, 0x02])];
        await using var device = await StartedDeviceAsync();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.CaptureFrameAsync().WaitAsync(TestTimeout));

        Assert.Equal(CameraError.CaptureFailed, ex.Error);
        Assert.Single(_api.ReleasedItems);
    }

    [Fact]
    public async Task CaptureFrameAsync_DownloadFails_ThrowsCaptureFailed_AndReleasesItem()
    {
        _api.DownloadResult = EDSDK.EDS_ERR_COMM_USB_BUS_ERR;
        await using var device = await StartedDeviceAsync();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.CaptureFrameAsync().WaitAsync(TestTimeout));

        Assert.Equal(CameraError.CaptureFailed, ex.Error);
        Assert.Single(_api.ReleasedItems);
    }

    [Fact]
    public async Task CaptureFrameAsync_NotStarted_ThrowsNotActive()
    {
        await using var device = CreateDevice();

        var ex = await Assert.ThrowsAsync<CameraException>(() => device.CaptureFrameAsync());

        Assert.Equal(CameraError.NotActive, ex.Error);
        Assert.Empty(_api.ShutterPresses);
    }

    [Fact]
    public async Task CaptureFrameAsync_Cancelled_ThrowsOperationCanceled_AndLateImageIsDiscarded()
    {
        _api.FilesPerShot = [];
        await using var device = await StartedDeviceAsync();
        using var cts = new CancellationTokenSource();

        var capture = device.CaptureFrameAsync(cts.Token);
        await WaitUntilAsync(() => _api.ShutterPresses.Contains(ShutterButtonState.Off));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TestTimeout));
        var lateItem = _api.RaiseTransfer("IMG_0001.JPG", TestJpeg.Create(10, 10));
        await WaitUntilAsync(() => _api.DiscardedItems.Contains(lateItem));
    }

    [Fact]
    public async Task UnsolicitedImage_FromCameraShutterButton_IsDiscarded()
    {
        await using var device = await StartedDeviceAsync();

        var item = _api.RaiseTransfer("IMG_0005.JPG", TestJpeg.Create(10, 10));

        await WaitUntilAsync(() => _api.DiscardedItems.Contains(item));
        Assert.Empty(_api.DownloadedItems);
    }

    // ---------------------------------------------------------------- Mất kết nối / tự tắt nguồn

    [Fact]
    public async Task ShutdownEvent_RaisesFaultedDisconnected_DeactivatesAndReleasesCamera()
    {
        await using var device = await StartedDeviceAsync();
        var faulted = new TaskCompletionSource<CameraError>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Faulted += (_, error) => faulted.TrySetResult(error);

        _api.RaiseStateEvent(EDSDK.StateEvent_Shutdown);

        Assert.Equal(CameraError.Disconnected, await faulted.Task.WaitAsync(TestTimeout));
        Assert.False(device.IsActive);
        await WaitUntilAsync(() => _api.ReleaseCameraCalls == 1);

        // Đã mất kết nối thì không đóng session nữa; StopAsync sau đó không lỗi.
        await device.StopAsync();
        Assert.Equal(0, _api.CloseSessionCalls);
    }

    [Fact]
    public async Task ShutdownEvent_WhileWaitingForImage_FailsCaptureWithDisconnected()
    {
        _api.FilesPerShot = [];
        await using var device = await StartedDeviceAsync();

        var capture = device.CaptureFrameAsync();
        await WaitUntilAsync(() => _api.ShutterPresses.Contains(ShutterButtonState.Off));
        _api.RaiseStateEvent(EDSDK.StateEvent_Shutdown);

        var ex = await Assert.ThrowsAsync<CameraException>(() => capture.WaitAsync(TestTimeout));
        Assert.Equal(CameraError.Disconnected, ex.Error);
    }

    [Fact]
    public async Task StartAsync_AfterDisconnect_ReconnectsWithNewSession()
    {
        await using var device = await StartedDeviceAsync();
        _api.RaiseStateEvent(EDSDK.StateEvent_Shutdown);
        await WaitUntilAsync(() => !device.IsActive && _api.ReleaseCameraCalls == 1);

        var result = await device.StartAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, _api.OpenSessionCalls);
        Assert.NotEmpty((await device.CaptureFrameAsync().WaitAsync(TestTimeout)).Data);
    }

    [Fact]
    public async Task WillSoonShutDownEvent_ExtendsShutDownTimer()
    {
        await using var device = await StartedDeviceAsync();

        _api.RaiseStateEvent(EDSDK.StateEvent_WillSoonShutDown);

        await WaitUntilAsync(() => _api.ExtendShutDownTimerCalls == 1);
        Assert.True(device.IsActive);
    }

    // ---------------------------------------------------------------- Luồng SDK

    [Fact]
    public async Task AllSdkCalls_RunOnSingleStaThread()
    {
        await using var device = await StartedDeviceAsync();
        await device.CaptureFrameAsync().WaitAsync(TestTimeout);
        await device.StopAsync();

        var threads = _api.CallingThreads.ToArray();
        Assert.Single(threads.Select(t => t.ThreadId).Distinct());
        Assert.All(threads, t => Assert.Equal(ApartmentState.STA, t.Apartment));
        Assert.DoesNotContain(threads, t => t.ThreadId == Environment.CurrentManagedThreadId);
    }

    // ---------------------------------------------------------------- Hỗ trợ

    private CanonCameraDevice CreateDevice(CanonCameraOptions? options = null) =>
        new(_host, FastOptions(options), NullLogger<CanonCameraDevice>.Instance);

    private async Task<CanonCameraDevice> StartedDeviceAsync(CanonCameraOptions? options = null)
    {
        var device = CreateDevice(options);
        var result = await device.StartAsync();
        Assert.True(result.Succeeded);
        return device;
    }

    /// <summary>Rút ngắn các khoảng chờ để test chạy nhanh, giữ nguyên các giá trị test đã chỉ định.</summary>
    private static CanonCameraOptions FastOptions(CanonCameraOptions? options)
    {
        var defaults = new CanonCameraOptions();
        options ??= defaults;
        return options with
        {
            ConnectRetryDelay = options.ConnectRetryDelay == defaults.ConnectRetryDelay ? TimeSpan.FromMilliseconds(5) : options.ConnectRetryDelay,
            BusyRetryDelay = TimeSpan.FromMilliseconds(1),
            TransferTimeout = options.TransferTimeout == defaults.TransferTimeout ? TimeSpan.FromSeconds(5) : options.TransferTimeout,
        };
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
