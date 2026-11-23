# Quy chuẩn phát triển mã nguồn — Photobooth Mini

> **Phạm vi:** Tất cả mã C#, XAML, Razor, JavaScript, CSS, file cấu hình và test thuộc dự án Photobooth.
>
> **Ngôn ngữ:** Tài liệu, mô tả thay đổi và giao tiếp với người dùng bằng **tiếng Việt**. Định danh code và API dùng **tiếng Anh** theo quy ước .NET.
>
> **Trạng thái:** Quy tắc áp dụng cho V1; có thể điều chỉnh bằng một quyết định kỹ thuật được ghi nhận rõ ràng.

## 1. Mục tiêu và thứ tự ưu tiên

1. Đúng nghiệp vụ trong `BUSINESS_RULES.md` và phạm vi `PROJECT_OVERVIEW.md`.
2. An toàn dữ liệu ảnh gốc, ổn định ứng dụng và khả năng phục hồi lỗi.
3. Code dễ hiểu, dễ kiểm thử, dễ thay thế webcam/máy in về sau.
4. Phản hồi giao diện tốt; không khóa UI thread.
5. Đơn giản, ít phụ thuộc, tránh trừu tượng hóa sớm.

**Không áp dụng máy móc:** Các ví dụ trong tài liệu là **quy ước đề xuất**. Nếu repository đã có một quy ước nhất quán, hãy ưu tiên quy ước hiện hữu và báo cáo khi có mâu thuẫn. Không tạo thêm project, pattern, interface chỉ để khớp sơ đồ minh họa.

## 2. Công nghệ và ranh giới

- Nền tảng: **.NET 10 + WPF + BlazorWebView** trên Windows.
- `Photobooth.Desktop`: WPF host, Razor UI, JS interop bridge, WPF window lifecycle và composition root.
- `Photobooth.Application`: điều phối phiên chụp, quy tắc sử dụng service, hợp đồng (`interface`) nghiệp vụ.
- `Photobooth.Domain`: kiểu dữ liệu/trạng thái/quy tắc thuần nghiệp vụ **nếu thực sự cần**.
- `Photobooth.Infrastructure`: lưu file, SQLite, xử lý ảnh, PDF, adapter phần cứng native **nếu cần**.
- `Photobooth.Tests`: test logic và integration có thể chạy không cần webcam thật.
- UI không truy cập trực tiếp EF Core, không chứa code ghép ảnh nhiều bước, không tự quyết định các chuyển trạng thái quan trọng của session.
- Application không tham chiếu WPF, Blazor, `IJSRuntime`, WebView2, thư viện camera hoặc SQLite provider.
- Không tự tạo backend HTTP, SignalR, React, cloud services khi ứng dụng hiện tại chạy cùng một tiến trình và không có yêu cầu từ người dùng.

## 3. Quy tắc đặt tên và bố trí file

| Thành phần | Quy ước | Ví dụ |
|---|---|---|
| Namespace, class, record, enum, method, property | `PascalCase` | `PhotoSessionService`, `CaptureAsync` |
| Interface | `I` + `PascalCase` | `ICameraService` |
| Local variable, parameter | `camelCase` | `sessionId`, `photoCount` |
| Private field | `_camelCase` | `_sessionRepository` |
| Hằng số | `PascalCase` | `DefaultCountdownSeconds` |
| Phương thức bất đồng bộ | Hậu tố `Async` | `SaveOriginalAsync` |
| Razor component | `PascalCase.razor` | `CameraPreview.razor` |
| Tên file JS/CSS | Theo tên module, nhất quán | `cameraInterop.js`, `camera-preview.css` |
| Test | `Method_Condition_ExpectedResult` hoặc convention nhất quán của repo | `CaptureAsync_NoCamera_ReturnsError` |

- Định danh **không dấu tiếng Việt**; không dùng tên chung chung như `Utils`, `Helper2`, `Data1` nếu có thể diễn đạt chính xác.
- Một file ưu tiên chứa một trách nhiệm chính; không chia file chỉ vì vượt một số dòng tùy ý.
- Dùng `record`/DTO cho dữ liệu truyền qua ranh giới phù hợp; không tạo chuỗi DTO chuyển đổi vô ích.
- Kiểu thời gian trong dữ liệu lưu trữ ưu tiên `DateTimeOffset` hoặc UTC nhất quán; chỉ chuyển sang giờ địa phương tại giao diện.
- Giá trị có đơn vị cần ghi rõ tên/kiểu: `CountdownSeconds`, `CanvasWidthPixels`, `PageWidthPoints`; không dùng một số nguyên mơ hồ cho cả pixel và point.

