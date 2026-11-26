# Quy chuẩn giao diện và trải nghiệm người dùng — Photobooth Mini V1

> **Ngôn ngữ giao diện:** Tiếng Việt. AI phải trao đổi và báo cáo bằng tiếng Việt; định danh Razor/CSS/JS vẫn theo quy ước kỹ thuật.
>
> **Nền tảng:** Ứng dụng Windows **WPF + BlazorWebView**. V1 dùng chuột/bàn phím của laptop; thiết kế **sẵn sàng cho màn hình cảm ứng** ở tương lai.
>
> **Trạng thái:** Đây là bộ **nguyên tắc**. Màu sắc, logo, bộ nhận diện và thiết kế pixel-perfect chính thức **CHƯA CHỐT**. Không tự tuyên bố các hình minh họa hay mã màu mẫu là thiết kế đã duyệt.

## 1. Mục tiêu trải nghiệm

1. Người lần đầu tiếp xúc vẫn có thể tự hoàn thành một lượt chụp ảnh.
2. Mỗi màn hình có **một hành động chính nổi bật**, không bắt người dùng đọc hướng dẫn dài.
3. Camera preview là trung tâm của màn hình chụp; bộ đếm ngược và tiến trình ảnh luôn dễ nhận thấy.
4. Khi lỗi xảy ra, giao diện không bế tắc: có lời giải thích ngắn gọn và hành động tiếp theo.
5. Các thao tác quan trọng phù hợp với chuột **và** cảm ứng.
6. Ảnh gốc/thành phẩm có thể xem lại; không vô tình làm mất dữ liệu do điều hướng hoặc thao tác trùng.
7. Ứng dụng offline và không có các thành phần phải tải mạng mới hiển thị.

## 2. Phân biệt hai chế độ

| Tiêu chí | Kiosk Mode (Khách) | Admin Mode (Quản trị) |
|---|---|---|
| Mục đích | Chụp ảnh, chọn mẫu, xem lại và lưu/xuất | Chọn webcam, điều chỉnh cấu hình, xem lịch sử và lỗi |
| Số lượng thao tác | Tối thiểu, ưu tiên nút lớn | Có thể nhiều trường, bảng và hộp thoại hơn |
| Điều hướng | Tuần tự theo phiên chụp | Phân khu mục rõ ràng, có quay lại Kiosk |
| Nội dung kỹ thuật | Hạn chế tối đa | Có trạng thái thiết bị và lỗi tóm tắt |
| Xử lý lỗi | Thông báo dễ hiểu, có nút thử lại | Có thông tin kỹ thuật cần thiết để chẩn đoán |

- **Không hiện chức năng quản trị trong luồng khách** nếu nó không cần cho thao tác chụp ảnh.
- Cơ chế mở Admin (đã chốt 19/11/2026 theo yêu cầu chủ sản phẩm): nút ở màn khởi động, và nút nhỏ **"Quản trị"** ở góc trên phải màn hình chào của Kiosk; cả hai đều mở bàn phím PIN. Nút ở màn hình chào chỉ có khi không có phiên chụp dang dở. Không phụ thuộc Internet. PIN cố định `1234` là rủi ro đã biết (`BUSINESS_RULES.md` mục 9).
- Không khóa hệ điều hành ở mức khiến quản trị viên không thể thoát an toàn. Cơ chế kiosk/fullscreen phải được kiểm thử trên Windows.

## 3. Cấu trúc điều hướng Kiosk

```text
Chào mừng
   ↓ Bắt đầu
Chọn template
   ↓ Tiếp tục
Camera preview
   ↓ Chụp
Đếm ngược & chụp nhiều ảnh
   ↓ Đủ ảnh
Xem lại / Chụp lại (theo chính sách đã chốt)
   ↓ Ghép ảnh
Xem thành phẩm
   ↓ Xuất JPEG / PNG / PDF
Hoàn thành → Phiên mới
```

