# CanonAdapterCheck — kiểm tra adapter Canon với máy ảnh thật

Chạy **chính** `CanonCameraDevice` (project `src/Photobooth.Camera.Canon`) — khác với `tools/CanonSpike` (gọi SDK trực tiếp).
Không thuộc solution chính. Chuẩn bị máy ảnh giống `tools/CanonSpike/README.md`.

```powershell
# Trong thư mục tools/CanonAdapterCheck
dotnet run                     # kết nối + chụp 4 ảnh như một phiên Photobooth
dotnet run -- --reconnect      # thêm: rút cáp → Faulted, cắm lại → tự kết nối lại → chụp 1 ảnh
```

Kết quả: `bin/Debug/net10.0-windows/adapter-output/<thời gian>/` gồm `report.txt` và các ảnh chụp.
Gửi lại phần **TÓM TẮT** trong `report.txt`.
