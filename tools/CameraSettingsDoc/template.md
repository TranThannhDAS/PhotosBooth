# Thông số máy ảnh Canon — Tài liệu tham chiếu cho việc dựng bộ thông số (preset)

> **Đối tượng đọc:** AI/chuyên viên xử lý màu ảnh, dựng hoặc chỉnh **bộ thông số máy ảnh** (file `.json`) cho phần mềm Photobooth.
>
> **Mục tiêu:** điều chỉnh màu/sáng của ảnh **ngay trên máy ảnh** mà **không đặt nhầm tham số**. Mọi tên tham số, mã giá trị và nhãn trong tài liệu này được **sinh tự động từ mã nguồn** (`src/Photobooth.Camera.Canon/CanonSettings.cs`) — dùng đúng nguyên văn.
>
> **Phiên bản:** EDSDK 13.21.10 · cập nhật 27/11/2026.
>
> *File này được sinh bởi `tools/CameraSettingsDoc/build.py` — sửa `tools/CameraSettingsDoc/template.md` rồi chạy lại, không sửa tay file này.*

---

## 1. Quy tắc bắt buộc (đọc trước)

1. **Chỉ dùng tên tham số trong mục 4** (`setting`), viết đúng chữ hoa/thường (camelCase). Tên lạ → cả file bị từ chối khi nhập.
2. **`code` là mã nội bộ của Canon, KHÔNG phải giá trị vật lý.** Ví dụ ISO 100 có `code` = `72`, không phải `100`. Luôn tra bảng ở mục 4. Riêng **Nhiệt độ màu** (`colorTemperature`) và **Tự tắt nguồn** (`autoPowerOff`) thì `code` chính là giá trị (Kelvin / giây).
3. **`code` ghi bằng số thập phân** trong JSON (không ghi `0x48`). Cột "Mã (JSON)" trong các bảng là giá trị cần ghi.
4. **`label` phải khớp nguyên văn** cột "Nhãn" trong bảng (kể cả dấu, khoảng trắng, ký tự `"`, `–`). Phần mềm áp dụng theo `code`; `label` để người đọc và để kiểm tra chéo — sai nhãn là dấu hiệu đặt nhầm mã.
5. **Chế độ chụp luôn là M** (`exposureMode` = `3`). Không đưa `exposureMode` vào mảng `values` — đây là núm vật lý trên máy, phần mềm không đặt được.
6. **Cân bằng trắng phải cố định theo Kelvin:** `whiteBalance` = `9` ("Nhiệt độ màu (K)") **và** có `colorTemperature`. Không dùng cân bằng trắng tự động (ảnh trong cùng một dải sẽ lệch màu nhau).
7. **Chất lượng ảnh chỉ được là JPEG** (mục 4.13). RAW/HEIF làm phần mềm không nhận được ảnh.
8. **Mỗi tham số xuất hiện tối đa một lần** trong `values`. Tham số không có trong `values` thì giữ nguyên giá trị đang đặt trên máy.
9. **Chi tiết Picture Style** (tương phản, bão hòa, tông màu, độ nét) nằm trong khoảng ở mục 4.9–4.12; số âm ghi dạng **số nguyên không dấu 32 bit** (ví dụ −1 → `4294967295`).
10. **Không tự bịa giá trị** không có trong bảng (ví dụ ISO 300, f/5.0 trên ống kính không hỗ trợ). Giá trị máy không chấp nhận sẽ bị **bỏ qua kèm lý do** khi áp dụng.
11. **Thang 1/3 stop — tránh mã của thang 1/2 stop (tốc độ, khẩu độ):** máy dùng bước **1/3 stop** (mặc định). Trong bảng tốc độ và khẩu độ, mã có **chữ số hex cuối là `4` hoặc `C`** thuộc thang 1/2 stop → không có trong danh sách máy cho phép, nên bị **bỏ qua kèm lý do** khi áp dụng.
    - **Nhãn trùng, hai mã:** luôn chọn mã có chữ số hex cuối **khác `4`/`C`**. Ví dụ tốc độ `20"`: dùng `0x15` = `21` (không dùng `0x14` = `20`); khẩu độ `f/1.8`: dùng `0x15` = `21`; `f/4.5`: dùng `0x2B` = `43` (không dùng `0x2C` = `44`).
    - **Nhãn chỉ có ở thang 1/2 stop — không dùng:** tốc độ `3"`, `1.5"`, `0.7"`, `1/45`, `1/90`, `1/180`, `1/350`, `1/750`, `1/1500`, `1/3000`, `1/6000`, `1/12000`, `1/24000`, `1/48000`; khẩu độ `f/6.7`, `f/9.5`, `f/19`, `f/27`, `f/38`, `f/54`, `f/76`.
    - `Tự động` (`0x4`) và `Bulb` (`0xC`) là giá trị đặc biệt (không phải thang 1/2 stop) — cũng **không dùng** trong photobooth.
    - Kiểm tra tự động: mọi cặp nhãn trùng trong bảng đều có đúng một mã thuộc thang 1/3 stop.