- Không tạo session trùng khi người dùng bấm **Bắt đầu** liên tục.
- Không cho điều hướng tạo thêm lần chụp song song khi camera đang bận.
- Nếu quay lại bước trước có thể làm mất thay đổi chưa lưu, hiển thị cảnh báo phù hợp và **không xóa ảnh gốc**.
- Hành vi quay lại/chụp lại/xuất tự động còn **CHƯA CHỐT** phải được xác nhận theo `BUSINESS_RULES.md`, không ngầm tự xử lý.

## 4. Danh sách màn hình và yêu cầu hiển thị

### 4.1 Màn hình chào (`Welcome`)

- Hiển thị tên/logo ứng dụng, một câu hướng dẫn ngắn, nút **Bắt đầu chụp** dễ nhìn.
- Có nút phụ **Thư viện sự kiện** (quyết định mới của chủ sản phẩm: chỉ-xem các phiên trong sự kiện hiện tại, offline, không chỉnh sửa/xóa/in/xuất/đổi sự kiện); không làm lu mờ nút **Bắt đầu chụp**.
- Nếu camera chưa cấu hình, có thể cho bắt đầu rồi xử lý ở bước camera; không báo lỗi giả khi chưa kiểm tra thiết bị.
- Khi có tiến trình khởi tạo đang chạy, phản hồi rõ để tránh bấm liên tục.

### 4.2 Chọn template (`TemplateSelection`)

- Hiển thị mẫu preview có thể nhận biết bố cục, tên mẫu và trạng thái đang chọn.
- Mẫu đang chọn cần có viền/trạng thái rõ; không chỉ dùng màu để biểu thị lựa chọn.
- Mỗi template hiển thị số ảnh cần chụp. Template vượt giới hạn an toàn số ảnh được đánh dấu không thể chọn và giải thích lý do.
- Có nút **Tiếp tục** và **Quay lại**.
- Có thể nhấp đúp vào thẻ khung hợp lệ để chọn mẫu đó và tiếp tục ngay; chạm một lần chỉ chọn mẫu, nút **Tiếp tục** vẫn dùng được trên cảm ứng và bàn phím.
- Không tự tải template hoặc phông chữ từ Internet.

### 4.3 Camera preview (`Camera`)

- Khung preview chiếm diện tích đủ lớn; tỉ lệ video tự nhiên, không kéo méo hình người; hiển thị **toàn bộ** khung hình camera (`object-fit: contain`, không dùng `cover`).
- **Khung hướng dẫn vùng ảnh thành phẩm:** viền nét đứt + nhãn ngắn, phần ngoài làm tối bán trong suốt (vẫn thấy hình); nằm đúng trên vùng nội dung video (không tính dải trống), cập nhật theo ảnh đang chụp, không chặn thao tác (`pointer-events: none`), nằm dưới số đếm ngược/thông báo.
- Hiển thị thông tin **Ảnh X/Y**, trạng thái camera, nút **Bắt đầu chụp** và **Quay lại/Hủy** khi phù hợp.
- Nút **× — Thoát phiên chụp** ở góc trên phải dùng được cả khi đang đếm ngược/chụp. Bấm để kết thúc phiên và về màn chào, không cần xác nhận vì ảnh đã lưu không bị xóa. Hủy lượt chụp, chờ ghi xong khung hình đã lấy và metadata rồi dừng camera; không tự ghép ảnh khi đang thoát.
- **Toàn vùng cửa sổ khi đang chụp:** khi nhấn nút chụp, khung camera mở rộng phủ toàn bộ vùng nội dung cửa sổ ứng dụng (ẩn thanh tiêu đề, cột điều khiển và thanh chân trang) trong suốt lượt chụp (đếm ngược, chụp, lưu giữa các ảnh) mà không đổi chế độ fullscreen của cửa sổ WPF/hệ điều hành; giữ nguyên phần tử video (`object-fit: contain`), khung hướng dẫn khớp vùng video, tiến trình **Ảnh X/Y**, số đếm ngược và lớp phủ điều khiển **× — Thoát phiên chụp**, **Dừng chụp** dùng được bằng chuột/bàn phím. Khi dừng, lỗi hoặc kết thúc lượt chụp thì quay lại bố cục thường hoặc điều hướng hiện hữu.
- Khi đếm ngược, con số lớn, độ tương phản cao; không che toàn bộ khuôn hình nếu không cần.
- Sau một ảnh thành công, có phản hồi ngắn (ví dụ thumbnail hoặc thông báo đã chụp), không chuyển bước âm thầm.
- Khi camera chưa sẵn sàng: dùng trạng thái loading hoặc lỗi cụ thể, không hiển thị khung đen như thể đang chụp bình thường.
- **Chế độ gương (đã chốt 19/11/2026):** mặc định bật; ảnh xem lại và ảnh thành phẩm/file xuất phải **cùng hướng với live preview**. Có nút bật/tắt trên màn hình chụp, áp dụng cho các ảnh chụp tiếp theo. Ảnh gốc trên ổ đĩa vẫn là khung hình thô của camera; việc lật chỉ thực hiện một lần trong C# khi render/ghép, chỉ lật ô ảnh, không lật chữ/logo/trang trí của template.
- Khi rời màn hình: dừng preview, giải phóng camera phù hợp; nếu tiến trình chụp đang diễn ra, không bỏ qua kết quả đang ghi file.

