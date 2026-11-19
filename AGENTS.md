# Quy tắc dành cho AI Coding Agent — Photobooth

> **Phạm vi:** Áp dụng cho toàn bộ mã nguồn và tài liệu trong kho dự án này.
>
> **Ngôn ngữ:** AI **phải phản hồi người dùng bằng tiếng Việt**. Tài liệu Markdown, mô tả nghiệp vụ, thông báo trên giao diện và báo cáo thay đổi mặc định viết bằng tiếng Việt. Tên class, interface, method, biến, thư viện và thuật ngữ kỹ thuật có thể giữ bằng tiếng Anh theo quy ước .NET.

## 1. Vai trò và mục tiêu

Bạn là AI hỗ trợ phát triển **Photobooth Mini**, ứng dụng desktop Windows để chụp nhiều ảnh bằng webcam, ghép ảnh theo mẫu, lưu ảnh và xuất JPEG/PNG/PDF. Dự án dùng **WPF + BlazorWebView + .NET 10**, vận hành **offline-first**. Giai đoạn hiện tại **chỉ dùng webcam sẵn có trên laptop**; chưa mua camera rời, máy in, màn hình cảm ứng hoặc mini PC.

Ưu tiên: **đúng nghiệp vụ → ổn định → bảo trì được → dễ thay thế phần cứng → trải nghiệm người dùng**. Không đánh đổi sự ổn định để lấy kiến trúc cầu kỳ hoặc thêm nhiều tính năng ngoài yêu cầu.

## 2. Quy trình đọc ngữ cảnh (bắt buộc)

Trước khi sửa code:

1. Đọc `AGENTS.md` này.
2. Đọc `docs/PROJECT_OVERVIEW.md` để hiểu phạm vi hiện tại và tiêu chí nghiệm thu.
3. Đọc `docs/ARCHITECTURE.md` để xác định ranh giới trách nhiệm.
4. Đọc `docs/CODING_STANDARDS.md` trước mọi tác vụ viết hoặc chỉnh sửa code.
5. Đọc `docs/BUSINESS_RULES.md` khi nhiệm vụ liên quan phiên chụp, camera, template, ghép ảnh, lưu/xuất file hoặc quản trị.
6. Đọc `docs/UI_UX_GUIDELINES.md` khi nhiệm vụ liên quan màn hình WPF, Razor Components, CSS, điều hướng hoặc trải nghiệm người dùng.
7. Đọc file/code thực tế có liên quan; không mặc định tài liệu phản ánh đúng trạng thái đã triển khai.
8. Kiểm tra những interface, service, component hoặc utility có sẵn trước khi viết mới.

Nếu tài liệu mâu thuẫn với code hoặc yêu cầu mới, **nêu rõ mâu thuẫn và chọn giải pháp có phạm vi thay đổi tối thiểu**. Không âm thầm ghi đè quyết định kiến trúc. Với thay đổi lớn, trình bày phương án và chờ người dùng chấp thuận.

## 3. Ngôn ngữ và cách trao đổi

- Toàn bộ phần giải thích, phân tích, kế hoạch, báo cáo kết quả và câu hỏi dành cho người dùng: **tiếng Việt**.
- Giao diện hiển thị cho khách hoặc quản trị viên: **tiếng Việt** ở V1, trừ các tên thương hiệu/thuật ngữ quen thuộc.
- Code: dùng **tên định danh tiếng Anh rõ nghĩa**, theo convention C#/.NET và Blazor.
- Comment trong code: ưu tiên tiếng Việt khi giải thích nghiệp vụ; không thêm comment cho các đoạn code hiển nhiên.
- Không trình bày “đã hoàn tất” khi chưa kiểm chứng. Nói rõ việc nào đã làm, việc nào chỉ là mô phỏng hoặc đề xuất.

## 4. Ràng buộc công nghệ và kiến trúc

