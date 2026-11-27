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

    [Fact]
    public async Task R50Led2mPresets_HaveCorrectStablePhotoSettingsAndDistinctUseCases()
    {
        var presets = (await LoadAsync())
            .Where(p => p.Name.StartsWith("R50 LED 2m - ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(6, presets.Length);
        Assert.Equal(6, presets.Select(p => p.Id).Distinct().Count());
        Assert.All(presets, preset =>
        {
            Assert.Equal(3u, preset.ExposureMode); // Vòng chế độ vật lý M; SDK chỉ kiểm tra.
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.AspectRatio && v.Code == 0); // 3:2
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.ImageQuality && v.Code == 1310479); // L Fine
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.WhiteBalance && v.Code == 9); // Kelvin
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.ColorTemperature && v.Code == 5500);
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.LiveViewAfMethod && v.Code == 2);
            Assert.Contains(preset.Values, v => v.Setting == CanonSetting.AutoPowerOff && v.Code == 0);
            Assert.DoesNotContain(preset.Values, v => v.Setting == CanonSetting.ExposureMode);
        });

        static uint Code(CanonPreset preset, CanonSetting setting) =>
            Assert.Single(preset.Values, v => v.Setting == setting).Code;

        CanonPreset Find(string suffix) =>
            Assert.Single(presets, p => p.Name.EndsWith(suffix, StringComparison.Ordinal));

        var standard = Find("Mặc định");
        Assert.Equal(112u, Code(standard, CanonSetting.ShutterSpeed)); // 1/125s
        Assert.Equal(48u, Code(standard, CanonSetting.Aperture)); // f/5.6
        Assert.Equal(88u, Code(standard, CanonSetting.Iso)); // ISO 400

        var couple = Find("Cặp đôi 1-2 người");
        Assert.Equal(44u, Code(couple, CanonSetting.Aperture)); // f/4.5: lens phải hỗ trợ
        Assert.Equal(80u, Code(couple, CanonSetting.Iso)); // ISO 200

        var group = Find("Nhóm đông nhiều hàng");
        Assert.Equal(56u, Code(group, CanonSetting.Aperture)); // f/8
        Assert.Equal(96u, Code(group, CanonSetting.Iso)); // ISO 800

        var movement = Find("Chuyển động ít nhòe");
        Assert.Equal(115u, Code(movement, CanonSetting.ShutterSpeed)); // 1/160s
        Assert.Equal(91u, Code(movement, CanonSetting.Iso)); // ISO 500

        var flicker = Find("Thử 1/100 đèn 50Hz");
        Assert.Equal(109u, Code(flicker, CanonSetting.ShutterSpeed)); // 1/100s
    }

    private Task<IReadOnlyList<CanonPreset>> LoadAsync() =>
        new CanonPresetStore(_library, BundledDirectory, NullLogger<CanonPresetStore>.Instance).ListAsync();
}