### 4.4 Xem lại ảnh (`Review`)

- Hiển thị tất cả ảnh hợp lệ theo đúng thứ tự, phân biệt ảnh đã chọn cho từng slot.
- Nút chụp lại (nếu được phép) phải xác định rõ đang chụp lại ảnh nào/toàn bộ phiên.
- Không làm biến mất ảnh cũ khi thay ảnh mới chưa chụp hoặc lưu thành công.
- Nút **Ghép ảnh/Tiếp tục** chỉ hoạt động khi đủ ảnh và template hợp lệ.

### 4.5 Xem thành phẩm (`Result`)

- Ưu tiên hiển thị ảnh ghép lớn, giữ đúng tỉ lệ template; không cắt mất mép quan trọng.
- Màn Thành phẩm ở Kiosk dùng bố cục **photo-first**: ảnh ghép chiếm phần lớn chiều cao còn lại giữa header/footer; không có cột tùy chọn thường trực. Nút **Chỉnh sửa / In ảnh** mở hộp điều khiển, **Lưu ảnh** mở hộp chọn JPEG/PNG/PDF, còn **Phóng to** mở trình xem riêng (vừa màn hình hoặc 200%).
- Trong popup **Chỉnh sửa / In ảnh**, đặt **Bộ lọc màu** ở cuối, sau lựa chọn ô ảnh, ảnh nguồn và bảng in. Giữ nguyên khả năng đổi nhanh bộ lọc khi Photo Composer đang xử lý.
- **Toàn màn hình là trạng thái cửa sổ của toàn ứng dụng**, không thuộc riêng trình xem ảnh. WPF mở mặc định không viền/toàn màn hình ngay khi khởi chạy; nút **Toàn màn hình / Thu nhỏ cửa sổ** đặt ở header chung của các màn hình (màn mở đầu và Chào mừng có nút ở góc trên), có thể bật/tắt mà không thay đổi ảnh hay phiên đang chạy. Tắt chế độ này khôi phục kích thước/vị trí cửa sổ ban đầu.
- **Khi vừa đến bước Thành phẩm**, tự động mở trình xem ảnh lớn (khi Photo Composer có kết quả); phóng vừa vùng xem, chưa zoom 200%. Chạm/click vùng tối ngoài **ảnh thực tế** đóng trình xem và trở về bố cục Thành phẩm, **không thoát toàn màn hình của ứng dụng**. Chạm ảnh hay nút trong trình xem không đóng. Nút **Phóng to 200%** vẫn là tùy chọn bổ sung; Esc đóng trình xem.
- Trình xem không tự chỉnh crop/template, không ghi đè ảnh, không tự in hay lưu và không làm thay đổi độ phân giải file.
- Popup chỉnh vùng cắt đóng khi chạm/nhấp vào vùng mờ bên ngoài, tương tự **Hủy**: không tự áp dụng crop chưa xác nhận. Tương tác bên trong popup, đặc biệt kéo/chụm trên ảnh, không được làm đóng popup.
- Khi đang xuất file hoặc gửi lệnh in, khóa việc đóng popup tùy chọn liên quan để tránh ngắt thao tác. Không thay đổi quy tắc xác nhận in, lưu phiên bản và kết thúc phiên.
- Hành động chính: **Lưu/Xuất ảnh**; có lựa chọn định dạng JPEG, PNG, PDF.
- Hiển thị tiến trình và kết quả riêng cho từng định dạng được yêu cầu.
- Khi xuất thành công, hiển thị thông báo **“Đã lưu ảnh”** và vị trí/tùy chọn mở thư mục khi có thể.
- Không khẳng định PDF/in thành công chỉ vì đã tạo preview trên giao diện.
- Có nút **Phiên mới** sau khi hoàn tất theo chính sách kết thúc phiên.
- Màn Hoàn thành có nút **Thư viện sự kiện** để khách xem lại toàn bộ phiên trong sự kiện hiện tại (chỉ-xem, offline); quay lại vẫn giữ đúng phiên vừa hoàn thành. Thư viện không mở được giữa các bước chụp, ghép hoặc xuất ảnh.

