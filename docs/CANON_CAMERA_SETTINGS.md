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

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `Tự động` | `0x4` | `4` |
| `Bulb` | `0xC` | `12` |
| `30"` | `0x10` | `16` |
| `25"` | `0x13` | `19` |
| `20"` | `0x14` | `20` |
| `20"` | `0x15` | `21` |
| `15"` | `0x18` | `24` |
| `13"` | `0x1B` | `27` |
| `10"` | `0x1C` | `28` |
| `10"` | `0x1D` | `29` |
| `8"` | `0x20` | `32` |
| `6"` | `0x23` | `35` |
| `6"` | `0x24` | `36` |
| `5"` | `0x25` | `37` |
| `4"` | `0x28` | `40` |
| `3.2"` | `0x2B` | `43` |
| `3"` | `0x2C` | `44` |
| `2.5"` | `0x2D` | `45` |
| `2"` | `0x30` | `48` |
| `1.6"` | `0x33` | `51` |
| `1.5"` | `0x34` | `52` |
| `1.3"` | `0x35` | `53` |
| `1"` | `0x38` | `56` |
| `0.8"` | `0x3B` | `59` |
| `0.7"` | `0x3C` | `60` |
| `0.6"` | `0x3D` | `61` |
| `0.5"` | `0x40` | `64` |
| `0.4"` | `0x43` | `67` |
| `0.3"` | `0x44` | `68` |
| `0.3"` | `0x45` | `69` |
| `1/4` | `0x48` | `72` |
| `1/5` | `0x4B` | `75` |
| `1/6` | `0x4C` | `76` |
| `1/6` | `0x4D` | `77` |
| `1/8` | `0x50` | `80` |
| `1/10` | `0x53` | `83` |
| `1/10` | `0x54` | `84` |
| `1/13` | `0x55` | `85` |
| `1/15` | `0x58` | `88` |
| `1/20` | `0x5B` | `91` |
| `1/20` | `0x5C` | `92` |
| `1/25` | `0x5D` | `93` |
| `1/30` | `0x60` | `96` |
| `1/40` | `0x63` | `99` |
| `1/45` | `0x64` | `100` |
| `1/50` | `0x65` | `101` |
| `1/60` | `0x68` | `104` |
| `1/80` | `0x6B` | `107` |
| `1/90` | `0x6C` | `108` |
| `1/100` | `0x6D` | `109` |
| `1/125` | `0x70` | `112` |
| `1/160` | `0x73` | `115` |
| `1/180` | `0x74` | `116` |
| `1/200` | `0x75` | `117` |
| `1/250` | `0x78` | `120` |
| `1/320` | `0x7B` | `123` |
| `1/350` | `0x7C` | `124` |
| `1/400` | `0x7D` | `125` |
| `1/500` | `0x80` | `128` |
| `1/640` | `0x83` | `131` |
| `1/750` | `0x84` | `132` |
| `1/800` | `0x85` | `133` |
| `1/1000` | `0x88` | `136` |
| `1/1250` | `0x8B` | `139` |
| `1/1500` | `0x8C` | `140` |
| `1/1600` | `0x8D` | `141` |
| `1/2000` | `0x90` | `144` |
| `1/2500` | `0x93` | `147` |
| `1/3000` | `0x94` | `148` |
| `1/3200` | `0x95` | `149` |
| `1/4000` | `0x98` | `152` |
| `1/5000` | `0x9B` | `155` |
| `1/6000` | `0x9C` | `156` |
| `1/6400` | `0x9D` | `157` |
| `1/8000` | `0xA0` | `160` |
| `1/10000` | `0xA3` | `163` |
| `1/12000` | `0xA4` | `164` |
| `1/12800` | `0xA5` | `165` |
| `1/16000` | `0xA8` | `168` |
| `1/20000` | `0xAB` | `171` |
| `1/24000` | `0xAC` | `172` |
| `1/25600` | `0xAD` | `173` |
| `1/32000` | `0xB0` | `176` |
| `1/40000` | `0xB3` | `179` |
| `1/48000` | `0xB4` | `180` |
| `1/51200` | `0xB5` | `181` |
| `1/64000` | `0xB8` | `184` |

### 4.2 `aperture` — Khẩu độ

