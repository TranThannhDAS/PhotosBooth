using System.Buffers.Text;
using System.Security.Cryptography;

namespace Photobooth.Application.Sharing;

/// <summary>
/// Sinh Share Token và URL chia sẻ hoàn toàn offline (không gọi Cloud).
/// Token: 24 byte (192 bit) từ <see cref="RandomNumberGenerator"/>, mã hóa Base64URL không padding = 32 ký tự.
/// Không dùng số thứ tự, đường dẫn file hay thông tin khách.
/// </summary>
public sealed class ShareLinkFactory
{
    public const int TokenBytes = 24;
    public const int TokenLength = 32;

    public ShareLinkFactory(CloudShareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Configuration = options.Describe();
    }

    public CloudShareOptions Options { get; }

    public CloudShareConfiguration Configuration { get; }

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Token đúng định dạng (32 ký tự Base64URL) — dùng khi đọc lại metadata từ ổ đĩa.</summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Tạo link mới; null khi chia sẻ chưa được cấu hình (không bao giờ tạo QR trỏ tới địa chỉ giả).</summary>
    public ShareLink? Create(DateTimeOffset now)
    {
        if (Configuration is not { IsReady: true, BaseUrl: { } baseUrl })
        {
            return null;
        }

        var token = NewToken();
        return new ShareLink(token, new Uri(baseUrl, $"s/{token}").AbsoluteUri, now);
    }
}
