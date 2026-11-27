# Quy tắc nghiệp vụ — Photobooth Mini V1

> **Ngôn ngữ:** Toàn bộ mô tả nghiệp vụ, thông báo và báo cáo của AI phải bằng **tiếng Việt**.
>
> **Phạm vi:** Phần mềm Windows sử dụng webcam laptop, chụp nhiều ảnh, ghép theo template, xem lại, lưu và xuất JPEG/PNG/PDF **offline**.
>
> **Nguyên tắc:** Tài liệu xác định hành vi mong muốn, **không mặc định tính năng đã được code**. Những lựa chọn chưa chốt được đánh dấu `CHƯA CHỐT`; AI không tự lựa chọn thay người dùng.

## 1. Mục tiêu và ranh giới

### 1.1 Phải đáp ứng trong V1 hoàn chỉnh

- Khách sử dụng luồng **Chào mừng → Chọn template → Camera preview → Chụp ảnh → Xem lại → Ghép ảnh → Xuất file → Hoàn tất**.
- Sử dụng webcam tích hợp của laptop; không cần camera ngoài, máy in thật hoặc Internet.
- Máy ảnh Canon EOS qua Canon EDSDK là nguồn camera **tùy chọn** (bổ sung 26/11/2026, đang kiểm chứng trên EOS M50 Mark II); không được làm luồng chính phụ thuộc vào nó.
- **Số ảnh mỗi phiên do template khách chọn quyết định** (xem mục 6.1), không có giá trị mặc định cố định. Đếm ngược mặc định **3 giây trước mỗi lần chụp**, có thể cấu hình, độc lập với số ảnh.
- Quản lý phiên (`SessionId`), thứ tự ảnh, lưu ảnh gốc và ảnh thành phẩm độc lập.
- Cho xuất **JPEG, PNG và PDF**; dữ liệu được lưu cục bộ, không tải lên mạng.
- Có chế độ **Kiosk** cho khách và **Admin** để quản lý sự kiện, thư viện ảnh thành phẩm, chỉnh sửa lại, in lại và máy in (mục 9).
- Mỗi sự kiện cho thuê lưu toàn bộ dữ liệu trong **một Event Workspace** riêng do người vận hành chọn (mục 7, `docs/EVENT_STORAGE.md`).

### 1.2 Không thuộc V1 hiện tại

- In tự động trên máy in vật lý, đọc trạng thái giấy/mực, SDK máy in, SDK máy ảnh Sony.
- QR nhận ảnh qua Internet, cloud, tài khoản khách, thanh toán và đồng bộ máy chủ.
- Màn hình cảm ứng rời, khung cơ khí và cấp nguồn riêng.
- Không coi adapter mock tương đương kết quả tích hợp phần cứng thật.

## 2. Thuật ngữ và đối tượng nghiệp vụ

| Thuật ngữ | Ý nghĩa |
|---|---|
| `PhotoSession` | Một lượt khách chụp ảnh, có `SessionId`, trạng thái, cấu hình và danh sách ảnh |
| `Capture` | Một lần chụp từ nguồn camera, tạo ảnh gốc và metadata tương ứng |
| `OriginalPhoto` | File ảnh gốc đã lưu, **không chỉnh sửa và không bị ghép đè** |
| `SelectedPhoto` | Ảnh được chọn cho một vị trí trong phiên, có thể thay đổi khi chụp lại |
| `PhotoTemplate` | Định nghĩa bố cục canvas, vùng đặt ảnh, khung, chữ và phần trang trí |
| `ComposedPhoto` | Ảnh được ghép từ các ảnh đã chọn theo template |
| `ExportFile` | File JPEG/PNG/PDF thành phẩm mà người dùng lưu hoặc xuất |
| `CameraSource` | Nguồn camera hiện được cấu hình, ban đầu là webcam laptop |

- `SessionId` và `PhotoId` là mã định danh nội bộ; không dựa vào tên file hiển thị để xác định duy nhất phiên/ảnh.
- Chỉ ảnh **đã lưu thành công** mới được tính vào danh sách ảnh có thể ghép.
- Không làm mất ảnh gốc do lỗi ở bước ghép hoặc xuất file.

## 3. Quy tắc cấu hình

