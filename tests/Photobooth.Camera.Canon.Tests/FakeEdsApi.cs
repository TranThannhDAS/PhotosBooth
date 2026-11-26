using System.Collections.Concurrent;
using EDSDKLib;
using Photobooth.Camera.Canon.Native;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>
/// SDK giả mô phỏng hành vi đã đo trên EOS M50 Mark II: callback chỉ được gọi khi luồng SDK pump sự kiện;
/// bấm chụp thành công → sự kiện có ảnh mới. Ghi nhận mọi lời gọi để kiểm tra.
/// </summary>
internal sealed class FakeEdsApi : IEdsApi
{
    public static readonly IntPtr CameraHandle = new(1);

    private readonly ConcurrentQueue<Action> _pendingEvents = new();
    private readonly ConcurrentDictionary<IntPtr, byte[]> _items = new();
    private long _nextItem = 1000;
    private Action<IntPtr, string>? _onTransfer;
    private Action<uint>? _onState;
    private Action<uint>? _onProperty;

    /// <summary>Giá trị hiện tại của thuộc tính (không có = máy không hỗ trợ).</summary>
    public ConcurrentDictionary<uint, uint> Properties { get; } = new();

    /// <summary>Thuộc tính dạng chuỗi (tên ống kính...).</summary>
    public ConcurrentDictionary<uint, string> Strings { get; } = new();

    /// <summary>Danh sách giá trị hợp lệ (EdsGetPropertyDesc) của từng thuộc tính.</summary>
    public ConcurrentDictionary<uint, IReadOnlyList<uint>> AllowedValues { get; } = new();

    /// <summary>Kết quả lần lượt khi đặt thuộc tính; hết hàng đợi thì trả OK.</summary>
    public ConcurrentQueue<uint> SetPropertyResults { get; } = new();

    public ConcurrentQueue<(uint PropertyId, uint Value)> SetCalls { get; } = new();

    /// <summary>Thuộc tính máy luôn từ chối khi đặt (EDS_ERR_INVALID_PARAMETER), không phụ thuộc thứ tự gọi.</summary>
    public ConcurrentDictionary<uint, bool> RejectedProperties { get; } = new();

    /// <summary>Chi tiết Picture Style theo mã kiểu (không có = máy không hỗ trợ đọc chi tiết).</summary>
    public ConcurrentDictionary<uint, EDSDK.EdsPictureStyleDesc> PictureStyleDescs { get; } = new();

    public int UnlockCalls;
    public bool UnlockedBeforeOpen;

    // ---- Cấu hình kịch bản (đặt trước khi chạy)
    public Exception? InitializeException { get; set; }

    public uint InitializeResult { get; set; } = EDSDK.EDS_ERR_OK;

    /// <summary>Kết quả lần lượt của FindFirstCamera; hết hàng đợi thì trả OK.</summary>
    public ConcurrentQueue<uint> FindCameraResults { get; } = new();

    public IReadOnlyList<CanonCameraInfo> ListedCameras { get; set; } = [];

    public uint ListCamerasResult { get; set; } = EDSDK.EDS_ERR_OK;

    public uint OpenSessionResult { get; set; } = EDSDK.EDS_ERR_OK;

    public uint SaveToHostResult { get; set; } = EDSDK.EDS_ERR_OK;

    /// <summary>Kết quả lần lượt khi bấm hết nút chụp; hết hàng đợi thì trả OK.</summary>
    public ConcurrentQueue<uint> ShutterResults { get; } = new();

    /// <summary>Các file máy ảnh gửi về sau mỗi lần bấm chụp thành công (rỗng = không gửi gì).</summary>
    public List<(string FileName, byte[] Data)> FilesPerShot { get; set; } = [("IMG_0001.JPG", TestJpeg.Create(6000, 4000))];

    public uint DownloadResult { get; set; } = EDSDK.EDS_ERR_OK;

    public uint StartLiveViewResult { get; set; } = EDSDK.EDS_ERR_OK;

    /// <summary>Kết quả lần lượt khi lấy khung Live View; hết hàng đợi thì trả OK.</summary>
    public ConcurrentQueue<uint> LiveViewResults { get; } = new();

    public byte[] LiveViewFrame { get; set; } = TestJpeg.Create(960, 640);

    public volatile bool LiveViewOn;
    public int StartLiveViewCalls;
    public int StopLiveViewCalls;
    public int LiveViewDownloadCalls;