## 4. C# — cách viết và xử lý lỗi

- Bật nullable reference types; không lạm dụng toán tử `!` để che lỗi null.
- Ưu tiên guard clause, phương thức ngắn đủ rõ; tránh lồng nhiều tầng không cần thiết.
- Không dùng `.Wait()`, `.Result` hoặc `Thread.Sleep()` trên UI thread; tác vụ I/O dùng `async/await`.
- Khi công việc có thể hủy, truyền `CancellationToken` từ UI đến Application và adapter; chỉ đánh dấu hủy khi thực sự xử lý xong.
- Tác vụ CPU-bound như xử lý ảnh cần được thực thi theo cách không khóa dispatcher/rendering thread; không tạo `Task.Run` tràn lan ở mọi lớp.
- Sử dụng `using`/`await using` để giải phóng stream, bitmap, file handle và thiết bị; quản lý vòng đời `IDisposable`/`IAsyncDisposable`.
- Bắt lỗi tại **ranh giới có khả năng xử lý**: hiển thị thông báo, thử lại, chuyển trạng thái hoặc thêm ngữ cảnh. Không `catch {}` và không trả về thành công khi thực tế thất bại.
- Tránh `throw ex;` làm mất stack trace; dùng `throw;` hoặc bọc ngoại lệ kèm nguyên nhân khi cần.
- Đối với thao tác file, kiểm tra quyền truy cập, đường dẫn, tên file trùng, ổ đầy và ngoại lệ I/O; không dùng kiểm tra `File.Exists` đơn thuần để bảo đảm ghi file an toàn trước cạnh tranh.
- Không ghi đè hoặc xóa ảnh gốc của phiên chỉ vì ghép ảnh hay xuất file lỗi.

## 5. Dependency Injection và trạng thái

- Đăng ký các service tại composition root của WPF; không tạo `new` service rải rác trong Razor Components.
- Chọn lifetime theo **trạng thái thực tế**: singleton cho service không trạng thái hoặc điều phối dùng chung nếu thread-safe, transient cho tác vụ độc lập, scoped chỉ khi ranh giới scope được định nghĩa rõ trong Blazor Hybrid.
- Không giả định `scoped` trong desktop tự tương đương mỗi HTTP request; kiểm tra thời gian sống của service và WebView.
- Không lưu các `DbContext`, stream camera hoặc bitmap mutable dài hạn trong singleton. EF Core `DbContext` dùng lifetime ngắn, không truy cập đồng thời.
- Chỉ duy trì một phiên chụp hoạt động trên một nguồn webcam trong V1. Nếu cần xử lý đồng thời, phải có cơ chế bảo vệ trạng thái và thứ tự sự kiện.
- Không dùng global static mutable state cho session, cấu hình đang sửa hoặc ảnh.
- Session Service làm chủ trạng thái nghiệp vụ; Razor chỉ biểu diễn và phát sinh ý định người dùng.

## 6. WPF, BlazorWebView và JS interop

### 6.1 WPF Host

- WPF chịu trách nhiệm cửa sổ, fullscreen/kiosk, lifecycle, đăng ký DI và những API native cần thiết.
- Khi tác động tới WPF UI từ luồng khác, sử dụng dispatcher phù hợp.
- Không gọi logic chụp/ghép ảnh nặng trực tiếp trong event handler WPF.
- Xử lý thoát chương trình, giải phóng camera và dừng công việc theo thứ tự an toàn.

### 6.2 Razor Components

- Component chỉ quản lý state trình bày và gọi Application Service.
- Hiển thị rõ `loading`, `empty`, `error`, `success` thay vì giả định thao tác luôn thành công.
- Không thao tác DOM bằng JS khi Blazor có thể thực hiện trực tiếp; dùng JS interop cho API trình duyệt thực sự cần thiết.
- Với các callback async, tránh `async void`, ngoại trừ event handler framework có chữ ký bắt buộc.
- Hủy subscription, event handler và JS reference khi component bị hủy; tránh component đã dispose vẫn cập nhật UI.

### 6.3 Webcam qua WebView2

