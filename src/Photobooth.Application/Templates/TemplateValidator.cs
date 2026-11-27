using System.Globalization;
using System.Text.RegularExpressions;
using Photobooth.Application.Sessions;

namespace Photobooth.Application.Templates;

public enum ValidationSeverity
{
    /// <summary>Không thể sử dụng template.</summary>
    Error,

    /// <summary>Vẫn dùng được nhưng có điểm cần lưu ý (có thể là chủ ý thiết kế).</summary>
    Warning,
}

/// <param name="Path">Vị trí thuộc tính, ví dụ "slots[2](photo-3).focalPoint.x".</param>
public sealed record TemplateValidationIssue(ValidationSeverity Severity, string Path, string Message)
{
    public override string ToString() =>
        $"{(Severity == ValidationSeverity.Error ? "Lỗi" : "Cảnh báo")} [{Path}]: {Message}";
}

public sealed record TemplateValidationResult(IReadOnlyList<TemplateValidationIssue> Issues)
{
    public static TemplateValidationResult Empty { get; } = new([]);

    public bool IsValid => Issues.All(i => i.Severity != ValidationSeverity.Error);

    public IEnumerable<TemplateValidationIssue> Errors => Issues.Where(i => i.Severity == ValidationSeverity.Error);

    public IEnumerable<TemplateValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning);

    public TemplateValidationResult Concat(IEnumerable<TemplateValidationIssue> more) => new([.. Issues, .. more]);
}

/// <summary>
/// Kiểm tra nội dung template (không truy cập ổ đĩa). Việc kiểm tra file asset tồn tại/đọc được
/// do catalog thực hiện khi nạp template.
/// </summary>
public static partial class TemplateValidator
{
    public const int MaxCanvasPixels = 10000;
    public const int MinDpi = 72;
    public const int MaxDpi = 1200;
    public const int MinZIndex = -1000;
    public const int MaxZIndex = 1000;

    public static TemplateValidationResult Validate(PhotoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = new List<TemplateValidationIssue>();
        void Error(string path, string message) => issues.Add(new(ValidationSeverity.Error, path, message));
        void Warn(string path, string message) => issues.Add(new(ValidationSeverity.Warning, path, message));

        if (string.IsNullOrWhiteSpace(template.Id) || !IdPattern().IsMatch(template.Id))
        {
            Error("id", "Chỉ được gồm a-z, 0-9 và '-' (tối đa 64 ký tự).");
        }

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            Error("name", "Thiếu tên template.");
        }

        var canvas = template.Canvas;
        if (canvas is null)
        {
            Error("canvas", "Thiếu canvas.");
            return new TemplateValidationResult(issues);
        }

        if (canvas.WidthPixels is <= 0 or > MaxCanvasPixels)
        {
            Error("canvas.widthPixels", $"Phải trong khoảng 1..{MaxCanvasPixels}.");
        }

        if (canvas.HeightPixels is <= 0 or > MaxCanvasPixels)
        {
            Error("canvas.heightPixels", $"Phải trong khoảng 1..{MaxCanvasPixels}.");
        }

        if (canvas.Dpi is < MinDpi or > MaxDpi)
        {
            Error("canvas.dpi", $"Phải trong khoảng {MinDpi}..{MaxDpi}.");
        }

        CheckColor(canvas.BackgroundColor, "canvas.backgroundColor", required: true, Error);

