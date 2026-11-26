using System.Diagnostics;
using System.Runtime.InteropServices;
using CanonSpike.Native;
using EDSDKLib;

namespace CanonSpike;

/// <summary>
/// Kịch bản kiểm chứng: khởi tạo SDK → tìm máy → mở session (SaveTo=Host) → Live View → chụp và nhận ảnh vào bộ nhớ
/// → Live View sau khi chụp → (tùy chọn) chờ rút cáp → dọn dẹp. Mọi lệnh SDK chạy tuần tự trên luồng STA chính.
/// </summary>
internal sealed class SpikeRunner
{
    private const int MaxBusyRetries = 5;
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(15);

    // Delegate phải được giữ sống suốt thời gian SDK có thể gọi lại, nếu không GC thu hồi sẽ gây crash.
    private static EDSDK.EdsPropertyEventHandler? s_propertyHandler;
    private static EDSDK.EdsObjectEventHandler? s_objectHandler;
    private static EDSDK.EdsStateEventHandler? s_stateHandler;

    private readonly SpikeOptions _options;
    private readonly string _outputDirectory;
    private readonly SpikeLog _log;
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Lock _gate = new();
    private readonly Queue<IntPtr> _pendingTransfers = new();
    private readonly HashSet<int> _callbackThreadIds = [];
    private readonly Dictionary<string, int> _eventCounts = [];

    private IntPtr _camera;
    private bool _sessionOpen;
    private bool _liveViewOn;
    private volatile bool _shutdownReceived;

    public SpikeRunner(SpikeOptions options, string outputDirectory, SpikeLog log)
    {
        _options = options;
        _outputDirectory = outputDirectory;
        _log = log;
    }

    public int Run()
    {
        var sdkInitialized = false;
        try
        {
            var err = EDSDK.EdsInitializeSDK();
            _log.Result("Khởi tạo SDK", err == EDSDK.EDS_ERR_OK, EdsInterop.Describe(err));
            if (err != EDSDK.EDS_ERR_OK)
            {
                return 1;
            }

            sdkInitialized = true;

            if (!ConnectFirstCamera() || !OpenSession())
            {
                return 1;
            }

            ReadCameraInfo();

            if (!_options.SkipLiveView)
            {
                if (StartLiveView())
                {
                    MeasureLiveView("Live View trước khi chụp", TimeSpan.FromSeconds(_options.LiveViewSeconds), "liveview-before.jpg");
                }
            }

            for (var shot = 1; shot <= _options.Shots && !_shutdownReceived; shot++)
            {
                CaptureOne(shot);
                PumpFor(TimeSpan.FromSeconds(1));
            }

            if (_liveViewOn && _options.Shots > 0 && !_shutdownReceived)
            {
                MeasureLiveView("Live View sau khi chụp", TimeSpan.FromSeconds(2), "liveview-after.jpg");
            }

            if (_options.WaitDisconnect && !_shutdownReceived)
            {
                _log.Info(">>> Hãy RÚT CÁP USB hoặc TẮT MÁY ẢNH trong vòng 60 giây...");
                var detected = PumpUntil(() => _shutdownReceived, TimeSpan.FromSeconds(60));
                _log.Result("Phát hiện mất kết nối", detected,
                    detected ? "Nhận StateEvent_Shutdown" : "Không nhận được sự kiện trong 60 giây");
            }

            return 0;
        }
        catch (Exception ex)
        {
            _log.Result("Ngoại lệ không mong đợi", false, ex.ToString());
            return 1;
        }
        finally
        {
            Cleanup(sdkInitialized);
            WriteThreadingSummary();
            _log.WriteSummary();
        }
    }

    // ------------------------------------------------------------------ Kết nối