- Số f càng **nhỏ** → ảnh càng **sáng**, hậu cảnh càng mờ, vùng nét càng mỏng.
- **Photobooth:** **f/5.6** (chung), **f/8.0** (nhóm đông, nhiều hàng), **f/4.0** (1–2 người, cần thêm sáng).
- **Bị giới hạn bởi ống kính và tiêu cự:** EF-M 15–45mm chỉ có f/3.5–f/4.0 ở góc rộng; ở 45mm nhỏ nhất là f/6.3. Giá trị ngoài khả năng ống kính sẽ bị bỏ qua.

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `f/1.0` | `0x8` | `8` |
| `f/1.1` | `0xB` | `11` |
| `f/1.2` | `0xC` | `12` |
| `f/1.2` | `0xD` | `13` |
| `f/1.4` | `0x10` | `16` |
| `f/1.6` | `0x13` | `19` |
| `f/1.8` | `0x14` | `20` |
| `f/1.8` | `0x15` | `21` |
| `f/2.0` | `0x18` | `24` |
| `f/2.2` | `0x1B` | `27` |
| `f/2.5` | `0x1C` | `28` |
| `f/2.5` | `0x1D` | `29` |
| `f/2.8` | `0x20` | `32` |
| `f/3.2` | `0x23` | `35` |
| `f/3.3` | `0x80` | `128` |
| `f/3.4` | `0x85` | `133` |
| `f/3.5` | `0x24` | `36` |
| `f/3.5` | `0x25` | `37` |
| `f/4.0` | `0x28` | `40` |
| `f/4.5` | `0x2B` | `43` |
| `f/4.5` | `0x2C` | `44` |
| `f/5.0` | `0x2D` | `45` |
| `f/5.6` | `0x30` | `48` |
| `f/6.3` | `0x33` | `51` |
| `f/6.7` | `0x34` | `52` |
| `f/7.1` | `0x35` | `53` |
| `f/8.0` | `0x38` | `56` |
| `f/9.0` | `0x3B` | `59` |
| `f/9.5` | `0x3C` | `60` |
| `f/10` | `0x3D` | `61` |
| `f/11` | `0x40` | `64` |
| `f/13` | `0x43` | `67` |
| `f/13` | `0x44` | `68` |
| `f/14` | `0x45` | `69` |
| `f/16` | `0x48` | `72` |
| `f/18` | `0x4B` | `75` |
| `f/19` | `0x4C` | `76` |
| `f/20` | `0x4D` | `77` |
| `f/22` | `0x50` | `80` |
| `f/25` | `0x53` | `83` |
| `f/27` | `0x54` | `84` |
| `f/29` | `0x55` | `85` |
| `f/32` | `0x58` | `88` |
| `f/36` | `0x5B` | `91` |
| `f/38` | `0x5C` | `92` |
| `f/40` | `0x5D` | `93` |
| `f/45` | `0x60` | `96` |
| `f/51` | `0x63` | `99` |
| `f/54` | `0x64` | `100` |
| `f/57` | `0x65` | `101` |
| `f/64` | `0x68` | `104` |
| `f/72` | `0x6B` | `107` |
| `f/76` | `0x6C` | `108` |
| `f/80` | `0x6D` | `109` |
| `f/91` | `0x70` | `112` |
| `Tự động` | `0xFF` | `255` |

### 4.3 `iso` — ISO

- **Núm chỉnh sáng chính** khi đã cố định tốc độ và khẩu độ. Tăng ISO → ảnh sáng hơn nhưng nhiễu hạt hơn.
- Mỗi bước trong bảng ≈ 1/3 stop; gấp đôi ISO = sáng gấp đôi (1 stop).
- **Photobooth với đèn LED:** thường **400–1600**. Trên 3200 ảnh M50 Mark II bắt đầu nhiễu rõ khi in.
- **Không dùng "Tự động"** (code 0): độ sáng nhảy giữa các ảnh.

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `Tự động` | `0x0` | `0` |
| `6` | `0x28` | `40` |
| `12` | `0x30` | `48` |
| `25` | `0x38` | `56` |
| `50` | `0x40` | `64` |
| `100` | `0x48` | `72` |
| `125` | `0x4B` | `75` |
| `160` | `0x4D` | `77` |
| `200` | `0x50` | `80` |
| `250` | `0x53` | `83` |
| `320` | `0x55` | `85` |
| `400` | `0x58` | `88` |
| `500` | `0x5B` | `91` |
| `640` | `0x5D` | `93` |
| `800` | `0x60` | `96` |
| `1000` | `0x63` | `99` |
| `1250` | `0x65` | `101` |
| `1600` | `0x68` | `104` |
| `2000` | `0x6B` | `107` |
| `2500` | `0x6D` | `109` |
| `3200` | `0x70` | `112` |
| `4000` | `0x73` | `115` |
| `5000` | `0x75` | `117` |
| `6400` | `0x78` | `120` |
| `8000` | `0x7B` | `123` |
| `10000` | `0x7D` | `125` |
| `12800` | `0x80` | `128` |
| `16000` | `0x83` | `131` |
| `20000` | `0x85` | `133` |
| `25600` | `0x88` | `136` |
| `32000` | `0x8B` | `139` |
| `40000` | `0x8D` | `141` |
| `51200` | `0x90` | `144` |
| `64000` | `0x93` | `147` |
| `80000` | `0x95` | `149` |
| `102400` | `0x98` | `152` |
| `204800` | `0xA0` | `160` |
| `409600` | `0xA8` | `168` |
| `819200` | `0xB0` | `176` |