| Thiết lập | Giá trị mặc định V1 | Quy tắc |
|---|---|---|
| Số ảnh mỗi phiên | Theo template | Tính từ `photoIndex` của template (mục 6.1); người dùng không nhập tay |
| Giới hạn an toàn số ảnh | 10 (`capture.maxPhotosPerSession`) | Cấu hình được trong 1..20 để tránh phiên quá dài/tốn tài nguyên; template vượt giới hạn được hiển thị nhưng không chọn được, kèm lý do. Đây là giới hạn tài nguyên, **không** phải số ảnh mặc định |
| Thời gian đếm ngược | 3 giây | Số nguyên dương, giới hạn min/max cụ thể `CHƯA CHỐT` |
| Template | Các template mẫu đi kèm | Phải hợp lệ; số ảnh cần chụp lấy từ chính template |
| Camera | Camera mặc định của Windows | Chọn webcam cho Kiosk trong Admin › Camera (lưu SQLite bảng `DeviceSettings`). Camera đã chọn không còn → Kiosk báo lỗi, **không** tự chuyển sang camera khác. Máy ảnh Canon (EDSDK) hiện chỉ được liệt kê, chưa chọn được cho Kiosk |
| Khổ xuất ban đầu | 4 × 6 inch | Có thể cấu hình qua template/export preset |
| Kích thước raster tham khảo | 1200 × 1800 px | Tương ứng 300 DPI cho 4 × 6 inch; không hardcode vào mọi mẫu |
| Thư mục lưu | Event Workspace do người vận hành chọn | Bắt buộc trước khi phục vụ khách; kiểm tra tồn tại, quyền ghi, dung lượng (≥ 200 MB); không hardcode |
| Số bản in | 1 bản, tối đa 10 (`print.maxCopies`, 1..50) | Không phụ thuộc số ảnh nguồn của template |
| Máy in | Chưa cấu hình | Chọn trong Admin: chưa dùng / máy in giả lập / máy in Windows |
| Chất lượng JPEG | `CHƯA CHỐT` | Chỉ lưu khi giá trị hợp lệ; không giả định 100% |
| Chụp lại | Có trong đích V1 | Cho phép chụp lại **từng ảnh hoặc toàn bộ phiên** (đã chốt 19/11/2026); giới hạn số lần `CHƯA CHỐT` (hiện không giới hạn) |
| Tự động xóa ảnh | Không triển khai ở giai đoạn đầu | Thời hạn lưu/xóa `CHƯA CHỐT` |

AI **không tự đặt** giới hạn số lần chụp lại, timeout quay về trang chào, số bản in hoặc hạn mức lưu trữ nếu người dùng chưa duyệt yêu cầu.

## 4. Luồng phiên chụp

### 4.1 Bắt đầu

1. Mở ứng dụng: hiển thị màn hình chào.
2. Khách chọn **Bắt đầu**: tạo một `PhotoSession` mới hoặc tạo ở thời điểm được định nghĩa thống nhất trong code; không tạo trùng session vì người dùng bấm liên tục.
3. Chọn một template hợp lệ; phiên lưu **snapshot template và số ảnh cần chụp** tại thời điểm chọn, để không bị ảnh hưởng nếu file mẫu bị sửa sau đó (BR-T04).
4. Vào preview và yêu cầu quyền camera nếu chưa có.
5. Camera sẵn sàng thì hiển thị hình trực tiếp; chưa sẵn sàng thì hiển thị trạng thái và hành động khắc phục.

### 4.2 Chụp ảnh

1. Khách nhấn **Chụp ảnh**.
2. Với mỗi ảnh cần chụp, hiển thị đếm ngược theo cấu hình; phải dễ nhận biết ảnh thứ mấy trong tổng số **theo template** (BR-T07).
   - **Khung hướng dẫn (Camera Framing Overlay, đã chốt 19/11/2026):** preview luôn hiện **toàn bộ** khung hình camera (không cắt, không méo); một khung nét đứt "Vùng ảnh thành phẩm" cho biết phần sẽ nằm trong ô ảnh sau Auto Crop, phần ngoài được làm tối nhưng vẫn nhìn thấy. Khung tính theo ô của ảnh đang chụp (ảnh dùng ở nhiều ô → ô lớn nhất, bằng nhau thì ô khai báo trước), theo đúng `fit`/`focalPoint` và chế độ gương như Photo Composer. Ô `contain`: báo toàn bộ khung hình được giữ. Khung chỉ để hướng dẫn: không cắt ảnh gốc, không có trong ảnh chụp, thành phẩm hay file xuất; Manual Crop sau khi chụp vẫn đổi được vùng ảnh.
3. Khi hết đếm ngược, lấy frame chụp từ camera; không sử dụng ảnh thumbnail ở độ phân giải thấp như ảnh gốc nếu API cho phép lấy chất lượng cao hơn.
4. Gửi dữ liệu ảnh sang service C# và **ghi ảnh gốc hoàn chỉnh** trước khi xác nhận lần chụp thành công.
5. Cập nhật danh sách ảnh, thứ tự và tiến trình. Lặp lại đến số ảnh được yêu cầu.
6. Một lần chụp lỗi **không được tự tính là ảnh thành công** hoặc làm tăng bộ đếm ảnh đã lưu; cho phép thử lại theo giao diện.
7. Khi đã đủ ảnh hợp lệ, chuyển sang bước xem lại.

**Chống thao tác trùng:** Không để hai lệnh chụp đồng thời, không tạo hai ảnh vì người dùng bấm nút liên tục hoặc vì callback xử lý chậm.

### 4.3 Xem lại và chụp lại

- Khách nhìn thấy các ảnh đã chụp theo **đúng thứ tự** và khả năng chụp lại nếu chính sách đã được chốt.
- Nếu chụp lại, file ảnh mới **không ghi đè ảnh gốc cũ**; kết quả ghép chỉ dùng ảnh đang được chọn cho vị trí đó.
- Nếu chụp lại thất bại, vẫn giữ ảnh đang được chọn trước đó và đưa ra phương án thử lại/quay lại.
- Chụp lại từng ảnh hoặc toàn bộ phiên: **đã chốt** (19/11/2026). Mọi ảnh gốc (kể cả ảnh bị thay) được giữ trên ổ đĩa và trong lịch sử phiên. Giới hạn số lần chụp lại: **CHƯA CHỐT**.
- Trước khi chụp ảnh đầu tiên, khách được đổi template; số ảnh cần chụp cập nhật theo template mới. Giả định Giai đoạn 2 (chưa phải quyết định chính thức): không cho đổi template sau khi đã có ảnh trong phiên — muốn đổi thì hủy phiên và bắt đầu phiên mới.
- Khi có thay đổi ảnh được chọn, bản ghép trước đó (nếu có) phải được đánh dấu cần tạo lại, không sử dụng nhầm kết quả cũ.

