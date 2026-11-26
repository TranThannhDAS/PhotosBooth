using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;

namespace Photobooth.Camera.Canon.Tests;

public sealed class CanonCameraDirectoryTests : IDisposable
{
    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;
    private readonly CanonCameraDirectory _directory;

    public CanonCameraDirectoryTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);
        _directory = new CanonCameraDirectory(_host, NullLogger<CanonCameraDirectory>.Instance);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ScanAsync_ReturnsAllConnectedCameras()
    {
        _api.ListedCameras = [new("Canon EOS M50m2", "usb#1"), new("Canon EOS R6", "usb#2")];

        var result = await _directory.ScanAsync();

        Assert.True(result.SdkAvailable);
        Assert.Equal(["Canon EOS M50m2", "Canon EOS R6"], result.Cameras.Select(c => c.Name));
    }

    [Fact]
    public async Task ScanAsync_NoCamera_ReturnsEmptyList()
    {
        var result = await _directory.ScanAsync();

        Assert.True(result.SdkAvailable);
        Assert.Empty(result.Cameras);
    }

    [Fact]
    public async Task ScanAsync_SdkMissing_ReportsUnavailable()
    {
        _api.InitializeException = new DllNotFoundException("EDSDK.dll");

        var result = await _directory.ScanAsync();

        Assert.False(result.SdkAvailable);
        Assert.Empty(result.Cameras);
    }

    [Fact]
    public async Task ScanAsync_ListError_ReturnsEmptyList()
    {
        _api.ListCamerasResult = EDSDK.EDS_ERR_INTERNAL_ERROR;

        var result = await _directory.ScanAsync();

        Assert.True(result.SdkAvailable);
        Assert.Empty(result.Cameras);
    }

    [Fact]
    public async Task ScanAsync_DoesNotOpenSession()
    {
        _api.ListedCameras = [new("Canon EOS M50m2", "usb#1")];

        await _directory.ScanAsync();

        Assert.Equal(0, _api.OpenSessionCalls);
    }
}
