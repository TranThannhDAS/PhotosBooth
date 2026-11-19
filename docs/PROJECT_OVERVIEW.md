# Tổng quan dự án Photobooth Mini

**Trạng thái:** Tài liệu định hướng cho phiên bản phần mềm đầu tiên (V1).  
**Ngôn ngữ làm việc:** Tiếng Việt.  
**Nền tảng:** Windows desktop — WPF + BlazorWebView + .NET 10.  
**Ngày khởi tạo:** 19/11/2026.

## 1. Bối cảnh và mục tiêu

Dự án hướng đến một **máy Photobooth mini di động**, phục vụ chụp ảnh tự động tại sự kiện. Về dài hạn, phần mềm sẽ vận hành với camera, màn hình cảm ứng, mini PC và máy in ảnh riêng.

**Ưu tiên hiện tại là hoàn thiện phần mềm trước khi mua bất kỳ phần cứng bổ sung nào.** Giai đoạn phát triển dùng **webcam tích hợp trên laptop**, màn hình hiện có và hệ thống lưu file Windows. Không được yêu cầu có máy in, camera rời hay Internet để chạy luồng chính.

Mục tiêu của V1:

1. Tạo ứng dụng desktop `.exe` có giao diện chụp ảnh dễ sử dụng.
2. Hiển thị webcam trực tiếp, đếm ngược và chụp nhiều ảnh trong một phiên.
3. Cho phép chọn khung ảnh (template), ghép ảnh và xem trước thành phẩm.
4. Xuất thành phẩm ra **JPEG, PNG và PDF** trên ổ đĩa.
5. Quản lý ảnh và cấu hình cục bộ, có khả năng phục hồi lỗi cơ bản.
6. Chuẩn bị các điểm tích hợp để thay webcam laptop bằng camera rời và thêm máy in ở giai đoạn sau.

## 2. Đối tượng sử dụng

### Khách chụp ảnh — Chế độ Kiosk

Người dùng thao tác trực tiếp trên giao diện: bắt đầu, chọn template, xem camera, tạo dáng, chụp, xem thành phẩm, lưu hoặc xuất ảnh. Về sau giao diện này chạy trên màn hình cảm ứng.

### Người quản lý — Chế độ Admin

Cấu hình camera, thời gian đếm ngược, số ảnh một phiên, template mặc định, thư mục lưu, chất lượng xuất ảnh và xem lịch sử phiên chụp. Trong V1, Admin chỉ quản lý thiết bị và dữ liệu cục bộ.

## 3. Phạm vi chức năng V1

| Nhóm chức năng | Bắt buộc trong V1 | Mô tả |
|---|---|---|
| Màn hình chào | Có | Bắt đầu phiên mới, chuyển sang màn hình chọn mẫu |
| Chọn template | Có | Chọn từ các mẫu đi kèm ứng dụng |
| Webcam preview | Có | Hiển thị hình ảnh trực tiếp từ webcam laptop |
| Chụp ảnh | Có | Mặc định 4 ảnh/phiên; đếm ngược trước mỗi ảnh |
| Xem lại/chụp lại | Có | Xem kết quả trung gian; có thể chụp lại theo chính sách đã chọn |
| Ghép ảnh | Có | Tạo ảnh thành phẩm theo cấu hình template |
| Lưu ảnh gốc | Có | Không sửa hoặc ghi đè ảnh gốc |
| Xuất ảnh | Có | JPEG, PNG, PDF lưu cục bộ |
| Thư viện ảnh | Có | Xem các phiên/ảnh trước đó, mở file xuất |
| Cài đặt | Có | Camera, số ảnh, thời gian đếm ngược, thư mục lưu, chất lượng xuất |
| Nhật ký lỗi | Có | Ghi nhận lỗi có thể chẩn đoán, không log nội dung ảnh |
| Máy in vật lý | Chưa | Mô phỏng/hàng đợi in chỉ khi cần, chưa xác nhận tương thích |
| QR/cloud/payment | Chưa | Không thuộc phạm vi V1 |

**Chú ý:** Danh sách chức năng V1 là **đích đến**, không yêu cầu triển khai toàn bộ ngay trong task đầu tiên.

## 4. Luồng chính của khách

1. **Chào mừng:** hiển thị nút “Bắt đầu”.
2. **Chọn khung ảnh:** chọn layout/màu khung; xem trước mẫu.
3. **Mở camera:** hiển thị webcam; nếu lỗi, hướng dẫn chọn lại/kiểm tra quyền truy cập.
4. **Chụp ảnh:** đếm ngược mặc định 3 giây, chụp lần lượt 4 ảnh; có phản hồi rõ ràng sau mỗi lần.
5. **Xem lại:** xem ảnh đã chụp, chụp lại nếu được phép; không tự động xóa ảnh thành công khi một bước sau bị lỗi.
6. **Ghép ảnh:** xử lý thành một thành phẩm theo template.
7. **Xuất file:** chọn JPEG/PNG/PDF; hiển thị vị trí đã lưu.
8. **Hoàn tất:** trở về màn hình chào, bắt đầu phiên mới.

Các con số 3 giây/4 ảnh là **giá trị mặc định cấu hình được**, không hardcode trong nhiều component.

## 5. Nguyên tắc nghiệp vụ

### Phiên chụp

- Mỗi phiên có **mã định danh riêng** (`SessionId`).
- Quản lý trạng thái tối thiểu: `Created`, `Previewing`, `Capturing`, `Reviewing`, `Composing`, `Ready`, `Completed`, `Cancelled`, `Failed` (tên enum cụ thể có thể điều chỉnh khi code).
- Có khả năng kiểm tra/khôi phục những file ảnh đã lưu nếu ứng dụng thoát bất thường.
- Tác vụ thất bại không được làm mất các ảnh đã chụp thành công.