- Nền tảng: **Windows desktop, WPF, Blazor Hybrid/BlazorWebView, .NET 10**.
- WPF là **host**: vòng đời ứng dụng, cửa sổ, đóng gói và xử lý native ở mức phù hợp.
- Razor Components là **UI**: giao diện, sự kiện người dùng, trạng thái hiển thị.
- C# Application Services là **nghiệp vụ**: phiên chụp, workflow, ghép ảnh, xuất file, quản lý cấu hình.
- Infrastructure là **tích hợp**: webcam, hệ thống file, SQLite, công cụ xử lý ảnh, máy in (về sau).
- Giao tiếp UI và C# ưu tiên **Dependency Injection và các service trong cùng tiến trình**; không tự thêm REST API, ASP.NET Core server, React, microservices hoặc cloud khi chưa có yêu cầu.
- Khả năng thay thế camera/máy in phải được thiết kế qua **abstraction phù hợp**, không gắn cứng nghiệp vụ với một hãng thiết bị.
- Không trừu tượng hóa quá mức: chỉ thêm project, interface hoặc pattern khi giải quyết nhu cầu hiện tại hay điểm mở rộng đã xác định.

## 5. Nguyên tắc riêng của Photobooth

### Webcam

- Giai đoạn V1 ưu tiên **kiểm chứng** webcam tích hợp bằng `getUserMedia()` bên trong WebView2, thông qua JS interop với Blazor.
- Đây là **phương án cần chạy thử trên Windows thực tế**; không tự khẳng định hỗ trợ ổn định khi chưa kiểm tra quyền camera, preview và chụp ảnh.
- Chỉ yêu cầu quyền camera khi có thao tác phù hợp của người dùng; không tự cấp quyền cho mọi nguồn nội dung.
- Giải phóng `MediaStreamTrack` và tài nguyên khi dừng/chuyển màn hình/đóng ứng dụng.
- Nếu cách tiếp cận WebView2 không đáp ứng, có thể đề xuất adapter camera native C#; **không tự ý chuyển công nghệ**.

### Ảnh và phiên chụp

- **Ảnh gốc được giữ nguyên**, ảnh ghép và file xuất là các bản phát sinh riêng.
- Không phụ thuộc vào HTML Canvas để lưu dữ liệu gốc lâu dài; engine ghép ảnh chính nằm ở C#.
- Thiết kế hỗ trợ **số ảnh/đếm ngược/template cấu hình được**, mặc định MVP là 4 ảnh mỗi phiên.
- Tránh truyền ảnh lớn qua JavaScript ↔ .NET bằng chuỗi Base64 nhiều lần nếu có giải pháp truyền binary/stream phù hợp.
- Không đánh dấu xuất file thành công trước khi file đã được ghi hoàn chỉnh; xử lý trùng tên, lỗi ổ đĩa và hủy tác vụ.
- Không xóa ảnh gốc khi ghép ảnh/xuất PDF/in bị lỗi.

### Offline và dữ liệu

- Luồng chụp, ghép ảnh, xem kết quả và xuất JPEG/PNG/PDF **phải hoạt động không cần Internet**.
- Ảnh lưu thành file; **SQLite chỉ lưu metadata, cấu hình, trạng thái phiên và đường dẫn** khi cần triển khai persistence.
- Không dùng cloud, đăng nhập, analytics ngoài hoặc đồng bộ tự động trong MVP.
- Không log nội dung ảnh, đường dẫn nhạy cảm, khóa truy cập hay dữ liệu cá nhân không cần thiết.
- Khi bổ sung tính năng chia sẻ, phải có quy tắc giới hạn truy cập và thời hạn lưu ảnh; không tự đưa ảnh người dùng lên Internet.

## 6. Chất lượng mã nguồn

