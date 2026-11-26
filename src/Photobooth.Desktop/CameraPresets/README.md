# Bộ thông số máy ảnh Canon đi kèm ứng dụng

> Tra cứu đầy đủ tên tham số, mã giá trị, nhãn và ràng buộc khi tự dựng bộ mới: [`docs/CANON_CAMERA_SETTINGS.md`](../../../docs/CANON_CAMERA_SETTINGS.md).

Cùng mô hình với thư mục `Templates`:

- Các file `*.json` ở đây được chép cạnh `Photobooth.exe` (thư mục `CameraPresets`) khi build/publish.
- Lần đầu ứng dụng chạy trên một máy, chúng được chép vào thư viện `%LOCALAPPDATA%\Photobooth\CameraPresets`.
  Thư viện đã có thì **không bao giờ bị chép đè** — dùng nút **Admin › Camera › “Thêm bộ mẫu còn thiếu”** để lấy bộ mới.
- Thêm bộ mẫu: trong **Admin › Camera › Bộ thông số đã lưu**, nhấn **Xuất** rồi lưu file vào thư mục này và commit.
- Chỉ mã giá trị của máy ảnh (`code`) được dùng khi áp dụng; `label` chỉ để đọc. Test `BundledPresetTests` kiểm tra nhãn khớp mã — sửa tay sai mã sẽ bị phát hiện.

## Các bộ có sẵn (giả định: đèn LED trắng liên tục)

Chung cho mọi bộ: chế độ **M** (xoay vòng chế độ trên máy), cân bằng trắng **theo Kelvin** (không dùng tự động để các ảnh trong một dải cùng màu),
lấy nét **Khuôn mặt + bám theo**, **JPEG L Fine**, tỉ lệ **3:2**, **không tự tắt nguồn**.

| Bộ | Tốc độ | Khẩu độ | ISO | Màu | Picture Style | Dùng khi |
|---|---|---|---|---|---|---|
| Đèn LED trắng (điểm khởi đầu) | 1/125 | f/5.6 | 800 | 5500 K | Chân dung | Bắt đầu ở địa điểm mới, rồi chỉnh ISO |
| LED trắng – Nhóm đông người | 1/125 | f/8.0 | 1600 | 5500 K | Tiêu chuẩn | 4 người trở lên, đứng nhiều hàng (cần nét sâu) |
| LED trắng – Chân dung 1–2 người | 1/125 | f/4.0 | 400 | 5500 K | Chân dung, tương phản −1, nét 2 | Cặp đôi, ảnh mặt gần, da mềm |
| LED trắng – Phòng tối, đèn yếu | 1/100 | f/4.0 | 1600 | 5500 K | Chân dung | Đèn LED công suất nhỏ hoặc xa khách |
| LED + đèn vàng hội trường | 1/100 | f/5.6 | 1250 | 4300 K | Chân dung | Hội trường/tiệc cưới có đèn vàng lẫn đèn LED |
| Màu tươi – Tiệc, sinh nhật | 1/125 | f/5.6 | 800 | 5500 K | Tiêu chuẩn, bão hòa +2, tương phản +1 | Tiệc vui, phông màu sặc sỡ |
| Đen trắng cổ điển | 1/125 | f/5.6 | 800 | 5500 K | Đơn sắc, tương phản +2 | Concept cổ điển/sang trọng |

## Cách dùng tại sự kiện

1. Dựng đèn, xoay máy về **M**, kết nối Canon trong Admin › Camera.
2. Chọn bộ gần nhất với tình huống → **Áp dụng**.
3. Nhìn Live View: tối thì **tăng ISO**, sáng thì **giảm ISO** (giữ nguyên tốc độ/khẩu độ). Da ám vàng thì **tăng** nhiệt độ màu, ám xanh thì **giảm**.
4. Chụp thử vài ảnh với người thật, hài lòng thì **Lưu thông số hiện tại** thành bộ riêng cho địa điểm đó.

## Lưu ý

- Đây là **điểm khởi đầu** theo kinh nghiệm chụp ảnh chung, chưa chụp thử trên máy thật — giá trị tốt nhất phụ thuộc công suất đèn, khoảng cách và phông nền.
- Ống kính kit EF-M 15–45mm: f/4.0 chỉ có ở góc rộng (~15–20mm). Ở tiêu cự dài, máy không cho f/4.0 → khi áp dụng, mục khẩu độ được **bỏ qua kèm lý do**; chọn khẩu độ nhỏ nhất máy cho.
- Chi tiết Picture Style (tương phản, bão hòa, độ nét) **chưa kiểm chứng trên EOS M50 Mark II**; nếu máy không hỗ trợ, các mục này được bỏ qua kèm lý do, phần còn lại vẫn áp dụng.
- Với đèn LED cùng nhiệt độ màu 5500 K thì dùng đúng số ghi trên đèn (ví dụ 5600 K) sẽ cho màu chuẩn hơn.