    // ---- Ghi nhận
    public int FindCameraCalls;
    public int OpenSessionCalls;
    public int CloseSessionCalls;
    public int ReleaseCameraCalls;
    public int TerminateCalls;
    public int ExtendShutDownTimerCalls;
    public bool SaveToHostSet;

    public ConcurrentQueue<ShutterButtonState> ShutterPresses { get; } = new();

    public ConcurrentBag<IntPtr> DiscardedItems { get; } = [];

    public ConcurrentBag<IntPtr> ReleasedItems { get; } = [];

    public ConcurrentBag<IntPtr> DownloadedItems { get; } = [];

    public ConcurrentBag<(int ThreadId, ApartmentState Apartment)> CallingThreads { get; } = [];

    public int CreatedItemCount => _items.Count;

    // ---- Mô phỏng sự kiện từ máy ảnh (được giao khi luồng SDK pump)
    public void RaiseStateEvent(uint stateEvent) => _pendingEvents.Enqueue(() => _onState?.Invoke(stateEvent));

    public IntPtr RaiseTransfer(string fileName, byte[] data)
    {
        var item = new IntPtr(Interlocked.Increment(ref _nextItem));
        _items[item] = data;
        _pendingEvents.Enqueue(() => _onTransfer?.Invoke(item, fileName));
        return item;
    }

    // ---- IEdsApi
    public uint Initialize()
    {
        Record();
        if (InitializeException is not null)
        {
            throw InitializeException;
        }

        return InitializeResult;
    }

    public uint Terminate()
    {
        Record();
        Interlocked.Increment(ref TerminateCalls);
        return EDSDK.EDS_ERR_OK;
    }

    public uint FindFirstCamera(out IntPtr camera, out string description)
    {
        Record();
        Interlocked.Increment(ref FindCameraCalls);
        var result = FindCameraResults.TryDequeue(out var queued) ? queued : EDSDK.EDS_ERR_OK;
        camera = result == EDSDK.EDS_ERR_OK ? CameraHandle : IntPtr.Zero;
        description = result == EDSDK.EDS_ERR_OK ? "Canon EOS M50m2" : string.Empty;
        return result;
    }

    public uint ListCameras(out IReadOnlyList<CanonCameraInfo> cameras)
    {
        Record();
        cameras = ListCamerasResult == EDSDK.EDS_ERR_OK ? ListedCameras : [];
        return ListCamerasResult;
    }

    public uint SetEventHandlers(
        IntPtr camera,
        Action<IntPtr, string> onTransferRequested,
        Action<uint> onStateEvent,
        Action<uint> onPropertyChanged)
    {
        Record();
        _onTransfer = onTransferRequested;
        _onState = onStateEvent;
        _onProperty = onPropertyChanged;
        return EDSDK.EDS_ERR_OK;
    }

    public uint GetUInt32(IntPtr camera, uint propertyId, out uint value)
    {
        Record();
        return Properties.TryGetValue(propertyId, out value) ? EDSDK.EDS_ERR_OK : EDSDK.EDS_ERR_PROPERTIES_UNAVAILABLE;
    }

    public uint GetString(IntPtr camera, uint propertyId, out string value)
    {
        Record();
        return Strings.TryGetValue(propertyId, out value!) ? EDSDK.EDS_ERR_OK : EDSDK.EDS_ERR_PROPERTIES_UNAVAILABLE;
    }

    public uint SetUInt32(IntPtr camera, uint propertyId, uint value)
    {
        Record();
        if (RejectedProperties.ContainsKey(propertyId))
        {
            return EDSDK.EDS_ERR_INVALID_PARAMETER;
        }

        if (SetPropertyResults.TryDequeue(out var queued) && queued != EDSDK.EDS_ERR_OK)
        {
            return queued;
        }

        Properties[propertyId] = value;
        SetCalls.Enqueue((propertyId, value));

        // Máy thật báo PropertyChanged sau khi đổi giá trị.
        RaisePropertyChanged(propertyId);
        return EDSDK.EDS_ERR_OK;
    }

    public uint GetAllowedValues(IntPtr camera, uint propertyId, out IReadOnlyList<uint> values)
    {
        Record();
        values = AllowedValues.TryGetValue(propertyId, out var list) ? list : [];
        return EDSDK.EDS_ERR_OK;
    }