---

## 2. Bối cảnh vận hành

| Mục | Giá trị |
|---|---|
| Máy ảnh | Canon **EOS M50 Mark II** (cảm biến APS-C 24 MP, ảnh 6000×4000, tỉ lệ 3:2) |
| Ống kính thường dùng | EF-M 15–45mm f/3.5–6.3 (kit) — **khẩu độ lớn nhất phụ thuộc tiêu cự**: f/3.5 ở 15mm, f/6.3 ở 45mm |
| Ánh sáng | **Đèn LED trắng liên tục** (mặc định ~5500 K; dùng đúng số Kelvin ghi trên đèn nếu có) |
| Kiểu chụp | Khách tạo dáng, đứng yên tương đối, 1–8 người, chụp liên tiếp 4 ảnh/phiên |
| Đầu ra | JPEG gốc từ máy → phần mềm ghép vào khung (template), có thể áp **bộ lọc màu phần mềm** (mục 7) → in 4×6 inch / chia sẻ |
| Ưu tiên | **Đều tay giữa các khách và giữa 4 ảnh**: không để máy tự quyết phơi sáng/màu |

**Thứ tự xử lý màu:** máy ảnh (preset này: cân bằng trắng, Picture Style, phơi sáng) → ảnh gốc JPEG được lưu nguyên → phần mềm ghép khung → bộ lọc phần mềm (tùy chọn, không đổi ảnh gốc). Preset quyết định **chất liệu gốc**; nên để ảnh gốc trung tính, đúng màu da, rồi mới "tạo phong cách" bằng Picture Style/bộ lọc.

---

## 3. Định dạng file bộ thông số

Mỗi bộ thông số là **một file JSON UTF-8**:

```json
{
  "id": "5ed1c0de-0000-4000-8000-000000005500",
  "name": "Đèn LED trắng (điểm khởi đầu)",
  "updatedAt": "2026-11-26T23:30:00+07:00",
  "exposureMode": 3,
  "values": [
    { "setting": "shutterSpeed", "code": 112, "label": "1/125" },
    { "setting": "aperture", "code": 48, "label": "f/5.6" },
    { "setting": "iso", "code": 96, "label": "800" },
    { "setting": "whiteBalance", "code": 9, "label": "Nhiệt độ màu (K)" },
    { "setting": "colorTemperature", "code": 5500, "label": "5500 K" },
    { "setting": "pictureStyle", "code": 130, "label": "Chân dung" },
    { "setting": "liveViewAfMethod", "code": 2, "label": "Khuôn mặt + bám theo" },
    { "setting": "imageQuality", "code": 1310479, "label": "JPEG Lớn – Tinh (L Fine)" },
    { "setting": "aspectRatio", "code": 0, "label": "3:2 (toàn khung)" },
    { "setting": "autoPowerOff", "code": 0, "label": "Không tự tắt" }
  ]
}
```

