# Cấu trúc Template — Photobooth Mini

> Tài liệu kỹ thuật cho người tạo khung ảnh (template) và cho Template Editor sau này.
> Chỉ có **một cấu trúc JSON duy nhất**; không có `schemaVersion`, không có cơ chế chuyển đổi tự động.
> Template viết theo cấu trúc cũ sẽ bị từ chối kèm lý do và cần được tạo lại.

## 1. Vị trí và cách thêm template

Mỗi template là **một thư mục** trong `src/Photobooth.Desktop/Templates/` (khi chạy: `Templates/` cạnh `Photobooth.exe`):

```text
Templates/
└── party-overlay/
    ├── template.json      ← bắt buộc
    ├── thumbnail.png      ← tùy chọn: ảnh đại diện ở màn chọn khung
    └── assets/            ← tùy chọn: ảnh trang trí
        ├── background.png
        ├── frame-overlay.png
        └── logo.png
```

Thêm template mới:

1. Tạo thư mục mới (nên đặt tên thư mục trùng `id`; khác tên chỉ bị cảnh báo).
2. Tạo `template.json` theo cấu trúc bên dưới; đặt ảnh trang trí trong thư mục đó.
3. Khởi động lại ứng dụng. **Không cần sửa code.**
4. Xem kết quả kiểm tra trong log `%LOCALAPPDATA%\Photobooth\Logs\photobooth-yyyyMMdd.log` (dòng `JsonTemplateCatalog`).

Quy tắc:

- Template **lỗi** (Error) bị bỏ qua, không làm dừng ứng dụng. Template có **cảnh báo** (Warning) vẫn được dùng.
- File `*.json` đặt trực tiếp trong `Templates/` (cấu trúc cũ) **không được nạp** và chỉ được báo trong log. Ứng dụng không sửa, xóa hay chuyển đổi các file này.
- Ứng dụng **không bao giờ ghi** vào `template.json`.
- Template chỉ được chọn khi số ảnh cần chụp (`photoIndex` lớn nhất + 1) bằng cấu hình `photosPerSession` (mặc định 4).
- Có thể sao chép nguyên thư mục template sang máy khác: mọi đường dẫn asset đều tương đối với thư mục template.

## 2. Quy ước chung

- Định dạng JSON chuẩn; được phép có **chú thích** `// ...` và dấu phẩy cuối.
- Tên thuộc tính viết **camelCase** đúng như tài liệu (`widthPixels`, không phải `WidthPixels`).
- **Thuộc tính lạ bị báo lỗi** (gõ sai tên, thuộc tính cũ như `layer`, `source`, `align`).
- Giá trị enum viết thường: `"cover"`, `"contain"`, `"text"`, `"image"`, `"center"`…
- Tọa độ và kích thước tính bằng **pixel trên canvas**, gốc (0, 0) ở góc trên trái.
- Màu: `#RRGGBB` hoặc `#AARRGGBB` (AA = độ đục, ví dụ `#80000000` là đen 50%).
- `id` của phần tử là tùy chọn, nhưng nếu khai báo thì phải **duy nhất** trong template (nên khai báo để dễ chẩn đoán và cho Template Editor).

## 3. Cấp template

| Thuộc tính | Bắt buộc | Mặc định | Ý nghĩa |
|---|---|---|---|
| `id` | Có | — | Mã template, chỉ `a-z`, `0-9`, `-`, tối đa 64 ký tự; duy nhất trong thư mục Templates |
| `name` | Có | — | Tên hiển thị cho khách |
| `description` | Không | không có | Mô tả ngắn ở màn chọn khung |
| `canvas` | Có | — | Xem mục 4 |
| `slots` | Không (nhưng phải có ≥ 1 ô) | `[]` | Ô ảnh, mục 5 |
| `decorations` | Không | `[]` | Trang trí, mục 7–8 |

## 4. Canvas

