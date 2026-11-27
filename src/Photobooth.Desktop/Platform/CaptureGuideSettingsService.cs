using Microsoft.Extensions.Logging;
using Photobooth.Infrastructure.Events;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Thiết lập vùng hướng dẫn Live View theo máy, dùng chung Admin và Kiosk.
/// Đọc/ghi bằng AppStateStore để không cần sửa appsettings.json thủ công.
/// </summary>
public sealed class CaptureGuideSettingsService
{
    private readonly AppStateStore _store;
    private readonly ILogger<CaptureGuideSettingsService> _logger;

    public CaptureGuideSettingsService(AppStateStore store, ILogger<CaptureGuideSettingsService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public CaptureGuideSettings Current => (_store.Current.CaptureGuide ?? new CaptureGuideSettings()).Normalize();

    public async Task SaveAsync(CaptureGuideSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _store.UpdateAsync(state => state with { CaptureGuide = settings }, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Đã lưu vùng hướng dẫn Live View: {Mode}, độ tối {Opacity}%, nhãn={ShowLabel}",
            settings.Mode, settings.OutsideOpacity * 100, settings.ShowGuideLabel);
    }
}
