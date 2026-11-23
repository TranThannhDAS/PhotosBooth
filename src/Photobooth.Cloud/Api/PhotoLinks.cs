using Photobooth.Cloud.Storage;

namespace Photobooth.Cloud.Api;

/// <summary>Các đường dẫn công khai của một ảnh. Với ảnh chia sẻ qua QR, <see cref="Id"/> là Share Token.</summary>
public sealed record PhotoLinks(string Id, string ViewUrl, string FileUrl, string DownloadUrl);

/// <summary>
/// Tạo link tuyệt đối cho ảnh. Ưu tiên <c>PublicBaseUrl</c> đã cấu hình; nếu không có thì dùng địa chỉ của request
/// (chỉ phù hợp khi chạy nội bộ — khi triển khai nên cấu hình PublicBaseUrl và AllowedHosts).
/// Ảnh chia sẻ (Share Token) có trang xem <c>/s/{token}</c> — đúng URL Desktop đã in trong QR.
/// </summary>
public sealed class PhotoLinkBuilder(Uri? publicBaseUrl)
{
    public PhotoLinks For(HttpRequest request, string id)
    {
        var baseUrl = publicBaseUrl is not null
            ? publicBaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        var escapedId = Uri.EscapeDataString(id);
        var viewPath = PhotoStore.IsValidShareToken(id) ? $"/s/{escapedId}" : $"/photos/{escapedId}";
        return new PhotoLinks(
            id,
            baseUrl + viewPath,
            $"{baseUrl}/api/photos/{escapedId}/file",
            $"{baseUrl}/api/photos/{escapedId}/download");
    }
}