### 4.5.1 Màn Hoàn thành — mã QR (Phase Cloud 2.1)

- Cloud đã cấu hình: hiển thị **mã QR lớn (240 px)** của đúng phiên bản khách vừa hoàn tất, kèm dòng "Quét mã QR để tải ảnh về điện thoại".
- Dòng trạng thái nhỏ bên dưới, **không bao giờ ẩn QR**:
  - "Ảnh đang được đồng bộ…"
  - "Ảnh đã sẵn sàng để tải xuống."
  - "Chưa có kết nối Internet. Ảnh sẽ tự đồng bộ khi có mạng — bạn vẫn có thể quét mã QR ngay."
  - "Ảnh chưa đồng bộ được. Vui lòng báo nhân viên — mã QR vẫn được giữ."
- Không hiển thị URL, lỗi kỹ thuật hay đường dẫn cho khách. Admin xem được URL, lỗi gần nhất và số lần thử.
- Không có thao tác nào khác trên QR (chỉ xem), nên không ảnh hưởng thao tác cảm ứng.

### 4.6 Admin (`Settings`, `Gallery`, `Diagnostics`)

- Nhóm cài đặt theo chủ đề: Camera, Phiên chụp, Template, Xuất file, Lưu trữ.
- Với mỗi thiết lập có label, giá trị hiện tại, mô tả đơn giản và kiểm tra dữ liệu hợp lệ.
- Khi thay đổi cấu hình ảnh hưởng phiên đang chạy, phải hiển thị thời điểm áp dụng hoặc khóa chỉnh sửa hợp lý.
- Gallery có thể hiển thị danh sách phiên theo ngày, xem thumbnail, mở ảnh/thư mục; dữ liệu lấy từ ổ đĩa/local metadata. Có ô chọn **"Sự kiện"** ở đầu danh sách (sự kiện đang dùng được ghi "(đang dùng)", sự kiện không truy cập được bị vô hiệu kèm lý do, "Tất cả sự kiện" khi có từ 2 sự kiện); lựa chọn được giữ khi mở rồi đóng một phiên.
- Diagnostics hiển thị trạng thái thiết bị và mã lỗi tóm tắt, không hiện stack trace đầy đủ cho khách.
- **Khung ảnh — Template Manager (Phase 3):** lưới thẻ có thumbnail, tên, kích thước canvas/DPI, số ảnh cần chụp, số ô/trang trí, công tắc "Hiện trên Kiosk" và các nút Chỉnh sửa/Sao chép/Đổi tên/Xuất .zip/Xóa. Template lỗi vẫn hiện thẻ đỏ kèm lỗi cụ thể. Có trạng thái đang tải, trống, lỗi đọc. Xóa luôn qua hộp thoại xác nhận (nút an toàn được focus). Kết quả nhập hiển thị danh sách lỗi theo vị trí thuộc tính.
- **Visual Template Editor:** ba vùng — trái: thêm phần tử + danh sách lớp (trên cùng ở đầu, nút ▲▼, nền canvas cố định dưới cùng); giữa: canvas (zoom −/+, "vừa khung", Ctrl + con lăn, lưới bắt dính); phải: thuộc tính phần tử đang chọn hoặc thông tin template/canvas, và khối "Kiểm tra" theo validator của Engine. Tay nắm đổi kích thước có vùng chạm 24 px; nhãn tọa độ/kích thước (pixel canvas) hiện khi chọn. Phím tắt: Ctrl+Z/Ctrl+Y, Ctrl+D, Delete, phím mũi tên (Shift = 10 px), Ctrl+S, Esc (bỏ chọn); không kích hoạt khi đang gõ trong ô nhập. Số liệu chỉ ghi nhận khi rời ô/Enter (một bước Undo). Khi đang chỉnh, các mục Admin khác bị khóa; đóng khi chưa lưu phải xác nhận. "Xem thử" mở ảnh render bằng Photo Composer (ảnh mẫu hoặc ô đánh số).

