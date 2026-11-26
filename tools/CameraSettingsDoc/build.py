"""Sinh docs/CANON_CAMERA_SETTINGS.md: điền bảng mã/nhãn từ CanonSettings.cs vào khung tài liệu."""
import re
import sys

repo, template_path = sys.argv[1], sys.argv[2]
src = open(f"{repo}/src/Photobooth.Camera.Canon/CanonSettings.cs", encoding="utf-8").read()
tpl = open(template_path, encoding="utf-8").read()

ENTRY = re.compile(r'\[(0[xX][0-9a-fA-F]+|\d+)\]\s*=\s*"(.*?)(?<!\\)"')


def parse_dict(name):
    start = src.index(f"Dictionary<uint, string> {name} = new()")
    end = src.index("};", start)
    rows = []
    for code, label in ENTRY.findall(src[start:end]):
        value = int(code, 16) if code.lower().startswith("0x") else int(code)
        rows.append((value, label.replace('\\"', '"')))
    if not rows:
        raise SystemExit(f"empty dict {name}")
    return rows


def table(rows, sort=True):
    if sort:
        rows = sorted(rows, key=lambda r: r[0])
    lines = ["| Nhãn (`label`) | Mã hex | Mã (JSON `code`) |", "|---|---|---|"]
    lines += [f"| `{label}` | `0x{value:X}` | `{value}` |" for value, label in rows]
    return "\n".join(lines)


def detail_table(lo, hi, signed):
    lines = ["| Giá trị | Nhãn (`label`) | Mã (JSON `code`) |", "|---|---|---|"]
    for v in range(lo, hi + 1):
        label = (f"+{v}" if v > 0 else str(v)) if signed else str(v)
        lines.append(f"| {v} | `{label}` | `{v & 0xFFFFFFFF}` |")
    return "\n".join(lines)


def signed8(value):
    return value - 256 if value >= 0x80 else value


fills = {
    "ShutterSpeeds": table(parse_dict("ShutterSpeeds")),
    "Apertures": table(parse_dict("Apertures"), sort=False),
    "IsoSpeeds": table(parse_dict("IsoSpeeds")),
    "ExposureCompensations": table(sorted(parse_dict("ExposureCompensations"), key=lambda r: -signed8(r[0])), sort=False),
    "WhiteBalances": table(parse_dict("WhiteBalances")),
    "PictureStyles": table(parse_dict("PictureStyles")),
    "LiveViewAfMethods": table(parse_dict("LiveViewAfMethods")),
    "FocusOperations": table(parse_dict("FocusOperations")),
    "JpegQualities": table(parse_dict("JpegQualities"), sort=False),
    "AspectRatios": table(parse_dict("AspectRatios")),
    "ExposureModes": table(parse_dict("ExposureModes")),
    "Contrast": detail_table(-4, 4, True),
    "Saturation": detail_table(-4, 4, True),
    "ColorTone": detail_table(-4, 4, True),
    "Sharpness": detail_table(0, 7, False),
}
for key, value in fills.items():
    tpl = tpl.replace("{{TABLE:" + key + "}}", value)

contracts = open(f"{repo}/src/Photobooth.Application/Composition/CompositionContracts.cs", encoding="utf-8").read()
enum_body = contracts[contracts.index("public enum ColorFilter"):]
enum_body = enum_body[enum_body.index("{") + 1:enum_body.index("}")]
filters = [f.strip() for f in enum_body.split(",") if f.strip() and f.strip() != "None"]
tpl = tpl.replace("{{FILTERS}}", ", ".join(f"`{f}`" for f in filters))

left = re.findall(r"\{\{[^}]+\}\}", tpl)
if left:
    raise SystemExit("unfilled: " + ", ".join(left))

with open(f"{repo}/docs/CANON_CAMERA_SETTINGS.md", "w", encoding="utf-8", newline="\n") as f:
    f.write(tpl)

print("ok lines=", len(tpl.splitlines()))
for key, value in fills.items():
    print(key, value.count("\n") - 1)