| Thuộc tính | Bắt buộc | Mặc định | Ràng buộc |
|---|---|---|---|
| `widthPixels` | Có | — | 1..10000 |
| `heightPixels` | Có | — | 1..10000 |
| `dpi` | Không | `300` | 72..1200. Quyết định khổ giấy PDF: `widthPixels / dpi` inch |
| `backgroundColor` | Không | `#FFFFFF` | Màu nền canvas, luôn vẽ đầu tiên |

Ví dụ: 1200×1800 px ở 300 DPI = 4×6 inch → trang PDF 288×432 point.

## 5. Ô ảnh (slots)

```json
{ "id": "photo-1", "photoIndex": 0, "x": 60, "y": 60, "width": 1080, "height": 810,
  "cornerRadius": 16, "fit": "cover", "focalPoint": { "x": 0.5, "y": 0.4 }, "zIndex": 0 }
```

| Thuộc tính | Bắt buộc | Mặc định | Ý nghĩa / ràng buộc |
|---|---|---|---|
| `id` | Không | không có | Mã phần tử |
| `photoIndex` | Có | — | Ảnh nguồn theo thứ tự chụp (0 = ảnh đầu tiên), 0..9. **Nhiều ô có thể dùng chung một ảnh** |
| `x`, `y` | Có | — | Góc trên trái của ô |
| `width`, `height` | Có | — | Phải > 0 |
| `cornerRadius` | Không | `0` | ≥ 0. Lớn hơn nửa cạnh ngắn → cảnh báo và tự giới hạn khi vẽ |
| `fit` | Không | `cover` | `cover`: lấp đầy ô, giữ tỉ lệ, cắt phần dư. `contain`: thấy toàn bộ ảnh, giữ tỉ lệ, phần trống là nền ô |
| `focalPoint` | Không | `{ "x": 0.5, "y": 0.5 }` | Điểm ưu tiên giữ lại khi `cover` cắt ảnh; `x`, `y` trong khoảng 0..1 (0 = trái/trên, 1 = phải/dưới) |
| `backgroundColor` | Không | trong suốt | Nền bên trong ô (thấy được với `contain`) |
| `borderWidth` | Không | `0` | Viền bên trong ô, ≥ 0 |
| `borderColor` | Khi `borderWidth` > 0 | — | Màu viền |
| `zIndex` | Không | `0` | Thứ tự vẽ, −1000..1000 (mục 6) |

Lưu ý khi vẽ:

- Ảnh **không bao giờ bị kéo giãn**: cả hai chế độ đều phóng/thu đều hai chiều.
- Nội dung ảnh bị cắt gọn trong khung bo góc của chính ô đó, không ảnh hưởng ô khác.
- `focalPoint` tính theo **ảnh khách nhìn thấy** (đã lật gương nếu đang bật chế độ gương). Ví dụ `x = 0` luôn ưu tiên phía bên trái của ảnh hiển thị.
- Bộ lọc màu (Đen trắng, Hoài cổ…) chỉ áp dụng cho ô ảnh, không áp dụng cho trang trí.
- Nếu một ô tham chiếu `photoIndex` không có ảnh tương ứng, việc ghép ảnh báo lỗi rõ ô nào (ví dụ `slots[3](photo-4)`) và không tạo thành phẩm sai.

## 6. Layer — thứ tự vẽ bằng `zIndex`

Chỉ có **một cơ chế** là `zIndex` (thuộc tính `layer` cũ không còn được hỗ trợ).

1. Nền canvas (`canvas.backgroundColor`) luôn vẽ đầu tiên.
2. Mọi ô ảnh và trang trí được gộp thành một danh sách, sắp theo `zIndex` **tăng dần** (nhỏ vẽ trước, nằm dưới).
3. Cùng `zIndex`: vẽ theo thứ tự khai báo, **các ô ảnh trước, sau đó đến trang trí** (thứ tự luôn ổn định).
4. Không khai báo → `zIndex = 0`. Vì vậy mặc định trang trí nằm **trên** ảnh.

Gợi ý quy ước:

| Mục đích | zIndex gợi ý |
|---|---|
| Ảnh nền, hình nền | −100 … −10 |
| Ô ảnh | 0 |
| Khung PNG phủ (overlay), sticker | 10 … 20 |
| Logo, chữ | 30 … 40 |

## 7. Trang trí dạng hình và chữ

Thuộc tính chung cho mọi trang trí:

| Thuộc tính | Bắt buộc | Mặc định | Ghi chú |
|---|---|---|---|
| `id` | Không | không có | |
| `type` | Có | — | `rectangle`, `ellipse`, `text`, `image` (giá trị khác bị báo lỗi) |
| `x`, `y` | Không | `0` | |
| `width`, `height` | Có trên thực tế | `0` | Phải > 0 |
| `zIndex` | Không | `0` | −1000..1000 |

### 7.1 `rectangle` và `ellipse`

| Thuộc tính | Mặc định | Ghi chú |
|---|---|---|
| `color` | không tô | Màu tô |
| `strokeColor`, `strokeWidth` | không viền | Viền (bắt buộc có `strokeColor` nếu `strokeWidth` > 0) |
| `cornerRadius` | `0` | Chỉ cho `rectangle` |

### 7.2 `text`

```json
{ "id": "title-1", "type": "text", "text": "Photobooth Mini", "x": 60, "y": 1440, "width": 1080, "height": 140,
  "fontFamily": "Arial", "fontSizePixels": 96, "bold": true, "textAlign": "center", "color": "#FFFFFF", "zIndex": 30 }
```

| Thuộc tính | Bắt buộc | Mặc định | Ghi chú |
|---|---|---|---|
| `text` | Có | — | Không được rỗng; hỗ trợ placeholder |
| `fontFamily` | Không | `Segoe UI` | Font cài trên Windows. Máy không có font → cảnh báo, dùng font thay thế |
| `fontSizePixels` | Không | `48` | > 0. Lớn hơn `height` → cảnh báo |
| `bold` | Không | `false` | |
| `textAlign` | Không | `center` | `left`, `center`, `right` trong khung `x..x+width` |
| `color` | Không | `#000000` | |

Cách vẽ: chữ một dòng, căn giữa theo chiều dọc của khung. Chữ dài hơn `width` được **tự thu nhỏ** để vừa khung.

Placeholder (thay bằng **một thời điểm thống nhất** của phiên chụp, giờ địa phương):

| Placeholder | Ví dụ |
|---|---|
| `{date}` | `19/11/2026` |
| `{time}` | `10:05` |
| `{datetime}` | `19/11/2026 10:05` |

Placeholder lấy theo thời điểm bắt đầu phiên chụp, giống nhau ở mọi chữ, mọi lần ghép lại và mọi định dạng xuất. Ở màn xem trước template, ứng dụng dùng thời điểm hiện tại.

## 8. Trang trí dạng ảnh (`image`)

```json
{ "id": "overlay-1", "type": "image", "assetPath": "assets/overlay.png",
  "x": 0, "y": 0, "width": 1200, "height": 1800, "zIndex": 20 }
```