## 5. Responsive và kích thước màn hình

- V1 phải sử dụng tốt trên **màn hình laptop thông dụng**, không giả định luôn fullscreen 1920×1080.
- Kiểm thử tối thiểu ở **1280×720**, **1366×768** và **1920×1080** (hoặc kích thước tương đương mà môi trường có thể chạy).
- Thiết kế cho màn hình cảm ứng **13,3 inch** ở giai đoạn tương lai; thiết kế portrait/landscape cuối cùng **CHƯA CHỐT**.
- Dùng layout co giãn (`flex`, `grid`, `minmax`, đơn vị tương đối); tránh cố định tất cả bằng pixel.
- Mỗi màn hình phải vừa trong không gian khả dụng; nếu có scroll, nút hành động chính nên vẫn dễ tìm, không bị che khuất.
- Đừng dùng `height: 100vh` thiếu kiểm soát để nội dung bị cắt khi WPF window có kích thước thay đổi.
- Ảnh chụp và canvas preview cần `object-fit` rõ ràng; kiểm tra khi đổi tỉ lệ cửa sổ.

## 6. Cảm ứng, bàn phím và khả năng tiếp cận

- Mục tiêu tối thiểu cho vùng chạm tương tác chính: **khoảng 44 × 44 CSS px trở lên** nếu phù hợp bố cục; nút quan trọng nên lớn hơn.
- Các nút cạnh nhau có khoảng trống tránh bấm nhầm; **Chụp** và **Hủy** không nên nằm sát nhau.
- Có trạng thái hover, focus, pressed, disabled; không dùng hover làm cách duy nhất hiện một chức năng.
- Mọi nút cần nhãn hoặc accessible name; ảnh preview/trang trí có `alt` phù hợp.
- Điều hướng bàn phím và focus phải hợp lý cho laptop: Enter/Space kích hoạt nút, Tab di chuyển qua phần tử tương tác.
- Khi mở dialog, focus nằm trong dialog, đóng xong trả về vị trí hợp lý nếu framework hỗ trợ.
- Không dựa hoàn toàn vào màu sắc: lỗi/thành công cần có text hoặc icon phù hợp.

### 6.1 Kiosk là màn cảm ứng (đã chốt 19/11/2026)

Chế độ khách được thiết kế cho **màn cảm ứng 100%**; chuột/bàn phím chỉ là phương án phụ.