- **Mốc 1:** thử `navigator.mediaDevices.getUserMedia()` trong nội dung nội bộ đáng tin cậy; không khẳng định đã tương thích nếu chưa chạy trên Windows.
- Video preview chạy trực tiếp trong `<video>`; không chuyển mỗi frame qua C#.
- Chỉ truyền frame cần lưu khi thao tác chụp diễn ra. Tránh vòng lặp chuỗi Base64 dung lượng lớn; xem xét stream/binary interop phù hợp nếu thực sự có vấn đề hiệu năng.
- Quyền camera chỉ cấp khi cần và chỉ cho nguồn tin cậy; không tự cấp quyền cho trang ngoài.
- Gọi `track.stop()` khi dừng preview, chuyển nguồn camera hay kết thúc phiên; hủy tham chiếu JS nếu có.
- Bắt và ánh xạ lỗi JS (không có thiết bị, từ chối quyền, camera bận, thiết bị mất kết nối) sang thông báo dễ hiểu.
- Nếu `getUserMedia()` không đạt POC, đề xuất native adapter theo `ARCHITECTURE.md`; không âm thầm thay công nghệ.

## 7. Ảnh, template và PDF

- **Ảnh gốc bất biến:** ảnh được lưu trước mọi thao tác chỉnh sửa; file ghép và file xuất dùng đường dẫn khác.
- Photo Composer làm việc độc lập với UI; có thể test bằng ảnh mẫu không dùng webcam.
- Kiểm tra dữ liệu ảnh (định dạng, kích thước, ảnh hỏng) và kích thước canvas trước khi phân bổ bộ nhớ lớn.
- Không kéo giãn sai tỉ lệ; thuật toán crop/fit phải nhất quán với template.
- Tọa độ template được mô tả bằng đơn vị rõ ràng, thông thường là pixel trên canvas gốc.
- Giữ nguyên thứ tự ảnh trong phiên; không lấy thứ tự ngẫu nhiên từ hệ thống file.
- PDF dùng **kích thước trang vật lý** (point), không mặc định 1 pixel = 1 point. Ví dụ 4×6 inch = 288×432 point; ảnh raster 1200×1800 px là cấu hình đề xuất cho 300 DPI.
- Không thêm watermark, tự động chia sẻ hoặc giới hạn kích thước ảnh ngoài yêu cầu.
- Khi chọn thư viện ảnh/PDF, kiểm tra compatibility với .NET 10, giấy phép dùng cho mục đích thương mại và bảo trì; không tự ý thêm package lớn.

## 8. Lưu trữ, cấu hình và bảo mật

- Dữ liệu ảnh lưu thành **file**; metadata lưu trong SQLite (Desktop) / PostgreSQL (Cloud) qua EF Core, dùng `IDbContextFactory` (DbContext ngắn). Thay đổi entity phải kèm EF Core migration (xem `docs/ARCHITECTURE.md` §9.1); không lưu đường dẫn tuyệt đối tới file trong Event Workspace.
- Đặt dữ liệu người dùng ở thư mục ứng dụng phù hợp trong `%LOCALAPPDATA%` hoặc thư mục họ chọn, **không ghi dữ liệu phiên vào Program Files**.
- Mặc định tách `Originals`, `Outputs`, `Logs`; không đặt dữ liệu trong thư mục build/deploy có thể bị ghi đè.
- Khi ghi file mới, ưu tiên ghi tạm và hoàn tất theo cách an toàn trước khi cập nhật metadata trạng thái thành công.
- Không cho phép tên file đầu vào chứa đường dẫn vượt thư mục dự kiến; xử lý ký tự không hợp lệ và trường hợp trùng tên.
- Không log bytes ảnh, ảnh Base64, nội dung nhận diện cá nhân, token hay đường dẫn người dùng không cần thiết.
- Không tự upload ảnh, bật analytics, gọi CDN/phông chữ từ xa hoặc yêu cầu đăng nhập mạng.
- Các cài đặt cấu hình phải có giá trị mặc định hợp lệ, validate đầu vào và xử lý cấu hình bị hỏng.

## 9. Logging và thông báo lỗi

- Log kỹ thuật gồm: thời điểm, loại sự kiện, module, `SessionId`/`PhotoId` khi an toàn, mã lỗi, exception liên quan.
- Thông báo người dùng **ngắn gọn, tiếng Việt, chỉ dẫn hành động tiếp theo**; không hiển thị stack trace.
- Khi khởi chạy và kết thúc luồng quan trọng, ghi log mức phù hợp; không log từng frame của webcam.
- Phân biệt lỗi có thể phục hồi (chưa cấp quyền, không chọn camera, thất bại một lần xuất) với lỗi làm ngừng phiên.
- Với WebView2, ghi nhận sự cố renderer và khả năng khởi tạo lại giao diện nếu phù hợp; không coi reset UI là lý do xóa ảnh đã lưu.

## 10. CSS, JavaScript và tài nguyên UI

