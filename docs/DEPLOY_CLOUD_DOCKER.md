# Triển khai Photobooth Cloud bằng Docker Compose

Tài liệu cho laptop Windows chạy Docker Desktop với WSL2 Linux containers. Giữ nguyên nghiệp vụ Cloud: .NET 10 lưu metadata vào PostgreSQL, ảnh JPEG/PNG vào thư mục file trên host. Không dùng MinIO hay S3.

## 1. Các dịch vụ

| Container | Vai trò | Dữ liệu |
|---|---|---|
| `cloud` | .NET 10 / Blazor / API QR | Bind mount `D:\PhotoboothData\Photos` → `/data/photos` |
| `postgres` | PostgreSQL 17 | Docker named volume `postgres_data` |
| `cloudflared` | Cloudflare Tunnel, profile `tunnel` | Không lưu ảnh, chỉ định tuyến |

Không công khai PostgreSQL ra host/Internet. Cloud chỉ bind cổng localhost `127.0.0.1:5180` khi chưa bật Tunnel.

## 2. Chuẩn bị Windows

Cài Docker Desktop, bật WSL2/Linux containers. Mở PowerShell tại thư mục gốc repository rồi chạy:

```powershell
New-Item -ItemType Directory -Force 'D:\PhotoboothData\Photos' | Out-Null
Copy-Item .env.example .env
# Chạy hai lần, tạo 2 giá trị KHÁC NHAU cho POSTGRES_PASSWORD và UPLOAD_API_KEY:
[Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
notepad .env
```

Trong file `.env`, cấu hình `POSTGRES_PASSWORD`, `UPLOAD_API_KEY`. API Key upload phải có ít nhất 32 ký tự; không dùng giá trị mẫu hoặc đưa khóa lên GitHub.

- `PHOTO_STORAGE_PATH=D:/PhotoboothData/Photos` là **đường dẫn trên Windows**. Khi chạy trong container, ứng dụng sẽ nhìn thấy cùng ảnh tại `/data/photos`.
- `CLOUD_PORT=5180` là cổng chỉ dành cho máy chủ local.
- `DB_MIGRATE_ON_STARTUP=true` chỉ phù hợp bước khởi tạo/thử nghiệm một instance; production nên áp dụng EF Core migrations trong bước triển khai riêng rồi đặt `false`.
- Mật khẩu PostgreSQL cần ổn định sau lần tạo database. Thay `POSTGRES_PASSWORD` trong .env khi volume đã tồn tại **không tự thay** mật khẩu của tài khoản database. Cần đổi mật khẩu database có kiểm soát trước.

## 3. Khởi động

```powershell
docker compose config --quiet
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 cloud
```

Kiểm tra trên laptop: **http://localhost:5180**.

Khi muốn theo dõi log trực tiếp:

```powershell
docker compose logs -f cloud
docker compose logs -f postgres
```

Nếu Cloud báo lỗi không kết nối được database, kiểm tra healthcheck PostgreSQL, mật khẩu trong `.env` và quyền truy cập volume. Nếu Cloud báo `Storage:RootPath` hoặc ghi file thất bại, xác nhận đường dẫn Windows đã tồn tại và Docker Desktop có quyền truy cập ổ D.

## 4. Kiểm tra dữ liệu tồn tại qua restart

1. Tạo một ảnh JPEG/PNG thử nghiệm (không dùng ảnh khách thật) và upload qua Photobooth Desktop hoặc API `POST /api/photos` với header `X-Api-Key`.
2. Kiểm tra file ảnh được ghi vào `D:\PhotoboothData\Photos`, nằm trong các thư mục con do Cloud tự tạo.
3. Thử dừng / tạo lại container:

   ```powershell
   docker compose down
   docker compose up -d
   ```

4. Mở lại link xem/tải ảnh và kiểm tra file vẫn tồn tại trên ổ D.
5. **Không chạy `docker compose down -v`:** lệnh này có thể xóa named volume PostgreSQL chứa metadata và Share Token của các QR cũ.

Dữ liệu được lưu ngoài container nhưng **không tự động có bản sao lưu**. Backup phải gồm **cả** ảnh và database PostgreSQL cùng một trạng thái nhất quán.

## 5. Kết nối Cloudflare Tunnel và tên miền (tùy chọn)

Không cần mở port router. Trên Cloudflare Zero Trust tạo Tunnel, lấy Tunnel Token và cấu hình Public Hostname:

- Hostname ví dụ: `photo.tenmien.vn`.
- Service URL bên trong Docker network: **`http://cloud:8080`**, không dùng localhost.
- Trong `.env`: `CLOUDFLARE_TUNNEL_TOKEN`, `PUBLIC_BASE_URL=https://photo.tenmien.vn`, `ALLOWED_HOSTS=localhost;photo.tenmien.vn`.

Sau đó chạy:

```powershell
docker compose --profile tunnel up -d
docker compose logs --tail=100 cloudflared
```

Photobooth Desktop sử dụng `cloud.baseUrl` là địa chỉ HTTPS công khai, `cloud.apiKey` trùng `UPLOAD_API_KEY` trên server. Cấu hình trực tiếp trên máy vận hành, không commit API Key thật.

Kiểm tra từ **điện thoại dùng mạng di động 4G/5G**, quét QR và tải ảnh. Cloud vẫn tạo link QR trước upload (offline-first); khi server offline, QR không tải được ảnh cho đến khi dịch vụ hoạt động lại.

## 6. An toàn dữ liệu và vận hành

- Ảnh dùng **bind mount** ra ổ Windows, PostgreSQL dùng **named volume**. Không xóa cả hai khi cập nhật container.
- Tạo lịch `pg_dump` và sao lưu thư mục ảnh ra **ổ đĩa khác** hoặc cloud backup, không chỉ một thư mục khác trên cùng ổ D.
- Ảnh có metadata trong PostgreSQL nhưng mất file không thể được khôi phục chỉ bằng database; ngược lại file còn mà metadata mất có thể làm QR cũ không truy cập được.
- Ảnh được chia sẻ qua QR có thể được xem bởi ai có URL. Cloud hiện chưa có đăng nhập người dùng, rate limiting và chính sách tự xóa ảnh hết hạn: thử nghiệm có kiểm soát trước khi đưa lên Internet phục vụ khách.
- Không công khai cổng PostgreSQL `5432` hoặc console quản trị database.
- Laptop phải không Sleep, nguồn điện và Internet ổn định; Docker Desktop/WSL2 cần khởi động sau khi reboot. `restart: unless-stopped` không tự thay thế việc khởi động Docker Engine.
- Cloud đang chạy user không đặc quyền (`$APP_UID`). Nếu permission bind mount của ổ D gây lỗi, kiểm tra quyền Docker Desktop/NTFS và log container, không mặc định đổi container sang root.

## 7. Những lệnh thường dùng

```powershell
docker compose ps
docker compose logs --tail=100 cloud
docker compose restart cloud
docker compose up -d --build
docker compose down
docker volume ls
```

Không dùng `docker compose down -v` trong quá trình vận hành khi chưa có bản backup và chủ đích xóa dữ liệu.

## 8. Kiểm chứng còn cần thực hiện

Các file đã được rà soát theo source Cloud hiện tại, nhưng phải chạy thực tế `docker compose config --quiet` và `docker compose up -d --build` trên máy Windows có Docker Engine. Cần xác minh thêm quyền ghi ổ D, EF migrations, upload/tải ảnh, QR, ngắt mạng và khởi động lại laptop.