- WebView2 tắt **chụm hai ngón phóng to cả trang** (`IsPinchZoomEnabled = false`) và **vuốt ngang để quay lại trang** (`IsSwipeNavigationEnabled = false`). Cử chỉ chụm chỉ có tác dụng trong vùng tự xử lý (`touch-action: none`), như trình chỉnh vùng cắt.
- **Manual Crop:** một ngón kéo ảnh; **hai ngón chụm/mở để thu nhỏ/phóng to**, điểm ảnh dưới hai ngón tay đứng yên; nhấc một ngón thì tiếp tục kéo bằng ngón còn lại mà ảnh không nhảy. Có thêm nút **−/+** (56 px, bước 10%) và thanh trượt có nút kéo 36 px (vùng chạm 48 px). Con lăn chuột vẫn dùng được trên laptop. Ngón "kẹt" (mất sự kiện nhấc tay) được tự bỏ khi chạm lại. Trong phiên khách có thể chụp lại, hộp chỉnh ảnh có nút **Chụp lại ảnh X** tách biệt cạnh nhóm **Hủy / Áp dụng** để bỏ bản nháp chưa áp dụng, đóng hộp và mở camera preview chụp lại đúng ảnh nguồn của ô đang chỉnh; nút này không hiển thị khi quản trị viên chỉnh sửa lại phiên đã lưu. Với ô `fit = contain` (hiển thị trọn ảnh), chạm vào ô hoặc chip vẫn mở hộp xem trọn ảnh đúng tỉ lệ (kèm giải thích ngắn, nút **Đóng** và nút **Chụp lại ảnh X** trong phiên khách), nhưng ẩn điều khiển kéo/phóng to/khôi phục/Áp dụng vì không hỗ trợ chỉnh vùng cắt.
- **Ô ảnh chạm được:** trên thiết bị không có rê chuột (`@media (hover: none)`), mỗi ô ảnh trong khung thành phẩm luôn có viền nét đứt mảnh và sáng lên khi chạm. Viền hover chỉ áp dụng cho chuột, tránh viền "dính" sau khi chạm. Danh sách chip "Các ô ảnh" vẫn là cách thay thế cho ô nhỏ.
- **Không mở hộp thoại Windows trong Kiosk:** màn thành phẩm của khách không có nút "Đổi thư mục" lưu ảnh, vì hộp thoại chọn thư mục cho phép duyệt/đổi tên/xóa file ở mọi nơi, kể cả ảnh của khách khác. Thư mục xuất do quản trị viên đổi trong Admin.
- Kiosk không có ô nhập chữ (không cần bàn phím ảo).
- Ngoài phạm vi ứng dụng: cử chỉ vuốt từ mép màn hình của Windows (Action Center, chuyển ứng dụng) phải tắt bằng cấu hình kiosk của Windows (Assigned Access/Group Policy) khi triển khai thật.
- Countdown/tiến trình không gây chớp nháy mạnh; tôn trọng `prefers-reduced-motion` nếu có animation.

## 7. Hệ thống thiết kế (design tokens)

**Chưa chốt thương hiệu.** Dưới đây là hệ thống token **tham khảo**, không phải chỉ định màu bắt buộc:

```css
:root {
  --color-bg: #F8FAFC;
  --color-surface: #FFFFFF;
  --color-text: #0F172A;
  --color-text-muted: #475569;
  --color-primary: #2563EB;
  --color-primary-contrast: #FFFFFF;
  --color-success: #15803D;
  --color-warning: #B45309;
  --color-danger: #B91C1C;
  --color-border: #CBD5E1;

  --radius-sm: 8px;
  --radius-md: 12px;
  --radius-lg: 18px;

  --space-1: 4px;
  --space-2: 8px;
  --space-3: 12px;
  --space-4: 16px;
  --space-6: 24px;
  --space-8: 32px;
}
```

- Khi người dùng chọn bộ nhận diện, **đổi token** thay vì thay từng giá trị CSS rải rác.
- Typography dùng font có sẵn/bundled, hỗ trợ tiếng Việt; tránh phụ thuộc Google Fonts/CDN để kiosk chạy offline.
- Duy trì phân cấp chữ rõ: tiêu đề màn hình → mô tả ngắn → trạng thái → nhãn nút.
- Không nhồi quá nhiều biểu tượng và animation; ảnh thành phẩm phải là trọng tâm của giao diện.
- Tương phản chữ/nền phải đủ đọc trên màn hình laptop và kiosk ở nơi sáng.

## 8. Thành phần UI dùng lại