### 4.4 Ghép ảnh

- Chỉ ghép khi đủ ảnh đang được chọn và template hợp lệ.
- Giữ nguyên thứ tự vị trí `photoSlots`; không dựa vào thứ tự file trong thư mục.
- Scale/crop giữ đúng tỉ lệ; mặc định dùng Auto Crop theo `focalPoint` của template (center-crop khi không khai báo) hoặc chỉnh vùng cắt khách đã xác nhận (mục 4.4.1), không được bóp méo ảnh.
- Quá trình ghép tạo **file mới** độc lập ảnh gốc; thất bại không được phá hủy ảnh đã lưu.
- Hiển thị trạng thái đang xử lý, tránh thao tác ghép trùng.
- Nếu không ghép được, thông báo lý do có thể hiểu và cho phép thực hiện lại.
- Chụp đủ ảnh theo template thì **tự động ghép** và chuyển sang màn xem thành phẩm (Photo Preview); ghép lỗi thì ở lại màn xem lại với thông báo và nút thử lại.

### 4.4.1 Photo Preview và Manual Crop (đã chốt 19/11/2026)

- Ảnh xem trước là bản thu nhỏ của **chính ảnh thành phẩm dùng để xuất** (cùng template, ảnh nguồn, bộ lọc, chỉnh vùng cắt); không có cách dựng ảnh thứ hai cho preview. Preview giữ đúng tỉ lệ canvas.
- Khách chọn một **ô ảnh** (chạm trực tiếp trên preview hoặc chọn trong danh sách ô) để chỉnh. Ô được xác định theo thứ tự ô trong template, không nhầm với `photoIndex`; hình trang trí/overlay không chặn việc chọn ô.
- Chỉnh vùng cắt gồm **kéo ảnh** và **phóng to 100–300%** (100% = vùng cắt mặc định của template). Ảnh luôn phủ kín ô, không méo, không xoay.
- Chỉ ô `fit = cover` được chỉnh vùng cắt; ô `contain` (hiển thị trọn ảnh) vẫn mở được hộp ảnh để xem trọn ảnh và chụp lại (trong phiên khách), đồng thời giải thích rõ chưa hỗ trợ kéo/phóng to vùng cắt.
- **Áp dụng** mới lưu chỉnh sửa vào phiên và tạo lại thành phẩm; **Hủy**/đóng thì bỏ thay đổi dang dở. **Khôi phục Auto Crop** đưa một ô về mặc định; **Khôi phục chỉnh sửa** (có xác nhận) đưa mọi ô về mặc định.
- Chỉnh sửa là **dữ liệu của phiên**, gắn với từng ô (hai ô dùng chung ảnh có thể cắt khác nhau); **không** sửa template JSON, không sửa ảnh gốc.
- Chụp lại một ảnh thì các ô dùng ảnh đó quay về Auto Crop (vùng cắt cũ được chỉnh cho ảnh cũ); các ô khác giữ nguyên.
- Chỉnh vùng cắt tính theo hướng ảnh khách nhìn thấy (sau khi lật gương), không lật hai lần, không lật chữ/logo.
- File JPEG/PNG/PDF xuất ra phải giống preview, kể cả vùng cắt đã chỉnh.
- Ảnh thành phẩm được **xác nhận** (Hoàn tất / Lưu ảnh / In) thành phiên bản `final-v{N}` không bao giờ bị ghi đè. Xuất/in dùng đúng file phiên bản; chỉnh sửa lại luôn tạo phiên bản mới.

### 4.4.2 Chỉnh sửa lại phiên đã lưu (Admin, đã chốt 19/11/2026)

- Quản trị viên mở phiên cũ từ thư viện ảnh của sự kiện; hệ thống khôi phục ảnh nguồn, template (snapshot), mirror, Manual Crop và bộ lọc của phiên bản được chọn rồi ghép lại bằng chính Photo Composer.
- **Lưu phiên bản mới** tạo phiên bản tiếp theo; bản cũ vẫn xem/xuất/in lại được. **Hủy** không tạo phiên bản và không đổi dữ liệu đã xác nhận.
- Không crop trên ảnh đã ghép; không ghi chỉnh sửa vào template JSON; không sửa ảnh gốc.
- Thiếu ảnh gốc hoặc snapshot template: vẫn xem/xuất/in lại file đã lưu, khóa chỉnh sửa và nêu lý do; không tạo dữ liệu giả.
- Phiên mở lại trong Admin không chụp lại được (không dùng camera).

### 4.5 Xuất ảnh

1. Khách xem ảnh thành phẩm đã ghép.
2. Chọn một hoặc nhiều định dạng xuất có hỗ trợ: JPEG, PNG, PDF.
3. Xác thực định dạng, thư mục, tên file và quyền ghi.
4. Xuất ra file mới; chỉ đánh dấu thành công **khi file đã được ghi hoàn chỉnh và có thể truy cập**.
5. Hiển thị đường dẫn/vị trí lưu và thao tác **Mở thư mục** khi phù hợp.
6. Khi một định dạng lỗi, không được xóa ảnh gốc và không được báo các định dạng khác thất bại nếu chúng đã thành công.
7. Không âm thầm ghi đè file có sẵn; xử lý trùng tên bằng cơ chế nhất quán và kiểm tra an toàn.