| Trường | Kiểu | Quy tắc |
|---|---|---|
| `id` | GUID | Khác `00000000-…`. Khi **nhập** qua Admin, phần mềm luôn cấp mã mới nên trùng cũng không sao. Với bộ **đi kèm ứng dụng** (thư mục `src/Photobooth.Desktop/CameraPresets`), tên file phải là `id` dạng 32 ký tự hex không gạch, ví dụ `5ed1c0de000040008000000000005500.json`. |
| `name` | chuỗi | Bắt buộc, tối đa **60 ký tự**, tiếng Việt có dấu được. Trùng tên khi nhập → tự thêm "(2)". |
| `updatedAt` | ISO 8601 có múi giờ | Ví dụ `2026-11-27T09:00:00+07:00`. |
| `exposureMode` | số | Luôn `3` (M). Chỉ dùng để cảnh báo nếu vòng chế độ trên máy khác. |
| `values` | mảng | Các phần tử `{ "setting", "code", "label" }` theo mục 4. Thứ tự trong file **không quan trọng** (phần mềm tự sắp, xem mục 5). |

**Nạp vào phần mềm:** Admin › Camera › Kết nối Canon › Bộ thông số đã lưu › **Nhập file .json** → **Áp dụng**. Kết quả áp dụng báo từng mục: *đã đặt*, *đã đúng sẵn*, *bỏ qua (kèm lý do)*, *lỗi*.

---

## 4. Danh mục tham số

Bảng tóm tắt (chi tiết từng tham số ở các mục con):

| `setting` | Tên hiển thị | Nhóm | Ảnh hưởng tới ảnh | Nên đưa vào preset màu? |
|---|---|---|---|---|
| `shutterSpeed` | Tốc độ màn trập | Phơi sáng | Độ sáng, nhòe chuyển động | Có (cố định 1/100–1/160) |
| `aperture` | Khẩu độ | Phơi sáng | Độ sáng, độ sâu trường ảnh | Có (f/4–f/8) |
| `iso` | ISO | Phơi sáng | **Độ sáng** (công cụ chính), nhiễu hạt | **Có — núm chỉnh sáng chính** |
| `exposureCompensation` | Bù sáng | Phơi sáng | **Không tác dụng ở chế độ M** | Không |
| `whiteBalance` | Cân bằng trắng | Màu sắc | Màu tổng thể | Bắt buộc = 9 |
| `colorTemperature` | Nhiệt độ màu | Màu sắc | **Ấm/lạnh** (công cụ màu chính) | **Có** |
| `pictureStyle` | Picture Style | Màu sắc | "Gu màu" nền: da, độ tươi, tương phản | **Có** |
| `pictureStyleContrast` | Độ tương phản | Màu sắc | Tương phản | Có |
| `pictureStyleSaturation` | Độ bão hòa màu | Màu sắc | Độ tươi màu | Có |
| `pictureStyleColorTone` | Tông màu da | Màu sắc | Da đỏ hơn ↔ vàng hơn | Có |
| `pictureStyleSharpness` | Độ sắc nét | Màu sắc | Độ nét/độ "gắt" chi tiết | Có |
| `liveViewAfMethod` | Kiểu lấy nét | Lấy nét | Lấy nét đúng mặt | Giữ = 2 |
| `focusOperation` | Thao tác lấy nét | Lấy nét | — | Không (máy tự quản khi Live View) |
| `imageQuality` | Chất lượng ảnh | Ảnh và vận hành | Kích thước/nén JPEG | Giữ L Fine |
| `aspectRatio` | Tỉ lệ khung | Ảnh và vận hành | Máy cắt ảnh khi chụp | Giữ = 0 (3:2) |
| `autoPowerOff` | Tự tắt nguồn | Ảnh và vận hành | — | Giữ = 0 |

### 4.1 `shutterSpeed` — Tốc độ màn trập

- Nhãn có `"` là **giây** (ví dụ `30"` = 30 giây, `0.5"` = 0,5 giây); nhãn `1/x` là phân số giây.
- **Photobooth:** dùng **1/100, 1/125 hoặc 1/160**. Chậm hơn 1/60 dễ nhòe khi khách cử động. Mỗi bước 1/3 stop.
- Không dùng để chỉnh màu; chỉ đổi khi cần thêm/bớt sáng mà ISO đã chạm giới hạn.

