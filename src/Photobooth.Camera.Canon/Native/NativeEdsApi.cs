using System.Runtime.InteropServices;
using EDSDKLib;
using Microsoft.Extensions.Logging;

namespace Photobooth.Camera.Canon.Native;

/// <summary>
/// Hiện thực <see cref="IEdsApi"/> bằng EDSDK.dll thật, dựa trên trình tự gọi API của sample CameraControl (Canon)
/// và kết quả spike trên EOS M50 Mark II.
/// </summary>
internal sealed class NativeEdsApi : IEdsApi
{
    private readonly ILogger _logger;

    // Delegate truyền xuống native phải được giữ sống suốt thời gian SDK có thể gọi lại.
    private EDSDK.EdsObjectEventHandler? _objectHandler;
    private EDSDK.EdsStateEventHandler? _stateHandler;
    private EDSDK.EdsPropertyEventHandler? _propertyHandler;

    public NativeEdsApi(ILogger logger)
    {
        _logger = logger;
    }

    public uint Initialize() => EDSDK.EdsInitializeSDK();

    public uint Terminate() => EDSDK.EdsTerminateSDK();

    public uint FindFirstCamera(out IntPtr camera, out string description)
    {
        camera = IntPtr.Zero;
        description = string.Empty;

        var err = EDSDK.EdsGetCameraList(out var cameraList);
        if (err != EDSDK.EDS_ERR_OK)
        {
            return err;
        }

        try
        {
            err = EDSDK.EdsGetChildCount(cameraList, out var count);
            if (err != EDSDK.EDS_ERR_OK)
            {
                return err;
            }

            if (count == 0)
            {
                return EDSDK.EDS_ERR_DEVICE_NOT_FOUND;
            }

            err = EDSDK.EdsGetChildAtIndex(cameraList, 0, out camera);
            if (err != EDSDK.EDS_ERR_OK)
            {
                return err;
            }

            if (EDSDK.EdsGetDeviceInfo(camera, out var info) == EDSDK.EDS_ERR_OK)
            {
                description = info.szDeviceDescription ?? string.Empty;
            }

            return EDSDK.EDS_ERR_OK;
        }
        finally
        {
            EDSDK.EdsRelease(cameraList);
        }
    }

    public uint ListCameras(out IReadOnlyList<CanonCameraInfo> cameras)
    {
        cameras = [];
        var err = EDSDK.EdsGetCameraList(out var cameraList);
        if (err != EDSDK.EDS_ERR_OK)
        {
            return err;
        }

        try
        {
            err = EDSDK.EdsGetChildCount(cameraList, out var count);
            if (err != EDSDK.EDS_ERR_OK)
            {
                return err;
            }

            var found = new List<CanonCameraInfo>(count);
            for (var index = 0; index < count; index++)
            {
                if (EDSDK.EdsGetChildAtIndex(cameraList, index, out var camera) != EDSDK.EDS_ERR_OK)
                {
                    continue;
                }

                try
                {
                    if (EDSDK.EdsGetDeviceInfo(camera, out var info) == EDSDK.EDS_ERR_OK)
                    {
                        found.Add(new CanonCameraInfo(info.szDeviceDescription ?? string.Empty, info.szPortName ?? string.Empty));
                    }
                }
                finally
                {
                    EDSDK.EdsRelease(camera);
                }
            }

            cameras = found;
            return EDSDK.EDS_ERR_OK;
        }
        finally
        {
            EDSDK.EdsRelease(cameraList);
        }
    }