**Thông số ban đầu:** PDF khổ 4×6 inch có kích thước trang **288×432 point**. Ảnh raster tham khảo 1200×1800 pixel; đây là hai đơn vị khác nhau. Phải test chiều xoay, tỉ lệ, vùng an toàn và kết quả in thử bằng PDF nếu có công cụ phù hợp.

### 4.5.1 In ảnh, in lại (đã chốt 19/11/2026)

- Khách chọn số bản (nút −/+, mặc định 1, tối đa theo cấu hình) và **xác nhận** trước khi gửi lệnh in. Một ảnh thành phẩm → một Print Job N bản.
- Mỗi phiên có thể có nhiều Print Job; mỗi Print Job ghi rõ **phiên bản thành phẩm** được in và được lưu vào database (SQLite) **trước khi** gửi tới máy in.
- Trạng thái: `Pending` (chưa rõ), `Submitted` (Windows đã tiếp nhận — chưa phải đã in xong), `Simulated` (máy in giả lập, **không có bản in thật**), `Completed` (chỉ khi quản trị viên xác nhận đã in xong), `Failed`, `Cancelled`.
- Không bao giờ tự đánh dấu "đã in xong"; không tự gửi lại. Phiên bản có lệnh in thất bại hoặc chưa rõ trạng thái phải được xác nhận lại trước khi in tiếp. Yêu cầu in giống hệt gửi liên tiếp (bấm nhiều lần) chỉ tạo một Print Job.
- Chưa cấu hình máy in: Kiosk báo "chưa thể in", không hiển thị kết quả giả.
- In lỗi không làm mất ảnh gốc, thành phẩm hay lịch sử.

### 4.6 Kết thúc

- **Hoàn tất** lưu ảnh thành phẩm đang xem thành một phiên bản trong Event Workspace (không bắt buộc xuất file ra ngoài) rồi kết thúc phiên.
- Sau khi hoàn tất, session có trạng thái kết thúc phù hợp; không tự xóa ảnh của phiên.
- Bấm **Phiên mới** tạo session khác, không sử dụng lại danh sách ảnh cũ.
- Hủy/thoát ứng dụng phải dừng camera và lưu trạng thái/file đã hoàn tất ở mức có thể phục hồi.

### 4.7 Chia sẻ ảnh qua QR — Offline-First (Phase Cloud 2.1, đã chốt 23/11/2026)

- **BR-QR01 — QR không phụ thuộc Internet.** Khi khách bấm "Hoàn tất" và Cloud đã cấu hình, Desktop sinh Share Token và URL ngay trên máy, ghi vào SQLite (`OutputVersions`), rồi hiển thị QR. Upload lên Cloud chạy nền sau đó; kết quả upload không bao giờ làm ẩn hoặc đổi QR.
- **BR-QR02 — Token.**
  - Sinh bằng `RandomNumberGenerator`, 192 bit, Base64URL 32 ký tự.
  - Không dùng số thứ tự, đường dẫn file hay thông tin khách.
  - Mỗi **phiên bản thành phẩm** có một token riêng.
  - In lại, mở lại phiên hay upload lại đều dùng đúng token cũ.
- **BR-QR03 — Phiên bản mới.** Chỉnh sửa lại tạo `final-v{N+1}` **không có QR**. Quản trị viên bấm "Tạo mã QR chia sẻ" để cấp token mới cho phiên bản đó. Token và ảnh của phiên bản đã chia sẻ không bao giờ bị ghi đè; Cloud trả `409` nếu cùng token mà khác ảnh.
- **BR-QR04 — Lưu bền trước khi hiện QR.** Không ghi được token vào SQLite thì không hiển thị QR (màn Hoàn thành báo "Chưa tạo được mã QR…"); phiên vẫn hoàn tất bình thường.
- **BR-QR05 — Chỉ upload ảnh thành phẩm**, không upload ảnh gốc. Không xóa ảnh local sau khi upload.
- **BR-QR06 — Thử lại.**
  - Lỗi tạm thời: tự thử lại với khoảng nghỉ tăng dần, tối đa 15 phút giữa hai lần.
  - Khởi động lại ứng dụng: upload tiếp các ảnh chưa xong ở mọi sự kiện đã biết.
  - Mất file local hoặc Cloud từ chối: đánh dấu lỗi, không thử lại vô hạn.
- **BR-QR07 — Cấu hình.** Mục `cloud` trong `appsettings.json` của Desktop gồm `enabled`, `baseUrl`, `uploadTimeoutSeconds`, `apiKey`; không hardcode địa chỉ. Tắt hoặc sai thì không tạo QR, không ảnh hưởng chụp/lưu/in, và Admin → "Chia sẻ QR" báo rõ lý do.
- **BR-QR08 — Trang của khách** (`/s/{token}`): chưa có ảnh thì hiện "Ảnh của bạn đang được chuẩn bị." kèm tự làm mới, không cache. Có ảnh thì hiện ảnh và nút "Tải ảnh xuống". Token sai định dạng trả 404. Không cần đăng nhập, không liệt kê ảnh.