    private bool ConnectFirstCamera()
    {
        var err = EDSDK.EdsGetCameraList(out var cameraList);
        if (err != EDSDK.EDS_ERR_OK)
        {
            _log.Result("Lấy danh sách máy ảnh", false, EdsInterop.Describe(err));
            return false;
        }

        try
        {
            EDSDK.EdsGetChildCount(cameraList, out var count);
            if (count == 0)
            {
                _log.Result("Tìm máy ảnh", false,
                    "Không thấy máy ảnh. Kiểm tra: cáp USB, máy đã bật, đã tắt EOS Utility/EOS Webcam Utility, Wi-Fi/Bluetooth trên máy ảnh đã tắt.");
                return false;
            }

            err = EDSDK.EdsGetChildAtIndex(cameraList, 0, out _camera);
            if (err != EDSDK.EDS_ERR_OK)
            {
                _log.Result("Tìm máy ảnh", false, EdsInterop.Describe(err));
                return false;
            }

            EDSDK.EdsGetDeviceInfo(_camera, out var info);
            _log.Result("Tìm máy ảnh", true, $"{count} máy; dùng máy đầu tiên: {info.szDeviceDescription} (port {info.szPortName})");
        }
        finally
        {
            EDSDK.EdsRelease(cameraList);
        }

        s_propertyHandler = OnPropertyEvent;
        s_objectHandler = OnObjectEvent;
        s_stateHandler = OnStateEvent;

        var handlersOk =
            EDSDK.EdsSetPropertyEventHandler(_camera, EDSDK.PropertyEvent_All, s_propertyHandler, IntPtr.Zero) == EDSDK.EDS_ERR_OK
            & EDSDK.EdsSetObjectEventHandler(_camera, EDSDK.ObjectEvent_All, s_objectHandler, IntPtr.Zero) == EDSDK.EDS_ERR_OK
            & EDSDK.EdsSetCameraStateEventHandler(_camera, EDSDK.StateEvent_All, s_stateHandler, IntPtr.Zero) == EDSDK.EDS_ERR_OK;
        _log.Result("Đăng ký callback", handlersOk, handlersOk ? "Property/Object/State" : "Có handler đăng ký lỗi");
        return handlersOk;
    }

    private bool OpenSession()
    {
        var err = WithBusyRetry(() => EDSDK.EdsOpenSession(_camera));
        _log.Result("Mở session", err == EDSDK.EDS_ERR_OK, EdsInterop.Describe(err));
        if (err != EDSDK.EDS_ERR_OK)
        {
            return false;
        }

        _sessionOpen = true;

        // Ảnh chụp được gửi thẳng về PC; báo SDK là PC còn nhiều dung lượng để máy ảnh không từ chối chụp.
        err = WithBusyRetry(() => EdsInterop.SetUInt32(_camera, EDSDK.PropID_SaveTo, (uint)EDSDK.EdsSaveTo.Host));
        if (err == EDSDK.EDS_ERR_OK)
        {
            var capacity = new EDSDK.EdsCapacity { NumberOfFreeClusters = 0x7FFFFFFF, BytesPerSector = 0x1000, Reset = 1 };
            err = WithBusyRetry(() => EDSDK.EdsSetCapacity(_camera, capacity));
        }

        _log.Result("Đặt SaveTo=Host + SetCapacity", err == EDSDK.EDS_ERR_OK, EdsInterop.Describe(err));
        return err == EDSDK.EDS_ERR_OK;
    }

    private void ReadCameraInfo()
    {
        PumpFor(TimeSpan.FromMilliseconds(500));

        EDSDK.EdsGetPropertyData(_camera, EDSDK.PropID_ProductName, 0, out string productName);
        _log.Info($"Tên máy: {productName}");

        ReadUInt("Pin (0xFFFFFFFF = nguồn AC)", EDSDK.PropID_BatteryLevel, asDecimal: true);
        ReadUInt("Chế độ AE (vòng chế độ)", EDSDK.PropID_AEMode);
        ReadUInt("ISO", EDSDK.PropID_ISOSpeed);
        ReadUInt("Khẩu độ Av", EDSDK.PropID_Av);
        ReadUInt("Tốc độ Tv", EDSDK.PropID_Tv);
        ReadUInt("Chất lượng ảnh", EDSDK.PropID_ImageQuality);
        ReadUInt("Evf_Mode", EDSDK.PropID_Evf_Mode);
        ReadUInt("Evf_OutputDevice", EDSDK.PropID_Evf_OutputDevice);
    }

