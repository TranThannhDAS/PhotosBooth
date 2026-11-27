using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Desktop.Camera;
using Photobooth.Desktop.Platform;
using Photobooth.Infrastructure.Events;

namespace Photobooth.Desktop.Components.Pages;

/// <summary>
/// Màn hình chụp: preview → lượt chụp nhiều ảnh (đếm ngược từng ảnh) → tự chuyển sang xem lại khi đủ ảnh.
/// Component chỉ giữ trạng thái hiển thị/camera; trình tự chụp và chuyển trạng thái do <see cref="PhotoSessionService"/> quyết định.
/// </summary>
public sealed partial class CaptureScreen : SessionScreenBase
{
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _captureCts;
    private ElementReference _videoElement;
    private CameraUiState _cameraState = CameraUiState.Off;
    private CameraError? _cameraError;
    private CaptureProgress? _progress;
    private StatusInfo? _message;
    private Task? _captureTask;
    private bool _exiting;

    private enum CameraUiState
    {
        Off,
        Connecting,
        Ready,
        Failed,
    }

    [Inject]
    private WebViewCameraDevice Camera { get; set; } = default!;

    [Inject]
    private WindowsShellService Shell { get; set; } = default!;

    [Inject]
    private CaptureGuideSettingsService GuideSettings { get; set; } = default!;

    private CaptureGuideSettings _guideSettings = new();

    private string GuideModeClass => _guideSettings.Mode switch
    {
        CaptureGuideMode.Outline => "guide-outline",
        CaptureGuideMode.CropPreview => "guide-crop-preview",
        _ => "guide-dim-outside",
    };

    [Inject]
    private ILogger<CaptureScreen> Logger { get; set; } = default!;

    [Parameter, EditorRequired]
    public EventCallback OnCancel { get; set; }

    private int SlotCount => Session?.RequiredPhotoCount ?? 0;

    private bool IsCapturing => Session?.State == PhotoSessionState.Capturing;

    private bool Mirror => Session?.Mirror ?? true;

    private bool CanCapture =>
        Session is { State: PhotoSessionState.Previewing, PendingPositions.Count: > 0 }
        && _cameraState == CameraUiState.Ready
        && _captureCts is null
        && !_exiting;

    private string PrimaryLabel
    {
        get
        {
            if (_exiting)
            {
                return "Đang thoát…";
            }

            if (IsCapturing || Session is null)
            {
                return "Đang chụp…";
            }

            var pending = Session.PendingPositions;
            if (Session.IsRetake)
            {
                return pending.Count == 1
                    ? string.Create(CultureInfo.InvariantCulture, $"Chụp lại ảnh {pending[0] + 1}")
                    : "Chụp lại tất cả";
            }

            return Session.CapturedCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Chụp tiếp ({pending.Count} ảnh)")
                : string.Create(CultureInfo.InvariantCulture, $"Bắt đầu chụp {pending.Count} ảnh");
        }
    }

    private string ProgressTitle => _progress is { } p
        ? UiText.PhotoLabel(p.Position, p.PhotoCount)
        : string.Create(CultureInfo.InvariantCulture, $"Đã chụp {Session?.CapturedCount ?? 0}/{SlotCount} ảnh");

    private StatusInfo CameraStatus => _cameraState switch
    {
        CameraUiState.Connecting => UiText.CameraConnecting,
        CameraUiState.Failed => UiText.ForCameraError(_cameraError),
        CameraUiState.Ready => IsCapturing ? UiText.Capturing : UiText.CameraReady,
        _ => UiText.CameraOff,
    };

    // Camera Framing Overlay: chỉ tính lại khi template/ảnh đang chụp/kích thước camera/chế độ gương đổi.
    private (PhotoTemplate Template, int PhotoIndex, int Width, int Height, bool Mirror)? _guideKey;
    private FramingGuide? _guide;