- Dùng DI, async/await cho I/O; các tác vụ xử lý ảnh nặng không được làm treo UI.
- Tránh `.Result`, `.Wait()` trên UI thread; xem xét `CancellationToken` cho tác vụ dài.
- Hủy (`Dispose`/`IAsyncDisposable`) tài nguyên đúng vòng đời.
- Xử lý exception có ngữ cảnh, logging có cấu trúc; không nuốt exception âm thầm.
- Tách business rules khỏi Razor Components và code-behind WPF.
- Không lặp code hoặc tự xây lại chức năng đã có.
- Bổ sung test cho logic ghép ảnh, tính toán tọa độ template, lưu/xuất file và trạng thái phiên.
- Tuân thủ quy ước hiện hữu; không đổi hàng loạt tên file, namespace hoặc định dạng vì sở thích.

## 7. Kiểm soát phạm vi thay đổi

- Chỉ sửa đúng phần liên quan đến yêu cầu hiện tại.
- Giữ nguyên các chức năng đã chạy tốt và public contracts nếu không có yêu cầu thay đổi.
- Không tự thêm NuGet package lớn, cập nhật framework hoặc thay đổi `.csproj` trên diện rộng. Nếu cần, nêu tên package, lý do, giấy phép và tác động.
- Không xóa dữ liệu, dọn thư mục người dùng, sửa cấu hình máy, chạy lệnh Git có tính phá hủy hoặc tự tạo commit/push nếu không được yêu cầu.
- Với tác vụ nhỏ, triển khai trực tiếp và báo cáo; với thay đổi kiến trúc hoặc dữ liệu lớn, trình bày kế hoạch và xin phê duyệt.

## 8. Kiểm chứng trước khi báo hoàn thành

Sau khi code:

1. Chạy `dotnet build` cho project/solution chịu ảnh hưởng **nếu môi trường cho phép**.
2. Chạy test liên quan (`dotnet test` nếu có).
3. Kiểm tra diff để bảo đảm không sửa nhầm file ngoài phạm vi.
4. Kiểm tra và cập nhật tài liệu nếu **quyết định kiến trúc hoặc nghiệp vụ thực sự thay đổi**.
5. Tách rõ kết quả: **đã kiểm chứng bằng lệnh**, **đã thử thủ công**, **chưa thể kiểm chứng**.

**Không được:**

- Tuyên bố webcam, WebView2, máy in hoặc PDF hoạt động nếu chưa chạy thử được.
- Tuyên bố build/test pass nếu chưa chạy lệnh hoặc chưa nhận được kết quả.
- Coi kiểm thử giả lập là bằng chứng tương đương kiểm thử phần cứng thật.

## 9. Khuôn mẫu báo cáo cuối mỗi nhiệm vụ

Luôn trả lời **bằng tiếng Việt**, ngắn gọn theo các mục:

1. **Đã thực hiện:** những thay đổi và hành vi mới.
2. **File đã thay đổi:** tên file/nhóm file chính.
3. **Kiểm chứng:** lệnh đã chạy, kết quả; các bước kiểm tra thủ công (nếu có).
4. **Hạn chế/rủi ro:** những điều chưa thử được, phụ thuộc môi trường/hardware.

## 10. Nguồn quy tắc và cập nhật

- `AGENTS.md`: nguyên tắc làm việc chung của AI.
- `docs/PROJECT_OVERVIEW.md`: mục tiêu, phạm vi và tiêu chí nghiệm thu sản phẩm.
- `docs/ARCHITECTURE.md`: kiến trúc và các quyết định tích hợp kỹ thuật.
- `docs/CODING_STANDARDS.md`: cách viết code, tổ chức service, xử lý lỗi và kiểm thử.
- `docs/BUSINESS_RULES.md`: trạng thái phiên chụp, xử lý ảnh, xuất file và tiêu chí nghiệm thu.
- `docs/UI_UX_GUIDELINES.md`: giao diện kiosk/admin, responsive, cảm ứng và phản hồi lỗi.
- Chỉ tạo **thêm** tài liệu chuyên sâu khi tính năng tương ứng bắt đầu triển khai.

Nếu thay đổi yêu cầu khiến tài liệu không còn đúng, hãy đề xuất **chỉnh đúng phần liên quan** thay vì viết lại toàn bộ tài liệu.
