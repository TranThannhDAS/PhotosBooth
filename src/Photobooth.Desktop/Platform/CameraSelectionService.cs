using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Persistence;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Webcam dùng cho Kiosk (chọn trong Admin › Camera, ghi nhớ trong SQLite).
/// Chưa chọn → camera mặc định của Windows, đúng hành vi trước khi có tính năng này.
/// </summary>
public sealed class CameraSelectionService
{
    private readonly DeviceSettingsStore _state;
    private readonly ILogger<CameraSelectionService> _logger;

    public CameraSelectionService(DeviceSettingsStore state, ILogger<CameraSelectionService> logger)
    {
        _state = state;
        _logger = logger;
    }

    public CameraSelection Selection => _state.Current.Camera;

    public async Task SaveAsync(CameraSelection selection)
    {
        await _state.SaveCameraAsync(selection).ConfigureAwait(false);
        _logger.LogInformation(
            "Đã chọn camera cho Kiosk: {Camera}",
            selection.IsDefault ? "camera mặc định của Windows" : selection.WebcamLabel ?? "(không tên)");
    }

    /// <summary>
    /// Camera đã chọn được tìm lại theo tên vì mã thiết bị đổi (ví dụ cắm sang cổng USB khác): ghi nhớ mã mới.
    /// Lỗi cập nhật SQLite chỉ được ghi log, không làm hỏng việc mở camera.
    /// </summary>
    public async Task RememberResolvedDeviceAsync(string expectedDeviceId, string resolvedDeviceId)
    {
        try
        {
            if (await _state.UpdateResolvedCameraAsync(expectedDeviceId, resolvedDeviceId).ConfigureAwait(false))
            {
                _logger.LogInformation("Mã thiết bị của camera đã chọn thay đổi; đã cập nhật lựa chọn");
            }
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Không lưu được mã thiết bị mới của camera đã chọn");
        }
    }
}