{{TABLE:ShutterSpeeds}}

### 4.2 `aperture` — Khẩu độ

- Số f càng **nhỏ** → ảnh càng **sáng**, hậu cảnh càng mờ, vùng nét càng mỏng.
- **Photobooth:** **f/5.6** (chung), **f/8.0** (nhóm đông, nhiều hàng), **f/4.0** (1–2 người, cần thêm sáng).
- **Bị giới hạn bởi ống kính và tiêu cự:** EF-M 15–45mm chỉ có f/3.5–f/4.0 ở góc rộng; ở 45mm nhỏ nhất là f/6.3. Giá trị ngoài khả năng ống kính sẽ bị bỏ qua.

{{TABLE:Apertures}}

### 4.3 `iso` — ISO

- **Núm chỉnh sáng chính** khi đã cố định tốc độ và khẩu độ. Tăng ISO → ảnh sáng hơn nhưng nhiễu hạt hơn.
- Mỗi bước trong bảng ≈ 1/3 stop; gấp đôi ISO = sáng gấp đôi (1 stop).
- **Photobooth với đèn LED:** thường **400–1600**. Trên 3200 ảnh M50 Mark II bắt đầu nhiễu rõ khi in.
- **Không dùng "Tự động"** (code 0): độ sáng nhảy giữa các ảnh.

{{TABLE:IsoSpeeds}}

### 4.4 `exposureCompensation` — Bù sáng

- **Không có tác dụng ở chế độ M** (phần mềm hiển thị dạng chỉ đọc). Chỉ dùng được ở P/Av/Tv — **không đưa vào preset photobooth**. Muốn sáng/tối hơn: đổi `iso`.

{{TABLE:ExposureCompensations}}

### 4.5 `whiteBalance` — Cân bằng trắng

- **Preset photobooth luôn dùng `9` ("Nhiệt độ màu (K)")** để màu cố định. Các giá trị khác chỉ để tham khảo/đọc trạng thái máy.
- Các mục "Tùy chỉnh…" phụ thuộc dữ liệu cân bằng trắng đã lưu trong máy — không dùng trong preset dùng chung.

{{TABLE:WhiteBalances}}

### 4.6 `colorTemperature` — Nhiệt độ màu (Kelvin)

- **Chỉ có hiệu lực khi `whiteBalance` = 9.** Phần mềm luôn đặt cân bằng trắng trước rồi mới đặt Kelvin.
- `code` = số Kelvin; `label` = `"{code} K"` (ví dụ `5500` → `"5500 K"`).
- Khoảng hợp lệ: **2500 – 10000**, bước **100** (máy có thể giới hạn hẹp hơn).
- **Chiều tác động (dễ nhầm):** đây là Kelvin **của nguồn sáng mà máy bù trừ**:
  - **Tăng** Kelvin (ví dụ 5500 → 6000) → ảnh **ấm hơn / vàng hơn**.
  - **Giảm** Kelvin (ví dụ 5500 → 5000) → ảnh **lạnh hơn / xanh hơn**.
- Điểm khởi đầu: đặt đúng số ghi trên đèn LED (5000–5600 K). Đèn vàng hội trường pha trộn: **4000–4500 K**. Đèn sợi đốt thuần: ~3200 K.
- Chỉnh tinh theo bước **100–200 K**; da người thường cần ảnh **hơi ấm** (+100–300 K so với số trên đèn).

### 4.7 `pictureStyle` — Picture Style

- "Gu màu" nền của máy, áp lên JPEG: đường cong tương phản, cách tái tạo màu da, độ tươi.
- **Photobooth:** **Chân dung** (`130`) cho da mềm, hồng tự nhiên — mặc định; **Tiêu chuẩn** (`129`) cho màu tươi/rực hơn; **Đơn sắc** (`134`) cho ảnh đen trắng.
- "Tự động" (`135`) để máy tự đổi theo cảnh → **không dùng** (thiếu nhất quán).
- "Người dùng 1–3" / "PC 1–3" phụ thuộc file style nạp vào máy — không dùng trong preset dùng chung.