## 5. Mô hình trạng thái đề xuất

Các trạng thái sau là **mô hình mục tiêu**, tên enum thực tế có thể điều chỉnh theo code hiện hành.

| Trạng thái | Ý nghĩa | Chuyển hợp lệ thông thường |
|---|---|---|
| `Created` | Phiên vừa tạo | `Previewing`, `Cancelled`, `Failed` |
| `Previewing` | Đang chuẩn bị/hiển thị camera | `Capturing`, `Cancelled`, `Failed` |
| `Capturing` | Đang thực hiện các lần chụp | `Reviewing`, `Cancelled`, `Failed` |
| `Reviewing` | Xem ảnh và quyết định bước tiếp | `Capturing` (chụp lại), `Composing`, `Cancelled`, `Failed` |
| `Composing` | Đang ghép ảnh | `Ready`, `Reviewing` (nếu lỗi có thể thử lại), `Failed` |
| `Ready` | Thành phẩm có thể xem/xuất | `Composing` (nếu đổi ảnh/template), `Completed`, `Cancelled` |
| `Completed` | Phiên đã hoàn tất | Không chuyển sang phiên khác bằng cách sửa `SessionId` |
| `Cancelled` | Khách hủy phiên | Trạng thái kết thúc; dữ liệu đã ghi được giữ theo chính sách |
| `Failed` | Lỗi không thể phục hồi trong luồng hiện tại | Hướng phục hồi/tiếp tục **cần thiết kế rõ**, không tự xóa dữ liệu |

**Chú ý:** Việc xuất một file là tác vụ có trạng thái riêng (`Pending`, `Processing`, `Succeeded`, `Failed` là ví dụ), không nhất thiết ép mỗi lần xuất file thành một trạng thái của toàn session. Không thiết kế flow phức tạp hơn nếu chưa cần.

### 5.1 Bất biến nghiệp vụ

- `SessionId` duy nhất và không đổi trong một phiên.
- `Completed` chỉ khi các điều kiện hoàn tất đã quy định được đáp ứng; không tự đồng nhất với "đã bấm Xuất" nếu thao tác xuất thất bại.
- Số ảnh hợp lệ tính theo ảnh được lưu và được chọn, không theo số lần camera phát sự kiện.
- Thành phẩm ghép phải có thể truy nguyên template và những ảnh được chọn đã dùng để tạo.
- Chỉ một thao tác chụp đang hoạt động trên camera trong V1.
- Lỗi PDF/ghi file không xóa ảnh gốc hoặc làm biến mất những file xuất trước đó.

## 6. Quy tắc template

- Template có `TemplateId`, tên hiển thị, kích thước canvas và danh sách `photoSlots`.
- Mỗi `photoSlot` có vị trí, kích thước hợp lệ, quy tắc crop/fit và thứ tự lấy ảnh.
- Vùng ảnh không nên vượt khỏi canvas; nếu vượt (chủ ý thiết kế tràn mép) thì phần thừa bị cắt và validation chỉ **cảnh báo**, không chặn (xem `docs/TEMPLATE_SCHEMA.md`).
- Số ảnh cần chụp được quyết định bởi template theo các quy tắc ở mục 6.1; template không hợp lệ thì **không cho bắt đầu chụp**.
- Template đi kèm ứng dụng phải có bản mẫu/preview; không phụ thuộc Internet để tải ảnh nền.
- Template có chữ/ngày giờ/logo/overlay/hình; quản trị viên tạo và sửa bằng Template Manager + Visual Template Editor (mục 6.2).
- Thay đổi template sau khi đã chụp: hành vi giữ/chụp lại/ghép mới **CHƯA CHỐT**; không tự quyết định ngầm.

### 6.1 Số lượng ảnh theo template (đã chốt 19/11/2026)

**BR-T01 — Số lượng ảnh phụ thuộc vào template**

- Mỗi template quy định các ảnh cần dùng thông qua danh sách Photo Slots (`slots`).
- Số ảnh cần chụp được xác định từ các `photoIndex` trong template.
- Không mặc định mọi phiên đều cần 4 ảnh; không yêu cầu người dùng nhập thủ công số ảnh.

**BR-T02 — Một ảnh có thể dùng nhiều lần**

- Một `photoIndex` có thể xuất hiện ở nhiều slot; các slot đó dùng chung một ảnh nguồn.
- Không yêu cầu chụp thêm ảnh chỉ vì ảnh được dùng ở nhiều vị trí.

**BR-T03 — Quy tắc `photoIndex`**

- Bắt đầu từ 0, là số nguyên không âm.
- Các chỉ số ảnh nguồn phải **liên tục từ 0 đến N-1**, không bỏ trống chỉ số trung gian; cho phép nhiều slot cùng chỉ số.
- Số ảnh cần chụp `N = max(photoIndex) + 1` sau khi validation hợp lệ — **không** dùng số lượng slot.
- Template không có Photo Slot hợp lệ thì không thể bắt đầu phiên chụp.
- Ví dụ: `[0, 1, 2]` → hợp lệ, 3 ảnh; `[0, 0, 1]` → hợp lệ, 2 ảnh; `[0, 2]` → **không hợp lệ** vì thiếu 1.

