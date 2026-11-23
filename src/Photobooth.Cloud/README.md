# Photobooth Cloud — lưu trữ và chia sẻ ảnh

Ứng dụng web **độc lập** với Desktop, nằm trong cùng Solution `Photobooth.slnx`.

- Công nghệ: .NET 10, Blazor Web App (**Static SSR**, không Interactive Server, không JavaScript) và ASP.NET Core Minimal API.
- Lưu trữ: ảnh là **file trên ổ đĩa của server**; **metadata ảnh trong PostgreSQL** (EF Core + Npgsql). Không lưu nội dung ảnh vào database.
- Phụ thuộc:
  - `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.4 (giấy phép PostgreSQL License).
  - `Microsoft.EntityFrameworkCore.Design` (chỉ dùng lúc thiết kế).
  - Không tham chiếu project Desktop/Application/Infrastructure.
- Phase Cloud 2.1 — **QR Offline-First**:
  - Desktop tự sinh Share Token và in QR `{BaseUrl}/s/{token}` ngay khi khách hoàn tất, kể cả khi mất mạng.
  - Desktop upload ảnh thành phẩm kèm `shareToken` ở chế độ chạy nền.
  - Khách quét QR trước khi ảnh lên server sẽ thấy trang chờ.

> ⚠️ **Bảo vệ upload:** đặt `Upload:ApiKey` (ít nhất 32 ký tự ngẫu nhiên) để `POST /api/photos` yêu cầu header `X-Api-Key`. Đây là khóa của **thiết bị**, không phải hệ thống đăng nhập người dùng.
> **Không có khóa thì không được mở API upload ra Internet công khai.** Khi đó chỉ chạy trong mạng nội bộ/máy phát triển, hoặc đặt sau reverse proxy chặn `POST /api/photos` từ bên ngoài (xem [Triển khai](#triển-khai)).

## Chạy khi phát triển

Cần PostgreSQL chạy local. Mật khẩu **không** nằm trong source: đặt connection string bằng .NET User Secrets (một lần trên mỗi máy):

```powershell
cd src/Photobooth.Cloud
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<mật khẩu>"
dotnet run --launch-profile http
# → http://localhost:5180 (Development: tự áp dụng migrations khi khởi động)
```

- Môi trường `Development`: nếu không cấu hình `Storage:RootPath`, ảnh được lưu ở `%LOCALAPPDATA%\Photobooth.Cloud\Photos`.
- Dùng thư mục khác: `dotnet run -- --Storage:RootPath=D:/PhotoboothData/Photos`.

## Cấu hình

Cấu hình đọc theo thứ tự ưu tiên tăng dần: `appsettings.json`, `appsettings.{Environment}.json`, biến môi trường, tham số dòng lệnh.

```json
{
  "Storage": {
    "RootPath": "D:/PhotoboothData/Photos",
    "MaxFileSizeMB": 20
  },
  "PublicBaseUrl": "https://photo.example.com",
  "AllowedHosts": "photo.example.com"
}
```

| Khóa | Bắt buộc | Ý nghĩa |
| --- | --- | --- |
| `Storage:RootPath` | Có (ngoài `Development`) | Đường dẫn **tuyệt đối** tới thư mục lưu ảnh. Phải nằm **ngoài** thư mục ứng dụng/publish. Thiếu, tương đối hoặc nằm trong thư mục publish thì ứng dụng **không khởi động**. |
| `Storage:MaxFileSizeMB` | Không (mặc định 20) | Dung lượng tối đa mỗi ảnh, từ 1 đến 100 MB. |
| `PublicBaseUrl` | Nên có khi triển khai | Địa chỉ công khai dùng để tạo link trả về, có thể kèm đường dẫn con (ví dụ `https://example.com/photobooth`). Bỏ trống thì link lấy theo request; chỉ nên bỏ trống khi chạy nội bộ. |
| `AllowedHosts` | Nên có khi triển khai | Tên miền hợp lệ, để chặn header `Host` giả mạo. |
| `ConnectionStrings:DefaultConnection` | Có | PostgreSQL chứa metadata ảnh. Thiếu thì ứng dụng **không khởi động**. Phát triển: User Secrets. Triển khai: biến môi trường `ConnectionStrings__DefaultConnection` (nên dùng database và tài khoản riêng cho production, không dùng `postgres`). |
| `Database:MigrateOnStartup` | Không (mặc định `false`; `true` trong `appsettings.Development.json`) | Tự áp dụng EF Core Migrations khi khởi động. Production nên để `false` và cập nhật schema chủ động (xem [Database](#database-postgresql)). |
| `Upload:ApiKey` | Bắt buộc nếu mở ra Internet | API Key của thiết bị cho `POST /api/photos` (header `X-Api-Key`), ít nhất 32 ký tự. Đặt bằng biến môi trường `Upload__ApiKey` hoặc file cấu hình trên server, **không** đưa vào source code. Bỏ trống thì upload không được bảo vệ (ứng dụng ghi cảnh báo khi khởi động). |

Ví dụ dùng biến môi trường (dấu `__` thay cho `:`):

```powershell
$env:Storage__RootPath = "D:\PhotoboothData\Photos"
$env:Storage__MaxFileSizeMB = "20"
$env:PublicBaseUrl = "https://photo.example.com"
```

### Thư mục lưu trữ

- Bố cục:
  - Ảnh upload thường: `{RootPath}/{2 ký tự đầu của ID}/{ID}.jpg|png`.
  - Ảnh chia sẻ QR: `{RootPath}/shares/{2 ký tự đầu hash}/{SHA-256(token)}.jpg|png`. Token không nằm trong tên file, và hai token chỉ khác hoa/thường không bao giờ trùng file trên NTFS.
  - Tên file **chỉ do server đặt**, không dùng tên file client gửi.
  - Phần mở rộng lấy theo **định dạng thực tế** của nội dung file.
- `{RootPath}/.incoming/` chứa file tạm trong lúc tải lên; file tạm bị xóa khi tải xong hoặc lỗi.
- Ảnh được ghi vào file tạm rồi **đổi tên không ghi đè**. Không bao giờ có file dở dang mang ID hợp lệ, và không bao giờ đè ảnh đã có.
- Ảnh nằm ngoài thư mục publish, nên restart hoặc deploy lại **không làm mất ảnh**.
- **Phân quyền:** chỉ tài khoản chạy dịch vụ được đọc/ghi thư mục này.
  - Linux: ứng dụng tự tạo thư mục với quyền `700` và file với quyền `600`.
  - Windows: cấp quyền bằng ACL, ví dụ với IIS App Pool:
    ```powershell
    icacls "D:\PhotoboothData\Photos" /inheritance:r /grant:r "IIS AppPool\Photobooth:(OI)(CI)M" "Administrators:(OI)(CI)F"
    ```
  - Không chia sẻ thư mục qua mạng và không cấu hình web server phục vụ trực tiếp thư mục này.
- Sao lưu: chép nguyên thư mục `RootPath` **và** sao lưu database PostgreSQL (`pg_dump`) cùng thời điểm. Ảnh chỉ được coi là có khi có cả metadata lẫn file.

## Database (PostgreSQL)

- **Bảng `photos`:**

  | Cột | Kiểu | Ghi chú |
  | --- | --- | --- |
  | `id` | varchar(26) | Khóa chính, ID ảnh do server sinh |
  | `share_token` | varchar(32), null | Share Token do Desktop sinh; **unique** |
  | `storage_path` | varchar(200) | Đường dẫn file tương đối với `Storage:RootPath`; unique; không lộ ra API |
  | `format`, `content_type` | varchar | `jpeg`/`png`, `image/jpeg`/`image/png` (định dạng thực tế) |
  | `size_bytes` | bigint | |
  | `sha256` | char(64) | Chống upload trùng và ghi đè bằng cùng token |
  | `uploaded_at` | timestamptz | |

- **Tạo/cập nhật schema** bằng EF Core Migrations (`Data/Migrations`):
  ```powershell
  dotnet ef database update --project src/Photobooth.Cloud --startup-project src/Photobooth.Cloud --context CloudDbContext
  ```
  `dotnet ef` đọc connection string từ User Secrets hoặc biến môi trường `ConnectionStrings__DefaultConnection`. Production có thể dùng migration bundle: `dotnet ef migrations bundle --project src/Photobooth.Cloud`.
- **Đổi entity:** `dotnet ef migrations add <TenThayDoi> --project src/Photobooth.Cloud --startup-project src/Photobooth.Cloud --context CloudDbContext --output-dir Data/Migrations`, xem lại file sinh ra, chạy test, rồi cập nhật database trước khi deploy.
- **Thứ tự ghi:**
  1. Nhận file vào file tạm.
  2. Đổi tên không ghi đè vào vị trí cuối.
  3. Ghi metadata. Ghi metadata lỗi thì **xóa file vừa đặt** (không để file mồ côi) và API trả `503`; Desktop sẽ thử lại cùng token.
- **Metadata còn mà file mất:**
  - API `file`/`download`/`link` trả `410 Gone`; trang hiển thị "Ảnh tạm thời không khả dụng" (không bao giờ báo tải thành công).
  - Upload lại đúng nội dung với cùng token sẽ khôi phục file.
- **File có trên đĩa nhưng không có metadata:** coi như chưa công bố (404 / trang chờ).

## API

ID ảnh gồm **26 ký tự** `a-z` và `2-7`, sinh ngẫu nhiên khoảng 130 bit nên khó đoán. ID sai định dạng được xử lý như ảnh không tồn tại.

| Phương thức | Đường dẫn | Kết quả |
| --- | --- | --- |
| `POST` | `/api/photos` | Tải ảnh lên. Gửi `multipart/form-data` có **đúng một** file ở trường `file`, kèm `shareToken` (tùy chọn). Thành công: `201 Created` (header `Location` = URL file) cùng JSON link. Có `shareToken` mà ảnh trùng đúng nội dung đã có: `200 OK` cùng link cũ, không tạo file mới. |
| `GET` | `/api/photos/{id}/link` | `200`: JSON `{ id, viewUrl, fileUrl, downloadUrl }`. |
| `GET` | `/api/photos/{id}/file` | `200`: nội dung ảnh, `Content-Type` `image/jpeg` hoặc `image/png`. |
| `GET` | `/api/photos/{id}/download` | `200`: file gốc kèm `Content-Disposition: attachment; filename=photobooth-{id}.jpg`. |
| `GET` | `/photos/{id}` | Trang xem ảnh cho khách: ảnh đúng tỉ lệ, responsive, nút "Tải ảnh xuống". Không có ảnh thì trả `404` kèm thông báo. |
| `GET` | `/s/{shareToken}` | Trang mở từ QR. Đã có ảnh: ảnh + nút "Tải ảnh xuống". Chưa có ảnh: trang chờ "Ảnh của bạn đang được chuẩn bị." (HTTP 200, tự làm mới sau 8 giây, `Cache-Control: no-store`). Token sai định dạng: `404`. |

`{id}` của các API `link`/`file`/`download` nhận cả ID ảnh lẫn Share Token. Với Share Token, `viewUrl` là `/s/{token}`.

### Share Token (hợp đồng Desktop ↔ Cloud)

- **Định dạng:** đúng **32 ký tự Base64URL** (`A-Z a-z 0-9 - _`, không padding) = 24 byte = 192 bit. Desktop sinh bằng `RandomNumberGenerator`; Cloud không bao giờ tự sinh token thay.
- **Trùng nội dung** (cùng token, so SHA-256): `200` với đúng link cũ.
- **Khác nội dung:** `409 Conflict`; ảnh đã chia sẻ giữ nguyên.
- **Có trường `shareToken` nhưng sai định dạng:** `400`; server không âm thầm chuyển sang sinh ID mới.
- **Phản hồi:** `id` = token. Desktop kiểm tra điều này để phát hiện Cloud bản cũ không hỗ trợ token.
- **Giới hạn MVP:** Cloud chưa đăng ký token trước, nên không phân biệt được token "chưa upload" với token chưa từng được tạo (cả hai đều hiện trang chờ).

Ví dụ JSON trả về:

```json
{
  "id": "ml67zrkakto7efltn462depgxd",
  "viewUrl": "https://photo.example.com/photos/ml67zrkakto7efltn462depgxd",
  "fileUrl": "https://photo.example.com/api/photos/ml67zrkakto7efltn462depgxd/file",
  "downloadUrl": "https://photo.example.com/api/photos/ml67zrkakto7efltn462depgxd/download"
}
```

Lỗi trả về dạng `application/problem+json`. Trường `title` là thông báo tiếng Việt.

| Mã | Khi nào |
| --- | --- |
| `400` | Thiếu trường `file`, gửi nhiều hơn một file, file rỗng, `shareToken` sai định dạng hoặc dữ liệu multipart hỏng. |
| `401` | Đã cấu hình `Upload:ApiKey` mà request thiếu hoặc sai header `X-Api-Key`. |
| `409` | `shareToken` đã gắn với một ảnh có nội dung khác. |
| `404` | Không có ảnh với ID này, hoặc ID sai định dạng. |
| `410` | Có metadata nhưng file ảnh không còn trên máy chủ. |
| `503` | Database tạm thời không truy cập được (upload: file vừa lưu đã được dọn, thử lại sau). |
| `413` | Ảnh vượt `MaxFileSizeMB`. |
| `415` | Không phải `multipart/form-data`, hoặc nội dung không phải JPEG/PNG. |

**Kiểm tra định dạng thực tế:**
- Server đọc các byte đầu file (magic bytes). JPEG phải có `FF D8 FF` cùng một marker hợp lệ. PNG phải có chữ ký 8 byte cùng chunk `IHDR` với kích thước lớn hơn 0.
- `Content-Type` và tên file client khai báo **không** được tin.
- Giới hạn: server không giải mã toàn bộ ảnh. File có header đúng nhưng phần sau bị hỏng vẫn được nhận.

**Giới hạn dung lượng:** áp dụng ở ba tầng — Kestrel (toàn request), bộ đọc form, và bộ đếm byte khi ghi file.
- Request có `Content-Length` vượt giới hạn bị từ chối **trước khi** server đọc phần thân.
- Client nên gửi header `Expect: 100-continue` để nhận được mã `413` rõ ràng. Nếu không, client có thể chỉ thấy kết nối bị ngắt.

### Thử bằng curl

```powershell
# Tải ảnh lên
curl.exe -F "file=@C:/path/anh.jpg" http://localhost:5180/api/photos

# Lấy link, xem, tải xuống
curl.exe http://localhost:5180/api/photos/<id>/link
curl.exe -o anh.jpg http://localhost:5180/api/photos/<id>/file
curl.exe -OJ http://localhost:5180/api/photos/<id>/download

# Mở trang xem ảnh trên trình duyệt hoặc điện thoại (cùng mạng)
start http://localhost:5180/photos/<id>

# Upload theo Share Token (như Desktop): gửi lại lần 2 → 200, cùng link
curl.exe -F "file=@C:/path/final-v1.png" -F "shareToken=AbCdEfGhIjKlMnOpQrStUvWxYz012-_9" -H "X-Api-Key: <khóa>" http://localhost:5180/api/photos
start http://localhost:5180/s/AbCdEfGhIjKlMnOpQrStUvWxYz012-_9

# File không hợp lệ → 415
curl.exe -F "file=@C:/path/khong-phai-anh.txt;type=image/jpeg" http://localhost:5180/api/photos
```

## Kiểm thử tự động

```powershell
dotnet test tests/Photobooth.Cloud.Tests
```

Test chạy **ứng dụng thật** (Kestrel, cổng ngẫu nhiên trên `127.0.0.1`) với **PostgreSQL thật**:
- Mỗi test dùng một thư mục lưu trữ tạm và một database riêng `photobooth_test_*`; database được tạo bằng migrations và tự xóa khi xong.
- Connection string lấy từ biến môi trường `PHOTOBOOTH_TEST_POSTGRES`, nếu không có thì từ User Secrets ở trên. Không có PostgreSQL thì các test cần database được **bỏ qua** (Skipped) kèm lý do. Các nội dung được kiểm tra:
- Upload JPEG/PNG và lưu đúng từng byte gốc.
- Link (kể cả khi cấu hình `PublicBaseUrl`).
- `Content-Type` của file ảnh, `Content-Disposition` của file tải xuống.
- Trả `404` khi ID không tồn tại hoặc sai định dạng (kể cả ID chữ hoa hay chuỗi path traversal).
- Từ chối file không phải ảnh, GIF, file rỗng và file quá dung lượng.
- Bỏ qua tên file client gửi.
- Không ghi đè ảnh khi trùng ID.
- Ảnh còn nguyên sau khi restart server.
- Trang xem ảnh và trang 404.
- Cấu hình sai thì không khởi động được.

## Triển khai

1. Publish:
   ```powershell
   dotnet publish src/Photobooth.Cloud -c Release -o <thư-mục-publish>
   ```
2. Tạo thư mục dữ liệu **ngoài** thư mục publish và phân quyền như mục [Thư mục lưu trữ](#thư-mục-lưu-trữ).
3. Cấu hình `Storage:RootPath`, `PublicBaseUrl` và `AllowedHosts`, bằng `appsettings.Production.json` hoặc biến môi trường.
4. Chạy `dotnet Photobooth.Cloud.dll`, đặt sau reverse proxy (IIS, Nginx, Caddy…) có HTTPS.
   - Proxy cần cho phép body đủ lớn. IIS mặc định giới hạn khoảng 28,6 MB (`maxAllowedContentLength`); Nginx mặc định 1 MB (`client_max_body_size`).
   - **Chặn `POST /api/photos` từ Internet** khi chưa có xác thực. Ví dụ với Nginx:
     ```nginx
     location = /api/photos { allow 10.0.0.0/8; allow 192.168.0.0/16; deny all; proxy_pass http://127.0.0.1:5000; }
     ```
   - Các đường dẫn xem/tải (`GET`) có thể mở công khai. Ai có link đều xem được ảnh; giới hạn truy cập dựa vào việc ID khó đoán.

## Cấu hình phía Desktop

Chỉnh mục `cloud` trong `src/Photobooth.Desktop/appsettings.json` (khi chạy/publish: file nằm cạnh `Photobooth.exe`), rồi khởi động lại ứng dụng. Không còn đọc `%LOCALAPPDATA%\Photobooth\settings.json`:

```json
{
  "cloud": {
    "enabled": true,
    "baseUrl": "https://photo.example.com",
    "uploadTimeoutSeconds": 60,
    "apiKey": "<trùng Upload:ApiKey của Cloud, nếu có>"
  }
}
```

Không commit API Key thật lên GitHub; chỉ bổ sung khóa trên máy vận hành hoặc dùng biến thể cấu hình không được theo dõi bởi Git. Trên máy cài trong `Program Files`, việc sửa file cạnh `.exe` có thể cần quyền quản trị.

Trạng thái cấu hình và đồng bộ xem ở Admin → **Chia sẻ QR**. Chi tiết nghiệp vụ: `docs/BUSINESS_RULES.md` §4.7. Lưu trữ: `docs/EVENT_STORAGE.md` §4.1.

## Chưa làm ở phase này

- Chưa có: đăng nhập/phân quyền người dùng, quản lý sự kiện/phiên, trang quản trị Cloud, R2/S3, đăng ký token trước khi upload.
- Chưa tự xóa ảnh hết hạn và chưa có giới hạn tần suất (rate limiting).
- Tất cả sẽ được bổ sung ở các phase sau.