{{TABLE:PictureStyles}}

### 4.8 Chi tiết Picture Style — quy tắc chung

- Bốn tham số 4.9–4.12 chỉnh **bên trong Picture Style đang chọn** (mỗi style lưu bộ chi tiết riêng trên máy).
- Phần mềm **đặt `pictureStyle` trước**, rồi mới đặt chi tiết → chi tiết luôn rơi vào đúng style trong preset.
- **Số âm** ghi dạng số nguyên không dấu 32 bit: `code = 4294967296 + giá trị` (−1 → `4294967295`, −4 → `4294967292`). `label` vẫn ghi `"-1"`, `"-4"`; số dương có dấu `+` (ví dụ `"+2"`); số 0 ghi `"0"`.
- **Đơn sắc (`134`):** `pictureStyleSaturation` và `pictureStyleColorTone` **không có ý nghĩa** — không đưa vào.
- **Chưa kiểm chứng trên EOS M50 Mark II:** nếu máy không cho chỉnh chi tiết qua SDK, các mục này bị **bỏ qua kèm lý do**, phần còn lại của preset vẫn áp dụng.

### 4.9 `pictureStyleContrast` — Độ tương phản (−4…+4)

- **+**: đậm, "punchy", vùng tối tối hơn; **−**: mềm, giữ chi tiết vùng sáng/tối, da mịn hơn.
- Photobooth: **−1 … +1** là an toàn; da người thường đẹp ở **0 hoặc −1**.

{{TABLE:Contrast}}

### 4.10 `pictureStyleSaturation` — Độ bão hòa màu (−4…+4)

- **+**: màu tươi; **−**: màu nhạt, "film". Quá cao (+3, +4) làm da cam/đỏ, phông màu bệt.
- Photobooth: **0 … +2**.

{{TABLE:Saturation}}

### 4.11 `pictureStyleColorTone` — Tông màu da (−4…+4)

- **−**: da **đỏ/hồng** hơn; **+**: da **vàng** hơn. Tác động chủ yếu lên màu da, ít ảnh hưởng màu khác.
- Dùng để sửa da sau khi đã chọn đúng Kelvin. Photobooth: **−1 … +1**.

{{TABLE:ColorTone}}

### 4.12 `pictureStyleSharpness` — Độ sắc nét (0…7)

- **Không có dấu, không có số âm.** `code` = `label` (ví dụ `2` → `"2"`).
- Cao: chi tiết gắt, lộ lỗ chân lông/nhiễu; thấp: mềm. Photobooth chân dung: **2–3**; nhóm/đen trắng: **3–4**.

{{TABLE:Sharpness}}

### 4.13 `imageQuality` — Chất lượng ảnh (chỉ JPEG)

- **Photobooth: luôn `1310479` ("JPEG Lớn – Tinh (L Fine)").** In 4×6 inch ở 300 dpi chỉ cần ~2 MP, nhưng ảnh gốc lớn giữ chỗ cho cắt cúp.
- Chỉ các giá trị dưới đây được chấp nhận; RAW/HEIF bị chặn.

{{TABLE:JpegQualities}}

### 4.14 `aspectRatio` — Tỉ lệ khung

- Tỉ lệ khác 3:2 khiến **máy cắt bỏ một phần ảnh ngay khi chụp** (mất dữ liệu gốc) và có thể lệch khung hướng dẫn trên Live View. Khung in do template quyết định.
- **Photobooth: luôn `0` ("3:2 (toàn khung)").**

{{TABLE:AspectRatios}}

### 4.15 `liveViewAfMethod` — Kiểu lấy nét

- **Photobooth: `2` ("Khuôn mặt + bám theo")** — lấy nét đúng mặt người ở mọi vị trí. Không ảnh hưởng màu.