    private uint? ReadUInt(string label, uint propertyId, bool asDecimal = false)
    {
        var err = EDSDK.EdsGetPropertyData(_camera, propertyId, 0, out uint value);
        if (err != EDSDK.EDS_ERR_OK)
        {
            _log.Info($"{label}: không đọc được ({EdsInterop.Describe(err)})");
            return null;
        }

        _log.Info($"{label}: {(asDecimal ? value.ToString() : $"0x{value:X}")}");
        return value;
    }

    // ------------------------------------------------------------------ Live View

    private bool StartLiveView()
    {
        var err = EDSDK.EdsGetPropertyData(_camera, EDSDK.PropID_Evf_Mode, 0, out uint evfMode);
        if (err == EDSDK.EDS_ERR_OK && evfMode == 0)
        {
            err = WithBusyRetry(() => EdsInterop.SetUInt32(_camera, EDSDK.PropID_Evf_Mode, 1));
        }

        if (err == EDSDK.EDS_ERR_OK)
        {
            err = EDSDK.EdsGetPropertyData(_camera, EDSDK.PropID_Evf_OutputDevice, 0, out uint device);
            if (err == EDSDK.EDS_ERR_OK)
            {
                err = WithBusyRetry(() => EdsInterop.SetUInt32(_camera, EDSDK.PropID_Evf_OutputDevice, device | EDSDK.EvfOutputDevice_PC));
            }
        }

        _liveViewOn = err == EDSDK.EDS_ERR_OK;
        _log.Result("Bật Live View (OutputDevice |= PC)", _liveViewOn, EdsInterop.Describe(err));
        return _liveViewOn;
    }

    private void StopLiveView()
    {
        if (!_liveViewOn)
        {
            return;
        }

        var err = EDSDK.EdsGetPropertyData(_camera, EDSDK.PropID_Evf_OutputDevice, 0, out uint device);
        if (err == EDSDK.EDS_ERR_OK)
        {
            err = WithBusyRetry(() => EdsInterop.SetUInt32(_camera, EDSDK.PropID_Evf_OutputDevice, device & ~EDSDK.EvfOutputDevice_PC));
        }

        _liveViewOn = false;
        _log.Info($"Tắt Live View: {EdsInterop.Describe(err)}");
    }

    private void MeasureLiveView(string step, TimeSpan duration, string sampleFileName)
    {
        var frames = 0;
        var notReady = 0;
        var otherErrors = new Dictionary<string, int>();
        long totalBytes = 0;
        byte[]? sample = null;
        var firstFrameAfter = TimeSpan.Zero;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration && !_shutdownReceived)
        {
            Pump();
            var (err, jpeg) = DownloadEvfFrame();
            if (err == EDSDK.EDS_ERR_OK && jpeg is not null)
            {
                if (frames == 0)
                {
                    firstFrameAfter = stopwatch.Elapsed;
                }

                frames++;
                totalBytes += jpeg.Length;
                sample = jpeg;
            }
            else if (err == EDSDK.EDS_ERR_OBJECT_NOTREADY)
            {
                notReady++;
                Thread.Sleep(10);
            }
            else
            {
                var key = EdsInterop.Describe(err);
                otherErrors[key] = otherErrors.GetValueOrDefault(key) + 1;
                Thread.Sleep(50);
            }
        }

        var seconds = stopwatch.Elapsed.TotalSeconds;
        if (sample is not null)
        {
            File.WriteAllBytes(Path.Combine(_outputDirectory, sampleFileName), sample);
        }