| Thuộc tính | Bắt buộc | Mặc định | Ghi chú |
|---|---|---|---|
| `assetPath` | Có | — | Đường dẫn **tương đối với thư mục template**, dùng `/` hoặc `\` |
| `fit` | Không | `contain` | `contain`: đặt vừa khung, giữ tỉ lệ. `cover`: lấp đầy khung, cắt phần dư (hợp cho ảnh nền) |

- Hỗ trợ PNG (giữ nguyên **độ trong suốt**, kể cả bán trong suốt), JPEG và các định dạng ảnh SkiaSharp đọc được.
- Dùng cho logo, sticker, khung phủ (overlay) và ảnh nền (đặt `zIndex` âm).
- Ảnh asset chỉ được đọc, không bị sửa.
- Mẹo cho overlay: tạo PNG cùng kích thước canvas, để **trong suốt** ở vùng các ô ảnh.

Đường dẫn bị **từ chối** (lỗi): đường dẫn tuyệt đối (`C:\…`, `/…`), đường dẫn mạng (`\\server\…`), URI (`http:…`) và mọi đường dẫn có `..`. File không tồn tại hoặc hỏng/không phải ảnh cũng là **lỗi**, và template sẽ không được nạp.

## 9. Kiểm tra (validation)

Khi nạp, mỗi vấn đề được báo kèm **vị trí thuộc tính**, ví dụ `slots[2](photo-3).focalPoint.x` hoặc `$.decorations[0].type`.

| Mức | Ví dụ |
|---|---|
| **Lỗi** (template không được dùng) | JSON sai cú pháp (có số dòng/cột); thiếu thuộc tính bắt buộc; thuộc tính lạ; `type`/`fit`/`textAlign` không hỗ trợ; canvas hoặc DPI ngoài giới hạn; ô/trang trí có kích thước ≤ 0; `photoIndex` ngoài 0..9; `focalPoint` ngoài 0..1; `cornerRadius` âm; `zIndex` ngoài −1000..1000; mã màu sai; `id` trùng; asset không an toàn, không tồn tại hoặc hỏng; trùng `id` template |
| **Cảnh báo** (vẫn dùng được) | Phần tử tràn hoặc nằm ngoài canvas; `cornerRadius` lớn hơn nửa cạnh ngắn; cỡ chữ lớn hơn chiều cao khung chữ; font không có trên máy; có ảnh được chụp nhưng không ô nào dùng; `id` khác tên thư mục |

## 10. Ví dụ hoàn chỉnh

Template `party-overlay` đi kèm ứng dụng: ảnh nền, 4 ô ảnh, khung PNG trong suốt, logo và chữ có ngày giờ.

```json
{
  "id": "party-overlay",
  "name": "Tiệc vui vẻ",
  "description": "Nền gradient, khung trang trí trong suốt, logo và ngày giờ chụp.",
  "canvas": { "widthPixels": 1200, "heightPixels": 1800, "dpi": 300, "backgroundColor": "#7C3AED" },
  "slots": [
    { "id": "photo-1", "photoIndex": 0, "x": 90, "y": 300, "width": 480, "height": 560, "cornerRadius": 20 },
    { "id": "photo-2", "photoIndex": 1, "x": 630, "y": 300, "width": 480, "height": 560, "cornerRadius": 20 },
    { "id": "photo-3", "photoIndex": 2, "x": 90, "y": 900, "width": 480, "height": 560, "cornerRadius": 20 },
    { "id": "photo-4", "photoIndex": 3, "x": 630, "y": 900, "width": 480, "height": 560, "cornerRadius": 20 }
  ],
  "decorations": [
    { "id": "background", "type": "image", "assetPath": "assets/background.png", "x": 0, "y": 0, "width": 1200, "height": 1800, "fit": "cover", "zIndex": -100 },
    { "id": "overlay", "type": "image", "assetPath": "assets/frame-overlay.png", "x": 0, "y": 0, "width": 1200, "height": 1800, "zIndex": 20 },
    { "id": "logo", "type": "image", "assetPath": "assets/logo.png", "x": 490, "y": 70, "width": 220, "height": 200, "zIndex": 30 },
    { "id": "title", "type": "text", "x": 90, "y": 1500, "width": 1020, "height": 130, "text": "Tiệc vui vẻ!", "fontFamily": "Segoe UI", "fontSizePixels": 104, "bold": true, "textAlign": "center", "color": "#FFFFFF", "zIndex": 40 },
    { "id": "datetime", "type": "text", "x": 90, "y": 1640, "width": 1020, "height": 70, "text": "{datetime}", "fontSizePixels": 44, "color": "#FDE68A", "zIndex": 40 }
  ]
}
```

Các template mẫu khác: `strip-four` (hai dải 4 ảnh, mỗi ảnh dùng ở 2 ô), `grid-2x2` (lưới 2×2, hình trang trí `zIndex` âm tràn mép), `hero-three` (ảnh lớn có `focalPoint`, 3 ảnh nhỏ).