    public uint SetEventHandlers(
        IntPtr camera,
        Action<IntPtr, string> onTransferRequested,
        Action<uint> onStateEvent,
        Action<uint> onPropertyChanged)
    {
        _propertyHandler = (inEvent, inPropertyId, _, _) =>
        {
            try
            {
                if (inEvent is EDSDK.PropertyEvent_PropertyChanged or EDSDK.PropertyEvent_PropertyDescChanged)
                {
                    onPropertyChanged(inPropertyId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý sự kiện thuộc tính 0x{Property:X} từ máy ảnh", inPropertyId);
            }

            return EDSDK.EDS_ERR_OK;
        };

        _objectHandler = (inEvent, inRef, _) =>
        {
            // Ngoại lệ không được thoát ra native code: sẽ làm sập tiến trình.
            try
            {
                if (inEvent == EDSDK.ObjectEvent_DirItemRequestTransfer)
                {
                    var fileName = EDSDK.EdsGetDirectoryItemInfo(inRef, out var info) == EDSDK.EDS_ERR_OK
                        ? info.szFileName ?? string.Empty
                        : string.Empty;
                    onTransferRequested(inRef, fileName);
                }
                else if (inRef != IntPtr.Zero)
                {
                    EDSDK.EdsRelease(inRef);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý sự kiện đối tượng 0x{Event:X} từ máy ảnh", inEvent);
            }

            return EDSDK.EDS_ERR_OK;
        };

        _stateHandler = (inEvent, _, _) =>
        {
            try
            {
                onStateEvent(inEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý sự kiện trạng thái 0x{Event:X} từ máy ảnh", inEvent);
            }

            return EDSDK.EDS_ERR_OK;
        };

        var err = EDSDK.EdsSetObjectEventHandler(camera, EDSDK.ObjectEvent_All, _objectHandler, IntPtr.Zero);
        if (err == EDSDK.EDS_ERR_OK)
        {
            err = EDSDK.EdsSetCameraStateEventHandler(camera, EDSDK.StateEvent_All, _stateHandler, IntPtr.Zero);
        }

        return err != EDSDK.EDS_ERR_OK
            ? err
            : EDSDK.EdsSetPropertyEventHandler(camera, EDSDK.PropertyEvent_All, _propertyHandler, IntPtr.Zero);
    }

    public uint GetUInt32(IntPtr camera, uint propertyId, out uint value) =>
        EDSDK.EdsGetPropertyData(camera, propertyId, 0, out value);

    public uint GetString(IntPtr camera, uint propertyId, out string value)
    {
        var err = EDSDK.EdsGetPropertyData(camera, propertyId, 0, out string text);
        value = err == EDSDK.EDS_ERR_OK ? text ?? string.Empty : string.Empty;
        return err;
    }

    public uint SetUInt32(IntPtr camera, uint propertyId, uint value) =>
        EdsSetPropertyDataUInt32(camera, propertyId, 0, sizeof(uint), ref value);

    public uint GetAllowedValues(IntPtr camera, uint propertyId, out IReadOnlyList<uint> values)
    {
        values = [];
        var err = EDSDK.EdsGetPropertyDesc(camera, propertyId, out var desc);
        if (err != EDSDK.EDS_ERR_OK)
        {
            return err;
        }

        var count = Math.Clamp(desc.NumElements, 0, desc.PropDesc?.Length ?? 0);
        values = desc.PropDesc is null ? [] : desc.PropDesc.Take(count).Select(v => unchecked((uint)v)).ToArray();
        return EDSDK.EDS_ERR_OK;
    }

    /// <summary>Cặp (khóa mở, mã thuộc tính) theo OpenSessionCommand của sample Canon — chỉ các thuộc tính adapter dùng.</summary>
    private static readonly (uint Key, uint PropertyId)[] PrivatePropertyKeys =
    [
        (0x3FB1718B, EDSDK.PropID_Aspect),
        (0x1C31565B, EDSDK.PropID_AutoPowerOffSetting),
        (0x14840DF1, EDSDK.PropID_TempStatus),
    ];

    public void UnlockPrivateProperties(IntPtr camera)
    {
        foreach (var (key, propertyId) in PrivatePropertyKeys)
        {
            var target = propertyId;
            var err = EdsSetPropertyDataUInt32(camera, 0x01000000, unchecked((int)key), sizeof(uint), ref target);
            if (err != EDSDK.EDS_ERR_OK)
            {
                _logger.LogDebug("Máy ảnh không mở khóa thuộc tính 0x{Property:X}: {Error}", propertyId, EdsErrors.Describe(err));
            }
        }
    }

    public uint OpenSession(IntPtr camera) => EDSDK.EdsOpenSession(camera);

    public uint GetPictureStyleDesc(IntPtr camera, uint pictureStyle, out EDSDK.EdsPictureStyleDesc desc)
    {
        desc = default;
        var size = Marshal.SizeOf<EDSDK.EdsPictureStyleDesc>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var err = EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_PictureStyleDesc, unchecked((int)pictureStyle), size, buffer);
            if (err == EDSDK.EDS_ERR_OK)
            {
                desc = Marshal.PtrToStructure<EDSDK.EdsPictureStyleDesc>(buffer);
            }

            return err;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public uint SetPictureStyleDesc(IntPtr camera, uint pictureStyle, EDSDK.EdsPictureStyleDesc desc) =>
        EdsSetPropertyDataPictureStyle(camera, EDSDK.PropID_PictureStyleDesc, unchecked((int)pictureStyle),
            Marshal.SizeOf<EDSDK.EdsPictureStyleDesc>(), ref desc);

    public uint CloseSession(IntPtr camera) => EDSDK.EdsCloseSession(camera);

    public void ReleaseCamera(IntPtr camera)
    {
        if (camera == IntPtr.Zero)
        {
            return;
        }

        // Handler null = hủy đăng ký (EDSDK cho phép); chữ ký C# của Canon không khai báo nullable.
        EDSDK.EdsSetObjectEventHandler(camera, EDSDK.ObjectEvent_All, null!, IntPtr.Zero);
        EDSDK.EdsSetCameraStateEventHandler(camera, EDSDK.StateEvent_All, null!, IntPtr.Zero);
        EDSDK.EdsSetPropertyEventHandler(camera, EDSDK.PropertyEvent_All, null!, IntPtr.Zero);
        EDSDK.EdsRelease(camera);
    }

    public uint SetSaveToHost(IntPtr camera)
    {
        var saveTo = (uint)EDSDK.EdsSaveTo.Host;
        var err = EdsSetPropertyDataUInt32(camera, EDSDK.PropID_SaveTo, 0, sizeof(uint), ref saveTo);
        if (err != EDSDK.EDS_ERR_OK)
        {
            return err;
        }

        // Báo PC còn nhiều dung lượng để máy ảnh không từ chối chụp khi lưu về PC.
        var capacity = new EDSDK.EdsCapacity { NumberOfFreeClusters = 0x7FFFFFFF, BytesPerSector = 0x1000, Reset = 1 };
        return EDSDK.EdsSetCapacity(camera, capacity);
    }

    public uint PressShutter(IntPtr camera, ShutterButtonState state) =>
        EDSDK.EdsSendCommand(camera, EDSDK.CameraCommand_PressShutterButton, (int)state);

    public uint ExtendShutDownTimer(IntPtr camera) =>
        EDSDK.EdsSendCommand(camera, EDSDK.CameraCommand_ExtendShutDownTimer, 0);

    public uint StartLiveView(IntPtr camera)
    {
        // Máy đặt "không dùng Live View" thì phải bật Evf_Mode trước (theo StartEvfCommand của sample Canon).
        var err = EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_Evf_Mode, 0, out uint evfMode);
        if (err == EDSDK.EDS_ERR_OK && evfMode == 0)
        {
            var enabled = 1u;
            err = EdsSetPropertyDataUInt32(camera, EDSDK.PropID_Evf_Mode, 0, sizeof(uint), ref enabled);
        }

        if (err != EDSDK.EDS_ERR_OK)
        {
            return err;
        }

        err = EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, out uint device);
        if (err != EDSDK.EDS_ERR_OK)
        {
            return err;
        }

        device |= EDSDK.EvfOutputDevice_PC;
        return EdsSetPropertyDataUInt32(camera, EDSDK.PropID_Evf_OutputDevice, 0, sizeof(uint), ref device);
    }

    public uint StopLiveView(IntPtr camera)
    {
        var err = EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, out uint device);
        if (err != EDSDK.EDS_ERR_OK || (device & EDSDK.EvfOutputDevice_PC) == 0)
        {
            return err;
        }

        device &= ~EDSDK.EvfOutputDevice_PC;
        return EdsSetPropertyDataUInt32(camera, EDSDK.PropID_Evf_OutputDevice, 0, sizeof(uint), ref device);
    }

    public uint DownloadLiveViewFrame(IntPtr camera, out byte[] jpeg)
    {
        jpeg = [];
        var stream = IntPtr.Zero;
        var evfImage = IntPtr.Zero;
        try
        {
            // Bộ đệm 2 MB như sample; khung thực tế ~185 KB (đo trên EOS M50 Mark II).
            var err = EDSDK.EdsCreateMemoryStream(2 * 1024 * 1024, out stream);
            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsCreateEvfImageRef(stream, out evfImage);
            }

            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsDownloadEvfImage(camera, evfImage);
            }

            if (err == EDSDK.EDS_ERR_OK)
            {
                jpeg = CopyStream(stream);
            }

            return err;
        }
        finally
        {
            if (evfImage != IntPtr.Zero)
            {
                EDSDK.EdsRelease(evfImage);
            }

            if (stream != IntPtr.Zero)
            {
                EDSDK.EdsRelease(stream);
            }
        }
    }

    public uint DownloadItem(IntPtr item, out byte[] data)
    {
        data = [];
        var stream = IntPtr.Zero;
        try
        {
            var err = EDSDK.EdsGetDirectoryItemInfo(item, out var info);
            if (err != EDSDK.EDS_ERR_OK)
            {
                return err;
            }

            err = EDSDK.EdsCreateMemoryStream(info.Size, out stream);
            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsDownload(item, info.Size, stream);
            }

            if (err != EDSDK.EDS_ERR_OK)
            {
                EDSDK.EdsDownloadCancel(item);
                return err;
            }

            err = EDSDK.EdsDownloadComplete(item);
            if (err != EDSDK.EDS_ERR_OK)
            {
                return err;
            }

            data = CopyStream(stream);
            return EDSDK.EDS_ERR_OK;
        }
        finally
        {
            if (stream != IntPtr.Zero)
            {
                EDSDK.EdsRelease(stream);
            }
        }
    }

    public void DiscardItem(IntPtr item)
    {
        EDSDK.EdsDownloadCancel(item);
        EDSDK.EdsRelease(item);
    }

    public void ReleaseItem(IntPtr item) => EDSDK.EdsRelease(item);

    public void PumpEvents() => Win32MessagePump.Drain();

    /// <summary>
    /// Số byte thật đã ghi là vị trí hiện tại của stream; <c>EdsGetLength</c> trả về kích thước bộ đệm đã cấp.
    /// </summary>
    private static byte[] CopyStream(IntPtr stream)
    {
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

    /// <summary>
    /// <c>EDSDK.EdsSetPropertyData</c> trong file của Canon dùng <c>UnmanagedType.AsAny</c>, không được hỗ trợ trên .NET 10.
    /// </summary>
    [DllImport("EDSDK.dll", EntryPoint = "EdsSetPropertyData")]
    private static extern uint EdsSetPropertyDataUInt32(
        IntPtr inRef, uint inPropertyID, int inParam, int inPropertySize, ref uint inPropertyData);

    [DllImport("EDSDK.dll", EntryPoint = "EdsSetPropertyData")]
    private static extern uint EdsSetPropertyDataPictureStyle(
        IntPtr inRef, uint inPropertyID, int inParam, int inPropertySize, ref EDSDK.EdsPictureStyleDesc inPropertyData);
}