{{TABLE:LiveViewAfMethods}}

### 4.16 `focusOperation` — Thao tác lấy nét

- Trên máy mirrorless khi chạy Live View, mục này thường do máy tự quản (chỉ đọc). **Không đưa vào preset.**

{{TABLE:FocusOperations}}

### 4.17 `autoPowerOff` — Tự tắt nguồn

- `code` = **số giây**; `0` = "Không tự tắt". Danh sách thực tế do máy trả về.
- Nhãn theo công thức: `0` → `"Không tự tắt"`; dưới 60 → `"{n} giây"`; từ 60 → `"{n/60} phút"` (chia lấy phần nguyên). Ví dụ `30` → `"30 giây"`, `60` → `"1 phút"`, `300` → `"5 phút"`.
- **Photobooth: luôn `0`.**

### 4.18 `exposureMode` — Chế độ chụp (chỉ đọc, không nằm trong `values`)

- Đọc từ vòng chế độ trên máy. Preset ghi `exposureMode: 3` (M). Bảng để hiểu trạng thái máy:

{{TABLE:ExposureModes}}

---

## 5. Ràng buộc và thứ tự áp dụng

Phần mềm tự sắp thứ tự, không phụ thuộc thứ tự trong file:

1. `whiteBalance`, `pictureStyle` (thông số "cha").
2. Các thông số còn lại (phơi sáng, lấy nét, chất lượng, tỉ lệ, tự tắt nguồn).
3. `colorTemperature` (cần `whiteBalance` = 9 đã có hiệu lực).
4. Chi tiết Picture Style (rơi vào `pictureStyle` vừa đặt).

Kết quả "**bỏ qua**" xảy ra khi:

| Tình huống | Lý do hiển thị | Cách xử lý |
|---|---|---|
| Vòng chế độ không ở M | "Không chỉnh được ở chế độ chụp hiện tại." | Xoay máy về M rồi áp dụng lại |
| Khẩu độ ngoài khả năng ống kính/tiêu cự | "Giá trị không dùng được với chế độ chụp/ống kính hiện tại." | Đổi tiêu cự hoặc chọn khẩu độ khác |
| `colorTemperature` khi cân bằng trắng không phải 9 | "Cân bằng trắng không ở chế độ “Nhiệt độ màu (K)”." | Thêm `whiteBalance` = 9 vào preset |
| Máy không có thông số | "Máy ảnh này không có thông số này." | Bỏ thông số khỏi preset |

---

## 6. Hướng dẫn chỉnh theo mục tiêu (dành cho người/AI chỉnh màu)

| Muốn | Đổi tham số | Hướng | Bước gợi ý | Tránh |
|---|---|---|---|---|
| Ảnh sáng hơn | `iso` | tăng | 1/3 stop (một dòng trong bảng) | Đổi `exposureCompensation` (vô tác dụng ở M) |
| Ảnh tối hơn | `iso` | giảm | 1/3 stop | Tốc độ chậm hơn 1/100 |
| Ấm hơn / bớt xanh | `colorTemperature` | **tăng** Kelvin | 100–200 K | Đổi `whiteBalance` khỏi 9 |
| Lạnh hơn / bớt vàng | `colorTemperature` | **giảm** Kelvin | 100–200 K | — |
| Da bớt vàng, hồng hơn | `pictureStyleColorTone` | giảm (−) | 1 | Quá −2 (da đỏ) |
| Da bớt đỏ | `pictureStyleColorTone` | tăng (+) | 1 | Quá +2 (da vàng) |
| Màu tươi hơn | `pictureStyleSaturation` hoặc `pictureStyle` = Tiêu chuẩn | tăng | 1 | +3 trở lên với da người |
| Ảnh mềm, "trong" | `pictureStyleContrast` | giảm | 1 | Dưới −2 (ảnh bệt) |
| Ảnh đậm, nổi khối | `pictureStyleContrast` | tăng | 1 | Trên +2 (mất chi tiết vùng tối) |
| Da mịn hơn | `pictureStyleSharpness` | giảm | 1 | Dưới 1 (ảnh mờ khi in) |
| Nhóm đông, nhiều hàng đều nét | `aperture` | tăng số f (f/7.1–f/8) | 1/3–2/3 stop | Quên **tăng `iso`** tương ứng để bù sáng |
| Ảnh đen trắng | `pictureStyle` = 134 (+ `pictureStyleContrast` +1…+2) | — | — | Đưa `pictureStyleSaturation`/`ColorTone` vào |

