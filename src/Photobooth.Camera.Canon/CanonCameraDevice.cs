using EDSDKLib;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Camera;
using Photobooth.Application.Photos;
using Photobooth.Camera.Canon.Native;

namespace Photobooth.Camera.Canon;

/// <summary>
/// Máy ảnh Canon EOS qua EDSDK, hiện thực <see cref="ICameraDevice"/>.
/// Chụp = bấm nút chụp → chờ máy ảnh báo có ảnh mới (<c>ObjectEvent_DirItemRequestTransfer</c>) → tải ảnh gốc JPEG vào bộ nhớ.
/// Ảnh không lưu trên thẻ nhớ (SaveTo = Host); việc lưu ảnh gốc vẫn do Session Service đảm nhận như với webcam.
/// </summary>
/// <remarks>
/// Trạng thái gắn với SDK (<see cref="_camera"/>, <see cref="_pendingTransfer"/>) chỉ được đọc/ghi trên luồng SDK
/// của <see cref="CanonSdkHost"/>, nên không cần khóa. Live View chưa thuộc lớp này.
/// </remarks>
public sealed class CanonCameraDevice : ICameraDevice, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan SyncDisposeTimeout = TimeSpan.FromSeconds(3);
    private readonly CanonSdkHost _host;
    private readonly CanonCameraOptions _options;
    private readonly ILogger<CanonCameraDevice> _logger;
    private const int MaxConsecutiveLiveViewErrors = 20;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _liveViewGate = new(1, 1);

    // Chỉ truy cập trên luồng SDK.
    private IntPtr _camera;
    private TaskCompletionSource<IntPtr>? _pendingTransfer;

    private volatile bool _isActive;
    private volatile CancellationTokenSource? _liveViewCts;
    private Task? _liveViewLoop;
    private bool _disposed;

    public CanonCameraDevice(CanonSdkHost host, CanonCameraOptions options, ILogger<CanonCameraDevice> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _host = host;
        _options = options;
        _logger = logger;
    }

    public event EventHandler<CameraError>? Faulted;

    public bool IsActive => _isActive;

    /// <summary>Tên máy ảnh đang kết nối (ví dụ "Canon EOS M50m2"); null khi chưa kết nối.</summary>
    public string? CameraName { get; private set; }

    /// <summary>Khung Live View mới nhất (JPEG ~960×640 trên EOS M50 Mark II).</summary>
    public LiveViewFrameBuffer LiveView { get; } = new();

    public bool IsLiveViewRunning => _liveViewLoop is { IsCompleted: false };

    /// <summary>
    /// Một thông số trong <see cref="CanonSettingCatalog"/> vừa đổi giá trị hoặc đổi danh sách giá trị hợp lệ
    /// (do xoay vòng chế độ trên máy hoặc do phần mềm đặt). Được gọi trên thread pool, không phải luồng giao diện.
    /// </summary>
    public event EventHandler? SettingsChanged;

    /// <summary>% pin, mức nhiệt hoặc ống kính vừa đổi. Được gọi trên thread pool, không phải luồng giao diện.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Đọc trạng thái máy (pin, cảnh báo nhiệt, ống kính); mục máy không cung cấp để null/Unknown.</summary>
    public async Task<CanonCameraStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isActive)
        {
            throw new CameraException(CameraError.NotActive, "Máy ảnh chưa kết nối.");
        }

        return await _host.InvokeAsync(api =>
        {
            if (_camera == IntPtr.Zero)
            {
                throw new CameraException(CameraError.Disconnected, "Máy ảnh đã bị ngắt kết nối.");
            }

            var hasBattery = api.GetUInt32(_camera, EDSDK.PropID_BatteryLevel, out var battery) == EDSDK.EDS_ERR_OK;
            var temperature = api.GetUInt32(_camera, EDSDK.PropID_TempStatus, out var temp) == EDSDK.EDS_ERR_OK
                ? CanonCameraStatus.ParseTemperature(temp)
                : CanonTemperatureStatus.Unknown;
            var lens = api.GetString(_camera, EDSDK.PropID_LensName, out var lensName) == EDSDK.EDS_ERR_OK && !string.IsNullOrWhiteSpace(lensName)
                ? lensName.Trim()
                : null;

            // 0xFFFFFFFF = đang dùng nguồn AC.
            var onAc = hasBattery && battery == 0xffffffff;
            int? percent = hasBattery && !onAc && battery <= 100 ? (int)battery : null;
            return new CanonCameraStatus(percent, onAc, temperature, lens);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Đọc các thông số máy ảnh hỗ trợ; thông số máy không có (không đọc được) được bỏ qua.</summary>
    public async Task<IReadOnlyList<CanonSettingState>> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isActive)
        {
            throw new CameraException(CameraError.NotActive, "Máy ảnh chưa kết nối.");
        }

        return await _host.InvokeAsync(ReadSettingsOnSdkThread, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Đặt thông số trực tiếp trên máy ảnh (máy tự ghi nhớ, kể cả sau khi tắt nguồn).
    /// Ném <see cref="CameraException"/> khi máy không chấp nhận giá trị ở chế độ chụp hiện tại.
    /// </summary>
    public async Task SetSettingAsync(CanonSetting setting, uint code, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (setting == CanonSetting.ExposureMode)
        {
            throw new ArgumentException("Chế độ chụp chỉ đổi được bằng vòng chế độ trên máy ảnh.", nameof(setting));
        }

        if (!_isActive)
        {
            throw new CameraException(CameraError.NotActive, "Máy ảnh chưa kết nối.");
        }

        var propertyId = CanonSettingCatalog.PropertyId(setting);
        var err = await SendWithBusyRetryAsync(
                api => CanonSettingCatalog.IsPictureStyleDetail(setting)
                    ? SetPictureStyleDetailOnSdkThread(api, setting, code)
                    : api.SetUInt32(_camera, propertyId, code),
                cancellationToken)
            .ConfigureAwait(false);
        var label = CanonSettingCatalog.Label(setting, code);
        if (err != EDSDK.EDS_ERR_OK)
        {
            _logger.LogWarning("Máy ảnh Canon không nhận {Setting} = {Value}: {Error}", setting, label, EdsErrors.Describe(err));
            throw err == EDSDK.EDS_ERR_COMM_DISCONNECTED
                ? new CameraException(CameraError.Disconnected, "Máy ảnh đã bị ngắt kết nối.")
                : new CameraException(CameraError.Unknown, $"Máy ảnh không nhận {CanonSettingCatalog.Name(setting)} = {label} ({EdsErrors.Describe(err)}).");
        }

        _logger.LogInformation("Đã đặt {Setting} = {Value} trên máy ảnh Canon", setting, label);
    }

    /// <summary>
    /// Bật Live View và bắt đầu nhận khung vào <see cref="LiveView"/>. Gọi lại khi đang chạy không có tác dụng.
    /// Ném <see cref="CameraException"/> khi máy ảnh chưa kết nối hoặc từ chối bật Live View.
    /// </summary>
    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _liveViewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isActive)
            {
                throw new CameraException(CameraError.NotActive, "Máy ảnh chưa kết nối.");
            }

            if (IsLiveViewRunning)
            {
                return;
            }

            var err = await SendWithBusyRetryAsync(api => api.StartLiveView(_camera), cancellationToken).ConfigureAwait(false);
            if (err != EDSDK.EDS_ERR_OK)
            {
                _logger.LogWarning("Máy ảnh Canon không bật được Live View: {Error}", EdsErrors.Describe(err));
                throw new CameraException(MapConnectError(err), $"Máy ảnh không bật được Live View ({EdsErrors.Describe(err)}).");
            }

            // Vòng cũ có thể đã tự thoát (mất kết nối) mà chưa được dọn.
            _liveViewCts?.Dispose();
            var cts = new CancellationTokenSource();
            _liveViewCts = cts;
            _liveViewLoop = Task.Run(() => RunLiveViewLoopAsync(cts.Token), CancellationToken.None);
            _logger.LogInformation("Đã bật Live View máy ảnh Canon");
        }
        finally
        {
            _liveViewGate.Release();
        }
    }

    /// <summary>Dừng nhận khung và tắt Live View trên máy ảnh. Gọi nhiều lần không lỗi.</summary>
    public async Task StopLiveViewAsync()
    {
        await _liveViewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = _liveViewCts;
            var loop = _liveViewLoop;
            _liveViewCts = null;
            _liveViewLoop = null;
            if (cts is null)
            {
                return;
            }

            cts.Cancel();
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }

            cts.Dispose();
            LiveView.Clear();

            try
            {
                await _host.InvokeAsync(api =>
                {
                    if (_camera != IntPtr.Zero)
                    {
                        api.StopLiveView(_camera);
                    }
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // SDK host đã dừng: máy ảnh tự thoát Live View khi kết thúc session.
            }

            _logger.LogInformation("Đã tắt Live View máy ảnh Canon");
        }
        finally
        {
            _liveViewGate.Release();
        }
    }

    public async Task<CameraStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isActive)
            {
                return CameraStartResult.Success();
            }

            if (!await TryInitializeSdkAsync(cancellationToken).ConfigureAwait(false))
            {
                return CameraStartResult.Failure(CameraError.NotSupported);
            }

            var err = EDSDK.EDS_ERR_DEVICE_NOT_FOUND;
            for (var attempt = 1; attempt <= _options.ConnectAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(_options.ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
                }

                (err, var name) = await _host.InvokeAsync(ConnectOnSdkThread, cancellationToken).ConfigureAwait(false);
                if (err == EDSDK.EDS_ERR_OK)
                {
                    CameraName = name;
                    _isActive = true;
                    _logger.LogInformation("Đã kết nối máy ảnh Canon {CameraName}", name);
                    return CameraStartResult.Success();
                }

                if (!IsRetryableConnectError(err))
                {
                    break;
                }

                _logger.LogDebug("Chưa kết nối được máy ảnh Canon (lần {Attempt}): {Error}", attempt, EdsErrors.Describe(err));
            }

            var error = MapConnectError(err);
            _logger.LogWarning("Không kết nối được máy ảnh Canon: {CameraError} ({Error})", error, EdsErrors.Describe(err));
            return CameraStartResult.Failure(error);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await StopLiveViewAsync().ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wasActive = _isActive;
            _isActive = false;
            CameraName = null;

            try
            {
                await _host.InvokeAsync(api => DisconnectOnSdkThread(api, closeSession: true)).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // SDK host đã dừng (đóng ứng dụng): EdsTerminateSDK đã giải phóng mọi thứ.
            }

            if (wasActive)
            {
                _logger.LogInformation("Đã ngắt kết nối máy ảnh Canon");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<CapturedFrame> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isActive)
            {
                throw new CameraException(CameraError.NotActive, "Máy ảnh chưa sẵn sàng.");
            }

            var pending = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _host.InvokeAsync(_ => _pendingTransfer = pending, cancellationToken).ConfigureAwait(false);

            var itemConsumed = false;
            try
            {
                await PressShutterAsync(cancellationToken).ConfigureAwait(false);

                IntPtr item;
                try
                {
                    item = await pending.Task.WaitAsync(_options.TransferTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new CameraException(
                        CameraError.CaptureFailed,
                        $"Máy ảnh không gửi ảnh về sau {_options.TransferTimeout.TotalSeconds:0} giây.",
                        ex);
                }

                itemConsumed = true;
                return await DownloadAsync(item).ConfigureAwait(false);
            }
            finally
            {
                await ClearPendingTransferAsync(pending, itemConsumed).ConfigureAwait(false);
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    /// <summary>
    /// Dùng khi DI container giải phóng đồng bộ lúc đóng ứng dụng. Chờ có giới hạn: mọi bước dừng chạy trên luồng SDK
    /// (không cần luồng gọi), nên không bị khóa chéo; quá thời gian thì EdsTerminateSDK vẫn giải phóng máy ảnh.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!Task.Run(StopAsync).Wait(SyncDisposeTimeout))
        {
            _logger.LogWarning("Hết thời gian chờ ngắt kết nối máy ảnh Canon khi đóng ứng dụng");
        }

        _disposed = true;
    }

    // ---------------------------------------------------------------- Kết nối

    private async Task<bool> TryInitializeSdkAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _host.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private (uint Error, string Name) ConnectOnSdkThread(IEdsApi api)
    {
        var err = api.FindFirstCamera(out var camera, out var name);
        if (err != EDSDK.EDS_ERR_OK)
        {
            return (err, string.Empty);
        }

        var sessionOpened = false;
        err = api.SetEventHandlers(camera, OnTransferRequested, OnStateEvent, OnPropertyChanged);
        if (err == EDSDK.EDS_ERR_OK)
        {
            api.UnlockPrivateProperties(camera);
            err = api.OpenSession(camera);
            sessionOpened = err == EDSDK.EDS_ERR_OK;
        }

        if (err == EDSDK.EDS_ERR_OK)
        {
            err = api.SetSaveToHost(camera);
        }

        if (err != EDSDK.EDS_ERR_OK)
        {
            if (sessionOpened)
            {
                api.CloseSession(camera);
            }

            api.ReleaseCamera(camera);
            return (err, string.Empty);
        }

        _camera = camera;
        return (EDSDK.EDS_ERR_OK, name);
    }

    private void DisconnectOnSdkThread(IEdsApi api, bool closeSession)
    {
        FailPendingTransfer(new CameraException(CameraError.NotActive, "Máy ảnh đã dừng."));
        if (_camera == IntPtr.Zero)
        {
            return;
        }

        var camera = _camera;
        _camera = IntPtr.Zero;
        if (closeSession)
        {
            var err = api.CloseSession(camera);
            if (err != EDSDK.EDS_ERR_OK)
            {
                _logger.LogWarning("Đóng session máy ảnh Canon lỗi: {Error}", EdsErrors.Describe(err));
            }
        }

        api.ReleaseCamera(camera);
    }

    private static bool IsRetryableConnectError(uint err) =>
        err is EDSDK.EDS_ERR_DEVICE_NOT_FOUND or EDSDK.EDS_ERR_DEVICE_BUSY;

    internal static CameraError MapConnectError(uint err) => err switch
    {
        EDSDK.EDS_ERR_DEVICE_NOT_FOUND => CameraError.NotFound,
        EDSDK.EDS_ERR_DEVICE_BUSY or EDSDK.EDS_ERR_COMM_PORT_IS_IN_USE => CameraError.InUse,
        EDSDK.EDS_ERR_COMM_DISCONNECTED => CameraError.Disconnected,
        _ => CameraError.Unknown,
    };

    /// <summary>
    /// Đưa máy ảnh về đúng bộ thông số đã lưu. Từng thông số được đặt độc lập: thông số không đặt được ở trạng thái hiện tại
    /// (chế độ chụp khác, ống kính không có khẩu độ đó...) được bỏ qua kèm lý do thay vì dừng cả bộ.
    /// Mất kết nối giữa chừng thì ném <see cref="CameraException"/>.
    /// </summary>
    public async Task<CanonPresetApplyResult> ApplyPresetAsync(CanonPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var states = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var currentMode = states.FirstOrDefault(s => s.Setting == CanonSetting.ExposureMode)?.Current?.Label;
        var expectedMode = preset.ExposureMode is { } mode ? CanonSettingCatalog.Label(CanonSetting.ExposureMode, mode) : null;

        // Thông số "cha" trước "con": Kelvin chỉ đặt được khi cân bằng trắng đang ở "Nhiệt độ màu";
        // chi tiết (tương phản, bão hòa...) thuộc về Picture Style đang chọn nên đặt sau Picture Style.
        var ordered = preset.Values
            .Where(v => v.Setting != CanonSetting.ExposureMode)
            .OrderBy(v => v.Setting switch
            {
                CanonSetting.WhiteBalance or CanonSetting.PictureStyle => 0,
                CanonSetting.ColorTemperature => 2,
                _ when CanonSettingCatalog.IsPictureStyleDetail(v.Setting) => 3,
                _ => 1,
            });

        var items = new List<CanonPresetApplyItem>();
        var parentChanged = false;
        foreach (var value in ordered)
        {
            var isChild = value.Setting == CanonSetting.ColorTemperature || CanonSettingCatalog.IsPictureStyleDetail(value.Setting);
            if (isChild && parentChanged)
            {
                states = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                parentChanged = false;
            }

            var item = await ApplyValueAsync(value, states, cancellationToken).ConfigureAwait(false);
            items.Add(item);
            parentChanged |= value.Setting is CanonSetting.WhiteBalance or CanonSetting.PictureStyle
                && item.Status == CanonPresetApplyStatus.Applied;
        }

        var result = new CanonPresetApplyResult(items, expectedMode, currentMode);
        _logger.LogInformation(
            "Áp dụng bộ thông số \"{PresetName}\": {Applied} đặt mới, {Unchanged} giữ nguyên, {Skipped} bỏ qua, {Failed} lỗi",
            preset.Name,
            items.Count(i => i.Status == CanonPresetApplyStatus.Applied),
            items.Count(i => i.Status == CanonPresetApplyStatus.Unchanged),
            items.Count(i => i.Status == CanonPresetApplyStatus.Skipped),
            items.Count(i => i.Status == CanonPresetApplyStatus.Failed));
        return result;
    }

    private async Task<CanonPresetApplyItem> ApplyValueAsync(
        CanonPresetValue value, IReadOnlyList<CanonSettingState> states, CancellationToken cancellationToken)
    {
        var name = CanonSettingCatalog.Name(value.Setting);
        var label = CanonSettingCatalog.Label(value.Setting, value.Code);
        CanonPresetApplyItem Item(CanonPresetApplyStatus status, string? reason = null) => new(value.Setting, name, label, status, reason);

        var state = states.FirstOrDefault(s => s.Setting == value.Setting);
        if (state is null)
        {
            return Item(CanonPresetApplyStatus.Skipped, "Máy ảnh này không có thông số này.");
        }

        if (state.Current?.Code == value.Code)
        {
            return Item(CanonPresetApplyStatus.Unchanged);
        }

        if (!state.IsWritable)
        {
            return Item(CanonPresetApplyStatus.Skipped, value.Setting == CanonSetting.ColorTemperature
                ? "Cân bằng trắng không ở chế độ “Nhiệt độ màu (K)”."
                : "Không chỉnh được ở chế độ chụp hiện tại.");
        }

        if (state.Options.Count > 0 && state.Options.All(o => o.Code != value.Code))
        {
            return Item(CanonPresetApplyStatus.Skipped, "Giá trị không dùng được với chế độ chụp/ống kính hiện tại.");
        }

        try
        {
            await SetSettingAsync(value.Setting, value.Code, cancellationToken).ConfigureAwait(false);
            return Item(CanonPresetApplyStatus.Applied);
        }
        catch (CameraException ex) when (ex.Error != CameraError.Disconnected)
        {
            return Item(CanonPresetApplyStatus.Failed, ex.Message);
        }
    }

    // ---------------------------------------------------------------- Thông số

    private IReadOnlyList<CanonSettingState> ReadSettingsOnSdkThread(IEdsApi api)
    {
        if (_camera == IntPtr.Zero)
        {
            throw new CameraException(CameraError.Disconnected, "Máy ảnh đã bị ngắt kết nối.");
        }

        uint? whiteBalance = null;
        uint? pictureStyle = null;
        EDSDK.EdsPictureStyleDesc? styleDetail = null;
        var states = new List<CanonSettingState>();
        foreach (var setting in CanonSettingCatalog.All)
        {
            if (CanonSettingCatalog.IsPictureStyleDetail(setting))
            {
                // Chi tiết của Picture Style đang chọn; máy không hỗ trợ thì cả nhóm được bỏ qua.
                styleDetail ??= pictureStyle is { } style && api.GetPictureStyleDesc(_camera, style, out var desc) == EDSDK.EDS_ERR_OK
                    ? desc
                    : null;
                if (styleDetail is { } detail)
                {
                    var current = ReadDetail(detail, setting);
                    states.Add(new CanonSettingState(
                        setting,
                        CanonSettingCatalog.Name(setting),
                        new CanonSettingValue(current, CanonSettingCatalog.Label(setting, current)),
                        CanonSettingCatalog.PictureStyleDetailRange(setting)
                            .Select(value => new CanonSettingValue(value, CanonSettingCatalog.Label(setting, value))).ToArray(),
                        IsWritable: true));
                }

                continue;
            }

            var propertyId = CanonSettingCatalog.PropertyId(setting);
            var hasCurrent = api.GetUInt32(_camera, propertyId, out var code) == EDSDK.EDS_ERR_OK;
            if (setting == CanonSetting.WhiteBalance && hasCurrent)
            {
                whiteBalance = code;
            }

            if (setting == CanonSetting.PictureStyle && hasCurrent)
            {
                pictureStyle = code;
            }

            IReadOnlyList<uint> allowed = [];
            if (setting != CanonSetting.ExposureMode && api.GetAllowedValues(_camera, propertyId, out var values) == EDSDK.EDS_ERR_OK)
            {
                allowed = values;
            }

            if (setting == CanonSetting.ImageQuality)
            {
                // Chỉ cho chọn JPEG: RAW/HEIF làm adapter không nhận được ảnh.
                allowed = allowed.Where(CanonSettingCatalog.IsJpegQuality).ToArray();
            }

            bool writable;
            if (setting == CanonSetting.ColorTemperature)
            {
                if (!hasCurrent)
                {
                    continue;
                }

                // Máy thường không trả danh sách cho Kelvin: dùng các mức chuẩn 2500–10000 K.
                allowed = allowed.Count > 0 ? allowed : CanonSettingCatalog.DefaultColorTemperatures;
                writable = whiteBalance == CanonSettingCatalog.WhiteBalanceColorTemperature;
            }
            else
            {
                writable = setting != CanonSetting.ExposureMode && allowed.Count > 1;
            }

            if (!hasCurrent && allowed.Count == 0)
            {
                // Máy này không có thông số này.
                continue;
            }

            states.Add(new CanonSettingState(
                setting,
                CanonSettingCatalog.Name(setting),
                hasCurrent ? new CanonSettingValue(code, CanonSettingCatalog.Label(setting, code)) : null,
                allowed.Select(value => new CanonSettingValue(value, CanonSettingCatalog.Label(setting, value))).ToArray(),
                writable));
        }

        return states;
    }

    private static uint ReadDetail(EDSDK.EdsPictureStyleDesc detail, CanonSetting setting) => setting switch
    {
        CanonSetting.PictureStyleContrast => unchecked((uint)detail.contrast),
        CanonSetting.PictureStyleSaturation => unchecked((uint)detail.saturation),
        CanonSetting.PictureStyleColorTone => unchecked((uint)detail.colorTone),
        _ => detail.sharpness,
    };

    /// <summary>Đọc chi tiết Picture Style hiện tại, đổi một mục, ghi lại (giữ nguyên các mục khác như hiệu ứng lọc/ngưỡng nét).</summary>
    private uint SetPictureStyleDetailOnSdkThread(IEdsApi api, CanonSetting setting, uint code)
    {
        var err = api.GetUInt32(_camera, EDSDK.PropID_PictureStyle, out var style);
        if (err == EDSDK.EDS_ERR_OK)
        {
            err = api.GetPictureStyleDesc(_camera, style, out var detail);
            if (err == EDSDK.EDS_ERR_OK)
            {
                var signed = unchecked((int)code);
                switch (setting)
                {
                    case CanonSetting.PictureStyleContrast:
                        detail.contrast = signed;
                        break;
                    case CanonSetting.PictureStyleSaturation:
                        detail.saturation = signed;
                        break;
                    case CanonSetting.PictureStyleColorTone:
                        detail.colorTone = signed;
                        break;
                    default:
                        detail.sharpness = code;
                        break;
                }

                err = api.SetPictureStyleDesc(_camera, style, detail);
            }
        }

        return err;
    }

    private void OnPropertyChanged(uint propertyId)
    {
        // Không gọi handler của ứng dụng trên luồng SDK.
        if (propertyId is EDSDK.PropID_BatteryLevel or EDSDK.PropID_TempStatus or EDSDK.PropID_LensName)
        {
            ThreadPool.QueueUserWorkItem(_ => StatusChanged?.Invoke(this, EventArgs.Empty));
        }

        if (CanonSettingCatalog.FromPropertyId(propertyId) is not null)
        {
            ThreadPool.QueueUserWorkItem(_ => SettingsChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    // ---------------------------------------------------------------- Live View

    /// <summary>
    /// Kéo khung liên tục: mỗi lần lấy là một công việc riêng trên luồng SDK, nên lệnh chụp xen vào được giữa hai khung
    /// (đã kiểm chứng trên EOS M50 Mark II: chụp khi đang Live View không lỗi).
    /// </summary>
    private async Task RunLiveViewLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveErrors = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Đưa khung vào bộ đệm ngay trên luồng SDK: cùng luồng với xử lý ngắt kết nối,
                // nên không thể có khung cũ được đưa vào sau khi máy đã ngắt (và bộ đệm đã bị xóa).
                var err = await _host.InvokeAsync(api =>
                {
                    if (_camera == IntPtr.Zero || cancellationToken.IsCancellationRequested)
                    {
                        return EDSDK.EDS_ERR_COMM_DISCONNECTED;
                    }

                    var result = api.DownloadLiveViewFrame(_camera, out var frame);
                    if (result == EDSDK.EDS_ERR_OK)
                    {
                        LiveView.Publish(frame);
                    }

                    return result;
                }, cancellationToken).ConfigureAwait(false);

                switch (err)
                {
                    case EDSDK.EDS_ERR_OK:
                        consecutiveErrors = 0;
                        break;
                    case EDSDK.EDS_ERR_OBJECT_NOTREADY:
                        // Khung chưa sẵn sàng: bình thường, nhất là ngay sau khi bật Live View hoặc khi đang chụp.
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                        break;
                    case EDSDK.EDS_ERR_DEVICE_BUSY:
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        break;
                    case EDSDK.EDS_ERR_COMM_DISCONNECTED:
                        return;
                    default:
                        if (++consecutiveErrors >= MaxConsecutiveLiveViewErrors)
                        {
                            _logger.LogError("Dừng Live View sau {Count} lỗi liên tiếp: {Error}", consecutiveErrors, EdsErrors.Describe(err));
                            return;
                        }

                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dừng theo yêu cầu.
        }
        catch (ObjectDisposedException)
        {
            // SDK host đã dừng (đóng ứng dụng).
        }
    }

    // ---------------------------------------------------------------- Chụp

    private async Task PressShutterAsync(CancellationToken cancellationToken)
    {
        var err = await SendWithBusyRetryAsync(api => api.PressShutter(_camera, ShutterButtonState.Completely), cancellationToken)
            .ConfigureAwait(false);

        // Luôn nhả nút chụp, kể cả khi bấm lỗi, để máy ảnh không kẹt ở trạng thái đang nhấn.
        var releaseErr = await SendWithBusyRetryAsync(api => api.PressShutter(_camera, ShutterButtonState.Off), CancellationToken.None)
            .ConfigureAwait(false);
        if (releaseErr != EDSDK.EDS_ERR_OK)
        {
            _logger.LogWarning("Nhả nút chụp lỗi: {Error}", EdsErrors.Describe(releaseErr));
        }

        if (err != EDSDK.EDS_ERR_OK)
        {
            _logger.LogWarning("Máy ảnh Canon từ chối chụp: {Error}", EdsErrors.Describe(err));
            throw MapShutterError(err);
        }
    }

    private async Task<uint> SendWithBusyRetryAsync(Func<IEdsApi, uint> command, CancellationToken cancellationToken)
    {
        var err = EDSDK.EDS_ERR_DEVICE_BUSY;
        for (var attempt = 0; attempt <= _options.BusyRetryAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_options.BusyRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            err = await _host
                .InvokeAsync(api => _camera == IntPtr.Zero ? EDSDK.EDS_ERR_COMM_DISCONNECTED : command(api), cancellationToken)
                .ConfigureAwait(false);
            if (err != EDSDK.EDS_ERR_DEVICE_BUSY)
            {
                return err;
            }
        }

        return err;
    }

    internal static CameraException MapShutterError(uint err) => err switch
    {
        EDSDK.EDS_ERR_TAKE_PICTURE_AF_NG =>
            new CameraException(CameraError.CaptureFailed, "Máy ảnh không lấy được nét (chủ thể quá gần hoặc thiếu sáng)."),
        EdsErrors.TakePictureSpecialMovieModeNg =>
            new CameraException(CameraError.CaptureFailed, "Máy ảnh đang ở chế độ quay phim; hãy chuyển vòng chế độ sang chụp ảnh."),
        EDSDK.EDS_ERR_DEVICE_BUSY =>
            new CameraException(CameraError.InUse, "Máy ảnh đang bận."),
        EDSDK.EDS_ERR_COMM_DISCONNECTED =>
            new CameraException(CameraError.Disconnected, "Máy ảnh đã bị ngắt kết nối."),
        _ => new CameraException(CameraError.CaptureFailed, $"Máy ảnh không chụp được ({EdsErrors.Describe(err)})."),
    };

    private async Task<CapturedFrame> DownloadAsync(IntPtr item)
    {
        // Đã nhận tham chiếu ảnh: luôn tải xong và giải phóng, không hủy giữa chừng.
        var (err, data) = await _host.InvokeAsync(api =>
        {
            try
            {
                var result = api.DownloadItem(item, out var bytes);
                return (result, bytes);
            }
            finally
            {
                api.ReleaseItem(item);
            }
        }).ConfigureAwait(false);

        if (err != EDSDK.EDS_ERR_OK)
        {
            throw new CameraException(CameraError.CaptureFailed, $"Không tải được ảnh từ máy ảnh ({EdsErrors.Describe(err)}).");
        }

        if (!JpegImageInfo.TryReadDimensions(data, out _, out _))
        {
            throw new CameraException(CameraError.CaptureFailed, "Ảnh nhận từ máy ảnh không phải JPEG hợp lệ.");
        }

        return new CapturedFrame(data, JpegImageInfo.ContentType);
    }

    /// <summary>
    /// Bỏ lệnh chờ ảnh (sau khi xong, lỗi, hết thời gian hoặc bị hủy). Nếu ảnh đã về nhưng chưa được tải
    /// (bị hủy đúng lúc ảnh tới) thì báo hủy với máy ảnh để không giữ tham chiếu.
    /// </summary>
    private async Task ClearPendingTransferAsync(TaskCompletionSource<IntPtr> pending, bool itemConsumed)
    {
        try
        {
            await _host.InvokeAsync(api =>
            {
                if (ReferenceEquals(_pendingTransfer, pending))
                {
                    _pendingTransfer = null;
                }

                if (!itemConsumed && pending.Task.IsCompletedSuccessfully)
                {
                    api.DiscardItem(pending.Task.Result);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // SDK host đã dừng: tham chiếu được giải phóng cùng EdsTerminateSDK.
        }
    }

    private void FailPendingTransfer(Exception exception)
    {
        var pending = _pendingTransfer;
        _pendingTransfer = null;
        pending?.TrySetException(exception);
    }

    // ---------------------------------------------------------------- Callback từ SDK (luồng SDK)

    private void OnTransferRequested(IntPtr item, string fileName)
    {
        // Chế độ RAW+JPEG gửi nhiều file cho một lần chụp: chỉ nhận JPEG.
        if (!IsJpegFileName(fileName))
        {
            _logger.LogInformation("Bỏ qua file không phải JPEG từ máy ảnh ({Extension})", Path.GetExtension(fileName));
            _host.Post(api => api.DiscardItem(item));
            return;
        }

        var pending = _pendingTransfer;
        _pendingTransfer = null;
        if (pending is null || !pending.TrySetResult(item))
        {
            // Ảnh không do ứng dụng yêu cầu (bấm nút chụp trên máy) hoặc đến sau khi đã hết thời gian chờ.
            _logger.LogInformation("Bỏ qua ảnh từ máy ảnh không thuộc lần chụp nào");
            _host.Post(api => api.DiscardItem(item));
        }
    }

    private void OnStateEvent(uint stateEvent)
    {
        if (stateEvent == EDSDK.StateEvent_Shutdown)
        {
            HandleDisconnected();
        }
        else if (stateEvent == EDSDK.StateEvent_WillSoonShutDown && _camera != IntPtr.Zero)
        {
            // Máy ảnh sắp tự tắt nguồn: gia hạn để kiosk không mất kết nối giữa các lượt khách.
            var camera = _camera;
            _host.Post(api =>
            {
                if (_camera != camera)
                {
                    return;
                }

                var err = api.ExtendShutDownTimer(camera);
                _logger.LogInformation("Gia hạn hẹn giờ tắt nguồn máy ảnh: {Error}", EdsErrors.Describe(err));
            });
        }
    }

    private void HandleDisconnected()
    {
        if (_camera == IntPtr.Zero)
        {
            return;
        }

        var camera = _camera;
        _camera = IntPtr.Zero;
        _isActive = false;
        CameraName = null;
        FailPendingTransfer(new CameraException(CameraError.Disconnected, "Máy ảnh bị ngắt kết nối."));

        // Vòng Live View tự thoát khi thấy máy đã ngắt; bỏ khung cũ để màn hình không giữ hình đứng yên.
        LiveView.Clear();

        // Không giải phóng tham chiếu máy ảnh ngay bên trong callback của chính nó.
        _host.Post(api => api.ReleaseCamera(camera));
        _logger.LogWarning("Máy ảnh Canon bị ngắt kết nối hoặc đã tắt");

        // Không gọi handler của ứng dụng trên luồng SDK để tránh chặn luồng này.
        ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(this, CameraError.Disconnected));
    }

    internal static bool IsJpegFileName(string fileName)
    {
        // Không đọc được tên file: vẫn nhận, dữ liệu sẽ được kiểm tra là JPEG sau khi tải.
        if (string.IsNullOrEmpty(fileName))
        {
            return true;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