**BR-T04 — Phiên chụp tuân thủ template đã chọn**

- Khách chọn template trước khi chụp; khi chọn, phiên xác định và lưu số ảnh cần chụp cùng snapshot template.
- Không thay đổi số ảnh giữa phiên vì file template nguồn bị chỉnh sửa.
- Đổi sang template có cấu trúc khác phải qua luồng đổi/hủy phiên rõ ràng (mục 4.3), không âm thầm thay đổi phiên đang chụp.

**BR-T05 — Hoàn thành phiên chụp**

- Chỉ chuyển sang xem lại/ghép khi có đủ **tất cả** ảnh nguồn template yêu cầu; không kiểm tra cố định kiểu `== 4`.
- Không tự chụp thêm ảnh ngoài yêu cầu của template.

**BR-T06 — Chụp lại ảnh**

- Cho phép chụp lại một ảnh bất kỳ; ảnh mới thay thế ảnh tại `photoIndex` tương ứng, không đổi các ảnh khác.
- Nếu ảnh được nhiều slot tham chiếu, mọi slot đó dùng ảnh mới khi ghép lại.

**BR-T07 — Hiển thị tiến trình**

- UI hiển thị số ảnh đã chụp và tổng số ảnh theo template, ví dụ `Ảnh 1/3`, `Ảnh 2/3`, `Ảnh 3/3`.
- Không hardcode nhãn `1/4`…`4/4`; danh sách thumbnail co giãn theo số ảnh.

### 6.2 Template Library, Template Manager và Editor (Phase 3, đã chốt 19/11/2026)

- **BR-TL01 — Thư viện dùng chung:** template nằm trong Template Library của máy (`%LOCALAPPDATA%\Photobooth\Templates`), dùng cho mọi sự kiện; **không** lưu trong Event Workspace. Phiên chụp lưu snapshot template riêng, nên sửa/xóa template trong thư viện không đổi kết quả của phiên cũ (BR-T04).
- **BR-TL02 — Bật/tắt:** chỉ template hợp lệ và **đang bật** hiện trên Kiosk. Template mới tạo, sao chép hoặc nhập **mặc định tắt** để không lộ template dở dang cho khách. Tắt template không ảnh hưởng phiên đã dùng nó.
- **BR-TL03 — Không lưu bản hỏng:** lưu/nhập chỉ thành công khi template hợp lệ hoàn toàn (nội dung + asset). Thất bại → không ghi gì, bản đang có giữ nguyên, lỗi hiển thị theo vị trí thuộc tính.
- **BR-TL04 — Không ghi đè ngoài ý muốn:** nhập template có id trùng → đặt id mới; sao chép tạo id mới và bản riêng của asset; đổi tên không đổi id.
- **BR-TL05 — Xóa có xác nhận:** xóa hẳn một template (không có thùng rác) sau khi xác nhận; không xóa asset của template khác hay dữ liệu Event Workspace. Không cho xóa/lưu đè template đang được một phiên chụp chưa kết thúc sử dụng.
- **BR-TL06 — Editor dùng Engine hiện có:** Editor chỉ tạo đúng cấu trúc `template.json` của Engine; số ảnh vẫn theo BR-T03; `focalPoint` là mặc định của template, Manual Crop của phiên không ghi vào template; "Xem thử" render bằng Photo Composer.

## 7. Quy tắc lưu trữ và phục hồi

File ảnh của phiên nằm trong **Event Workspace**; metadata (sự kiện, phiên, ảnh, phiên bản, xuất/in, chia sẻ, trạng thái upload) nằm trong **SQLite** `%LOCALAPPDATA%/Photobooth/photobooth.db` (chi tiết: `docs/EVENT_STORAGE.md`). `%LOCALAPPDATA%/Photobooth/` còn chứa log và hồ sơ WebView2. Máy in, webcam và vùng hướng dẫn chụp được lưu trong SQLite (`DeviceSettings`). Cấu hình Desktop nằm tại `appsettings.json` cạnh file `.exe`.

```text
{Thư mục sự kiện}/
├── event.json            (dấu nhận diện: mã sự kiện)
└── Sessions/{yyyyMMdd-HHmmss-xxxxxxxx}/
    ├── Template/         (snapshot template + asset)
    ├── Originals/
    ├── Outputs/          (final-v{N}.png, ảnh thu nhỏ)
    └── Drafts/           (bản ghép tạm, được dọn)
```

- Metadata lưu trong SQLite qua EF Core (đã chốt 23/11/2026), đường dẫn tương đối với thư mục sự kiện. **Không đưa ảnh vào database.** Không chuyển dữ liệu JSON cũ; thư mục sự kiện cũ vẫn mở được nhưng phiên cũ không hiện trong Thư viện ảnh và không bị xóa.
- Share Token đã lưu không bao giờ đổi; trạng thái upload lưu trong database để tiếp tục sau khi khởi động lại.
- Trước khi phục vụ khách phải có Event Workspace hợp lệ; thư mục mất/không ghi được thì báo lỗi, **không** lưu sang chỗ khác và **không** tự tạo lại thư mục đã mất.
- Không đổi sự kiện khi đang có phiên chụp chưa kết thúc; dữ liệu các sự kiện không trộn lẫn.
- Không phụ thuộc vào thư mục dự án/build để lưu phiên ảnh.
- Nếu ghi file lỗi giữa chừng, không tạo metadata “thành công” trỏ vào file chưa hoàn chỉnh.
- Khởi động lại phải không làm mất file ảnh đã lưu; ứng dụng có thể đánh dấu phiên đang dang dở để người quản trị xem/xử lý.
- Phục hồi **tự động cả một phiên chưa hoàn tất** là lựa chọn triển khai cần xác nhận; yêu cầu nền tảng là không xóa dữ liệu đã ghi.
- Nếu phiên bị hủy, không tự động xóa ảnh vì chưa có chính sách lưu trữ chính thức.
- Không tự động upload, gửi email hoặc tạo QR từ ảnh **gốc**. Ngoại lệ đã chốt: chia sẻ QR ảnh **thành phẩm** khi Cloud được cấu hình (mục 4.7).