### 4.4 `exposureCompensation` — Bù sáng

- **Không có tác dụng ở chế độ M** (phần mềm hiển thị dạng chỉ đọc). Chỉ dùng được ở P/Av/Tv — **không đưa vào preset photobooth**. Muốn sáng/tối hơn: đổi `iso`.

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `+5` | `0x28` | `40` |
| `+4 2/3` | `0x25` | `37` |
| `+4 1/2` | `0x24` | `36` |
| `+4 1/3` | `0x23` | `35` |
| `+4` | `0x20` | `32` |
| `+3 2/3` | `0x1D` | `29` |
| `+3 1/2` | `0x1C` | `28` |
| `+3 1/3` | `0x1B` | `27` |
| `+3` | `0x18` | `24` |
| `+2 2/3` | `0x15` | `21` |
| `+2 1/2` | `0x14` | `20` |
| `+2 1/3` | `0x13` | `19` |
| `+2` | `0x10` | `16` |
| `+1 2/3` | `0xD` | `13` |
| `+1 1/2` | `0xC` | `12` |
| `+1 1/3` | `0xB` | `11` |
| `+1` | `0x8` | `8` |
| `+2/3` | `0x5` | `5` |
| `+1/2` | `0x4` | `4` |
| `+1/3` | `0x3` | `3` |
| `0` | `0x0` | `0` |
| `-1/3` | `0xFD` | `253` |
| `-1/2` | `0xFC` | `252` |
| `-2/3` | `0xFB` | `251` |
| `-1` | `0xF8` | `248` |
| `-1 1/3` | `0xF5` | `245` |
| `-1 1/2` | `0xF4` | `244` |
| `-1 2/3` | `0xF3` | `243` |
| `-2` | `0xF0` | `240` |
| `-2 1/3` | `0xED` | `237` |
| `-2 1/2` | `0xEC` | `236` |
| `-2 2/3` | `0xEB` | `235` |
| `-3` | `0xE8` | `232` |
| `-3 1/3` | `0xE5` | `229` |
| `-3 1/2` | `0xE4` | `228` |
| `-3 2/3` | `0xE3` | `227` |
| `-4` | `0xE0` | `224` |
| `-4 1/3` | `0xDD` | `221` |
| `-4 1/2` | `0xDC` | `220` |
| `-4 2/3` | `0xDB` | `219` |
| `-5` | `0xD8` | `216` |

### 4.5 `whiteBalance` — Cân bằng trắng