Chỉ xây dựng các component khi có từ **hai nơi trở lên** cần hành vi hoặc hình thức giống nhau, hoặc component phức tạp cần test độc lập. Các thành phần có thể cân nhắc:

- `PrimaryButton`, `SecondaryButton`, `DangerButton`: hệ thống nút nhất quán.
- `AppHeader`, `StepIndicator`: ngữ cảnh vị trí trong quy trình.
- `CameraPreview`, `CountdownOverlay`, `CaptureProgress`: chỉ phục vụ phần camera.
- `TemplateCard`, `PhotoThumbnail`, `PhotoStripPreview`: tái sử dụng trong nhiều màn hình.
- `LoadingState`, `EmptyState`, `ErrorState`, `ConfirmDialog`: phản hồi thống nhất.
- `ExportOptions`, `ExportStatus`: chọn định dạng và hiển thị kết quả.

**Không yêu cầu tạo trước toàn bộ các component** khi chưa có màn hình sử dụng. Tránh xây design system phức tạp trước khi làm xong luồng webcam.

## 9. Quy chuẩn trạng thái và thông báo

| Tình huống | Thông điệp mẫu (tiếng Việt) | Hành động |
|---|---|---|
| Đang tìm webcam | “Đang kết nối máy ảnh…” | Hiển thị loading ngắn, không giả thành công |
| Không tìm thấy webcam | “Không tìm thấy máy ảnh trên thiết bị này.” | “Thử lại”, “Cài đặt” |
| Thiếu quyền camera | “Ứng dụng chưa được cấp quyền sử dụng máy ảnh.” | “Kiểm tra quyền camera” |
| Camera bị ứng dụng khác dùng | “Máy ảnh đang được ứng dụng khác sử dụng.” | “Thử lại” |
| Đang đếm ngược | “Chuẩn bị chụp ảnh 2/4” | Số đếm lớn và vị trí hiện tại |
| Chụp ảnh thất bại | “Chưa chụp được ảnh. Vui lòng thử lại.” | “Chụp lại” |
| Đang ghép ảnh | “Đang tạo ảnh của bạn…” | Thông báo đang xử lý |
| Ghép ảnh thất bại | “Không thể ghép ảnh lúc này.” | “Thử lại” / “Quay lại” |
| Xuất file thành công | “Đã lưu ảnh thành công.” | “Mở thư mục”, “Phiên mới” |
| Xuất file thất bại | “Không thể lưu ảnh vào vị trí đã chọn.” | “Chọn vị trí khác”, “Thử lại” |

**Quy tắc chung:** Không hiển thị nguyên chuỗi exception, stack trace hoặc đường dẫn hệ thống nhạy cảm trên màn hình khách. Chỉ thông báo “Thành công” sau khi service xác nhận thao tác thực tế đã hoàn tất.

## 10. Loading, phản hồi và chống thao tác trùng

- Hiển thị trạng thái ngay sau khi người dùng nhấn nút bắt đầu thao tác; dùng spinner/progress ngắn gọn khi cần.
- Vô hiệu hóa hành động đang chạy (nhấn Chụp, Ghép, Xuất) hoặc dùng cơ chế chống gửi trùng trong Application Service.
- Không chặn toàn bộ app trong lúc ghép ảnh/PDF; có thể hiện lớp phủ đang xử lý, nhưng UI phải tiếp tục phản hồi.
- Không mô phỏng thanh tiến trình % nếu backend không cung cấp tiến độ thật; dùng thông điệp “Đang xử lý…” trung thực.
- Khi lỗi, không xóa màn hình preview/ảnh gốc đã có nếu không cần thiết.
- Không dùng notification biến mất quá nhanh cho lỗi quan trọng khiến khách không kịp đọc.

## 11. Dữ liệu riêng tư và tài nguyên offline