    public void RaisePropertyChanged(uint propertyId) => _pendingEvents.Enqueue(() => _onProperty?.Invoke(propertyId));

    public void UnlockPrivateProperties(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref UnlockCalls);
        UnlockedBeforeOpen = OpenSessionCalls == 0;
    }

    public uint GetPictureStyleDesc(IntPtr camera, uint pictureStyle, out EDSDK.EdsPictureStyleDesc desc)
    {
        Record();
        return PictureStyleDescs.TryGetValue(pictureStyle, out desc) ? EDSDK.EDS_ERR_OK : EDSDK.EDS_ERR_PROPERTIES_UNAVAILABLE;
    }

    public uint SetPictureStyleDesc(IntPtr camera, uint pictureStyle, EDSDK.EdsPictureStyleDesc desc)
    {
        Record();
        if (SetPropertyResults.TryDequeue(out var queued) && queued != EDSDK.EDS_ERR_OK)
        {
            return queued;
        }

        PictureStyleDescs[pictureStyle] = desc;
        RaisePropertyChanged(EDSDK.PropID_PictureStyleDesc);
        return EDSDK.EDS_ERR_OK;
    }

    public uint OpenSession(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref OpenSessionCalls);
        return OpenSessionResult;
    }

    public uint CloseSession(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref CloseSessionCalls);
        return EDSDK.EDS_ERR_OK;
    }

    public void ReleaseCamera(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref ReleaseCameraCalls);
        _onTransfer = null;
        _onState = null;
        _onProperty = null;
    }

    public uint SetSaveToHost(IntPtr camera)
    {
        Record();
        SaveToHostSet = SaveToHostResult == EDSDK.EDS_ERR_OK;
        return SaveToHostResult;
    }

    public uint PressShutter(IntPtr camera, ShutterButtonState state)
    {
        Record();
        ShutterPresses.Enqueue(state);
        if (state != ShutterButtonState.Completely)
        {
            return EDSDK.EDS_ERR_OK;
        }

        var result = ShutterResults.TryDequeue(out var queued) ? queued : EDSDK.EDS_ERR_OK;
        if (result == EDSDK.EDS_ERR_OK)
        {
            foreach (var (fileName, data) in FilesPerShot)
            {
                RaiseTransfer(fileName, data);
            }
        }

        return result;
    }

    public uint ExtendShutDownTimer(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref ExtendShutDownTimerCalls);
        return EDSDK.EDS_ERR_OK;
    }

    public uint StartLiveView(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref StartLiveViewCalls);
        LiveViewOn = StartLiveViewResult == EDSDK.EDS_ERR_OK;
        return StartLiveViewResult;
    }

    public uint StopLiveView(IntPtr camera)
    {
        Record();
        Interlocked.Increment(ref StopLiveViewCalls);
        LiveViewOn = false;
        return EDSDK.EDS_ERR_OK;
    }

    public uint DownloadLiveViewFrame(IntPtr camera, out byte[] jpeg)
    {
        Record();
        Interlocked.Increment(ref LiveViewDownloadCalls);
        var result = LiveViewResults.TryDequeue(out var queued) ? queued : EDSDK.EDS_ERR_OK;
        jpeg = result == EDSDK.EDS_ERR_OK && LiveViewOn ? LiveViewFrame : [];
        return LiveViewOn ? result : EDSDK.EDS_ERR_OBJECT_NOTREADY;
    }

    public uint DownloadItem(IntPtr item, out byte[] data)
    {
        Record();
        DownloadedItems.Add(item);
        data = DownloadResult == EDSDK.EDS_ERR_OK && _items.TryGetValue(item, out var bytes) ? bytes : [];
        return DownloadResult;
    }

    public void DiscardItem(IntPtr item)
    {
        Record();
        DiscardedItems.Add(item);
    }

    public void ReleaseItem(IntPtr item)
    {
        Record();
        ReleasedItems.Add(item);
    }

    public void PumpEvents()
    {
        Record();
        while (_pendingEvents.TryDequeue(out var raise))
        {
            raise();
        }
    }

    private void Record() =>
        CallingThreads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
}

internal static class TestJpeg
{
    /// <summary>JPEG tối thiểu (SOI + SOF0 + EOI) đủ để <c>JpegImageInfo</c> đọc được kích thước.</summary>
    public static byte[] Create(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xD9,
    ];
}
