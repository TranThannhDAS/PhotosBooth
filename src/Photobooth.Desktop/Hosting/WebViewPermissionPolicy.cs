using Microsoft.Web.WebView2.Core;

namespace Photobooth.Desktop.Hosting;

/// <summary>
/// Chính sách quyền của WebView2: chỉ cấp quyền camera cho nội dung nội bộ của ứng dụng,
/// từ chối mọi quyền khác và mọi nguồn khác.
/// </summary>
/// <remarks>
/// BlazorWebView phục vụ nội dung từ địa chỉ ảo https://0.0.0.1 (.NET 8 trở lên) hoặc
/// https://0.0.0.0 (khi bật chế độ tương thích cũ). Cả hai đều chỉ tồn tại bên trong ứng dụng.
/// Yêu cầu quyền camera chỉ phát sinh khi khách chủ động vào màn hình chụp.
/// </remarks>
public static class WebViewPermissionPolicy
{
    private static readonly string[] AppHosts = ["0.0.0.1", "0.0.0.0"];

    public static CoreWebView2PermissionState Decide(CoreWebView2PermissionKind kind, string? uri) =>
        kind == CoreWebView2PermissionKind.Camera && IsAppOrigin(uri)
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;

    public static bool IsAppOrigin(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsAppOrigin(parsed);

    public static bool IsAppOrigin(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && AppHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Chỉ ghi scheme + host vào log, không ghi đường dẫn/query.</summary>
    public static string GetOriginForLog(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.GetLeftPart(UriPartial.Authority) : "(không xác định)";
}