    /// <summary>Ảnh sắp/đang chụp: ảnh trong lượt chụp, hoặc vị trí chờ chụp đầu tiên.</summary>
    private int? GuidePhotoIndex => _progress?.Position
        ?? (Session is { PendingPositions.Count: > 0 } s ? s.PendingPositions[0] : null);

    /// <summary>Khung hướng dẫn vùng ảnh thành phẩm trên live preview (null = không hiển thị).</summary>
    private FramingGuide? Guide
    {
        get
        {
            if (_cameraState != CameraUiState.Ready || Session?.Template is not { } template
                || GuidePhotoIndex is not { } photoIndex || Camera.FrameSize is not { } frame)
            {
                return null;
            }

            var key = (template, photoIndex, frame.Width, frame.Height, Mirror);
            if (_guideKey != key)
            {
                _guideKey = key;
                _guide = CaptureFraming.Compute(template, photoIndex, frame.Width, frame.Height, Mirror);
            }

            return _guide;
        }
    }

    private string FramingStyle => Camera.FrameSize is { } frame
        ? string.Create(CultureInfo.InvariantCulture,
            $"--frame-aspect: {frame.Width} / {frame.Height}; --frame-ratio: {(double)frame.Width / frame.Height}; " +
            $"--guide-outside-opacity: {_guideSettings.OutsideOpacity};")
        : string.Empty;

    private static string GuideWindowStyle(FramingGuide guide) => string.Create(
        CultureInfo.InvariantCulture,
        $"left: {guide.X * 100}%; top: {guide.Y * 100}%; width: {guide.Width * 100}%; height: {guide.Height * 100}%;");

    protected override void OnInitialized()
    {
        base.OnInitialized();
        // Chốt thiết lập hiển thị khi vào màn chụp, không thay đổi giữa một lượt chụp.
        _guideSettings = GuideSettings.Current;
        Camera.Faulted += OnCameraFaulted;
        Camera.FrameSizeChanged += OnFrameSizeChanged;
    }

