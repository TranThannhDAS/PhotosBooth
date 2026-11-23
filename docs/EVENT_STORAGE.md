# Lưu trữ theo sự kiện (Event Workspace)

> Áp dụng từ Phase 2.2.5–2.2.8 (19/11/2026). Từ 23/11/2026 **metadata nằm trong SQLite** (`%LOCALAPPDATA%\Photobooth\photobooth.db`), file ảnh vẫn nằm trong Event Workspace. Tài liệu dành cho người vận hành và lập trình viên.

## 1. Event Workspace là gì

Mỗi lần cho thuê Photobooth (đám cưới, sinh nhật, hội nghị...) dùng **một thư mục riêng** gọi là Event Workspace. Toàn bộ **file** của sự kiện nằm trong thư mục đó:

- ảnh gốc;
- snapshot template;
- các phiên bản ảnh thành phẩm và ảnh thu nhỏ.

**Metadata** (sự kiện, phiên, danh sách ảnh, phiên bản, dữ liệu chỉnh sửa, lịch sử xuất/in, link chia sẻ QR, trạng thái upload) nằm trong **SQLite** của máy: `%LOCALAPPDATA%\Photobooth\photobooth.db`. Database chỉ lưu đường dẫn **tương đối với thư mục sự kiện**, không lưu nội dung ảnh.

Dữ liệu của các sự kiện không bao giờ bị trộn lẫn. Thư mục do người vận hành chọn, **không hardcode trong code**. Có thể đặt ở bất kỳ ổ nào ghi được, tên có dấu tiếng Việt cũng được.

## 2. Chọn sự kiện

- Màn khởi động hiện khi mở ứng dụng và mỗi khi thoát chế độ quản trị. Đang phục vụ khách thì vào lại quản trị bằng nút "Quản trị" ở màn hình chào (cần PIN).
  - Ứng dụng tự mở lại sự kiện **mở gần nhất** (bảng `Events` trong SQLite).
  - Bảng `Events` cũng là **danh bạ các sự kiện đã mở trên máy** (mã, tên, thư mục, lần mở gần nhất) cho bộ lọc của Thư viện ảnh. Một thư mục chỉ thuộc một sự kiện.
  - **"Chọn thư mục sự kiện" / "Đổi sự kiện khác"** mở hộp thoại chọn thư mục của Windows. Hộp thoại có sẵn nút *New folder* để tạo thư mục mới, nên không phải gõ đường dẫn.
    - Thư mục **đã có** `event.json`: mở lại sự kiện cũ theo mã trong file (kể cả khi thư mục đã được di chuyển — thư mục mới được ghi vào database).
    - Thư mục **chưa có** `event.json`: hỏi tên sự kiện (mặc định là tên thư mục), tạo `event.json` (chỉ chứa mã sự kiện) và bản ghi trong SQLite.
  - `event.json` chỉ là **dấu nhận diện** thư mục (mã sự kiện); tên sự kiện nằm trong SQLite. Đổi tên sự kiện không sửa file trong thư mục.
  - **"Bắt đầu phục vụ khách"** chỉ bấm được khi sự kiện hợp lệ. Sau khi bấm, ứng dụng chuyển sang Kiosk.
  - **"Chế độ quản trị"** yêu cầu PIN. PIN được khai báo ở `AdminAccessService.Pin`.
- Trong Admin, tab **Sự kiện** cho đổi tên sự kiện ("Lưu và áp dụng"), đổi sang sự kiện khác, và mở thư mục bằng Explorer.
- Không đổi được sự kiện khi đang có phiên chụp chưa kết thúc. Ngoài ra, mỗi phiên chốt thư mục của mình ngay lúc tạo, nên dữ liệu không thể lẫn sang sự kiện khác.

### Kiểm tra khi mở sự kiện

| Tình huống | Hành vi |
|---|---|
| Thư mục không tồn tại (bị đổi tên/di chuyển, chưa cắm ổ) | Báo lỗi kèm đường dẫn cũ, có nút "Thử lại". **Không tự tạo lại thư mục trống.** |
| Không ghi được (thiếu quyền, ổ chỉ đọc) | Báo lỗi; không cho bắt đầu phục vụ. |
| Ổ đĩa còn dưới 200 MB | Báo "Ổ đĩa gần đầy"; không cho bắt đầu phục vụ. |
| `event.json` hỏng | Báo lỗi; **không sửa hay ghi đè** file này. |
| Mất thư mục giữa sự kiện | Không tạo được phiên mới; màn chào báo lỗi. Ảnh **không** được lưu sang thư mục khác. |

## 3. Cấu trúc thư mục

```text
{Thư mục sự kiện}/
├── event.json                         { eventId, createdAt } — dấu nhận diện thư mục
└── Sessions/
    └── 20261119-145223-4ae6062a/      yyyyMMdd-HHmmss (giờ máy) + 8 ký tự đầu SessionId
        ├── Template/
        │   ├── template.json          snapshot template lúc chụp
        │   └── assets/…               chỉ asset ảnh mà template dùng
        ├── Originals/
        │   └── photo-001-{PhotoId}.jpg     ảnh gốc, bất biến
        ├── Outputs/
        │   ├── final-v1.png           phiên bản thành phẩm (không bao giờ ghi đè)
        │   ├── final-v1.thumb.jpg     ảnh thu nhỏ cho thư viện ảnh
        │   └── final-v2.png …
        └── Drafts/                    bản ghép tạm khi đang chỉnh (tự dọn khi xong)
```

