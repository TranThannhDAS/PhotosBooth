using Microsoft.Extensions.Logging;
using Photobooth.Application.Sharing;

namespace Photobooth.Application.Sessions;

// Offline-First QR Sharing: link chia sẻ được sinh và LƯU trên máy trước khi hiển thị QR; upload lên Cloud chạy nền sau đó.
public sealed partial class PhotoSessionService
{
    /// <summary>Cấu hình chia sẻ QR (để UI báo trạng thái cho người vận hành).</summary>
    public CloudShareConfiguration ShareConfiguration => _shareLinks.Configuration;

    /// <summary>Trạng thái upload của một link chia sẻ (null = chưa có thông tin).</summary>
    public ShareUploadState? GetShareUploadState(ShareLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return _uploads.GetState(link.Token);
    }

    /// <summary>
    /// Bảo đảm phiên bản <paramref name="version"/> có link chia sẻ:
    /// <list type="number">
    /// <item>Đã có → trả đúng link cũ (in lại, mở lại phiên, gọi lại khi giao diện render lại đều không đổi QR).</item>
    /// <item>Chưa có → sinh Share Token ngẫu nhiên và URL (offline), GHI metadata (bền vững), rồi mới trả về để hiển thị QR.</item>
    /// <item>Đưa ảnh thành phẩm vào hàng đợi upload nền (không chờ kết quả upload).</item>
    /// </list>
    /// </summary>
    /// <returns>null nếu chưa cấu hình Cloud, không có phiên bản/file, hoặc chưa ghi được metadata (khi đó KHÔNG hiển thị QR).</returns>
    public Task<ShareLink?> EnsureShareLinkAsync(int version, CancellationToken cancellationToken = default) =>
        EnsureShareLinkAsync(_currentTarget, version, cancellationToken);

    /// <summary>Như <see cref="EnsureShareLinkAsync(int, CancellationToken)"/> nhưng cho một phiên bất kỳ (phiên hiện tại hoặc phiên đã lưu).</summary>
    private async Task<ShareLink?> EnsureShareLinkAsync(SessionTarget target, int version, CancellationToken cancellationToken)
    {
        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (target.Session is not { Directory: not null } session || session.FindVersion(version) is not { } output)
            {
                return null;
            }

            if (output.Share is { } existing)
            {
                ScheduleUpload(session, output, existing);
                return existing;
            }

            if (!File.Exists(output.FilePath) || _shareLinks.Create(_timeProvider.GetUtcNow()) is not { } link)
            {
                return null;
            }

            var attached = false;
            target.Mutate(s =>
            {
                if (s.SessionId != session.SessionId || s.FindVersion(version) is not { Share: null })
                {
                    return null;
                }

                attached = true;
                return WithShare(s, version, link);
            });

            if (!attached)
            {
                return null;
            }

            if (!await target.PersistAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // Token chưa được lưu bền vững → không hiển thị QR (tránh phát QR mà sau khi tắt máy không còn ai biết để upload).
                target.Mutate(s => s.SessionId == session.SessionId && s.FindVersion(version)?.Share == link ? WithShare(s, version, null) : null);
                _logger.LogError("Không ghi được link chia sẻ của phiên {SessionId} v{Version}; không hiển thị QR", session.SessionId, version);
                return null;
            }

            _logger.LogInformation("Đã tạo link chia sẻ cho phiên {SessionId} v{Version}", session.SessionId, version);
            ScheduleUpload(session, output, link);
            return link;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    private void ScheduleUpload(PhotoSession session, OutputVersion output, ShareLink link)
    {
        try
        {
            _uploads.Enqueue(new ShareUploadItem(session.SessionId, session.Directory!, output.Version, output.FilePath, link));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Upload là phần nền: lỗi xếp hàng không được ảnh hưởng QR/phiên; lần khởi động sau sẽ quét lại.
            _logger.LogWarning(ex, "Không xếp hàng upload được cho phiên {SessionId} v{Version}", session.SessionId, output.Version);
        }
    }

    private static PhotoSession WithShare(PhotoSession s, int version, ShareLink? link) =>
        s with { Versions = s.Versions.Select(v => v.Version == version ? v with { Share = link } : v).ToArray() };
}
