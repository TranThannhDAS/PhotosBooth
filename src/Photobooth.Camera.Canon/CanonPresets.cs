namespace Photobooth.Camera.Canon;

/// <summary>Một giá trị trong bộ thông số. <see cref="Label"/> chỉ để người đọc file JSON dễ hiểu; khi áp dụng dùng <see cref="Code"/>.</summary>
public sealed record CanonPresetValue(CanonSetting Setting, uint Code, string Label);

/// <summary>
/// Bộ thông số máy ảnh Canon do quản trị viên dựng sẵn, áp dụng lại bất cứ lúc nào để đưa máy về đúng mẫu.
/// <see cref="ExposureMode"/> là chế độ chụp lúc lưu (vòng chế độ vật lý, không đặt được từ phần mềm) — chỉ dùng để cảnh báo.
/// </summary>
public sealed record CanonPreset(
    Guid Id,
    string Name,
    DateTimeOffset UpdatedAt,
    uint? ExposureMode,
    IReadOnlyList<CanonPresetValue> Values)
{
    public const int MaxNameLength = 60;

    /// <summary>
    /// Tạo bộ thông số từ trạng thái máy hiện tại: chỉ lấy thông số đang chỉnh được (giá trị máy tự chọn ở Av/Tv/P không được lưu).
    /// </summary>
    public static CanonPreset FromCurrent(Guid id, string name, DateTimeOffset updatedAt, IReadOnlyList<CanonSettingState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var mode = states.FirstOrDefault(s => s.Setting == CanonSetting.ExposureMode)?.Current?.Code;
        var values = states
            .Where(s => s.Setting != CanonSetting.ExposureMode && s.IsWritable && s.Current is not null)
            .Where(s => s.Setting != CanonSetting.ImageQuality || CanonSettingCatalog.IsJpegQuality(s.Current!.Code))
            .Select(s => new CanonPresetValue(s.Setting, s.Current!.Code, s.Current.Label))
            .ToArray();
        return new CanonPreset(id, name.Trim(), updatedAt, mode, values);
    }

    /// <summary>Kiểm tra tên; trả về thông báo lỗi tiếng Việt hoặc null nếu hợp lệ.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Vui lòng nhập tên bộ thông số.";
        }

        return name.Trim().Length > MaxNameLength ? $"Tên tối đa {MaxNameLength} ký tự." : null;
    }
}

public enum CanonPresetApplyStatus
{
    /// <summary>Đã đặt giá trị mới lên máy.</summary>
    Applied,

    /// <summary>Máy đã đúng giá trị này sẵn.</summary>
    Unchanged,

    /// <summary>Không đặt được ở trạng thái hiện tại của máy (chế độ chụp, ống kính...).</summary>
    Skipped,

    /// <summary>Máy từ chối khi đặt.</summary>
    Failed,
}

public sealed record CanonPresetApplyItem(CanonSetting Setting, string Name, string Label, CanonPresetApplyStatus Status, string? Reason);

/// <summary>Kết quả áp dụng một bộ thông số, từng mục một.</summary>
public sealed record CanonPresetApplyResult(
    IReadOnlyList<CanonPresetApplyItem> Items,
    string? ExpectedExposureMode,
    string? CurrentExposureMode)
{
    public bool ExposureModeMismatch => ExpectedExposureMode is not null && CurrentExposureMode is not null && ExpectedExposureMode != CurrentExposureMode;

    public bool FullyApplied => Items.All(i => i.Status is CanonPresetApplyStatus.Applied or CanonPresetApplyStatus.Unchanged);
}
