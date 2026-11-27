using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Configuration;

public sealed class DeviceSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SQLite_SaveAndReload_PreservesPrinterWebcamAndGuide()
    {
        var paths = new PhotoboothStoragePaths(_temp.Path);
        var factory = TestDatabase.For(paths);
        var store = new DeviceSettingsStore(factory);

        Assert.Equal(PrinterKind.None, store.Current.Printer.Kind);
        Assert.True(store.Current.Camera.IsDefault);
        Assert.Equal(CaptureGuideMode.DimOutside, store.Current.CaptureGuide.Mode);
        Assert.Equal(0.65, store.Current.CaptureGuide.OutsideOpacity, 6);

        await store.SavePrinterAsync(new PrinterSelection
        {
            Kind = PrinterKind.Windows, WindowsPrinterName = "Máy in Windows 01",
        });
        await store.SaveCameraAsync(new CameraSelection
        {
            WebcamDeviceId = "usb-camera-1", WebcamLabel = "USB Camera",
        });
        await store.SaveCaptureGuideAsync(new CaptureGuideSettings
        {
            Mode = CaptureGuideMode.Outline,
            OutsideOpacity = 0.4,
            ShowGuideLabel = false,
        });

        using (var db = TestDatabase.Open(paths))
        {
            var row = Assert.Single(db.DeviceSettings);
            Assert.Equal(DeviceSettingsRecord.SingletonId, row.Id);
            Assert.Equal(PrinterKind.Windows, row.PrinterKind);
            Assert.Equal("Máy in Windows 01", row.WindowsPrinterName);
            Assert.Equal("usb-camera-1", row.WebcamDeviceId);
            Assert.Equal(CaptureGuideMode.Outline, row.GuideMode);
        }

        // Một instance mới phải đọc từ SQLite, không dựa vào cache của instance cũ.
        var reopened = new DeviceSettingsStore(factory);
        Assert.Equal("Máy in Windows 01", reopened.Current.Printer.WindowsPrinterName);
        Assert.Equal("USB Camera", reopened.Current.Camera.WebcamLabel);
        Assert.Equal(CaptureGuideMode.Outline, reopened.Current.CaptureGuide.Mode);
        Assert.Equal(0.4, reopened.Current.CaptureGuide.OutsideOpacity, 6);
        Assert.False(reopened.Current.CaptureGuide.ShowGuideLabel);
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "app-state.json")));
    }

    [Fact]
    public async Task SQLite_ChangedDeviceDoesNotOverwriteOtherSettings()
    {
        var paths = new PhotoboothStoragePaths(_temp.Path);
        var store = new DeviceSettingsStore(TestDatabase.For(paths));

        await store.SavePrinterAsync(new PrinterSelection { Kind = PrinterKind.Mock });
        await store.SaveCaptureGuideAsync(new CaptureGuideSettings
        {
            Mode = CaptureGuideMode.CropPreview,
        });
        await store.SaveCameraAsync(new CameraSelection { WebcamDeviceId = "camera-old" });

        Assert.False(await store.UpdateResolvedCameraAsync("another-camera", "wrong-id"));
        Assert.True(await store.UpdateResolvedCameraAsync("camera-old", "camera-new"));
        Assert.Equal("camera-new", store.Current.Camera.WebcamDeviceId);
        Assert.Equal(PrinterKind.Mock, store.Current.Printer.Kind);
        Assert.Equal(CaptureGuideMode.CropPreview, store.Current.CaptureGuide.Mode);

        var reopened = new DeviceSettingsStore(TestDatabase.For(paths));
        Assert.Equal("camera-new", reopened.Current.Camera.WebcamDeviceId);
        Assert.Equal(PrinterKind.Mock, reopened.Current.Printer.Kind);
        Assert.Equal(CaptureGuideMode.CropPreview, reopened.Current.CaptureGuide.Mode);
    }

    [Fact]
    public async Task SQLite_IgnoresExistingLegacyAppStateJson()
    {
        var paths = new PhotoboothStoragePaths(_temp.Path);
        var legacyPath = Path.Combine(paths.RootDirectory, "app-state.json");
        Directory.CreateDirectory(paths.RootDirectory);
        var original = """{"printer":{"kind":"windows","windowsPrinterName":"Legacy Printer"},"camera":{"webcamDeviceId":"old-cam"},"captureGuide":{"mode":"outline","outsideOpacity":0.3}}""";
        await File.WriteAllTextAsync(legacyPath, original);

        var store = new DeviceSettingsStore(TestDatabase.For(paths));

        Assert.Equal(PrinterKind.None, store.Current.Printer.Kind);
        Assert.True(store.Current.Camera.IsDefault);
        Assert.Equal(CaptureGuideMode.DimOutside, store.Current.CaptureGuide.Mode);

        await store.SavePrinterAsync(new PrinterSelection { Kind = PrinterKind.Mock });
        Assert.Equal(original, await File.ReadAllTextAsync(legacyPath));
    }
}