- **Preset photobooth luôn dùng `9` ("Nhiệt độ màu (K)")** để màu cố định. Các giá trị khác chỉ để tham khảo/đọc trạng thái máy.
- Các mục "Tùy chỉnh…" phụ thuộc dữ liệu cân bằng trắng đã lưu trong máy — không dùng trong preset dùng chung.

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `Tự động (giữ không khí)` | `0x0` | `0` |
| `Ánh nắng` | `0x1` | `1` |
| `Trời mây` | `0x2` | `2` |
| `Đèn sợi đốt` | `0x3` | `3` |
| `Đèn huỳnh quang trắng` | `0x4` | `4` |
| `Đèn flash` | `0x5` | `5` |
| `Tùy chỉnh 1` | `0x6` | `6` |
| `Bóng râm` | `0x8` | `8` |
| `Nhiệt độ màu (K)` | `0x9` | `9` |
| `Tùy chỉnh PC-1` | `0xA` | `10` |
| `Tùy chỉnh PC-2` | `0xB` | `11` |
| `Tùy chỉnh PC-3` | `0xC` | `12` |
| `Tùy chỉnh 2` | `0xF` | `15` |
| `Tùy chỉnh 3` | `0x10` | `16` |
| `Dưới nước` | `0x11` | `17` |
| `Tùy chỉnh 4` | `0x12` | `18` |
| `Tùy chỉnh 5` | `0x13` | `19` |
| `Tùy chỉnh PC-4` | `0x14` | `20` |
| `Tùy chỉnh PC-5` | `0x15` | `21` |
| `Tự động (ưu tiên trắng)` | `0x17` | `23` |
| `Nhiệt độ màu 2` | `0x18` | `24` |
| `Nhiệt độ màu 3` | `0x19` | `25` |
| `Nhiệt độ màu 4` | `0x1A` | `26` |

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

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `Người dùng 1` | `0x21` | `33` |
| `Người dùng 2` | `0x22` | `34` |
| `Người dùng 3` | `0x23` | `35` |
| `PC 1` | `0x41` | `65` |
| `PC 2` | `0x42` | `66` |
| `PC 3` | `0x43` | `67` |
| `Tiêu chuẩn` | `0x81` | `129` |
| `Chân dung` | `0x82` | `130` |
| `Phong cảnh` | `0x83` | `131` |
| `Trung tính` | `0x84` | `132` |
| `Trung thực` | `0x85` | `133` |
| `Đơn sắc` | `0x86` | `134` |
| `Tự động` | `0x87` | `135` |
| `Chi tiết` | `0x88` | `136` |

### 4.8 Chi tiết Picture Style — quy tắc chung

- Bốn tham số 4.9–4.12 chỉnh **bên trong Picture Style đang chọn** (mỗi style lưu bộ chi tiết riêng trên máy).
- Phần mềm **đặt `pictureStyle` trước**, rồi mới đặt chi tiết → chi tiết luôn rơi vào đúng style trong preset.
- **Số âm** ghi dạng số nguyên không dấu 32 bit: `code = 4294967296 + giá trị` (−1 → `4294967295`, −4 → `4294967292`). `label` vẫn ghi `"-1"`, `"-4"`; số dương có dấu `+` (ví dụ `"+2"`); số 0 ghi `"0"`.
- **Đơn sắc (`134`):** `pictureStyleSaturation` và `pictureStyleColorTone` **không có ý nghĩa** — không đưa vào.
- **Chưa kiểm chứng trên EOS M50 Mark II:** nếu máy không cho chỉnh chi tiết qua SDK, các mục này bị **bỏ qua kèm lý do**, phần còn lại của preset vẫn áp dụng.

### 4.9 `pictureStyleContrast` — Độ tương phản (−4…+4)

- **+**: đậm, "punchy", vùng tối tối hơn; **−**: mềm, giữ chi tiết vùng sáng/tối, da mịn hơn.
- Photobooth: **−1 … +1** là an toàn; da người thường đẹp ở **0 hoặc −1**.

| Giá trị | Nhãn (`label`) | Mã (JSON `code`) |
|---|---|---|
| -4 | `-4` | `4294967292` |
| -3 | `-3` | `4294967293` |
| -2 | `-2` | `4294967294` |
| -1 | `-1` | `4294967295` |
| 0 | `0` | `0` |
| 1 | `+1` | `1` |
| 2 | `+2` | `2` |
| 3 | `+3` | `3` |
| 4 | `+4` | `4` |

### 4.10 `pictureStyleSaturation` — Độ bão hòa màu (−4…+4)

- **+**: màu tươi; **−**: màu nhạt, "film". Quá cao (+3, +4) làm da cam/đỏ, phông màu bệt.
- Photobooth: **0 … +2**.

| Giá trị | Nhãn (`label`) | Mã (JSON `code`) |
|---|---|---|
| -4 | `-4` | `4294967292` |
| -3 | `-3` | `4294967293` |
| -2 | `-2` | `4294967294` |
| -1 | `-1` | `4294967295` |
| 0 | `0` | `0` |
| 1 | `+1` | `1` |
| 2 | `+2` | `2` |
| 3 | `+3` | `3` |
| 4 | `+4` | `4` |

