# Canon EDSDK — thư viện native

| Mục | Giá trị |
|---|---|
| Phiên bản | Canon EDSDK **13.21.10** (Release 08/03/2026) |
| Nguồn | Gói `EDSDK132110CD(13.21.10)` do Canon cung cấp, thư mục `Windows\EDSDK_64\Dll` |
| Kiến trúc | Chỉ **x64** (`win-x64/`). Ứng dụng nạp DLL này phải chạy 64-bit |
| File | `EDSDK.dll`, `EdsImage.dll` — giữ nguyên, không chỉnh sửa |

## Lưu ý

- Việc đưa DLL vào repo đã được chủ dự án quyết định (26/11/2026). Điều khoản sử dụng/phân phối lại do Canon quy định theo thỏa thuận tải SDK.
- EDSDK có sử dụng mã của Independent JPEG Group. Theo `IJG-README.txt`, khi phân phối bản chạy phải ghi trong tài liệu kèm theo: *"this software is based in part on the work of the Independent JPEG Group"*.
- Khi nâng cấp EDSDK: thay cả hai DLL cùng lúc, cập nhật bảng trên và thay `src/Photobooth.Camera.Canon/Native/EDSDK.cs` bằng bản trong sample C# của cùng phiên bản (kiểm tra lại các overload bổ sung trong `NativeEdsApi.cs`).
- `src/Photobooth.Camera.Canon` tự chép hai DLL này vào thư mục output của project tham chiếu nó.
