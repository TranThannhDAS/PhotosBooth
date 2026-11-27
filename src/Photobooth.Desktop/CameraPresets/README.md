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

## Bộ dành riêng cho Canon EOS R50 — LED liên tục, đứng khoảng 2m

Bổ sung 6 bộ có tiền tố **`R50 LED 2m`**. Đó là các **điểm khởi đầu để thử chụp**, không phải thông số phơi sáng tối ưu trong mọi phòng.
Đều dùng chế độ M, JPEG L Fine, khung **3:2**, cân bằng trắng cố định **5500K**, Picture Style phù hợp, lấy nét khuôn mặt + bám theo (nếu máy cho phép).

| Bộ R50 | Màn trập | Khẩu độ | ISO | Dùng khi |
|---|---|---|---|---|
| **Mặc định** | 1/125 | f/5.6 | 400 | Khởi đầu cho 2–4 người, LED trắng mạnh |
| **Cặp đôi 1–2 người** | 1/125 | f/4.5 | 200 | Góc rộng, đủ sáng; giảm tương phản da |
| **Nhóm đông nhiều hàng** | 1/125 | f/8.0 | 800 | Cần độ sâu trường ảnh tốt hơn |
| **Chuyển động ít nhòe** | 1/160 | f/5.6 | 500 | Khách tạo dáng nhanh; đèn phải đủ sáng |
| **Thử 1/100 đèn 50Hz** | 1/100 | f/5.6 | 400 | Thử khi ánh sáng có vệt tối/sáng do LED nhấp nháy; không bảo đảm triệt tiêu mọi kiểu flicker |
| **Đèn rất mạnh ISO thấp** | 1/125 | f/5.6 | 200 | Phông sáng, LED công suất cao; kiểm tra ảnh không thiếu sáng |

### Cấu hình vật lý — KHÔNG lưu/điều khiển được bằng các JSON preset này

- **Lens:** chưa xác nhận model ống kính. Nếu dùng kit **RF-S18–45mm f/4.5–6.3 IS STM**, góc rộng nhất là **18mm**, không thể đặt 15mm. Nếu muốn **15mm**, phải chọn lens Canon RF/RF-S tương thích khác. Mẫu cặp đôi ở f/4.5 chỉ dùng khi lens/mức zoom hỗ trợ, nếu không phần mềm sẽ bỏ qua thông số khẩu độ và báo lý do.
- **Khoảng cách 2m:** chỉ là vị trí lắp đặt/tham khảo, phải đánh dấu chỗ khách đứng; không phải thông số Canon SDK. Với R50 + 18mm ở khoảng cách 2m, chiều rộng ảnh gốc lý thuyết khoảng 2,48m, nhưng vùng crop của template có thể hẹp hơn đáng kể.
- **Đèn LED:** chưa rõ hãng/model, công suất, **độ rọi thực tế tại mặt khách**, tản sáng/softbox và số lượng đèn. Những thông số này quyết định nên dùng ISO 200, 400, 800 hay cao hơn. Tránh đèn chiếu thẳng gây chói mắt khách.
- **Màu đèn:** chưa rõ CCT/Kelvin thực (ví dụ 5600K), CRI/TLCI, mức pha trộn với đèn vàng trong sảnh. Các bộ này mặc định **5500K**, cần đổi theo ánh sáng thực tế hoặc đo bằng thẻ xám.
- **Nhấp nháy LED:** chưa rõ driver PWM và nguồn điện tại nơi tổ chức. R50 có chế độ **Anti-flicker shooting**, nên kiểm tra trên máy. Preset này không có setting điều khiển Anti-flicker vì catalog EDSDK hiện không khai báo; 1/100s là phương án thử ở nguồn 50Hz, không phải cam kết chống sọc.
- **Bố cục:** chưa rõ số người lớn nhất (đặc biệt 5–8 người), khoảng cách giữa các hàng và chiều rộng phông. Nhóm đông cần thử thực tế trước khi vận hành; không thể đảm bảo mọi người vừa khung của template dọc.
- **Tương thích EDSDK:** Canon công bố EOS R50 có hỗ trợ EDSDK, nhưng các giá trị `liveViewAfMethod`, `whiteBalance`, khẩu độ và điều khiển chụp cần thử với **máy thật + SDK/firmware đang dùng**. Những thuộc tính không được máy cho chỉnh sẽ được báo **Bỏ qua** trong Admin.
- **Kết nối Kiosk hiện tại:** từ mã nguồn `ServiceRegistration.cs`, `ICameraDevice` của Kiosk vẫn được gán sang `WebViewCameraDevice`; còn preset R50 là chức năng **Admin › Camera** dùng EDSDK. Chỉ cài preset **chưa làm Kiosk chụp bằng EOS R50**. Cần tích hợp `CanonCameraDevice` riêng khi chuyển hệ thống sang Canon hoàn toàn.


### Cài preset R50 trên máy đã có thư viện CameraPresets

Preset đi kèm ứng dụng nằm trong `src/Photobooth.Desktop/CameraPresets/`, nhưng máy đã chạy trước đây có thư viện riêng:
`%LOCALAPPDATA%\Photobooth\CameraPresets`.

Sau khi build/publish lại, vào **Admin → Camera → Thêm bộ mẫu còn thiếu** để đưa 6 bộ mới vào thư viện cục bộ.
Không cần sửa hoặc xóa các preset cũ.

### Thử và hiệu chỉnh lúc lắp đặt

1. Gắn đúng lens, xoay góc rộng phù hợp, bố trí đèn và đánh dấu khoảng cách đứng khoảng 2m. Xoay **vòng chế độ R50 về M**.
2. Bắt đầu với **R50 LED 2m - Mặc định**, áp dụng preset, chụp thử người thật (không chỉ nhìn Live View, vì độ sáng Preview có thể khác ảnh JPEG).
3. Nếu ảnh tối/sáng, ưu tiên tăng/giảm **ISO 1/3 stop**, giữ tốc độ chụp và khẩu độ. Đo lại khi bật đèn màu của hội trường.
4. Với nhóm đông chia hàng, chọn **Nhóm đông nhiều hàng**; kiểm tra mọi khuôn mặt đều nét và nằm **trong vùng crop của template**.
5. Kiểm tra 4 ảnh liên tiếp xem có sọc hoặc sai màu giữa các lần chụp không. Nếu có, thử chế độ Anti-flicker trên R50 và preset 1/100.
6. Khi ảnh đạt yêu cầu, dùng **Lưu thông số hiện tại** để lưu cấu hình thực tế thành một preset riêng cho sự kiện.

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