### 4.11 `pictureStyleColorTone` — Tông màu da (−4…+4)

- **−**: da **đỏ/hồng** hơn; **+**: da **vàng** hơn. Tác động chủ yếu lên màu da, ít ảnh hưởng màu khác.
- Dùng để sửa da sau khi đã chọn đúng Kelvin. Photobooth: **−1 … +1**.

| Giá trị | Nhãn (`label`) | Mã (JSON `code`) |
|---|---|---|
| -4 | `-4` | `4294967292` |
| -3 | `-3` | `4294967293` |
| -2 | `-2` | `4294967294` |
| -1 | `-1` | `4294967295` |
| 0 | `0` | `0` |
| 1 | `+1` | `1` |
| 2 | `+2` | `2` |
| 3 | `+3` | `3` |
| 4 | `+4` | `4` |

### 4.12 `pictureStyleSharpness` — Độ sắc nét (0…7)

- **Không có dấu, không có số âm.** `code` = `label` (ví dụ `2` → `"2"`).
- Cao: chi tiết gắt, lộ lỗ chân lông/nhiễu; thấp: mềm. Photobooth chân dung: **2–3**; nhóm/đen trắng: **3–4**.

| Giá trị | Nhãn (`label`) | Mã (JSON `code`) |
|---|---|---|
| 0 | `0` | `0` |
| 1 | `1` | `1` |
| 2 | `2` | `2` |
| 3 | `3` | `3` |
| 4 | `4` | `4` |
| 5 | `5` | `5` |
| 6 | `6` | `6` |
| 7 | `7` | `7` |

### 4.13 `imageQuality` — Chất lượng ảnh (chỉ JPEG)

- **Photobooth: luôn `1310479` ("JPEG Lớn – Tinh (L Fine)").** In 4×6 inch ở 300 dpi chỉ cần ~2 MP, nhưng ảnh gốc lớn giữ chỗ cho cắt cúp.
- Chỉ các giá trị dưới đây được chấp nhận; RAW/HEIF bị chặn.

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `JPEG Lớn – Tinh (L Fine)` | `0x13FF0F` | `1310479` |
| `JPEG Lớn – Thường (L Normal)` | `0x12FF0F` | `1244943` |
| `JPEG Lớn (L)` | `0x10FF0F` | `1113871` |
| `JPEG Vừa – Tinh (M Fine)` | `0x113FF0F` | `18087695` |
| `JPEG Vừa – Thường (M Normal)` | `0x112FF0F` | `18022159` |
| `JPEG Vừa (M)` | `0x110FF0F` | `17891087` |
| `JPEG Vừa 1 (M1)` | `0x510FF0F` | `84999951` |
| `JPEG Vừa 2 (M2)` | `0x610FF0F` | `101777167` |
| `JPEG Nhỏ – Tinh (S Fine)` | `0x213FF0F` | `34864911` |
| `JPEG Nhỏ – Thường (S Normal)` | `0x212FF0F` | `34799375` |
| `JPEG Nhỏ (S)` | `0x210FF0F` | `34668303` |
| `JPEG Nhỏ 1 – Tinh (S1 Fine)` | `0xE13FF0F` | `236191503` |
| `JPEG Nhỏ 1 – Thường (S1 Normal)` | `0xE12FF0F` | `236125967` |
| `JPEG Nhỏ 1 (S1)` | `0xE10FF0F` | `235994895` |
| `JPEG Nhỏ 2 (S2)` | `0xF13FF0F` | `252968719` |
| `JPEG Nhỏ 2 (S2)` | `0xF10FF0F` | `252772111` |
| `JPEG Nhỏ 3 (S3)` | `0x1013FF0F` | `269745935` |

### 4.14 `aspectRatio` — Tỉ lệ khung

- Tỉ lệ khác 3:2 khiến **máy cắt bỏ một phần ảnh ngay khi chụp** (mất dữ liệu gốc) và có thể lệch khung hướng dẫn trên Live View. Khung in do template quyết định.
- **Photobooth: luôn `0` ("3:2 (toàn khung)").**

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `3:2 (toàn khung)` | `0x0` | `0` |
| `1:1` | `0x1` | `1` |
| `4:3` | `0x2` | `2` |
| `16:9` | `0x7` | `7` |
| `Crop 1.6x` | `0xD` | `13` |

