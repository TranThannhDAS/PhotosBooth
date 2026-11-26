using Microsoft.Extensions.Logging.Abstractions;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>Các bộ thông số thật trong src/Photobooth.Desktop/CameraPresets phải đọc được và đúng mã của Canon.</summary>
public sealed class BundledPresetTests : IDisposable
{
    private static readonly string BundledDirectory = Path.Combine(AppContext.BaseDirectory, "CameraPresets");

    private readonly string _library = Path.Combine(Path.GetTempPath(), "pb-canon-bundled-check-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_library))
        {
            Directory.Delete(_library, recursive: true);
        }
    }

    [Fact]
    public async Task AllBundledPresets_LoadWithoutErrors_WithUniqueNamesAndMatchingFileNames()
    {
        var files = Directory.GetFiles(BundledDirectory, "*.json");
        var presets = await LoadAsync();

        Assert.NotEmpty(files);
        Assert.Equal(files.Length, presets.Count);
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Count());

        // Tên file = mã bộ thông số: "Thêm bộ mẫu còn thiếu" so sánh theo mã.
        var fileIds = files.Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToArray();
        Assert.Equal(fileIds, presets.Select(p => p.Id.ToString("N")).Order().ToArray());
    }

    [Fact]
    public async Task AllBundledPresets_LabelsMatchCatalog_AndAreSafeForPhotobooth()
    {
        foreach (var preset in await LoadAsync())
        {
            // Chế độ M: tốc độ/khẩu độ/ISO cố định giữa các khách.
            Assert.Equal(3u, preset.ExposureMode);
            Assert.Equal(preset.Values.Count, preset.Values.Select(v => v.Setting).Distinct().Count());

            foreach (var value in preset.Values)
            {
                // Nhãn trong file phải khớp bảng mã: phát hiện gõ nhầm mã khi sửa tay.
                Assert.Equal(CanonSettingCatalog.Label(value.Setting, value.Code), value.Label);

                if (CanonSettingCatalog.IsPictureStyleDetail(value.Setting))
                {
                    Assert.Contains(value.Code, CanonSettingCatalog.PictureStyleDetailRange(value.Setting));
                }
            }

            Assert.All(preset.Values.Where(v => v.Setting == CanonSetting.ImageQuality), v => Assert.True(CanonSettingCatalog.IsJpegQuality(v.Code)));

            // Không dùng cân bằng trắng tự động: 4 ảnh trong một dải phải cùng màu.
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.WhiteBalance && v.Code == CanonSettingCatalog.WhiteBalanceColorTemperature);
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.ColorTemperature);
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.AutoPowerOff && v.Code == 0);
        }
    }

    [Fact]
    public async Task LedStartingPreset_IsPresent()
    {
        var presets = await LoadAsync();

        Assert.Contains(presets, p => p.Name.StartsWith("Đèn LED trắng", StringComparison.Ordinal));
    }

    private Task<IReadOnlyList<CanonPreset>> LoadAsync() =>
        new CanonPresetStore(_library, BundledDirectory, NullLogger<CanonPresetStore>.Instance).ListAsync();
}
