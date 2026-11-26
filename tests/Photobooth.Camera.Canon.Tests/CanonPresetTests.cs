using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;

namespace Photobooth.Camera.Canon.Tests;

/// <summary>Bộ thông số máy ảnh: tạo từ máy, áp dụng lại (SDK giả) và lưu file.</summary>
public sealed class CanonPresetTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 23, 0, 0, TimeSpan.FromHours(7));

    private readonly FakeEdsApi _api = new();
    private readonly CanonSdkHost _host;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pb-canon-presets-" + Guid.NewGuid().ToString("N"));

    public CanonPresetTests()
    {
        _host = new CanonSdkHost(_api, NullLogger<CanonSdkHost>.Instance);

        // Máy ở chế độ M.
        _api.Properties[EDSDK.PropID_AEMode] = 3;
        _api.Properties[EDSDK.PropID_ISOSpeed] = 0x48;
        _api.AllowedValues[EDSDK.PropID_ISOSpeed] = [0x00, 0x48, 0x50, 0x58];
        _api.Properties[EDSDK.PropID_Av] = 0x28;
        _api.AllowedValues[EDSDK.PropID_Av] = [0x28, 0x30, 0x38];
        _api.Properties[EDSDK.PropID_Tv] = 0x70;
        _api.AllowedValues[EDSDK.PropID_Tv] = [0x68, 0x70, 0x78];
        _api.Properties[EDSDK.PropID_WhiteBalance] = 0;
        _api.AllowedValues[EDSDK.PropID_WhiteBalance] = [0, 1, 9];
        _api.Properties[EDSDK.PropID_ColorTemperature] = 5200;
        _api.Properties[EDSDK.PropID_PictureStyle] = 0x81;
        _api.AllowedValues[EDSDK.PropID_PictureStyle] = [0x81, 0x82];
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- Tạo từ máy

    [Fact]
    public async Task FromCurrent_KeepsOnlyWritableSettings_AndRemembersExposureMode()
    {
        await using var device = await StartedDeviceAsync();

        var preset = CanonPreset.FromCurrent(Guid.NewGuid(), "  Tiệc cưới  ", Now, await device.ReadSettingsAsync());

        Assert.Equal("Tiệc cưới", preset.Name);
        Assert.Equal(3u, preset.ExposureMode);
        Assert.DoesNotContain(preset.Values, v => v.Setting == CanonSetting.ExposureMode);

        // Cân bằng trắng đang Tự động → nhiệt độ màu không chỉnh được nên không lưu.
        Assert.DoesNotContain(preset.Values, v => v.Setting == CanonSetting.ColorTemperature);
        Assert.Contains(new CanonPresetValue(CanonSetting.Iso, 0x48, "100"), preset.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ValidateName_Empty_IsRejected(string? name) => Assert.NotNull(CanonPreset.ValidateName(name));

    [Fact]
    public void ValidateName_TooLong_IsRejected() => Assert.NotNull(CanonPreset.ValidateName(new string('a', CanonPreset.MaxNameLength + 1)));

    // ---------------------------------------------------------------- Áp dụng

    [Fact]
    public async Task ApplyPresetAsync_RestoresChangedValues_AndReportsUnchanged()
    {
        await using var device = await StartedDeviceAsync();
        var preset = CanonPreset.FromCurrent(Guid.NewGuid(), "Chuẩn", Now, await device.ReadSettingsAsync());

        // Ai đó chỉnh lệch ISO và Picture Style trên máy.
        _api.Properties[EDSDK.PropID_ISOSpeed] = 0x58;
        _api.Properties[EDSDK.PropID_PictureStyle] = 0x82;

        var result = await device.ApplyPresetAsync(preset);

        Assert.True(result.FullyApplied);
        Assert.Equal(0x48u, _api.Properties[EDSDK.PropID_ISOSpeed]);
        Assert.Equal(0x81u, _api.Properties[EDSDK.PropID_PictureStyle]);
        Assert.Equal(CanonPresetApplyStatus.Applied, Status(result, CanonSetting.Iso));
        Assert.Equal(CanonPresetApplyStatus.Unchanged, Status(result, CanonSetting.Aperture));
        Assert.Equal(2, _api.SetCalls.Count);
    }

    [Fact]
    public async Task ApplyPresetAsync_SetsWhiteBalanceBeforeKelvin()
    {
        await using var device = await StartedDeviceAsync();
        var preset = new CanonPreset(Guid.NewGuid(), "Đèn vàng", Now, 3,
        [
            new(CanonSetting.ColorTemperature, 3200, "3200 K"),
            new(CanonSetting.WhiteBalance, CanonSettingCatalog.WhiteBalanceColorTemperature, "Nhiệt độ màu (K)"),
        ]);

        var result = await device.ApplyPresetAsync(preset);

        Assert.True(result.FullyApplied);
        Assert.Equal([EDSDK.PropID_WhiteBalance, EDSDK.PropID_ColorTemperature], _api.SetCalls.Select(c => c.PropertyId));
        Assert.Equal(3200u, _api.Properties[EDSDK.PropID_ColorTemperature]);
    }

    [Fact]
    public async Task ApplyPresetAsync_SettingNotWritableInCurrentMode_IsSkippedWithReason()
    {
        await using var device = await StartedDeviceAsync();
        var preset = CanonPreset.FromCurrent(Guid.NewGuid(), "Chế độ M", Now, await device.ReadSettingsAsync());

        // Vòng chế độ bị xoay sang Av: tốc độ do máy chọn.
        _api.Properties[EDSDK.PropID_AEMode] = 2;
        _api.Properties[EDSDK.PropID_Tv] = 0x78;
        _api.AllowedValues[EDSDK.PropID_Tv] = [0x78];

        var result = await device.ApplyPresetAsync(preset);

        Assert.False(result.FullyApplied);
        Assert.True(result.ExposureModeMismatch);
        Assert.Equal("M (Chỉnh tay)", result.ExpectedExposureMode);
        var tv = Assert.Single(result.Items, i => i.Setting == CanonSetting.ShutterSpeed);
        Assert.Equal(CanonPresetApplyStatus.Skipped, tv.Status);
        Assert.NotNull(tv.Reason);
    }

    [Fact]
    public async Task ApplyPresetAsync_ValueNotAvailable_IsSkipped()
    {
        await using var device = await StartedDeviceAsync();
        var preset = new CanonPreset(Guid.NewGuid(), "Ống kính khác", Now, 3, [new(CanonSetting.Aperture, 0x18, "f/2.0")]);

        var result = await device.ApplyPresetAsync(preset);

        Assert.Equal(CanonPresetApplyStatus.Skipped, Status(result, CanonSetting.Aperture));
        Assert.Empty(_api.SetCalls);
    }

    [Fact]
    public async Task ApplyPresetAsync_CameraRejectsOneValue_ContinuesWithOthers()
    {
        await using var device = await StartedDeviceAsync();
        var preset = new CanonPreset(Guid.NewGuid(), "Thử", Now, 3,
        [
            new(CanonSetting.Iso, 0x58, "400"),
            new(CanonSetting.PictureStyle, 0x82, "Chân dung"),
        ]);
        _api.RejectedProperties[EDSDK.PropID_ISOSpeed] = true;

        var result = await device.ApplyPresetAsync(preset);

        Assert.Equal(CanonPresetApplyStatus.Failed, Status(result, CanonSetting.Iso));
        Assert.Equal(CanonPresetApplyStatus.Applied, Status(result, CanonSetting.PictureStyle));
    }

    // ---------------------------------------------------------------- Thư viện (cùng mô hình Template Library)

    private string Library => Path.Combine(_directory, "library");

    private string Bundled => Path.Combine(_directory, "bundled");

    [Fact]
    public async Task Store_SaveThenList_RoundTripsWithVietnameseName()
    {
        var store = CreateStore();
        var preset = new CanonPreset(Guid.NewGuid(), "Tiệc cưới – đèn vàng", Now, 3,
            [new(CanonSetting.Iso, 0x48, "100"), new(CanonSetting.WhiteBalance, 9, "Nhiệt độ màu (K)")]);

        await store.SaveAsync(preset);
        var loaded = Assert.Single(await store.ListAsync());

        Assert.Equal(preset.Id, loaded.Id);
        Assert.Equal(preset.Name, loaded.Name);
        Assert.Equal(preset.Values, loaded.Values);
        Assert.Empty(Directory.GetFiles(Library, "*.tmp"));

        var json = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Library, "*.json")));
        Assert.Contains("Tiệc cưới", json);
        Assert.Contains("\"setting\": \"iso\"", json);
    }

    [Fact]
    public async Task Store_SaveSameId_Overwrites_AndListIsSortedByName()
    {
        var store = CreateStore();
        var id = Guid.NewGuid();
        await store.SaveAsync(new CanonPreset(id, "Bản cũ", Now, 3, []));
        await store.SaveAsync(new CanonPreset(id, "Zeta", Now, 3, [new(CanonSetting.Iso, 0x50, "200")]));
        await store.SaveAsync(new CanonPreset(Guid.NewGuid(), "Alpha", Now, 3, []));

        var list = await store.ListAsync();

        Assert.Equal(["Alpha", "Zeta"], list.Select(p => p.Name));
    }

    [Fact]
    public async Task Store_Delete_RemovesFile()
    {
        var store = CreateStore();
        var preset = new CanonPreset(Guid.NewGuid(), "Xóa tôi", Now, 3, []);
        await store.SaveAsync(preset);

        await store.DeleteAsync(preset.Id);

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Store_CorruptFile_IsSkipped_OthersStillLoad()
    {
        var store = CreateStore();
        await store.SaveAsync(new CanonPreset(Guid.NewGuid(), "Tốt", Now, 3, []));
        await File.WriteAllTextAsync(Path.Combine(Library, "hong.json"), "{ hỏng");

        Assert.Equal("Tốt", Assert.Single(await store.ListAsync()).Name);
    }

    [Fact]
    public async Task Store_InvalidName_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateStore().SaveAsync(new CanonPreset(Guid.NewGuid(), " ", Now, 3, [])));
    }

    [Fact]
    public async Task FirstRun_SeedsLibraryFromBundledPresets()
    {
        var bundled = new CanonPreset(Guid.NewGuid(), "Mẫu đi kèm", Now, 3, [new(CanonSetting.Iso, 0x48, "100")]);
        await new CanonPresetStore(Bundled, null, NullLogger<CanonPresetStore>.Instance).SaveAsync(bundled);

        var loaded = Assert.Single(await CreateStore().ListAsync());

        Assert.Equal(bundled.Id, loaded.Id);
        Assert.True(File.Exists(Path.Combine(Library, $"{bundled.Id:N}.json")));
    }

    [Fact]
    public async Task ExistingLibrary_IsNeverOverwrittenByBundledPresets()
    {
        var store = CreateStore();
        var mine = new CanonPreset(Guid.NewGuid(), "Của tôi", Now, 3, []);
        await store.SaveAsync(mine);

        // Bản build mới mang theo bộ mẫu khác: thư viện đã có thì không chép thêm/đè.
        await new CanonPresetStore(Bundled, null, NullLogger<CanonPresetStore>.Instance)
            .SaveAsync(new CanonPreset(Guid.NewGuid(), "Mẫu mới", Now, 3, []));
        var list = await CreateStore().ListAsync();

        Assert.Equal("Của tôi", Assert.Single(list).Name);
    }

    [Fact]
    public async Task FirstRun_NoBundledFolder_CreatesEmptyLibrary()
    {
        Assert.Empty(await CreateStore().ListAsync());
        Assert.True(Directory.Exists(Library));
    }

    [Fact]
    public async Task ExportThenImport_CreatesNewCopyWithUniqueName()
    {
        var store = CreateStore();
        var original = new CanonPreset(Guid.NewGuid(), "Đèn vàng", Now, 3, [new(CanonSetting.WhiteBalance, 9, "Nhiệt độ màu (K)")]);
        await store.SaveAsync(original);
        var exported = Path.Combine(_directory, "share", "den-vang.json");

        await store.ExportAsync(original, exported);
        var imported = await store.ImportAsync(exported, Now.AddDays(1));

        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal("Đèn vàng (2)", imported.Name);
        Assert.Equal(original.Values, imported.Values);
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Theory]
    [InlineData("{ hỏng")]
    [InlineData("""{ "id": "00000000-0000-0000-0000-000000000000", "name": "Thiếu mã", "values": [] }""")]
    [InlineData("""{ "id": "6f1c0b1e-0000-0000-0000-000000000001", "name": "Sai thông số", "values": [ { "setting": "khongCo", "code": 1, "label": "x" } ] }""")]
    [InlineData("""{ "id": "6f1c0b1e-0000-0000-0000-000000000002", "name": "Chế độ chụp", "values": [ { "setting": "exposureMode", "code": 3, "label": "M" } ] }""")]
    public async Task Import_InvalidFile_IsRejectedWithoutChangingLibrary(string content)
    {
        var store = CreateStore();
        var file = Path.Combine(_directory, "nhap.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(file, content);

        await Assert.ThrowsAsync<CanonPresetImportException>(() => store.ImportAsync(file, Now));

        Assert.Empty(await store.ListAsync());
    }

    private static CanonPresetApplyStatus Status(CanonPresetApplyResult result, CanonSetting setting) =>
        Assert.Single(result.Items, i => i.Setting == setting).Status;

    private CanonPresetStore CreateStore() => new(Library, Bundled, NullLogger<CanonPresetStore>.Instance);

    private async Task<CanonCameraDevice> StartedDeviceAsync()
    {
        var device = new CanonCameraDevice(
            _host,
            new CanonCameraOptions { ConnectRetryDelay = TimeSpan.FromMilliseconds(5), BusyRetryDelay = TimeSpan.FromMilliseconds(1) },
            NullLogger<CanonCameraDevice>.Instance);
        Assert.True((await device.StartAsync()).Succeeded);
        return device;
    }
}
