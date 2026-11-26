# CanonSpike — kiểm chứng Canon EDSDK trên .NET 10

Console app thử nghiệm, **không thuộc solution chính**, dùng để xác nhận các rủi ro trước khi viết `CanonCameraDevice` cho Photobooth.
Kết quả của spike quyết định thiết kế adapter (xem `docs/ARCHITECTURE.md`, mục "Máy ảnh Canon qua EDSDK").

## Nội dung

| File | Vai trò |
|---|---|
| `Native/EDSDK.cs` | Khai báo P/Invoke **nguyên bản** từ sample C# của Canon (EDSDK 13.21.10), không chỉnh sửa |
| `Native/EdsInterop.cs` | Phần bổ sung: `EdsGetEvent` (sample không khai báo) và `SetUInt32` (thay `EdsSetPropertyData` dùng `UnmanagedType.AsAny`, kiểu marshal không được hỗ trợ trên .NET 10) |
| `Native/Win32MessagePump.cs` | Xử lý Windows message trên luồng STA, thay cho message loop của WinForms |
| `SpikeRunner.cs` | Kịch bản kiểm thử từng bước |

DLL được copy từ `lib/EDSDK/win-x64/` khi build. Ứng dụng bắt buộc chạy x64.

## Chuẩn bị máy ảnh (EOS M50 Mark II)

1. Sạc đầy pin hoặc dùng bộ nguồn AC; tắt **Wi-Fi/Bluetooth** trên máy ảnh.
2. Vòng chế độ để ở **M** hoặc **P**; chất lượng ảnh **JPEG** (không RAW); ống kính để **AF**.
3. Thoát **EOS Utility**, **EOS Webcam Utility** và hộp thoại nhập ảnh tự động của Windows (nếu bật lên khi cắm máy).
4. Cắm cáp USB vào laptop rồi bật máy ảnh.

## Các lượt chạy cần thử

Chạy trong thư mục `tools/CanonSpike`:

```powershell
# 1. Mặc định: message pump Win32, Live View 5 giây, chụp 2 ảnh có AF
dotnet run

# 2. Nhận callback bằng EdsGetEvent thay vì message pump
dotnet run -- --pump getevent

# 3. Nếu lượt 1 chụp lỗi/timeout: thử nhấn nửa để lấy nét trước, hoặc không AF
dotnet run -- --shutter halfthenfull
dotnet run -- --shutter nonaf

# 4. Phát hiện mất kết nối: chương trình chờ 60 giây, bạn rút cáp USB (hoặc tắt máy ảnh)
dotnet run -- --shots 0 --no-liveview --wait-disconnect
```

Mỗi lượt tạo một thư mục `bin/Debug/net10.0-windows/spike-output/<thời gian>/` gồm `report.txt`, ảnh chụp `capture-N.jpg` và khung Live View mẫu `liveview-before.jpg` / `liveview-after.jpg`.

## Cần gửi lại sau khi chạy

- Nội dung `report.txt` của từng lượt (phần **TÓM TẮT** và dòng **"callback chạy trên"** là quan trọng nhất).
- Ảnh `capture-N.jpg` có mở được và đúng độ phân giải gốc không; khung Live View có đúng hình không.
- Màn hình máy ảnh có hiện thông báo lạ, hoặc máy có bị treo cần tắt/bật lại không.
