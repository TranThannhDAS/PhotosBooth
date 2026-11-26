using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Events;

/// <summary>Lựa chọn webcam cho Kiosk được lưu cùng máy in trong app-state.json.</summary>
public sealed class AppStateCameraSelectionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string StateFile => Path.Combine(_temp.Path, "app-state.json");

    private AppStateStore CreateStore() =>
        new(new PhotoboothStoragePaths(_temp.Path), NullLogger<AppStateStore>.Instance);

    [Fact]
    public void NewMachine_UsesWindowsDefaultCamera()
    {
        var store = CreateStore();

        Assert.True(store.Current.Camera.IsDefault);
    }

    [Fact]
    public async Task SavedWebcam_SurvivesRestart_AndKeepsPrinterSelection()
    {
        var store = CreateStore();
        await store.UpdateAsync(s => s with { Printer = new PrinterSelection { Kind = PrinterKind.Mock } });
        await store.UpdateAsync(s => s with { Camera = new CameraSelection { WebcamDeviceId = "abc123", WebcamLabel = "Logitech C920" } });

        var reloaded = CreateStore().Current;

        Assert.Equal("abc123", reloaded.Camera.WebcamDeviceId);
        Assert.Equal("Logitech C920", reloaded.Camera.WebcamLabel);
        Assert.False(reloaded.Camera.IsDefault);
        Assert.Equal(PrinterKind.Mock, reloaded.Printer.Kind);
    }

    [Fact]
    public async Task ChoosingDefaultAgain_ClearsSelection()
    {
        var store = CreateStore();
        await store.UpdateAsync(s => s with { Camera = new CameraSelection { WebcamDeviceId = "abc123", WebcamLabel = "Logitech C920" } });
        await store.UpdateAsync(s => s with { Camera = new CameraSelection() });

        Assert.True(CreateStore().Current.Camera.IsDefault);
    }

    [Fact]
    public void LegacyFileWithoutCamera_LoadsAsDefault_WithoutLosingPrinter()
    {
        File.WriteAllText(StateFile, """{ "printer": { "kind": "mock" } }""");

        var state = CreateStore().Current;

        Assert.True(state.Camera.IsDefault);
        Assert.Equal(PrinterKind.Mock, state.Printer.Kind);
    }

    [Fact]
    public async Task ComputedIsDefault_IsNotWrittenToFile()
    {
        var store = CreateStore();
        await store.UpdateAsync(s => s with { Camera = new CameraSelection { WebcamDeviceId = "abc123" } });

        var json = await File.ReadAllTextAsync(StateFile);

        Assert.Contains("\"webcamDeviceId\": \"abc123\"", json);
        Assert.DoesNotContain("isDefault", json, StringComparison.OrdinalIgnoreCase);
    }
}
