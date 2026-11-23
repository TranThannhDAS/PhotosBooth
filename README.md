# PhotosBooth

Solution `Photobooth.slnx` gồm hai ứng dụng chạy/deploy độc lập:

| Project | Mô tả | Tài liệu |
| --- | --- | --- |
| `src/Photobooth.Desktop` | Ứng dụng Windows (WPF + BlazorWebView) chụp, ghép, xuất ảnh — offline-first | `docs/` |
| `src/Photobooth.Cloud` | Web lưu trữ và chia sẻ ảnh qua QR (Minimal API + Blazor Static SSR) — upload chỉ được bảo vệ bằng API Key tùy chọn của thiết bị | [`src/Photobooth.Cloud/README.md`](src/Photobooth.Cloud/README.md) |

```powershell
dotnet build Photobooth.slnx
dotnet test Photobooth.slnx
```

Lưu trữ metadata (chi tiết: `docs/ARCHITECTURE.md` §9.1):

- **Desktop:** SQLite `%LOCALAPPDATA%\Photobooth\photobooth.db`, schema tự tạo bằng EF Core Migrations khi mở ứng dụng; ảnh vẫn là file trong thư mục sự kiện.
- **Cloud:** PostgreSQL (`ConnectionStrings:DefaultConnection`, mật khẩu đặt qua User Secrets/biến môi trường); ảnh là file trong `Storage:RootPath`.
- **Cấu hình Desktop:** `src/Photobooth.Desktop/appsettings.json` được copy cạnh `Photobooth.exe` khi build/publish. `%LOCALAPPDATA%\Photobooth\settings.json` cũ không còn được đọc; không commit API Key thật.
- Test Cloud và test end-to-end cần PostgreSQL local (xem README của Cloud); không có thì các test đó được bỏ qua.