## 8. Quy tắc lỗi và hành động khắc phục

| Tình huống | Ứng dụng phải làm | Không được làm |
|---|---|---|
| Không có webcam | Hiển thị thông báo, cho thử lại hoặc kiểm tra cài đặt | Treo UI, tự báo đã chụp |
| Từ chối quyền camera | Giải thích cách cấp quyền phù hợp | Tự cấp quyền cho mọi nguồn website |
| Camera đang bị ứng dụng khác dùng | Thông báo và cho phép thử lại sau | Lặp vô hạn gây tải máy |
| Ngắt camera giữa phiên | Tạm dừng quy trình, giữ ảnh đã lưu | Xóa ảnh phiên hoặc ghi đè ảnh giả |
| Chụp thất bại | Giữ thứ tự và bộ đếm ảnh thành công, cho thử lại | Tính ảnh lỗi vào số ảnh đã đủ |
| Template không hợp lệ | Chặn thao tác ghép/chụp với lý do cụ thể | Xuất ảnh trắng và báo thành công |
| Ghép ảnh lỗi | Giữ ảnh gốc và cho thực hiện lại | Xóa hoặc chỉnh sửa file gốc |
| Hết dung lượng/quyền ghi | Báo lỗi dễ hiểu, giữ dữ liệu đã có | Ghi metadata thành công khi file chưa có |
| Xuất PDF lỗi | Không đánh dấu job thành công, cho thử lại | Xóa PNG/JPEG đã xuất thành công |
| WebView2 lỗi renderer | Ghi log, phục hồi giao diện khi có thể | Mất session do chỉ lưu trong một component |

## 9. Chế độ Admin

Quản trị viên trong V1 cần có khả năng:

- **Đã triển khai (19/11/2026):** chọn/tạo/mở lại/đổi tên sự kiện; thư viện ảnh thành phẩm có **bộ lọc theo sự kiện** (sự kiện hiện tại, một sự kiện khác đã mở trên máy, hoặc tất cả — chỉ để xem, không đổi sự kiện của Kiosk; xem `docs/EVENT_STORAGE.md`) (xem, phóng to, chọn phiên bản); chỉnh sửa lại; xuất và in lại đúng phiên bản; lịch sử in và xác nhận trạng thái in thực tế; chọn máy in.
- Xem và chỉnh cấu hình camera (trong phạm vi thiết bị có sẵn), giới hạn an toàn số ảnh, thời gian đếm ngược, thư mục lưu và tùy chọn xuất.
- **Đã triển khai (Phase 3, 19/11/2026):** Template Manager (danh sách có thumbnail, tạo, sao chép, đổi tên, xóa, nhập/xuất `.zip`, bật/tắt trên Kiosk) và Visual Template Editor (mục 6.2, `docs/TEMPLATE_SCHEMA.md` mục 11).
- Xem lịch sử phiên/ảnh đã lưu, mở file hoặc thư mục chứa file.
- Thấy trạng thái lỗi cơ bản để chẩn đoán, không phải mở console của developer.
- Điều chỉnh cấu hình mới mà **không phá hủy phiên đang chụp**; thời điểm áp dụng cài đặt (ngay hay phiên sau) cần nhất quán và thể hiện rõ.

**Bảo vệ truy cập Admin (đã chốt 19/11/2026 theo yêu cầu chủ sản phẩm):** màn khởi động hiện khi mở ứng dụng; tại đó có nút vào Admin bằng **PIN cố định `1234`** (khai báo duy nhất ở `AdminAccessService.Pin`). Sau khi bấm "Bắt đầu phục vụ khách", chế độ Admin (và thư viện quản trị có bộ lọc nhiều sự kiện, chỉnh sửa, in/xuất lại, tạo QR) bị ẩn. **Cập nhật 19/11/2026 (theo yêu cầu chủ sản phẩm):** màn hình chào của Kiosk có nút nhỏ **"Quản trị"** hiện rõ ở góc; bấm và nhập đúng PIN thì vào lại Admin mà không cần mở lại ứng dụng. Nút chỉ có ở màn hình chào, nên không làm gián đoạn phiên chụp đang dở. Thoát Admin → màn khởi động → "Bắt đầu phục vụ khách" để quay lại Kiosk. Vì nút hiện rõ, khách có thể tò mò bấm; PIN `1234` rất dễ đoán, nên nên đổi sang PIN khó hơn trước khi dùng thật. Lưu ý rủi ro: PIN cố định, ngắn chỉ ngăn khách bấm nhầm, không phải bảo mật mạnh.

