using EDSDKLib;
using Microsoft.Extensions.Logging;
using Photobooth.Camera.Canon.Native;

namespace Photobooth.Camera.Canon;

/// <summary>Một máy ảnh Canon đang kết nối qua USB (<see cref="PortName"/> là đường dẫn thiết bị USB của Windows).</summary>
public sealed record CanonCameraInfo(string Name, string PortName);

/// <summary>Kết quả quét máy ảnh Canon. <see cref="SdkAvailable"/> = false khi không nạp được Canon EDSDK.</summary>
public sealed record CanonCameraScanResult(bool SdkAvailable, IReadOnlyList<CanonCameraInfo> Cameras);

/// <summary>Liệt kê máy ảnh Canon đang kết nối (cho màn Quản trị); không mở session, không ảnh hưởng máy đang dùng.</summary>
public sealed class CanonCameraDirectory
{
    private readonly CanonSdkHost _host;
    private readonly ILogger<CanonCameraDirectory> _logger;

    public CanonCameraDirectory(CanonSdkHost host, ILogger<CanonCameraDirectory> logger)
    {
        _host = host;
        _logger = logger;
    }

    public async Task<CanonCameraScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _host.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false))
            {
                return new CanonCameraScanResult(SdkAvailable: false, []);
            }

            var (err, cameras) = await _host.InvokeAsync(api =>
            {
                var result = api.ListCameras(out var list);
                return (result, list);
            }, cancellationToken).ConfigureAwait(false);

            if (err != EDSDK.EDS_ERR_OK)
            {
                _logger.LogWarning("Không liệt kê được máy ảnh Canon: {Error}", EdsErrors.Describe(err));
                return new CanonCameraScanResult(SdkAvailable: true, []);
            }

            return new CanonCameraScanResult(SdkAvailable: true, cameras);
        }
        catch (ObjectDisposedException)
        {
            return new CanonCameraScanResult(SdkAvailable: false, []);
        }
    }
}
