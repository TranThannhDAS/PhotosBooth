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
- Mặc định chụp **4 ảnh/phiên**, đếm ngược **3 giây trước mỗi lần chụp**. Đây là **giá trị mặc định có thể cấu hình**, không phải giá trị cố định.
- Quản lý phiên (`SessionId`), thứ tự ảnh, lưu ảnh gốc và ảnh thành phẩm độc lập.
- Cho xuất **JPEG, PNG và PDF**; dữ liệu được lưu cục bộ, không tải lên mạng.
- Có chế độ **Kiosk** cho khách và **Admin** để quản lý cấu hình, mẫu ảnh, lịch sử.

### 1.2 Không thuộc V1 hiện tại

- In tự động trên máy in vật lý, đọc trạng thái giấy/mực, SDK Canon/Sony.
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
| Số ảnh mỗi phiên | 4 | Số nguyên dương, cần phù hợp với template được chọn |
| Thời gian đếm ngược | 3 giây | Số nguyên dương, giới hạn min/max cụ thể `CHƯA CHỐT` |
| Template | Một template mẫu đi kèm | Phải hợp lệ và có đủ vị trí ảnh theo cấu hình |
| Camera | Webcam laptop hiện có | Thông báo khi không tìm thấy hoặc không truy cập được |
| Khổ xuất ban đầu | 4 × 6 inch | Có thể cấu hình qua template/export preset |
| Kích thước raster tham khảo | 1200 × 1800 px | Tương ứng 300 DPI cho 4 × 6 inch; không hardcode vào mọi mẫu |
| Thư mục lưu | Vùng dữ liệu ứng dụng hợp lệ trên Windows | Cho phép cấu hình; kiểm tra quyền ghi và dung lượng |
| Chất lượng JPEG | `CHƯA CHỐT` | Chỉ lưu khi giá trị hợp lệ; không giả định 100% |
| Chụp lại | Có trong đích V1 | Cho phép chụp lại **từng ảnh hoặc toàn bộ phiên** (đã chốt 19/11/2026); giới hạn số lần `CHƯA CHỐT` (hiện không giới hạn) |
| Tự động xóa ảnh | Không triển khai ở giai đoạn đầu | Thời hạn lưu/xóa `CHƯA CHỐT` |

AI **không tự đặt** giới hạn số lần chụp lại, timeout quay về trang chào, số bản in hoặc hạn mức lưu trữ nếu người dùng chưa duyệt yêu cầu.

## 4. Luồng phiên chụp

### 4.1 Bắt đầu

1. Mở ứng dụng: hiển thị màn hình chào.
2. Khách chọn **Bắt đầu**: tạo một `PhotoSession` mới hoặc tạo ở thời điểm được định nghĩa thống nhất trong code; không tạo trùng session vì người dùng bấm liên tục.
3. Chọn một template hợp lệ; lưu tham chiếu/template snapshot cần thiết để phiên không bị ảnh hưởng nếu mẫu bị sửa sau đó.
4. Vào preview và yêu cầu quyền camera nếu chưa có.
5. Camera sẵn sàng thì hiển thị hình trực tiếp; chưa sẵn sàng thì hiển thị trạng thái và hành động khắc phục.

### 4.2 Chụp ảnh

1. Khách nhấn **Chụp ảnh**.
2. Với mỗi ảnh cần chụp, hiển thị đếm ngược theo cấu hình; phải dễ nhận biết ảnh thứ mấy trong tổng số.
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
- Giả định Giai đoạn 2 (chưa phải quyết định chính thức): không cho đổi template sau khi đã có ảnh trong phiên.
- Khi có thay đổi ảnh được chọn, bản ghép trước đó (nếu có) phải được đánh dấu cần tạo lại, không sử dụng nhầm kết quả cũ.

### 4.4 Ghép ảnh

- Chỉ ghép khi đủ ảnh đang được chọn và template hợp lệ.
- Giữ nguyên thứ tự vị trí `photoSlots`; không dựa vào thứ tự file trong thư mục.
- Scale/crop giữ đúng tỉ lệ; mặc định có thể dùng center-crop khi template không quy định khác, nhưng không được bóp méo ảnh.
- Quá trình ghép tạo **file mới** độc lập ảnh gốc; thất bại không được phá hủy ảnh đã lưu.
- Hiển thị trạng thái đang xử lý, tránh thao tác ghép trùng.
- Nếu không ghép được, thông báo lý do có thể hiểu và cho phép thực hiện lại.

### 4.5 Xuất ảnh

1. Khách xem ảnh thành phẩm đã ghép.
2. Chọn một hoặc nhiều định dạng xuất có hỗ trợ: JPEG, PNG, PDF.
3. Xác thực định dạng, thư mục, tên file và quyền ghi.
4. Xuất ra file mới; chỉ đánh dấu thành công **khi file đã được ghi hoàn chỉnh và có thể truy cập**.
5. Hiển thị đường dẫn/vị trí lưu và thao tác **Mở thư mục** khi phù hợp.
6. Khi một định dạng lỗi, không được xóa ảnh gốc và không được báo các định dạng khác thất bại nếu chúng đã thành công.
7. Không âm thầm ghi đè file có sẵn; xử lý trùng tên bằng cơ chế nhất quán và kiểm tra an toàn.

**Thông số ban đầu:** PDF khổ 4×6 inch có kích thước trang **288×432 point**. Ảnh raster tham khảo 1200×1800 pixel; đây là hai đơn vị khác nhau. Phải test chiều xoay, tỉ lệ, vùng an toàn và kết quả in thử bằng PDF nếu có công cụ phù hợp.

### 4.6 Kết thúc

