using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Photobooth.Application.Camera;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Desktop.Platform;
using Photobooth.Infrastructure.Events;

namespace Photobooth.Desktop.Camera;

/// <summary>
/// Adapter webcam chạy trong WebView2: <c>getUserMedia()</c> + phần tử &lt;video&gt; (xem wwwroot/js/camera.js).
/// Live preview chỉ nằm trong video element; dữ liệu ảnh chỉ được truyền sang .NET khi chụp,
/// dưới dạng stream nhị phân (IJSStreamReference) thay vì chuỗi Base64.
/// </summary>
/// <remarks>Đăng ký scoped vì phụ thuộc <see cref="IJSRuntime"/> của BlazorWebView.</remarks>
public sealed class WebViewCameraDevice : ICameraDevice, IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IJSRuntime _jsRuntime;
    private readonly CaptureOptions _options;
    private readonly CameraSelectionService _selection;
    private readonly ILogger<WebViewCameraDevice> _logger;
    private DotNetObjectReference<WebViewCameraDevice>? _selfReference;
    private ElementReference? _previewElement;
    private bool _disposed;

    public WebViewCameraDevice(
        IJSRuntime jsRuntime,
        CaptureOptions options,
        CameraSelectionService selection,
        ILogger<WebViewCameraDevice> logger)
    {
        _jsRuntime = jsRuntime;
        _options = options;
        _selection = selection;
        _logger = logger;
    }

    public event EventHandler<CameraError>? Faulted;

    /// <summary>Kích thước khung hình camera thay đổi (cũng là kích thước ảnh sẽ chụp: chụp từ chính video).</summary>
    public event EventHandler? FrameSizeChanged;

    public bool IsActive { get; private set; }

    /// <summary>Kích thước khung hình hiện tại của camera (null khi chưa mở).</summary>
    public (int Width, int Height)? FrameSize { get; private set; }

    /// <summary>Gắn phần tử &lt;video&gt; dùng để hiển thị preview. Phải gọi trước <see cref="StartAsync"/>.</summary>
    public void AttachPreview(ElementReference videoElement) => _previewElement = videoElement;

    /// <summary>Mở webcam đã chọn trong Admin (hoặc camera mặc định nếu chưa chọn).</summary>
    public Task<CameraStartResult> StartAsync(CancellationToken cancellationToken = default) =>
        StartCoreAsync(_selection.Selection, rememberResolvedDevice: true, cancellationToken);

    /// <summary>Mở một webcam cụ thể để xem thử trong Admin, không thay đổi lựa chọn đã lưu.</summary>
    public Task<CameraStartResult> StartPreviewAsync(CameraSelection selection, CancellationToken cancellationToken = default) =>
        StartCoreAsync(selection, rememberResolvedDevice: false, cancellationToken);

    /// <summary>
    /// Danh sách webcam Windows nhận thấy. <paramref name="requestLabels"/>: cho phép bật camera thoáng qua để lấy tên thiết bị
    /// (WebView2 chỉ cho biết tên sau khi đã cấp quyền camera) — chỉ dùng khi người quản trị chủ động mở danh sách.
    /// </summary>
    public async Task<WebcamListResult> ListWebcamsAsync(bool requestLabels, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var result = await _jsRuntime.InvokeAsync<JsListResult>("photoboothCamera.listDevices", cancellationToken, requestLabels);
            var devices = (result.Devices ?? [])
                .Where(d => !string.IsNullOrEmpty(d.DeviceId))
                .Select(d => new WebcamInfo(d.DeviceId!, d.Label ?? string.Empty))
                .ToArray();
            var error = result.ErrorName is null ? (CameraError?)null : MapBrowserError(result.ErrorName);
            return new WebcamListResult(devices, error);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Không đọc được danh sách webcam");
            return new WebcamListResult([], CameraError.Unknown);
        }
    }

    private async Task<CameraStartResult> StartCoreAsync(
        CameraSelection selection, bool rememberResolvedDevice, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_previewElement is null)
        {
            throw new InvalidOperationException("Chưa gắn phần tử preview cho camera.");
        }

        _selfReference ??= DotNetObjectReference.Create(this);
        IsActive = false;
        FrameSize = null;

        JsStartResult result;
        try
        {
            result = await _jsRuntime.InvokeAsync<JsStartResult>(
                "photoboothCamera.start",
                cancellationToken,
                _previewElement.Value,
                _selfReference,
                new
                {
                    idealWidth = _options.IdealWidthPixels,
                    idealHeight = _options.IdealHeightPixels,
                    deviceId = selection.IsDefault ? null : selection.WebcamDeviceId,
                    deviceLabel = selection.IsDefault ? null : selection.WebcamLabel,
                });
        }
        catch (JSException ex)
        {
            _logger.LogError(ex, "Lỗi JavaScript khi mở camera");
            return CameraStartResult.Failure(CameraError.Unknown);
        }

        if (result.Ok)
        {
            IsActive = true;
            FrameSize = result.Width > 0 && result.Height > 0 ? (result.Width, result.Height) : null;
            _logger.LogInformation("Camera {Camera} đã sẵn sàng ({Width}x{Height})", result.Label, result.Width, result.Height);

            if (rememberResolvedDevice
                && !selection.IsDefault
                && !string.IsNullOrEmpty(result.ResolvedDeviceId)
                && result.ResolvedDeviceId != selection.WebcamDeviceId)
            {
                await _selection.RememberResolvedDeviceAsync(selection.WebcamDeviceId!, result.ResolvedDeviceId);
            }

            return CameraStartResult.Success();
        }

        if (result.ErrorName == "Superseded")
        {
            throw new OperationCanceledException("Lần mở camera đã bị thay thế bởi thao tác khác.");
        }

        var error = MapBrowserError(result.ErrorName);
        _logger.LogWarning("Không mở được camera: {CameraError} ({BrowserError})", error, result.ErrorName);
        return CameraStartResult.Failure(error);
    }

    public async Task StopAsync()
    {
        var wasActive = IsActive;
        IsActive = false;
        FrameSize = null;
        try
        {
            // Giới hạn thời gian chờ: khi WebView đang đóng, lời gọi JS có thể không bao giờ được phản hồi.
            using var timeout = new CancellationTokenSource(StopTimeout);
            await _jsRuntime.InvokeVoidAsync("photoboothCamera.stop", timeout.Token);
            if (wasActive)
            {
                _logger.LogInformation("Đã dừng camera");
            }
        }
        catch (JSDisconnectedException)
        {
            // WebView đã đóng: trình duyệt đã tự giải phóng camera.
        }
        catch (ObjectDisposedException)
        {
            // Renderer đã hủy trong lúc đóng ứng dụng.
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Hết thời gian chờ khi dừng camera");
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Lỗi khi dừng camera");
        }
    }

    public async Task<CapturedFrame> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsActive)
        {
            throw new CameraException(CameraError.NotActive, "Camera chưa sẵn sàng.");
        }

        IJSStreamReference streamReference;
        try
        {
            streamReference = await _jsRuntime.InvokeAsync<IJSStreamReference>(
                "photoboothCamera.capture", cancellationToken, _options.JpegQuality);
        }
        catch (JSException ex) when (ex.Message.Contains("PB_NOT_ACTIVE", StringComparison.Ordinal))
        {
            IsActive = false;
            throw new CameraException(CameraError.NotActive, "Camera không còn hoạt động.", ex);
        }
        catch (JSException ex)
        {
            throw new CameraException(CameraError.CaptureFailed, "Không chụp được khung hình.", ex);
        }

        await using (streamReference)
        {
            if (streamReference.Length > _options.MaxFrameBytes)
            {
                throw new CameraException(CameraError.CaptureFailed, "Khung hình vượt quá dung lượng cho phép.");
            }

            try
            {
                await using var source = await streamReference.OpenReadStreamAsync(_options.MaxFrameBytes, cancellationToken);
                using var buffer = new MemoryStream(capacity: (int)Math.Max(0, streamReference.Length));
                await source.CopyToAsync(buffer, cancellationToken);
                return new CapturedFrame(buffer.ToArray(), JpegImageInfo.ContentType);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JSException)
            {
                throw new CameraException(CameraError.CaptureFailed, "Không nhận được dữ liệu ảnh từ camera.", ex);
            }
        }
    }

    /// <summary>Được camera.js gọi khi kích thước khung hình camera thay đổi.</summary>
    [JSInvokable]
    public void OnVideoResized(int width, int height)
    {
        if (!IsActive || width <= 0 || height <= 0 || FrameSize == (width, height))
        {
            return;
        }

        FrameSize = (width, height);
        _logger.LogInformation("Khung hình camera đổi thành {Width}x{Height}", width, height);
        FrameSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Được camera.js gọi khi track video kết thúc ngoài ý muốn (rút camera, bị chiếm quyền...).</summary>
    [JSInvokable]
    public void OnCameraTrackEnded()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        _logger.LogWarning("Camera bị ngắt kết nối khi đang hoạt động");
        Faulted?.Invoke(this, CameraError.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _selfReference?.Dispose();
        _selfReference = null;
    }

    internal static CameraError MapBrowserError(string? errorName) => errorName switch
    {
        "SelectedDeviceMissing" => CameraError.SelectedDeviceNotFound,
        "NoDevice" or "NotFoundError" or "DevicesNotFoundError" or "OverconstrainedError" => CameraError.NotFound,
        "NotAllowedError" or "PermissionDeniedError" or "SecurityError" => CameraError.PermissionDenied,
        "NotReadableError" or "TrackStartError" or "AbortError" => CameraError.InUse,
        "NotSupported" => CameraError.NotSupported,
        _ => CameraError.Unknown,
    };

    private sealed record JsStartResult(bool Ok, string? ErrorName, int Width, int Height, string? ResolvedDeviceId, string? Label);

    private sealed record JsListResult(JsDevice[]? Devices, string? ErrorName);

    private sealed record JsDevice(string? DeviceId, string? Label);
}

/// <summary>Một webcam Windows nhận thấy. <see cref="Label"/> rỗng khi chưa được cấp quyền camera.</summary>
public sealed record WebcamInfo(string DeviceId, string Label);

/// <summary>Kết quả liệt kê webcam; <see cref="Error"/> khác null khi không lấy được đầy đủ (ví dụ chưa có quyền).</summary>
public sealed record WebcamListResult(IReadOnlyList<WebcamInfo> Devices, CameraError? Error);