- Tên thư mục phiên luôn duy nhất. Nếu trùng tên thì dùng toàn bộ SessionId.
- Mọi file (ảnh, thành phẩm, snapshot) được ghi theo kiểu **ghi file tạm rồi đổi tên**; metadata ghi trong transaction của SQLite (chế độ WAL). Ứng dụng dừng đột ngột cũng không làm hỏng bản cũ.
- Thư mục sự kiện của bản JSON cũ (có `session.json`, `cloud-upload.json`) vẫn mở được làm sự kiện, nhưng **các phiên cũ không được chuyển sang database** và không hiện trong Thư viện ảnh. Ứng dụng không xóa các file cũ đó.
- Ứng dụng **không tự xóa** ảnh gốc, thành phẩm hay thư mục sự kiện. Chỉ các file `Drafts/composed-*.png` (bản ghép tạm, có thể tạo lại) được dọn khi phiên kết thúc hoặc khi lưu/hủy chỉnh sửa.

## 4. Metadata trong SQLite (`photobooth.db`)

Schema tạo/cập nhật bằng **EF Core Migrations** khi ứng dụng khởi động (`Persistence/Migrations` trong `Photobooth.Infrastructure`). Mọi đường dẫn file là **tương đối với thư mục sự kiện**, dùng dấu `/` (ví dụ `Sessions/20261119-145223-4ae6062a/Originals/photo-001-….jpg`); khi đọc luôn được kiểm tra không vượt ra ngoài thư mục sự kiện. Riêng `Exports.FilePath` là file đã xuất ra ngoài sự kiện, chỉ để tham khảo.

| Bảng | Nội dung | Khóa / ràng buộc |
|---|---|---|
| `Events` | Mã, tên, thư mục (tuyệt đối), ngày tạo, lần mở gần nhất | PK `Id` (= mã trong event.json); `FolderPath` duy nhất (không phân biệt hoa/thường) |
| `Sessions` | Thư mục phiên (tương đối), trạng thái `inProgress/completed/cancelled`, template (`TemplateId`, `TemplateName`, `TemplateFile` = snapshot), `Mirror`, `Filter`, `SelectedPhotoIds`, `SlotCrops` (JSON nhỏ theo **chỉ số ô**), thời điểm | PK `Id`; FK `EventId` → `Events`; (`EventId`, `DirectoryPath`) duy nhất |
| `Photos` | Ảnh gốc (kể cả ảnh bị thay khi chụp lại): thứ tự, vị trí, kích thước, file, cờ `Mirrored` | PK `Id`; FK `SessionId`; (`SessionId`, `Sequence`) duy nhất |
| `OutputVersions` | Phiên bản thành phẩm: file, ảnh thu nhỏ, kích thước, DPI, template, bộ lọc, ảnh nguồn, crop, **`ShareToken`, `ShareUrl`** | PK `Id`; FK `SessionId`; (`SessionId`, `Version`) duy nhất; `ShareToken` duy nhất |
| `CloudUploads` | Trạng thái upload ảnh chia sẻ: `pending/uploading/uploaded/failed`, số lần thử, thời điểm, lỗi gần nhất | PK = FK `OutputVersionId` (1-1 với phiên bản đã chia sẻ) |
| `Exports` | Định dạng, đường dẫn, thời điểm, phiên bản đã xuất | FK `SessionId` |
| `PrintJobs` | `Id`, phiên bản, file, số bản, máy in, máy in thật/giả lập, trạng thái, thời điểm, lỗi | PK `Id`; FK `SessionId` |

- Mọi khóa ngoại là **Restrict**: không bao giờ xóa dây chuyền dữ liệu nghiệp vụ.
- Ứng dụng chỉ **thêm/cập nhật**; không xóa phiên, ảnh, phiên bản hay lịch sử.
- `ShareToken` một khi đã ghi thì **không bao giờ bị đổi hay xóa**, kể cả khi lưu một ảnh chụp trạng thái cũ chưa có token.
- `CloudUploads` tách riêng vì do tiến trình upload nền ghi, độc lập với lúc lưu phiên. `uploading` còn lại sau khi ứng dụng dừng đột ngột được hiểu là `pending`. Thiếu bản ghi `CloudUploads` thì ảnh vẫn được tìm thấy (qua `ShareToken`) và upload lại; Cloud nhận diện ảnh trùng nên không tạo bản ghi trùng.

## 5. Khi nào dữ liệu được ghi