        ValidateSlots(template, Error, Warn);
        ValidateDecorations(template, Error, Warn);
        ValidateIds(template, Error);
        return new TemplateValidationResult(issues);
    }

    private static void ValidateSlots(PhotoTemplate template, Action<string, string> error, Action<string, string> warn)
    {
        if (template.Slots.Count == 0)
        {
            error("slots", "Template phải có ít nhất một ô ảnh.");
            return;
        }

        for (var i = 0; i < template.Slots.Count; i++)
        {
            var slot = template.Slots[i];
            var path = ElementPath("slots", i, slot.Id);

            if (slot.PhotoIndex < 0 || slot.PhotoIndex >= CaptureOptions.AbsoluteMaxPhotosPerSession)
            {
                error($"{path}.photoIndex", $"Phải trong khoảng 0..{CaptureOptions.AbsoluteMaxPhotosPerSession - 1}.");
            }

            var sizeValid = CheckBox(template.Canvas, path, slot.X, slot.Y, slot.Width, slot.Height, error, warn);
            CheckCornerRadius(path, slot.CornerRadius, slot.Width, slot.Height, sizeValid, error, warn);
            CheckZIndex(path, slot.ZIndex, error);

            if (slot.FocalPoint is null)
            {
                error($"{path}.focalPoint", "Không được null.");
            }
            else
            {
                if (!IsUnit(slot.FocalPoint.X))
                {
                    error($"{path}.focalPoint.x", "Phải trong khoảng 0..1.");
                }

                if (!IsUnit(slot.FocalPoint.Y))
                {
                    error($"{path}.focalPoint.y", "Phải trong khoảng 0..1.");
                }
            }

            CheckColor(slot.BackgroundColor, $"{path}.backgroundColor", required: false, error);
            if (slot.BorderWidth < 0)
            {
                error($"{path}.borderWidth", "Không được âm.");
            }
            else if (slot.BorderWidth > 0)
            {
                CheckColor(slot.BorderColor, $"{path}.borderColor", required: true, error);
            }
        }

        // BR-T03: chỉ số ảnh nguồn phải liên tục 0..N-1 (cho phép trùng). Thiếu chỉ số trung gian
        // nghĩa là khách phải chụp một ảnh không dùng ở đâu → lỗi, không chỉ cảnh báo.
        var used = template.Slots.Select(s => s.PhotoIndex).Where(i => i >= 0).ToHashSet();
        for (var index = 0; index < template.RequiredPhotoCount; index++)
        {
            if (!used.Contains(index))
            {
                error("slots", string.Create(CultureInfo.InvariantCulture, $"Thiếu ô ảnh dùng photoIndex {index}: các photoIndex phải liên tục từ 0."));
            }
        }
    }

    private static void ValidateDecorations(PhotoTemplate template, Action<string, string> error, Action<string, string> warn)
    {
        for (var i = 0; i < template.Decorations.Count; i++)
        {
            var decoration = template.Decorations[i];
            var path = ElementPath("decorations", i, decoration.Id);

            var sizeValid = CheckBox(template.Canvas, path, decoration.X, decoration.Y, decoration.Width, decoration.Height, error, warn);
            CheckZIndex(path, decoration.ZIndex, error);
            CheckColor(decoration.Color, $"{path}.color", required: false, error);

            if (decoration.StrokeWidth < 0)
            {
                error($"{path}.strokeWidth", "Không được âm.");
            }
            else if (decoration.StrokeWidth > 0)
            {
                CheckColor(decoration.StrokeColor, $"{path}.strokeColor", required: true, error);
            }

            switch (decoration.Type)
            {
                case DecorationKind.Rectangle:
                    CheckCornerRadius(path, decoration.CornerRadius, decoration.Width, decoration.Height, sizeValid, error, warn);
                    break;

                case DecorationKind.Text:
                    if (string.IsNullOrWhiteSpace(decoration.Text))
                    {
                        error($"{path}.text", "Chữ phải có nội dung.");
                    }

                    if (decoration.FontSizePixels <= 0)
                    {
                        error($"{path}.fontSizePixels", "Phải là số dương.");
                    }
                    else if (sizeValid && decoration.FontSizePixels > decoration.Height)
                    {
                        warn($"{path}.fontSizePixels", "Cỡ chữ lớn hơn chiều cao khung chữ; chữ có thể tràn khung.");
                    }

                    break;

                case DecorationKind.Image:
                    if (!TemplateAssetPath.IsSafeRelative(decoration.AssetPath))
                    {
                        error($"{path}.assetPath", "Phải là đường dẫn tương đối trong thư mục template (không dùng đường dẫn tuyệt đối, ổ đĩa, mạng hay '..').");
                    }

                    break;

                case DecorationKind.QrCode:
                    ValidateQrArea(template.Canvas, path, decoration, sizeValid, error);
                    break;
            }
        }

        var qrAreas = template.Decorations.Count(d => d.Type == DecorationKind.QrCode);
        if (qrAreas > 1)
        {
            error("decorations", string.Create(CultureInfo.InvariantCulture, $"Chỉ được có tối đa một vùng mã QR (đang có {qrAreas})."));
        }
    }

    /// <summary>
    /// Cạnh nhỏ nhất của vùng QR (pixel) để điện thoại quét được khi in: <see cref="MinQrSizeMillimeters"/> mm ở DPI của canvas.
    /// </summary>
    public static int MinQrSizePixels(int dpi) =>
        (int)Math.Ceiling(MinQrSizeMillimeters / 25.4 * Math.Max(1, dpi));

    /// <summary>Kích thước in tối thiểu của mã QR (mm).</summary>
    public const double MinQrSizeMillimeters = 15;

    private static void ValidateQrArea(TemplateCanvas canvas, string path, TemplateDecoration qr, bool sizeValid, Action<string, string> error)
    {
        if (!sizeValid)
        {
            return;
        }

        if (qr.Width != qr.Height)
        {
            error(path, "Vùng mã QR phải là hình vuông (rộng = cao).");
        }

        var min = MinQrSizePixels(canvas.Dpi);
        if (Math.Min(qr.Width, qr.Height) < min)
        {
            error(path, string.Create(CultureInfo.InvariantCulture,
                $"Vùng mã QR quá nhỏ để quét: cần tối thiểu {min} px (≈ {MinQrSizeMillimeters:0} mm khi in ở {canvas.Dpi} DPI)."));
        }

        if (qr.X < 0 || qr.Y < 0 || (long)qr.X + qr.Width > canvas.WidthPixels || (long)qr.Y + qr.Height > canvas.HeightPixels)
        {
            error(path, "Vùng mã QR phải nằm trọn trong canvas (QR bị cắt sẽ không quét được).");
        }
    }

    private static void ValidateIds(PhotoTemplate template, Action<string, string> error)
    {
        var ids = template.Slots.Select((s, i) => (s.Id, Path: ElementPath("slots", i, null)))
            .Concat(template.Decorations.Select((d, i) => (d.Id, Path: ElementPath("decorations", i, null))))
            .Where(x => x.Id is not null);

        foreach (var group in ids.GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            error(string.Join(", ", group.Select(x => x.Path)), $"Id '{group.Key}' bị trùng.");
        }

        foreach (var (id, path) in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                error($"{path}.id", "Id không được rỗng và tối đa 64 ký tự.");
            }
        }
    }

    /// <summary>Kiểm tra kích thước (lỗi) và vị trí so với canvas (cảnh báo). Trả về true nếu kích thước hợp lệ.</summary>
    private static bool CheckBox(TemplateCanvas canvas, string path, int x, int y, int width, int height, Action<string, string> error, Action<string, string> warn)
    {
        var valid = true;
        if (width <= 0)
        {
            error($"{path}.width", "Phải là số dương.");
            valid = false;
        }

        if (height <= 0)
        {
            error($"{path}.height", "Phải là số dương.");
            valid = false;
        }

        if (!valid)
        {
            return false;
        }

        var right = (long)x + width;
        var bottom = (long)y + height;
        if (right <= 0 || bottom <= 0 || x >= canvas.WidthPixels || y >= canvas.HeightPixels)
        {
            warn(path, "Nằm hoàn toàn ngoài canvas, sẽ không hiển thị.");
        }
        else if (x < 0 || y < 0 || right > canvas.WidthPixels || bottom > canvas.HeightPixels)
        {
            warn(path, "Vượt ra ngoài canvas; phần thừa sẽ bị cắt.");
        }

        return true;
    }

    private static void CheckCornerRadius(string path, int radius, int width, int height, bool sizeValid, Action<string, string> error, Action<string, string> warn)
    {
        if (radius < 0)
        {
            error($"{path}.cornerRadius", "Không được âm.");
        }
        else if (sizeValid && radius * 2 > Math.Min(width, height))
        {
            warn($"{path}.cornerRadius", "Lớn hơn một nửa cạnh ngắn; sẽ được giới hạn khi vẽ.");
        }
    }

    private static void CheckZIndex(string path, int zIndex, Action<string, string> error)
    {
        if (zIndex is < MinZIndex or > MaxZIndex)
        {
            error($"{path}.zIndex", $"Phải trong khoảng {MinZIndex}..{MaxZIndex}.");
        }
    }

    private static void CheckColor(string? value, string path, bool required, Action<string, string> error)
    {
        if (value is null && !required)
        {
            return;
        }

        if (!HexColor.TryParse(value, out _))
        {
            error(path, $"Mã màu không hợp lệ '{value}' (dùng #RRGGBB hoặc #AARRGGBB).");
        }
    }

    private static bool IsUnit(double value) => value is >= 0 and <= 1 && !double.IsNaN(value);

    private static string ElementPath(string collection, int index, string? id) =>
        id is null
            ? string.Create(CultureInfo.InvariantCulture, $"{collection}[{index}]")
            : string.Create(CultureInfo.InvariantCulture, $"{collection}[{index}]({id})");

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();
}