- Chỉ hiển thị preview webcam trong quá trình sử dụng tính năng có liên quan; không tự bật camera khi chỉ mở ứng dụng.
- Không tự đưa ảnh đã chụp lên website/dịch vụ bên thứ ba.
- Không tạo gallery công khai có thể truy cập từ thiết bị khác khi chưa có yêu cầu.
- Khi người dùng chọn mở thư mục, chỉ mở đúng đường dẫn hợp lệ của phiên/tệp đang thao tác.
- Không lưu ảnh trong `wwwroot` nếu có nguy cơ nội dung cá nhân bị coi là tài nguyên UI công khai; nội dung ảnh thuộc vùng dữ liệu ứng dụng.
- Asset của UI, icon, ảnh nền/template mẫu phải được đóng gói nội bộ và hoạt động offline.

## 12. Kiểm thử giao diện trước khi nghiệm thu

### 12.1 Checklist chung

- [ ] Chữ tiếng Việt đầy đủ dấu, không lỗi font.
- [ ] Có trạng thái hover/focus/disabled/loading và lỗi cần thiết.
- [ ] Điều hướng được bằng chuột và bàn phím cơ bản.
- [ ] Không cắt mất nút chính trên màn hình laptop mục tiêu.
- [ ] Không dùng quá nhiều click để hoàn thành một phiên thông thường.
- [ ] Ảnh/video giữ đúng tỉ lệ, không bị kéo méo hoặc cắt nhầm.
- [ ] Không dựa vào Internet để hiển thị phần chính.
- [ ] Không có thông báo thành công trước khi lưu file xong.
- [ ] Nút Chụp/Ghép/Xuất không tạo tác vụ trùng khi bấm nhanh.
- [ ] Khi camera lỗi, người dùng không bị kẹt ở màn hình đen.

### 12.2 Các kích thước cần kiểm tra

- [ ] 1280×720.
- [ ] 1366×768.
- [ ] 1920×1080 hoặc độ phân giải laptop hiện có tương đương.
- [ ] Resize cửa sổ khi không ở fullscreen.
- [ ] Sau này: kiểm tra kích thước và hướng màn hình cảm ứng thật.

### 12.3 Kịch bản bắt buộc cho Camera UI

1. Khởi động ứng dụng khi camera chưa có quyền → thông báo và hướng khắc phục.
2. Cấp quyền và mở preview → không để video bị méo tỉ lệ.
3. Bắt đầu chụp nhiều ảnh → đếm ngược, tiến trình và số ảnh chính xác.
4. Bấm Chụp liên tục → chỉ một tác vụ được thực hiện.
5. Ngắt camera giữa phiên → hiển thị lỗi; giữ ảnh đã lưu.
6. Quay về màn hình khác → stream được giải phóng; mở lại khi cần.
7. Đổi kích thước cửa sổ → thao tác chính vẫn dễ sử dụng.

## 13. Những điều chưa chốt

- Màu sắc, font thương hiệu, logo, phong cách ảnh minh họa và mức độ animation.
- Layout portrait hay landscape khi chuyển sang kiosk phần cứng.
- Luồng chụp lại từng ảnh/toàn bộ phiên và vị trí nút tương ứng.
- Có sử dụng QR và in ảnh vật lý ở phiên bản tiếp theo hay không.
- Cơ chế truy cập Admin và khả năng bảo vệ bằng PIN.
- Timeout khi khách không thao tác và việc tự quay lại màn hình chào.

Đối với task không phụ thuộc các mục trên, AI phải tiếp tục triển khai theo quy tắc đã rõ; với task phụ thuộc, trình bày lựa chọn cụ thể trước khi khóa hành vi hoặc thiết kế.

## 14. Liên kết tài liệu

- `AGENTS.md`: quy tắc AI và ngôn ngữ làm việc.
- `docs/PROJECT_OVERVIEW.md`: phạm vi, màn hình và mục tiêu V1.
- `docs/ARCHITECTURE.md`: Blazor Hybrid, lifecycle và ranh giới UI/Service.
- `docs/BUSINESS_RULES.md`: trạng thái phiên, chụp/ghép/xuất và lỗi.
- `docs/CODING_STANDARDS.md`: cách viết Razor/CSS/JS, DI, test và bảo mật.