### Ảnh gốc và thành phẩm

- Ảnh gốc lưu riêng, không chỉnh sửa trực tiếp.
- Ảnh thành phẩm là kết quả ghép từ các ảnh gốc theo template.
- Template mô tả các vùng ảnh, khung và thành phần trang trí; dễ bổ sung mẫu mới mà không cần sửa logic ghép.
- Khi cắt ảnh để vừa khung phải có quy tắc rõ ràng (ví dụ center-crop), không tự kéo méo tỷ lệ.

### Xuất file

- JPEG/PNG dành cho chia sẻ, lưu và in ảnh sau này.
- PDF có **khổ trang vật lý xác định**, độc lập với kích thước pixel ảnh; bảo đảm tỷ lệ khi in.
- Khổ tham khảo ban đầu: **4 × 6 inch**, ảnh raster tham khảo **1200 × 1800 px ở 300 DPI**. Đây là cấu hình mặc định để thử nghiệm, không phải giới hạn cố định.
- Người dùng có thể chọn thư mục lưu, không ghi đè file cũ ngoài ý muốn.

### Quyền riêng tư

- Chỉ truy cập camera phục vụ phiên chụp.
- Không tự động tải ảnh lên Internet.
- Dữ liệu được lưu cục bộ; cần thiết kế khả năng xóa ảnh và chính sách lưu trữ khi có người dùng thực tế.

## 6. Yêu cầu phi chức năng

- **Offline-first:** chụp, ghép, xem và xuất ảnh hoạt động khi không có mạng.
- **Ổn định:** lỗi camera/WebView2/ghi file phải có thông báo và đường phục hồi phù hợp.
- **Mượt:** không đóng băng giao diện trong quá trình ghép ảnh và xuất PDF.
- **Có thể thay thế thiết bị:** giao diện và nghiệp vụ không phụ thuộc camera của một hãng cụ thể.
- **Dễ dùng trên cảm ứng:** nút bấm rõ ràng, vùng chạm đủ lớn, trạng thái tiến trình dễ nhận biết.
- **Dễ chẩn đoán:** log lỗi kỹ thuật có cấu trúc; không log dữ liệu ảnh cá nhân.
- **Dễ cài đặt:** tương lai đóng gói thành phần mềm Windows dùng được ngoài môi trường phát triển.

## 7. Các cột mốc triển khai

### Mốc 1 — Khởi tạo và webcam

- Tạo WPF + BlazorWebView chạy trên .NET 10.
- Hiển thị giao diện chào và màn hình camera.
- Webcam laptop preview được, chụp được một ảnh và lưu JPEG.
- **Chỉ được đánh dấu đạt sau khi kiểm tra thực tế trên Windows.**

### Mốc 2 — Phiên chụp nhiều ảnh

- Đếm ngược, chụp 4 ảnh, có xem lại và tùy chọn chụp lại.
- Xử lý khi người dùng hủy phiên hoặc camera bị ngắt.

### Mốc 3 — Template và xuất ảnh

- Xây dựng Photo Composer độc lập UI.
- Có ít nhất một template hoàn chỉnh.
- Xuất JPEG, PNG và PDF 4×6 inch; kiểm tra tỷ lệ và biên cắt.

### Mốc 4 — Quản trị và lưu trữ

- Lưu metadata phiên/cấu hình vào SQLite nếu cần.
- Giao diện Admin, lịch sử và quản lý file.

### Mốc 5 — Kiểm thử, đóng gói, vận hành offline

- Build/kiểm thử các chức năng có thể tự động hóa.
- Kiểm thử thủ công camera, WebView2 và khôi phục lỗi.
- Tạo bộ cài hoặc gói chạy trên một máy Windows khác.

## 8. Ngoài phạm vi giai đoạn hiện tại

Không tự triển khai hoặc yêu cầu mua ngay:

- Máy ảnh Canon/Sony hay SDK máy ảnh chuyên dụng.
- Máy in ảnh và điều khiển giấy mực thực tế.
- Cấu tạo cơ khí, nguồn pin, vỏ kiosk và màn hình cảm ứng rời.
- Hệ thống tài khoản, thanh toán, nhận ảnh qua QR/cloud, đồng bộ máy chủ hoặc SaaS.

Khi bổ sung phần cứng, kiến trúc phải hỗ trợ một adapter tương ứng mà không phải viết lại toàn bộ quy trình chụp.

## 9. Các quyết định chưa chốt

- Giao diện chi tiết và bộ template chính thức.
- Quy tắc chụp lại: cho chụp lại một ảnh hay cả phiên.
- Chính sách tự động dọn ảnh cũ và thời hạn lưu.
- Cơ chế chọn thư mục lưu, đặt tên file và sao lưu.
- Công nghệ camera native dự phòng nếu `getUserMedia()` không ổn định trên WebView2.
- Công cụ packaging/cập nhật phiên bản cuối cùng.

Các điểm này cần được quyết định khi bắt đầu triển khai phần liên quan, **không tự suy đoán thành yêu cầu đã chốt**.

## 10. Định nghĩa hoàn thành một tính năng

Một tính năng chỉ được coi là hoàn thành khi:

1. Có triển khai thực tế đúng phạm vi yêu cầu.
2. Có xử lý trạng thái lỗi và tương tác người dùng liên quan.
3. Build/test có thể chạy đã được thực hiện, hoặc nêu rõ lý do chưa chạy được.
4. Với camera/UI desktop, có hướng dẫn và kết quả thử thủ công trên Windows.
5. Không làm hỏng các chức năng trước đó.
6. Tài liệu liên quan được cập nhật nếu đã thay đổi quyết định quan trọng.