**Bù sáng khi đổi khẩu độ** (giữ nguyên độ sáng): mỗi khi khẩu độ **đóng** thêm 1 stop (ví dụ f/5.6 → f/8.0), cần **tăng ISO gấp đôi** (ví dụ 800 → 1600), và ngược lại.

---

## 7. Những gì preset KHÔNG làm được

| Không làm được bằng preset | Thay thế |
|---|---|
| Đổi chế độ chụp (M/Av/P…) | Xoay vòng chế độ trên máy |
| Dịch cân bằng trắng (xanh lá↔đỏ tía), Auto Lighting Optimizer, ưu tiên vùng sáng, khử nhiễu | Chưa hỗ trợ trong phần mềm — chỉnh một lần trong menu máy nếu cần |
| Chỉnh màu cục bộ, LUT, tách tông vùng sáng/tối | Bộ lọc phần mềm (dưới đây) hoặc hậu kỳ ngoài |
| Đèn flash | Không dùng — giả định đèn LED liên tục |

**Bộ lọc màu phần mềm** (áp lên ảnh thành phẩm, ảnh gốc không đổi; chọn trong phần chỉnh sửa ảnh, không nằm trong preset máy ảnh): {{FILTERS}}.
Nên giữ preset máy ảnh **trung tính, đúng màu da**, dùng bộ lọc phần mềm để tạo phong cách — tránh "nướng" phong cách mạnh vào ảnh gốc.

---

## 8. Checklist trước khi giao một preset

- [ ] `exposureMode` = `3`; không có `exposureMode` trong `values`.
- [ ] Có `whiteBalance` = `9` **và** `colorTemperature` (2500–10000, bội số 100).
- [ ] Mọi `setting` thuộc danh sách mục 4; không trùng lặp.
- [ ] Mọi `code` lấy từ bảng (thập phân); số âm chi tiết Picture Style đã đổi sang uint32.
- [ ] Mọi `label` khớp nguyên văn bảng.
- [ ] `imageQuality` = `1310479`, `aspectRatio` = `0`, `autoPowerOff` = `0`, `liveViewAfMethod` = `2`.
- [ ] `shutterSpeed` trong 1/100–1/160; `iso` không phải "Tự động".
- [ ] Đơn sắc: không có `pictureStyleSaturation` / `pictureStyleColorTone`.
- [ ] `name` ≤ 60 ký tự, mô tả tình huống (ví dụ "LED trắng – Tiệc cưới đèn vàng").
- [ ] JSON hợp lệ, mã hóa UTF-8.

---

## 9. Mức độ kiểm chứng

| Hạng mục | Trạng thái |
|---|---|
| Đọc chế độ chụp, ISO, khẩu độ, tốc độ, cân bằng trắng, nhiệt độ màu trên EOS M50 Mark II | Đã thấy trên máy thật (Admin › Camera) |
| Đặt ISO/khẩu độ/tốc độ/cân bằng trắng/Picture Style từ phần mềm | Đã kiểm thử bằng SDK giả; chờ xác nhận trên máy thật |
| Chi tiết Picture Style, tỉ lệ khung, tự tắt nguồn, mức nhiệt | **Chưa kiểm chứng** trên M50 Mark II |
| Bảng mã/nhãn | Sinh từ `CanonSettingCatalog` (theo sample Canon EDSDK 13.21.10); test tự động kiểm tra các preset đi kèm khớp bảng |

Các preset mẫu có sẵn: `src/Photobooth.Desktop/CameraPresets/` (xem README trong thư mục đó).
