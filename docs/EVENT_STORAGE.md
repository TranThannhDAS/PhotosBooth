# Lưu trữ theo sự kiện (Event Workspace)

> Áp dụng từ Phase 2.2.5–2.2.8 (19/11/2026). Tài liệu dành cho người vận hành và lập trình viên.

## 1. Event Workspace là gì

Mỗi lần cho thuê Photobooth (đám cưới, sinh nhật, hội nghị...) dùng **một thư mục riêng** gọi là Event Workspace. Toàn bộ dữ liệu của sự kiện nằm trong thư mục đó:

- ảnh gốc;
- snapshot template;
- các phiên bản ảnh thành phẩm;
- dữ liệu chỉnh sửa;
- lịch sử in.

Dữ liệu của các sự kiện không bao giờ bị trộn lẫn. Thư mục do người vận hành chọn, **không hardcode trong code**. Có thể đặt ở bất kỳ ổ nào ghi được, tên có dấu tiếng Việt cũng được.

## 2. Chọn sự kiện

- Màn khởi động hiện khi mở ứng dụng và mỗi khi thoát chế độ quản trị. Đang phục vụ khách thì vào lại quản trị bằng nút "Quản trị" ở màn hình chào (cần PIN).
  - Ứng dụng tự mở lại sự kiện dùng lần trước, được ghi nhớ trong `%LOCALAPPDATA%\Photobooth\app-state.json`.
  - `app-state.json` cũng giữ **danh bạ các sự kiện đã mở trên máy** (`knownEvents`: mã sự kiện, tên, thư mục, lần mở gần nhất) cho bộ lọc của Thư viện ảnh. Danh bạ chỉ chứa đường dẫn, không chứa ảnh; mỗi mã sự kiện một mục (thư mục mới nhất thắng).
  - **"Chọn thư mục sự kiện" / "Đổi sự kiện khác"** mở hộp thoại chọn thư mục của Windows. Hộp thoại có sẵn nút *New folder* để tạo thư mục mới, nên không phải gõ đường dẫn.
    - Thư mục **đã có** `event.json`: mở lại sự kiện cũ.
    - Thư mục **chưa có** `event.json`: hỏi tên sự kiện (mặc định là tên thư mục) rồi tạo `event.json`.
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
├── event.json                         { eventId, name, createdAt }
└── Sessions/
    └── 20261119-145223-4ae6062a/      yyyyMMdd-HHmmss (giờ máy) + 8 ký tự đầu SessionId
        ├── session.json               metadata của phiên
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
- Mọi file metadata được ghi theo kiểu **ghi file tạm rồi đổi tên**. Ứng dụng dừng đột ngột cũng không làm hỏng bản cũ.
- Ứng dụng **không tự xóa** ảnh gốc, thành phẩm hay thư mục sự kiện. Chỉ các file `Drafts/composed-*.png` (bản ghép tạm, có thể tạo lại) được dọn khi phiên kết thúc hoặc khi lưu/hủy chỉnh sửa.

## 4. Nội dung `session.json`

Mọi đường dẫn đều **tương đối với thư mục phiên**. Riêng `exports[].filePath` là file đã xuất ra ngoài sự kiện, chỉ để tham khảo.

| Thuộc tính | Ý nghĩa |
|---|---|
| `sessionId`, `eventId`, `createdAt`, `updatedAt` | Định danh, thời điểm |
| `status` | `inProgress` / `completed` / `cancelled` (thông tin) |
| `templateId`, `templateName`, `templateFile` | Template đã dùng và vị trí snapshot |
| `mirror`, `photos[]` | Ảnh gốc (kể cả ảnh bị thay khi chụp lại), cờ `mirrored` từng ảnh |
| `selectedPhotoIds[]` | Ảnh đang chọn cho từng `photoIndex` |
| `filter`, `slotCrops[]` | Bộ lọc và Manual Crop hiện tại (theo **chỉ số ô**, `null` = Auto Crop) |
| `versions[]` | Mỗi phiên bản: `file`, `thumbnail`, kích thước, DPI, `filter`, `photoIds`, `slotCrops`, `createdAt` |
| `exports[]` | Định dạng, đường dẫn, thời điểm, phiên bản đã xuất |
| `printJobs[]` | `printJobId`, `version`, `file`, `copies`, `printerName`, `isPhysicalPrinter`, `status`, thời điểm, lỗi |