        var detail = $"{frames} khung / {seconds:0.0}s ≈ {frames / seconds:0.0} fps; " +
                     $"khung đầu sau {firstFrameAfter.TotalMilliseconds:0} ms; " +
                     $"TB {(frames > 0 ? totalBytes / frames / 1024 : 0)} KB/khung; NOTREADY={notReady}" +
                     (otherErrors.Count > 0 ? "; lỗi khác: " + string.Join(", ", otherErrors.Select(e => $"{e.Key}×{e.Value}")) : string.Empty) +
                     (sample is not null ? $"; mẫu: {sampleFileName}" : string.Empty);
        _log.Result(step, frames > 0, detail);
    }

    private (uint Err, byte[]? Jpeg) DownloadEvfFrame()
    {
        var stream = IntPtr.Zero;
        var evfImage = IntPtr.Zero;
        try
        {
            var err = EDSDK.EdsCreateMemoryStream(2 * 1024 * 1024, out stream);
            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsCreateEvfImageRef(stream, out evfImage);
            }

            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsDownloadEvfImage(_camera, evfImage);
            }

            return err == EDSDK.EDS_ERR_OK ? (err, CopyStream(stream)) : (err, null);
        }
        finally
        {
            if (evfImage != IntPtr.Zero) EDSDK.EdsRelease(evfImage);
            if (stream != IntPtr.Zero) EDSDK.EdsRelease(stream);
        }
    }

    // ------------------------------------------------------------------ Chụp ảnh

    private void CaptureOne(int shot)
    {
        var step = $"Chụp ảnh #{shot} ({_options.Shutter})";
        ReleasePendingTransfers();

        var stopwatch = Stopwatch.StartNew();
        var err = PressShutter();
        if (err != EDSDK.EDS_ERR_OK)
        {
            _log.Result(step, false, $"Gửi lệnh chụp lỗi: {EdsInterop.Describe(err)}");
            return;
        }

        var commandTime = stopwatch.Elapsed;

        // Trong Photobooth, Live View vẫn chạy khi chờ ảnh: tiếp tục kéo khung để kiểm tra hai việc không xung đột.
        var evfErrors = 0;
        var received = PumpUntil(
            () => PendingTransferCount() > 0,
            TransferTimeout,
            whileWaiting: _liveViewOn ? () => { if (DownloadEvfFrame().Err is not (EDSDK.EDS_ERR_OK or EDSDK.EDS_ERR_OBJECT_NOTREADY)) evfErrors++; } : null);

        if (!received)
        {
            _log.Result(step, false,
                $"Không nhận được sự kiện DirItemRequestTransfer trong {TransferTimeout.TotalSeconds:0}s " +
                "(có thể AF không lấy được nét, hoặc callback không được gọi với chế độ pump này)");
            return;
        }

        var eventTime = stopwatch.Elapsed;
        var item = DequeuePendingTransfer();
        var (downloadErr, data, fileName) = DownloadDirectoryItem(item);
        var totalTime = stopwatch.Elapsed;

        if (downloadErr != EDSDK.EDS_ERR_OK || data is null)
        {
            _log.Result(step, false, $"Tải ảnh lỗi: {EdsInterop.Describe(downloadErr)}");
            return;
        }

        var outputName = $"capture-{shot}{Path.GetExtension(fileName)}";
        File.WriteAllBytes(Path.Combine(_outputDirectory, outputName), data);
        _log.Result(step, true,
            $"{fileName} → {outputName}, {data.Length / 1024} KB; lệnh {commandTime.TotalMilliseconds:0} ms, " +
            $"nhận sự kiện sau {eventTime.TotalMilliseconds:0} ms, tải xong sau {totalTime.TotalMilliseconds:0} ms; " +
            $"lỗi Live View khi chờ: {evfErrors}");
    }

    private uint PressShutter()
    {
        uint Send(EDSDK.EdsShutterButton state) =>
            WithBusyRetry(() => EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_PressShutterButton, (int)state));

        uint err;
        switch (_options.Shutter)
        {
            case ShutterMode.NonAf:
                err = Send(EDSDK.EdsShutterButton.CameraCommand_ShutterButton_Completely_NonAF);
                break;
            case ShutterMode.HalfThenFull:
                err = Send(EDSDK.EdsShutterButton.CameraCommand_ShutterButton_Halfway);
                if (err == EDSDK.EDS_ERR_OK)
                {
                    PumpFor(TimeSpan.FromMilliseconds(700));
                    err = Send(EDSDK.EdsShutterButton.CameraCommand_ShutterButton_Completely);
                }

                break;
            default:
                err = Send(EDSDK.EdsShutterButton.CameraCommand_ShutterButton_Completely);
                break;
        }

        // Luôn nhả nút chụp, kể cả khi bước trước lỗi, để máy không bị kẹt ở trạng thái nhấn.
        var releaseErr = Send(EDSDK.EdsShutterButton.CameraCommand_ShutterButton_OFF);
        return err != EDSDK.EDS_ERR_OK ? err : releaseErr;
    }

    private (uint Err, byte[]? Data, string FileName) DownloadDirectoryItem(IntPtr item)
    {
        var stream = IntPtr.Zero;
        try
        {
            var err = EDSDK.EdsGetDirectoryItemInfo(item, out var info);
            if (err != EDSDK.EDS_ERR_OK)
            {
                return (err, null, string.Empty);
            }

            // Tải vào bộ nhớ thay vì ghi file theo tên gốc như sample.
            err = EDSDK.EdsCreateMemoryStream(info.Size, out stream);
            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsDownload(item, info.Size, stream);
            }

            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsDownloadComplete(item);
            }
            else
            {
                EDSDK.EdsDownloadCancel(item);
            }

            return err == EDSDK.EDS_ERR_OK ? (err, CopyStream(stream), info.szFileName) : (err, null, info.szFileName);
        }
        finally
        {
            if (stream != IntPtr.Zero) EDSDK.EdsRelease(stream);
            EDSDK.EdsRelease(item);
        }
    }

    // ------------------------------------------------------------------ Callback từ SDK

    private uint OnPropertyEvent(uint inEvent, uint inPropertyId, uint inParam, IntPtr inContext)
    {
        RecordCallback($"Property 0x{inEvent:X}");
        return EDSDK.EDS_ERR_OK;
    }

    private uint OnObjectEvent(uint inEvent, IntPtr inRef, IntPtr inContext)
    {
        RecordCallback($"Object 0x{inEvent:X}");
        if (inEvent == EDSDK.ObjectEvent_DirItemRequestTransfer)
        {
            // Giữ tham chiếu để tải ngoài callback; sẽ được EdsRelease sau khi tải.
            lock (_gate)
            {
                _pendingTransfers.Enqueue(inRef);
            }

            _log.Info("Nhận ObjectEvent_DirItemRequestTransfer");
        }
        else if (inRef != IntPtr.Zero)
        {
            EDSDK.EdsRelease(inRef);
        }

        return EDSDK.EDS_ERR_OK;
    }

    private uint OnStateEvent(uint inEvent, uint inParameter, IntPtr inContext)
    {
        RecordCallback($"State 0x{inEvent:X}");
        if (inEvent == EDSDK.StateEvent_Shutdown)
        {
            _shutdownReceived = true;
            _log.Warn("Nhận StateEvent_Shutdown — máy ảnh đã tắt hoặc bị rút cáp");
        }

        return EDSDK.EDS_ERR_OK;
    }

    private void RecordCallback(string key)
    {
        lock (_gate)
        {
            _callbackThreadIds.Add(Environment.CurrentManagedThreadId);
            _eventCounts[key] = _eventCounts.GetValueOrDefault(key) + 1;
        }
    }

    private int PendingTransferCount()
    {
        lock (_gate)
        {
            return _pendingTransfers.Count;
        }
    }

    private IntPtr DequeuePendingTransfer()
    {
        lock (_gate)
        {
            return _pendingTransfers.Dequeue();
        }
    }

    private void ReleasePendingTransfers()
    {
        lock (_gate)
        {
            while (_pendingTransfers.TryDequeue(out var item))
            {
                EDSDK.EdsRelease(item);
            }
        }
    }

    // ------------------------------------------------------------------ Pump / tiện ích

    private void Pump()
    {
        if (_options.Pump is PumpMode.Win32 or PumpMode.Both)
        {
            Win32MessagePump.Drain();
        }

        if (_options.Pump is PumpMode.GetEvent or PumpMode.Both)
        {
            EdsInterop.EdsGetEvent();
        }
    }

    private bool PumpUntil(Func<bool> condition, TimeSpan timeout, Action? whileWaiting = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            Pump();
            if (condition())
            {
                return true;
            }

            whileWaiting?.Invoke();
            Thread.Sleep(5);
        }

        Pump();
        return condition();
    }

    private void PumpFor(TimeSpan duration) => PumpUntil(() => false, duration);

    /// <summary>Thử lại khi máy báo DEVICE_BUSY, cách nhau ~500 ms như khuyến nghị trong sample của Canon.</summary>
    private uint WithBusyRetry(Func<uint> action)
    {
        var err = action();
        for (var attempt = 1; err == EDSDK.EDS_ERR_DEVICE_BUSY && attempt <= MaxBusyRetries; attempt++)
        {
            _log.Info($"DEVICE_BUSY, thử lại lần {attempt}");
            PumpFor(BusyRetryDelay);
            err = action();
        }

        return err;
    }

    private static byte[] CopyStream(IntPtr stream)
    {
        // EdsGetLength trả về kích thước bộ đệm đã cấp (2 MB với Live View), không phải số byte thực có dữ liệu;
        // số byte đã ghi là vị trí hiện tại của stream.
        EDSDK.EdsGetPointer(stream, out var pointer);
        EDSDK.EdsGetPosition(stream, out var length);
        if (length == 0)
        {
            EDSDK.EdsGetLength(stream, out length);
        }

        var data = new byte[checked((int)length)];
        Marshal.Copy(pointer, data, 0, data.Length);
        return data;
    }

    private void Cleanup(bool sdkInitialized)
    {
        ReleasePendingTransfers();

        if (_camera != IntPtr.Zero && _sessionOpen && !_shutdownReceived)
        {
            StopLiveView();
            var err = WithBusyRetry(() => EDSDK.EdsCloseSession(_camera));
            _log.Info($"Đóng session: {EdsInterop.Describe(err)}");
        }

        if (_camera != IntPtr.Zero)
        {
            EDSDK.EdsRelease(_camera);
            _camera = IntPtr.Zero;
        }

        if (sdkInitialized)
        {
            var err = EDSDK.EdsTerminateSDK();
            _log.Info($"Kết thúc SDK: {EdsInterop.Describe(err)}");
        }
    }

    private void WriteThreadingSummary()
    {
        lock (_gate)
        {
            var threads = _callbackThreadIds.Count == 0 ? "(không có callback nào)" : string.Join(", ", _callbackThreadIds.Select(id => $"T{id}"));
            var sameThread = _callbackThreadIds.Count > 0 && _callbackThreadIds.All(id => id == _mainThreadId);
            _log.Info($"Luồng chính: T{_mainThreadId}; callback chạy trên: {threads}" +
                      (_callbackThreadIds.Count > 0 ? (sameThread ? " → cùng luồng chính" : " → CÓ luồng khác luồng chính") : string.Empty));
            _log.Info("Số callback theo loại: " +
                      (_eventCounts.Count == 0 ? "(không có)" : string.Join(", ", _eventCounts.Select(e => $"{e.Key}×{e.Value}"))));
        }
    }
}
