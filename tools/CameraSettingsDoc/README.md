# Sinh tài liệu thông số máy ảnh Canon

`docs/CANON_CAMERA_SETTINGS.md` được sinh từ khung `template.md`: các chỗ `{{TABLE:…}}` được điền bằng bảng mã/nhãn
đọc trực tiếp từ `src/Photobooth.Camera.Canon/CanonSettings.cs`, nên tài liệu luôn khớp với code.

Chạy lại khi sửa bảng mã trong `CanonSettings.cs` hoặc sửa nội dung `template.md` (chạy tại thư mục gốc repo):

```powershell
python -X utf8 tools/CameraSettingsDoc/build.py . tools/CameraSettingsDoc/template.md
```

Script dừng với lỗi nếu còn chỗ `{{…}}` chưa điền hoặc không tìm thấy bảng mã.