### 4.15 `liveViewAfMethod` — Kiểu lấy nét

- **Photobooth: `2` ("Khuôn mặt + bám theo")** — lấy nét đúng mặt người ở mọi vị trí. Không ảnh hưởng màu.

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `Nhanh (Quick)` | `0x0` | `0` |
| `1 điểm` | `0x1` | `1` |
| `Khuôn mặt + bám theo` | `0x2` | `2` |
| `Nhiều điểm (FlexiZone)` | `0x3` | `3` |
| `Vùng` | `0x4` | `4` |
| `Mở rộng vùng AF` | `0x5` | `5` |
| `Mở rộng vùng AF: xung quanh` | `0x6` | `6` |
| `Vùng lớn: ngang` | `0x7` | `7` |
| `Vùng lớn: dọc` | `0x8` | `8` |
| `Catch AF` | `0x9` | `9` |
| `Điểm nhỏ (Spot)` | `0xA` | `10` |
| `Vùng linh hoạt 1` | `0xB` | `11` |
| `Vùng linh hoạt 2` | `0xC` | `12` |
| `Vùng linh hoạt 3` | `0xD` | `13` |
| `Toàn khung` | `0xE` | `14` |
| `Điểm nhỏ (không bám)` | `0xF` | `15` |
| `1 điểm (không bám)` | `0x10` | `16` |
| `Mở rộng vùng AF (không bám)` | `0x11` | `17` |
| `Mở rộng xung quanh (không bám)` | `0x12` | `18` |

### 4.16 `focusOperation` — Thao tác lấy nét

- Trên máy mirrorless khi chạy Live View, mục này thường do máy tự quản (chỉ đọc). **Không đưa vào preset.**

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `One-Shot (lấy nét một lần)` | `0x0` | `0` |
| `AI Servo (bám liên tục)` | `0x1` | `1` |
| `AI Focus (tự chọn)` | `0x2` | `2` |
| `Lấy nét tay (MF)` | `0x3` | `3` |

### 4.17 `autoPowerOff` — Tự tắt nguồn

- `code` = **số giây**; `0` = "Không tự tắt". Danh sách thực tế do máy trả về.
- Nhãn theo công thức: `0` → `"Không tự tắt"`; dưới 60 → `"{n} giây"`; từ 60 → `"{n/60} phút"` (chia lấy phần nguyên). Ví dụ `30` → `"30 giây"`, `60` → `"1 phút"`, `300` → `"5 phút"`.
- **Photobooth: luôn `0`.**

### 4.18 `exposureMode` — Chế độ chụp (chỉ đọc, không nằm trong `values`)

- Đọc từ vòng chế độ trên máy. Preset ghi `exposureMode: 3` (M). Bảng để hiểu trạng thái máy:

| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |
|---|---|---|
| `P (Chương trình)` | `0x0` | `0` |
| `Tv (Ưu tiên tốc độ)` | `0x1` | `1` |
| `Av (Ưu tiên khẩu độ)` | `0x2` | `2` |
| `M (Chỉnh tay)` | `0x3` | `3` |
| `Bulb` | `0x4` | `4` |
| `Tự động` | `0x9` | `9` |
| `Chân dung ban đêm` | `0xA` | `10` |
| `Thể thao` | `0xB` | `11` |
| `Chân dung` | `0xC` | `12` |
| `Phong cảnh` | `0xD` | `13` |
| `Cận cảnh` | `0xE` | `14` |
| `Không đèn flash` | `0xF` | `15` |
| `Creative Auto` | `0x13` | `19` |
| `Quay phim` | `0x14` | `20` |
| `Tự động thông minh` | `0x16` | `22` |
| `SCN (Cảnh)` | `0x19` | `25` |
| `Bộ lọc sáng tạo` | `0x1D` | `29` |
| `Tự chụp chân dung` | `0x32` | `50` |
| `Da mịn` | `0x34` | `52` |
| `Fv` | `0x37` | `55` |

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

**Bộ lọc màu phần mềm** (áp lên ảnh thành phẩm, ảnh gốc không đổi; chọn trong phần chỉnh sửa ảnh, không nằm trong preset máy ảnh): `Warm`, `Cool`, `Bright`, `Pastel`, `SoftRose`, `GoldenHour`, `Film`, `Cinematic`, `Moody`, `Sepia`, `Grayscale`.
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