Không có `schemaVersion`. Thuộc tính thiếu thì dùng giá trị mặc định; thuộc tính lạ được bỏ qua.

## 5. Khi nào dữ liệu được ghi

- **Sau mỗi ảnh chụp thành công:** ghi `session.json`. Lần ghi đầu tiên còn tạo snapshot template và asset.
- **Khi xác nhận thành phẩm:**
  - Các thời điểm xác nhận: Kiosk bấm "Hoàn tất", "Lưu ảnh" hoặc "In"; Admin bấm "Lưu phiên bản mới".
  - Bản ghép đang xem được sao chép thành `final-v{N}.png`, kèm ảnh thu nhỏ.
  - Nếu đã có phiên bản tạo từ đúng các đầu vào này (ảnh, crop, bộ lọc) thì dùng lại, không tạo bản trùng.
- **Khi xuất file, tạo hoặc cập nhật Print Job, kết thúc phiên:** ghi `session.json`.
- Ghi `session.json` thất bại (ổ đầy, mất quyền) thì ảnh đã lưu vẫn còn. Lần ghi sau sẽ ghi lại toàn bộ.

## 6. Mở lại và chỉnh sửa phiên cũ (Admin)

1. Tab **Thư viện ảnh** liệt kê các phiên, mới nhất trước, mỗi trang 24 phiên. Ô **"Sự kiện"** lọc theo: sự kiện hiện tại (mặc định), một sự kiện khác đã mở trên máy này, hoặc **Tất cả sự kiện** (mỗi thẻ ghi tên sự kiện). Lọc chỉ để xem: không đổi sự kiện Kiosk đang lưu ảnh. Mở/chỉnh sửa lại/in lại phiên của sự kiện khác thì phiên bản mới và lịch sử in được ghi vào đúng thư mục của sự kiện đó. Sự kiện không truy cập được (thư mục bị đổi tên, ổ đĩa chưa cắm) hiện mờ và bị bỏ qua trong "Tất cả". Giao diện chỉ truyền **mã sự kiện**, ứng dụng tự tra thư mục trong danh bạ và kiểm tra lại `event.json` — không nhận đường dẫn tùy ý.
   - Chỉ đọc `session.json` và ảnh thu nhỏ, không tải ảnh gốc.
   - Phiên lỗi được ghi chú rõ: thiếu file thành phẩm, `session.json` hỏng.
2. Chọn một phiên để xem chi tiết:
   - xem đúng file phiên bản đang chọn, có nút phóng to;
   - xem thông tin phiên và danh sách phiên bản (v1, v2…);
   - xuất ảnh, in, in lại theo **đúng phiên bản đang chọn**;
   - xem lịch sử in.
3. **Chỉnh sửa từ vN:**
   - Khôi phục ảnh nguồn, mirror, Manual Crop và bộ lọc của vN, rồi dùng lại trình chỉnh crop, bộ lọc và Photo Composer của Kiosk.
   - **Lưu phiên bản mới** tạo `final-v{N+1}.png`; các bản cũ giữ nguyên.
   - **Hủy chỉnh sửa** quay về đúng vN: không tạo phiên bản, không đổi dữ liệu đã lưu.
4. Phiên thiếu ảnh gốc hoặc snapshot template vẫn **xem, xuất và in lại** được file đã lưu. Chỉ chức năng chỉnh sửa bị khóa, kèm lý do.

Chỉ chế độ quản trị mới truy cập được thư viện ảnh. Khách ở Kiosk chỉ thấy phiên của chính mình.

## 7. Sao chép sự kiện sang máy khác

- Sao chép **nguyên thư mục sự kiện** (USB, ổ mạng...). Mọi đường dẫn bên trong là tương đối nên mở lại được ngay.
- Trên máy mới: Admin → **Sự kiện** → **Đổi sự kiện khác**, rồi chọn thư mục vừa sao chép. Ứng dụng nhận `event.json` và giữ nguyên tên, mã sự kiện.
- Máy mới không cần có template trong thư viện, vì mỗi phiên đã mang theo snapshot template và asset.