- UI áp dụng `UI_UX_GUIDELINES.md`; khai báo design tokens cho màu, khoảng cách, typography.
- Sử dụng CSS scoped/component khi phù hợp, hạn chế `!important` và selector toàn cục dễ gây xung đột.
- Không thêm framework CSS/JS nặng nếu nhu cầu có thể giải quyết bằng CSS, Razor, công cụ đang có.
- Đường dẫn asset nội bộ ổn định, hoạt động khi ứng dụng offline và khi đóng gói bản phát hành.
- JS chỉ giữ logic liên quan API trình duyệt hoặc thao tác mà Blazor không thể thực hiện thuận tiện; nghiệp vụ quản lý session nằm ở C#.
- Đảm bảo toàn bộ nút, trường và trạng thái có text tiếng Việt phù hợp.

## 11. Kiểm thử bắt buộc theo loại thay đổi

| Thay đổi | Kiểm thử tối thiểu |
|---|---|
| Quy tắc session, thứ tự ảnh, hủy/chụp lại | Unit test cho chuyển trạng thái và dữ liệu |
| Photo Composer / template | Unit test tọa độ, crop/fit; integration test với ảnh mẫu |
| Xuất JPEG/PNG/PDF | Kiểm tra file tạo được, định dạng, kích thước ảnh/trang, lỗi ghi file |
| Webcam JS/WebView2 | Build và hướng dẫn **thử thủ công trên Windows**; test mock không thay thế test thật |
| UI Razor/WPF | Build, kiểm tra luồng điều hướng, trạng thái lỗi/loading và không khóa UI |
| Persistence/SQLite | Test đọc/ghi và khôi phục dữ liệu, không ghi đè ảnh gốc |

- Chạy `dotnet build` và `dotnet test` theo phạm vi phù hợp **nếu môi trường thực hiện được**.
- Kiểm tra diff trước khi kết luận: không xóa file user, không thay đổi module ngoài phạm vi.
- Không nói “đã test thành công” nếu chưa chạy test hoặc không có kết quả.
- Nếu môi trường không có Windows/WebView2/camera, ghi chính xác **chưa kiểm chứng** và đưa các bước thử thủ công.
- Khi sửa lỗi, ưu tiên thêm test tái hiện lỗi nếu có thể.

## 12. Quy trình làm việc của AI trên mỗi tác vụ

1. Đọc `AGENTS.md` và tài liệu có liên quan; đọc code hiện trạng.
2. Xác định quy tắc đã chốt và các **điểm chưa chốt**; không biến đề xuất thành yêu cầu bắt buộc.
3. Chỉ ra các file/service thực sự cần sửa; với thay đổi lớn, trình bày phương án và xin phép trước.
4. Viết code tối thiểu nhưng hoàn chỉnh theo yêu cầu; giữ nguyên hành vi đang ổn định.
5. Build, test, kiểm tra diff; ghi rõ những bước không thể chạy.
6. Cập nhật Markdown nếu quyết định nghiệp vụ/kiến trúc đã thay đổi.
7. Báo cáo **bằng tiếng Việt** theo mẫu: **Đã thực hiện – File thay đổi – Kiểm chứng – Hạn chế/rủi ro**.

## 13. Những điều không được tự ý làm

- Thêm React, Web API, microservices, cloud hoặc tài khoản người dùng.
- Bắt buộc camera rời, máy in, Internet hay bất kỳ phần cứng chưa mua.
- Tự quyết định thời hạn xóa ảnh, chính sách chụp lại, mẫu UI cuối cùng khi chưa có thống nhất.
- Đổi toàn bộ cấu trúc thư mục, namespace, tên class mà không phục vụ yêu cầu.
- Tự `git commit`, `git push`, xóa dữ liệu, reset/hard-clean repository hoặc thực thi lệnh nguy hiểm.
- Tự nâng .NET/package hàng loạt, thêm package không rõ giấy phép thương mại.
- Bỏ qua lỗi, tắt validation, khai báo thành công giả hoặc sửa test chỉ để chuyển từ đỏ sang xanh.

## 14. Tài liệu liên quan

- `AGENTS.md` — quy định chung của AI Coding Agent.
- `docs/PROJECT_OVERVIEW.md` — phạm vi sản phẩm V1.
- `docs/ARCHITECTURE.md` — phân chia module, camera bridge và dữ liệu.
- `docs/BUSINESS_RULES.md` — luồng nghiệp vụ và tiêu chí nghiệm thu.
- `docs/UI_UX_GUIDELINES.md` — quy tắc giao diện kiosk/admin.