    private void OnFrameSizeChanged(object? sender, EventArgs e) => _ = InvokeAsync(() =>
    {
        if (!IsDisposed)
        {
            StateHasChanged();
        }
    });

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            Camera.AttachPreview(_videoElement);
            await StartCameraAsync();
        }
    }

    private async Task StartCameraAsync()
    {
        if (IsDisposed || _exiting || _cameraState == CameraUiState.Connecting)
        {
            return;
        }

        _cameraState = CameraUiState.Connecting;
        _cameraError = null;
        StateHasChanged();

        try
        {
            var result = await Camera.StartAsync(_lifetimeCts.Token);
            if (IsDisposed)
            {
                return;
            }

            if (result.Succeeded)
            {
                _cameraState = CameraUiState.Ready;
                Sessions.MarkPreviewReady();
            }
            else
            {
                _cameraState = CameraUiState.Failed;
                _cameraError = result.Error;
            }
        }
        catch (OperationCanceledException)
        {
            // Rời màn hình hoặc có lệnh dừng camera mới trong lúc đang mở.
            if (IsDisposed)
            {
                return;
            }

            _cameraState = CameraUiState.Off;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            Logger.LogError(ex, "Không khởi động được camera");
            _cameraState = CameraUiState.Failed;
            _cameraError = CameraError.Unknown;
        }

        StateHasChanged();
    }

    private Task CaptureAsync()
    {
        if (!CanCapture)
        {
            return Task.CompletedTask;
        }

        _captureTask = CaptureCoreAsync();
        return _captureTask;
    }

    private async Task CaptureCoreAsync()
    {
        _message = null;
        _captureCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var progress = new Progress<CaptureProgress>(value => _ = InvokeAsync(() =>
        {
            if (IsDisposed || _captureCts is null)
            {
                return;
            }

            _progress = value;
            StateHasChanged();
        }));

        try
        {
            var result = await Sessions.CaptureSequenceAsync(Camera, progress, _captureCts.Token);
            if (result.Succeeded && !_exiting && Sessions.Current?.State == PhotoSessionState.Reviewing)
            {
                // Đủ ảnh theo template → tự ghép và chuyển sang màn xem thành phẩm (Photo Preview).
                // Ghép lỗi thì phiên ở lại màn xem lại với thông báo và nút "Ghép ảnh" để thử lại.
                await Sessions.ComposeAsync();
            }

            if (IsDisposed)
            {
                // Đủ ảnh → phiên chuyển sang xem lại và màn hình này đã được gỡ.
                return;
            }

            _message = UiText.ForCaptureFailure(result);
            if (result.Failure == CaptureFailureReason.CameraFailed
                && result.CameraError is CameraError.NotActive or CameraError.Disconnected)
            {
                _cameraState = CameraUiState.Failed;
                _cameraError = CameraError.Disconnected;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ranh giới UI: lỗi ngoài dự kiến không làm sập giao diện; ảnh đã lưu không bị ảnh hưởng.
            Logger.LogError(ex, "Lỗi ngoài dự kiến khi chụp ảnh");
            _message = UiText.UnexpectedError;
        }
        finally
        {
            _captureCts?.Dispose();
            _captureCts = null;
            _progress = null;
        }

        if (!IsDisposed)
        {
            StateHasChanged();
        }
    }

    private void CancelCapture() => _captureCts?.Cancel();

    private async Task ExitCaptureAsync()
    {
        if (_exiting || IsDisposed)
        {
            return;
        }

        _exiting = true;
        var sessionId = Session?.SessionId;
        try
        {
            _captureCts?.Cancel();
            // Khung hình đã lấy phải được ghi và cập nhật metadata trước khi kết thúc phiên.
            if (_captureTask is not null)
            {
                await _captureTask;
            }

            // Phiên có thể vừa chuyển sang xem lại ở ảnh cuối; vẫn hoàn tất yêu cầu thoát,
            // nhưng không được kết thúc một phiên mới do thao tác khác tạo ra.
            if (Sessions.Current?.SessionId != sessionId)
            {
                return;
            }

            await Camera.StopAsync();
            _cameraState = CameraUiState.Off;
            if (Sessions.Current?.SessionId == sessionId)
            {
                await OnCancel.InvokeAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Không thoát được phiên chụp");
            if (!IsDisposed)
            {
                _message = new StatusInfo(StatusKind.Error, "Chưa thoát được phiên chụp.", "Ảnh đã chụp vẫn được giữ. Vui lòng thử lại.");
            }
        }
        finally
        {
            _exiting = false;
        }
    }

    private void ToggleMirror() => Sessions.SetMirror(!Mirror);

    private async Task KeepOldPhotosAsync()
    {
        await Camera.StopAsync();
        _cameraState = CameraUiState.Off;
        Sessions.ReturnToReview();
    }

    private async Task ChangeTemplateAsync()
    {
        await Camera.StopAsync();
        _cameraState = CameraUiState.Off;
        Sessions.ReturnToTemplateSelection();
    }

    private void OpenCameraSettings()
    {
        if (!Shell.OpenCameraPrivacySettings())
        {
            _message = new StatusInfo(StatusKind.Warning, "Không mở được Cài đặt Windows.", "Hãy mở Cài đặt › Quyền riêng tư › Camera theo cách thủ công.");
        }
    }

    private void OnCameraFaulted(object? sender, CameraError error)
    {
        _ = InvokeAsync(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            // Dừng lượt chụp đang chạy: không thể chụp khi camera đã mất. Ảnh đã lưu được giữ.
            _captureCts?.Cancel();
            _cameraState = CameraUiState.Failed;
            _cameraError = error;
            StateHasChanged();
        });
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        Camera.Faulted -= OnCameraFaulted;
        Camera.FrameSizeChanged -= OnFrameSizeChanged;

        // Hủy đếm ngược; nếu khung hình đã lấy được, service vẫn ghi trọn file gốc.
        _lifetimeCts.Cancel();
        _captureCts?.Cancel();

        await Camera.StopAsync();
        _lifetimeCts.Dispose();
    }
}