**Thư viện sự kiện trong Client Mode (quyết định mới của chủ sản phẩm, 26/11/2026):** thay thế quy tắc cũ "khách ở Kiosk không xem được ảnh của khách khác". Ở màn Chào mừng và màn Hoàn thành (không mở giữa các bước chụp/ghép/xuất), khách được mở **Thư viện sự kiện** ở chế độ **chỉ xem (read-only)** và **offline** để xem toàn bộ các phiên, ảnh thành phẩm, ảnh gốc (kể cả ảnh chụp lại), thông tin phiên và mã QR đã tạo (nếu có) của **riêng sự kiện đang phục vụ**. Không cho lọc/đổi sự kiện, không thêm/sửa/xóa/in/xuất/mở Explorer/tạo QR mới và không hủy hay thay đổi phiên chụp hiện tại. Thư viện quản trị (`EventGalleryService`) vẫn chỉ dành cho Admin có PIN.

## 10. Tiêu chí nghiệm thu theo kịch bản

| Mã | Kịch bản | Kết quả mong muốn |
|---|---|---|
| BR-01 | Không có Internet, mở ứng dụng | Có thể vào luồng chụp và xuất file bình thường |
| BR-02 | Webcam không tồn tại/không cấp quyền | Có thông báo tiếng Việt và hướng thử lại; không crash |
| BR-03 | Chọn template cần N ảnh (ví dụ 1, 2, 3, 4, 6), chụp thành công | Chụp đúng N lần, lưu đủ N ảnh gốc, đúng thứ tự, không trùng file |
| BR-04 | Nhấn Chụp liên tục | Không tạo nhiều yêu cầu chụp song song |
| BR-05 | Chụp ảnh thứ 3 bị lỗi | Hai ảnh đầu vẫn còn; bộ đếm ảnh thành công không nhảy thành 3 |
| BR-06 | Ghép theo template (kể cả nhiều slot dùng chung `photoIndex`) | Mỗi slot hiển thị đúng ảnh theo `photoIndex`, đúng tỉ lệ |
| BR-07 | Template/ảnh không hợp lệ | Trả lỗi có thể hiểu; không báo ghép thành công |
| BR-08 | Xuất JPEG và PNG | Tạo file mở được, không ghi đè ngoài ý muốn |
| BR-09 | Xuất PDF 4×6 inch | Trang PDF có kích thước 288×432 point và đúng hướng |
| BR-10 | Xuất lỗi vì không có quyền ghi | Giữ nguyên ảnh gốc, đưa thông báo và cho chọn vị trí khác |
| BR-11 | Đóng/mở ứng dụng | File ảnh đã lưu vẫn tồn tại, có thể truy cập qua thư viện khi tính năng đã triển khai |
| BR-12 | Chụp/ghép/xuất offline | Không có lời gọi cloud hay yêu cầu login để hoàn tất luồng (upload QR chạy nền, không chặn) |
| BR-QR | Hoàn tất khi mất Internet | Vẫn có QR; khi có mạng lại, ảnh tự đồng bộ và đúng QR cũ mở được ảnh |

**Kiểm thử phân lớp:** BR-03/04/05/06/09 ưu tiên unit/integration test với camera giả và ảnh mẫu. BR-02 và preview webcam **phải thử thật trên Windows**; mock không xác nhận webcam hoạt động.

## 11. Những quyết định chưa chốt — AI phải hỏi/đề xuất khi đến chức năng liên quan

1. ~~Chụp lại từng tấm hay toàn bộ phiên~~ (đã chốt: cả hai); có giới hạn số lần không?
2. Chỉnh template sau khi đã chụp có cho phép không?
3. Khi khách đã xem kết quả, phần mềm tự xuất vào thư mục mặc định hay chỉ xuất sau khi khách chọn?
4. ~~Cách chọn và bảo vệ Admin Mode~~ (đã chốt: màn khởi động + PIN cố định, mục 9); có cần đổi PIN được không?
5. Thời hạn lưu, cách xóa dữ liệu cũ và chính sách bảo mật ảnh khi dùng thật.
6. Thiết lập chính thức của JPEG quality, kích thước template và thời gian chờ không thao tác.
7. Người dùng được xuất bao nhiêu lần/định dạng trong mỗi phiên và quy tắc hoàn tất phiên.

Nếu nhiệm vụ được giao **không phụ thuộc** một điểm chưa chốt, không đặt câu hỏi thừa; triển khai phần đã rõ. Nếu nó **phụ thuộc trực tiếp**, nêu lựa chọn hợp lý và xin người dùng chốt trước khi biến thành hành vi chính thức.

## 12. Liên kết tài liệu

- `AGENTS.md`: quy tắc làm việc của AI và báo cáo tiếng Việt.
- `docs/PROJECT_OVERVIEW.md`: mục tiêu, phạm vi MVP và các mốc.
- `docs/ARCHITECTURE.md`: phân tách WPF/Blazor/Application/Infrastructure.
- `docs/CODING_STANDARDS.md`: quy chuẩn triển khai, test và xử lý lỗi.
- `docs/UI_UX_GUIDELINES.md`: hành vi và nguyên tắc thể hiện trên màn hình.
- `docs/EVENT_STORAGE.md`: Event Workspace, cấu trúc lưu trữ, mở lại/chỉnh sửa phiên, sao chép sự kiện.
