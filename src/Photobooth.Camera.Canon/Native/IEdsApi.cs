namespace Photobooth.Camera.Canon.Native;

/// <summary>
/// Các thao tác EDSDK mà adapter cần, ở mức vừa đủ để thay bằng SDK giả khi kiểm thử.
/// Mọi phương thức chỉ được gọi trên luồng SDK của <see cref="CanonSdkHost"/>; giá trị trả về <see cref="uint"/> là mã lỗi EDSDK.
/// </summary>
internal interface IEdsApi
{
    uint Initialize();

    uint Terminate();

    /// <summary>Lấy máy ảnh đầu tiên đang kết nối; trả về <c>EDS_ERR_DEVICE_NOT_FOUND</c> nếu không có.</summary>
    uint FindFirstCamera(out IntPtr camera, out string description);

    /// <summary>Liệt kê mọi máy ảnh Canon đang kết nối (không mở session, không giữ tham chiếu).</summary>
    uint ListCameras(out IReadOnlyList<CanonCameraInfo> cameras);

    /// <summary>
    /// Đăng ký callback. <paramref name="onTransferRequested"/> nhận tham chiếu ảnh mới (người nhận chịu trách nhiệm giải phóng)
    /// kèm tên file trên máy ảnh; <paramref name="onStateEvent"/> nhận mã sự kiện trạng thái (<c>StateEvent_*</c>);
    /// <paramref name="onPropertyChanged"/> nhận mã thuộc tính vừa đổi giá trị hoặc đổi danh sách giá trị hợp lệ.
    /// </summary>
    uint SetEventHandlers(
        IntPtr camera,
        Action<IntPtr, string> onTransferRequested,
        Action<uint> onStateEvent,
        Action<uint> onPropertyChanged);

    uint GetUInt32(IntPtr camera, uint propertyId, out uint value);

    uint GetString(IntPtr camera, uint propertyId, out string value);

    uint SetUInt32(IntPtr camera, uint propertyId, uint value);

    /// <summary>Các giá trị máy ảnh cho phép đặt lúc này (rỗng = không chỉnh được ở chế độ hiện tại).</summary>
    uint GetAllowedValues(IntPtr camera, uint propertyId, out IReadOnlyList<uint> values);

    /// <summary>
    /// Mở khóa các thuộc tính riêng của Canon (mã 0x01…, ví dụ tỉ lệ khung, tự tắt nguồn) — phải gọi TRƯỚC khi mở session,
    /// theo OpenSessionCommand của sample Canon. Máy không hỗ trợ thì bỏ qua.
    /// </summary>
    void UnlockPrivateProperties(IntPtr camera);

    uint OpenSession(IntPtr camera);

    /// <summary>Chi tiết (tương phản, độ nét, bão hòa, tông màu...) của một Picture Style.</summary>
    uint GetPictureStyleDesc(IntPtr camera, uint pictureStyle, out EDSDKLib.EDSDK.EdsPictureStyleDesc desc);

    uint SetPictureStyleDesc(IntPtr camera, uint pictureStyle, EDSDKLib.EDSDK.EdsPictureStyleDesc desc);

    uint CloseSession(IntPtr camera);

    /// <summary>Hủy đăng ký callback và giải phóng tham chiếu máy ảnh.</summary>
    void ReleaseCamera(IntPtr camera);

    /// <summary>Ảnh chụp được gửi thẳng về PC (SaveTo = Host) và báo dung lượng PC còn trống.</summary>
    uint SetSaveToHost(IntPtr camera);

    uint PressShutter(IntPtr camera, ShutterButtonState state);

    /// <summary>Gia hạn bộ hẹn giờ tự tắt nguồn của máy ảnh.</summary>
    uint ExtendShutDownTimer(IntPtr camera);

    /// <summary>Bật Live View xuất về PC (Evf_Mode = 1 nếu đang tắt, Evf_OutputDevice |= PC).</summary>
    uint StartLiveView(IntPtr camera);

    /// <summary>Tắt Live View xuất về PC (Evf_OutputDevice bỏ cờ PC).</summary>
    uint StopLiveView(IntPtr camera);

    /// <summary>
    /// Lấy một khung Live View (JPEG). <c>EDS_ERR_OBJECT_NOTREADY</c> là bình thường khi khung chưa sẵn sàng
    /// (đặc biệt ngay sau khi bật Live View).
    /// </summary>
    uint DownloadLiveViewFrame(IntPtr camera, out byte[] jpeg);

    /// <summary>Tải toàn bộ ảnh vào bộ nhớ và báo hoàn tất cho máy ảnh. Không giải phóng <paramref name="item"/>.</summary>
    uint DownloadItem(IntPtr item, out byte[] data);

    /// <summary>Báo hủy nhận ảnh và giải phóng tham chiếu.</summary>
    void DiscardItem(IntPtr item);

    void ReleaseItem(IntPtr item);

    /// <summary>Cho SDK cơ hội gọi các callback đang chờ (Windows message pump).</summary>
    void PumpEvents();
}

internal enum ShutterButtonState : uint
{
    Off = 0x00000000,
    Completely = 0x00000003,
}
