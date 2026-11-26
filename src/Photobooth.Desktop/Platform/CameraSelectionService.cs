using System.IO;
using Microsoft.Extensions.Logging;
using Photobooth.Infrastructure.Events;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Webcam dùng cho Kiosk (chọn trong Admin › Camera, ghi nhớ trong app-state.json).
/// Chưa chọn → camera mặc định của Windows, đúng hành vi trước khi có tính năng này.
/// </summary>
public sealed class CameraSelectionService
{
    private readonly AppStateStore _state;
    private readonly ILogger<CameraSelectionService> _logger;

    public CameraSelectionService(AppStateStore state, ILogger<CameraSelectionService> logger)
    {
        _state = state;
        _logger = logger;
    }

    public CameraSelection Selection => _state.Current.Camera;

    public async Task SaveAsync(CameraSelection selection)
    {
        await _state.UpdateAsync(s => s with { Camera = selection }).ConfigureAwait(false);
        _logger.LogInformation(
            "Đã chọn camera cho Kiosk: {Camera}",
            selection.IsDefault ? "camera mặc định của Windows" : selection.WebcamLabel ?? "(không tên)");
    }

    /// <summary>
    /// Camera đã chọn được tìm lại theo tên vì mã thiết bị đổi (ví dụ cắm sang cổng USB khác): ghi nhớ mã mới.
    /// Lỗi ghi file chỉ được ghi log, không làm hỏng việc mở camera.
    /// </summary>
    public async Task RememberResolvedDeviceAsync(string expectedDeviceId, string resolvedDeviceId)
    {
        try
        {
            await _state.UpdateAsync(s => s.Camera.WebcamDeviceId == expectedDeviceId
                    ? s with { Camera = s.Camera with { WebcamDeviceId = resolvedDeviceId } }
                    : s)
                .ConfigureAwait(false);
            _logger.LogInformation("Mã thiết bị của camera đã chọn thay đổi; đã cập nhật lựa chọn");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không lưu được mã thiết bị mới của camera đã chọn");
        }
    }
}
