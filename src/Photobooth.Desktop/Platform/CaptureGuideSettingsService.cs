using Microsoft.Extensions.Logging;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Persistence;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Thiết lập vùng hướng dẫn Live View theo máy, dùng chung Admin và Kiosk.
/// Đọc/ghi bằng SQLite qua DeviceSettingsStore, không dùng app-state.json.
/// </summary>
public sealed class CaptureGuideSettingsService
{
    private readonly DeviceSettingsStore _store;
    private readonly ILogger<CaptureGuideSettingsService> _logger;

    public CaptureGuideSettingsService(DeviceSettingsStore store, ILogger<CaptureGuideSettingsService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public CaptureGuideSettings Current => (_store.Current.CaptureGuide ?? new CaptureGuideSettings()).Normalize();

    public async Task SaveAsync(CaptureGuideSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _store.SaveCaptureGuideAsync(settings, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Đã lưu vùng hướng dẫn Live View: {Mode}, độ tối {Opacity}%, nhãn={ShowLabel}",
            settings.Mode, settings.OutsideOpacity * 100, settings.ShowGuideLabel);
    }
}