- **Sau mỗi ảnh chụp thành công:** ghi metadata phiên vào SQLite. Lần ghi đầu tiên còn tạo snapshot template và asset (file).
- **Khi xác nhận thành phẩm:**
  - Các thời điểm xác nhận: Kiosk bấm "Hoàn tất", "Lưu ảnh" hoặc "In"; Admin bấm "Lưu phiên bản mới".
  - Bản ghép đang xem được sao chép thành `final-v{N}.png`, kèm ảnh thu nhỏ.
  - Nếu đã có phiên bản tạo từ đúng các đầu vào này (ảnh, crop, bộ lọc) thì dùng lại, không tạo bản trùng.
- **Khi xuất file, tạo hoặc cập nhật Print Job, kết thúc phiên:** ghi metadata phiên.
- **Khi khách bấm "Hoàn tất"** (Cloud đã cấu hình): sinh Share Token cho phiên bản vừa hoàn tất và ghi vào `OutputVersions` **trước** khi hiển thị QR. Không ghi được thì không hiển thị QR.
- **Mỗi lần đổi trạng thái upload:** ghi bảng `CloudUploads`.
- Mỗi lần lưu phiên là **một transaction** (phiên + ảnh mới + phiên bản + xuất + in). Ghi thất bại (ổ đầy, database lỗi) thì ảnh đã lưu vẫn còn; phiên được đánh dấu chưa lưu được và lần ghi sau ghi lại toàn bộ.

## 6. Mở lại và chỉnh sửa phiên cũ (Admin)

1. Tab **Thư viện ảnh** liệt kê các phiên, mới nhất trước, mỗi trang 24 phiên. Ô **"Sự kiện"** lọc theo: sự kiện hiện tại (mặc định), một sự kiện khác đã mở trên máy này, hoặc **Tất cả sự kiện** (mỗi thẻ ghi tên sự kiện). Lọc chỉ để xem: không đổi sự kiện Kiosk đang lưu ảnh. Mở/chỉnh sửa lại/in lại phiên của sự kiện khác thì phiên bản mới và lịch sử in được ghi vào đúng thư mục của sự kiện đó. Sự kiện không truy cập được (thư mục bị đổi tên, ổ đĩa chưa cắm) hiện mờ và bị bỏ qua trong "Tất cả". Giao diện chỉ truyền **mã sự kiện**, ứng dụng tự tra thư mục trong database và kiểm tra lại `event.json` — không nhận đường dẫn tùy ý.
   - Chỉ đọc metadata (SQLite) và ảnh thu nhỏ, không tải ảnh gốc.
   - Phiên lỗi được ghi chú rõ: chưa có thành phẩm, thiếu file thành phẩm (hoặc đường dẫn trong database không hợp lệ).
2. Chọn một phiên để xem chi tiết:
   - xem đúng file phiên bản đang chọn, có nút phóng to;
   - xem thông tin phiên và danh sách phiên bản (v1, v2…);
   - xuất ảnh, in, in lại theo **đúng phiên bản đang chọn**;
   - xem lịch sử in;
   - xem **mã QR** và trạng thái đồng bộ của phiên bản (nếu đã chia sẻ), hoặc **Tạo mã QR chia sẻ** cho phiên bản chưa có (token mới, không ảnh hưởng QR của phiên bản khác).
3. **Chỉnh sửa từ vN:**
   - Khôi phục ảnh nguồn, mirror, Manual Crop và bộ lọc của vN, rồi dùng lại trình chỉnh crop, bộ lọc và Photo Composer của Kiosk.
   - **Lưu phiên bản mới** tạo `final-v{N+1}.png`; các bản cũ giữ nguyên.
   - **Hủy chỉnh sửa** quay về đúng vN: không tạo phiên bản, không đổi dữ liệu đã lưu.
4. Phiên thiếu ảnh gốc hoặc snapshot template vẫn **xem, xuất và in lại** được file đã lưu. Chỉ chức năng chỉnh sửa bị khóa, kèm lý do.

Chỉ chế độ quản trị mới truy cập được thư viện ảnh. Khách ở Kiosk chỉ thấy phiên của chính mình.

## 7. Di chuyển hoặc sao chép thư mục sự kiện

- **Di chuyển trên cùng máy** (đổi ổ, đổi tên thư mục): Admin → **Sự kiện** → **Đổi sự kiện khác**, rồi chọn thư mục ở vị trí mới. Ứng dụng nhận ra sự kiện qua `event.json`, cập nhật thư mục trong database; mọi phiên, phiên bản, lịch sử và link chia sẻ vẫn dùng được vì đường dẫn là tương đối.
- **Sang máy khác:** metadata nằm trong `photobooth.db` của máy cũ, nên chép riêng thư mục sự kiện chỉ mang theo **file**. Máy mới nhận ra sự kiện (cùng mã) nhưng Thư viện ảnh không có các phiên cũ. Muốn chuyển cả lịch sử thì chép kèm `photobooth.db` (khi ứng dụng đã đóng; file này sẽ **thay toàn bộ** dữ liệu của máy mới). Đồng bộ nhiều máy chưa thuộc phạm vi.
- Máy mới không cần có template trong thư viện, vì mỗi phiên đã mang theo snapshot template và asset.