- Sau khi hoàn tất, session có trạng thái kết thúc phù hợp; không tự xóa ảnh của phiên.
- Bấm **Phiên mới** tạo session khác, không sử dụng lại danh sách ảnh cũ.
- Hủy/thoát ứng dụng phải dừng camera và lưu trạng thái/file đã hoàn tất ở mức có thể phục hồi.

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
- Nếu số slot không khớp số ảnh cấu hình, **không cho bắt đầu chụp với cấu hình không hợp lệ**, hoặc có quy tắc ánh xạ đã được người dùng duyệt.
- Template đi kèm ứng dụng phải có bản mẫu/preview; không phụ thuộc Internet để tải ảnh nền.
- Cho phép mở rộng thêm chữ/ngày/logo/overlay sau, nhưng không mặc định mọi template phải hỗ trợ editor kéo-thả trong V1.
- Thay đổi template sau khi đã chụp: hành vi giữ/chụp lại/ghép mới **CHƯA CHỐT**; không tự quyết định ngầm.

## 7. Quy tắc lưu trữ và phục hồi

Ví dụ cấu trúc đề xuất (có thể thay đổi khi phát triển):

```text
%LOCALAPPDATA%/Photobooth/
├── Data/
│   └── photobooth.db
├── Sessions/
│   └── {SessionId}/
│       ├── Originals/
│       └── Outputs/
└── Logs/
```

- Không đưa ảnh gốc vào database dưới dạng BLOB mặc định; SQLite lưu metadata, đường dẫn và trạng thái khi persistence đã được triển khai.
- Không phụ thuộc vào thư mục dự án/build để lưu phiên ảnh.
- Nếu ghi file lỗi giữa chừng, không tạo metadata “thành công” trỏ vào file chưa hoàn chỉnh.
- Khởi động lại phải không làm mất file ảnh đã lưu; ứng dụng có thể đánh dấu phiên đang dang dở để người quản trị xem/xử lý.
- Phục hồi **tự động cả một phiên chưa hoàn tất** là lựa chọn triển khai cần xác nhận; yêu cầu nền tảng là không xóa dữ liệu đã ghi.
- Nếu phiên bị hủy, không tự động xóa ảnh vì chưa có chính sách lưu trữ chính thức.
- Không tự động upload, gửi email hoặc tạo QR từ ảnh đã chụp.

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

- Xem và chỉnh cấu hình camera (trong phạm vi thiết bị có sẵn), số ảnh, thời gian đếm ngược, thư mục lưu và tùy chọn xuất.
- Xem danh sách template có sẵn; việc tạo editor template đồ họa là **CHƯA CHỐT**.
- Xem lịch sử phiên/ảnh đã lưu, mở file hoặc thư mục chứa file.
- Thấy trạng thái lỗi cơ bản để chẩn đoán, không phải mở console của developer.
- Điều chỉnh cấu hình mới mà **không phá hủy phiên đang chụp**; thời điểm áp dụng cài đặt (ngay hay phiên sau) cần nhất quán và thể hiện rõ.

**Bảo vệ truy cập Admin:** cách mở Admin và có đặt PIN hay không `CHƯA CHỐT`; không tự thêm xác thực Internet hoặc mật khẩu mặc định không an toàn.

## 10. Tiêu chí nghiệm thu theo kịch bản

| Mã | Kịch bản | Kết quả mong muốn |
|---|---|---|
| BR-01 | Không có Internet, mở ứng dụng | Có thể vào luồng chụp và xuất file bình thường |
| BR-02 | Webcam không tồn tại/không cấp quyền | Có thông báo tiếng Việt và hướng thử lại; không crash |
| BR-03 | Cấu hình 4 ảnh, chụp thành công | Lưu đủ 4 ảnh gốc, đúng thứ tự, không trùng file |
| BR-04 | Nhấn Chụp liên tục | Không tạo nhiều yêu cầu chụp song song |
| BR-05 | Chụp ảnh thứ 3 bị lỗi | Hai ảnh đầu vẫn còn; bộ đếm ảnh thành công không nhảy thành 3 |
| BR-06 | Chọn template đủ 4 slot | Thành phẩm hiển thị đúng ảnh và đúng tỉ lệ |
| BR-07 | Template/ảnh không hợp lệ | Trả lỗi có thể hiểu; không báo ghép thành công |
| BR-08 | Xuất JPEG và PNG | Tạo file mở được, không ghi đè ngoài ý muốn |
| BR-09 | Xuất PDF 4×6 inch | Trang PDF có kích thước 288×432 point và đúng hướng |
| BR-10 | Xuất lỗi vì không có quyền ghi | Giữ nguyên ảnh gốc, đưa thông báo và cho chọn vị trí khác |
| BR-11 | Đóng/mở ứng dụng | File ảnh đã lưu vẫn tồn tại, có thể truy cập qua thư viện khi tính năng đã triển khai |
| BR-12 | Chụp/ghép/xuất offline | Không có lời gọi cloud hay yêu cầu login để hoàn tất luồng |

**Kiểm thử phân lớp:** BR-03/04/05/06/09 ưu tiên unit/integration test với camera giả và ảnh mẫu. BR-02 và preview webcam **phải thử thật trên Windows**; mock không xác nhận webcam hoạt động.

## 11. Những quyết định chưa chốt — AI phải hỏi/đề xuất khi đến chức năng liên quan

1. ~~Chụp lại từng tấm hay toàn bộ phiên~~ (đã chốt: cả hai); có giới hạn số lần không?
2. Chỉnh template sau khi đã chụp có cho phép không?
3. Khi khách đã xem kết quả, phần mềm tự xuất vào thư mục mặc định hay chỉ xuất sau khi khách chọn?
4. Cách chọn và bảo vệ Admin Mode, có dùng PIN không?
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
