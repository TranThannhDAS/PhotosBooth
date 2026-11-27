# Wedding Luxury Collection — Photobooth Mini

Bộ **6 mẫu template cưới mới**, được thiết kế riêng cho Photobooth Mini với họa tiết PNG nguyên bản, không dùng các file template thương mại từ Internet.

## Danh sách

| ID thư mục | Tên hiển thị | Ảnh chụp/phiên | Bố cục |
|---|---|---:|---|
| `wedding-royal-ivory-gold` | Hoàng Gia Ngà Vàng | 4 | 1 ảnh lớn + 3 ảnh nhỏ |
| `wedding-blush-peony` | Vườn Mẫu Đơn Hồng | 4 | Lưới 2×2 |
| `wedding-midnight-gala` | Dạ Tiệc Ánh Kim | 2 | 2 ảnh lớn |
| `wedding-sage-garden` | Khu Vườn Xanh Ngọc | 3 | 1 ảnh lớn + 2 ảnh nhỏ |
| `wedding-burgundy-velvet` | Nhung Đỏ Vương Giả | 1 | 1 ảnh lớn, thích hợp chụp nhóm |
| `wedding-champagne-double-strip` | Champagne Hai Dải 2×6 | 4 | 2 dải giống nhau (mỗi dải 4 ảnh) |

Mỗi mẫu sử dụng **canvas 1200×1800px, 300 DPI (4×6 inch)**. Các ô ảnh có tỷ lệ xấp xỉ **3:2** (nguồn Canon M50 Mark II). Mỗi thư mục bao gồm `template.json` và `assets/background.png`; các chữ, tên cặp đôi và ngày có thể chỉnh qua **Visual Template Editor**. Tên demo mặc định là **MINH & LINH**; ngày lấy theo `{date}` khi khách chụp.

## Cài bộ mẫu vào thư viện trên máy hiện tại

Trong mã nguồn, các template được đóng gói tại:

```text
src/Photobooth.Desktop/Templates/<id>/template.json
src/Photobooth.Desktop/Templates/<id>/assets/background.png
```

Khi build, `Photobooth.Desktop.csproj` sẽ sao chép chúng vào `Templates/` cạnh executable.

**Lưu ý quan trọng:** Ứng dụng chỉ tự chép thư viện template đi kèm trong **lần chạy đầu tiên**, khi thư mục `%LOCALAPPDATA%\\Photobooth\\Templates` chưa tồn tại. Nếu bạn đã chạy ứng dụng trước đó thì cập nhật repository **không tự hiện bộ mẫu mới**.

Để cài cả 6 mẫu vào thư viện đang có, tại thư mục gốc repository chạy:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-wedding-templates.ps1
```

Script **chỉ sao chép thư mục chưa tồn tại**; không sửa/xóa `library.json`, không ghi đè template tùy chỉnh. Vào **Admin › Template Manager › Tải lại** để thấy template mới. Theo quy ước tính năng event-scoped, cả sáu template mới sẽ là **Template chung** vì không có `eventTemplates` trong `library.json` cho chúng; bạn có thể bỏ tích "Template chung" và chọn sự kiện tương ứng. Trong Admin có thể bật/tắt mẫu cho Kiosk.

Nếu muốn thủ công, mở **Admin › Template Manager › Mở thư mục** và sao chép 6 thư mục template từ `src/Photobooth.Desktop/Templates/` vào thư viện, không sao chép file README này.

## Quy tắc thiết kế

- Ảnh nguyên bản không bị crop trước; template chỉ chỉ định slot `cover` và `focalPoint` cho Composer.
- Họa tiết nằm dưới các ô ảnh (`zIndex: -90`); viền và chữ nằm trên ảnh.
- Nền nghệ thuật được raster hóa PNG indexed 600×900 và co giãn lên canvas 1200×1800, giữ phong cách hoa/botanical/art-deco và dung lượng vừa phải.
- Mẫu hai dải có **8 ô ảnh** nhưng dùng `photoIndex` 0..3 hai lần: ứng dụng **chỉ chụp 4 lần**.
- Không có asset truy cập Internet, không phụ thuộc font tải ngoài; dùng font **Georgia** phổ biến trên Windows.
