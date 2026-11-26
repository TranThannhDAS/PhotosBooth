using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Photobooth.Camera.Canon;

namespace Photobooth.Desktop.Hosting;

/// <summary>
/// Phục vụ khung Live View của máy ảnh Canon cho giao diện qua <c>WebResourceRequested</c> của WebView2:
/// <c>GET https://liveview.photobooth/canon/frame?after={số thứ tự}</c> → JPEG nhị phân (không Base64, không mở cổng mạng).
/// </summary>
/// <remarks>
/// Yêu cầu được giữ (tối đa <see cref="WaitTimeout"/>) cho tới khi có khung mới hơn <c>after</c>: trình duyệt chỉ xin khung tiếp theo
/// sau khi vẽ xong khung trước, nên tự điều tiết tốc độ và không dồn khung cũ. Chỉ phục vụ nội dung nội bộ của ứng dụng.
/// </remarks>
public sealed class LiveViewEndpoint
{
    public const string BaseUrl = "https://liveview.photobooth/";
    private const string CanonFramePath = "/canon/frame";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(1);

    private readonly CanonCameraDevice _canon;
    private readonly ILogger<LiveViewEndpoint> _logger;

    public LiveViewEndpoint(CanonCameraDevice canon, ILogger<LiveViewEndpoint> logger)
    {
        _canon = canon;
        _logger = logger;
    }

    public void Attach(CoreWebView2 webView)
    {
        webView.AddWebResourceRequestedFilter(BaseUrl + "*", CoreWebView2WebResourceContext.All);
        webView.WebResourceRequested += OnWebResourceRequested;
    }

    // async void: chữ ký event handler bắt buộc của WebView2; mọi ngoại lệ được bắt bên trong.
    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 webView
            || !e.Request.Uri.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var origin = e.Request.Headers.Contains("Origin") ? e.Request.Headers.GetHeader("Origin") : null;
            if (!WebViewPermissionPolicy.IsAppOrigin(origin))
            {
                e.Response = webView.Environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
                return;
            }

            var uri = new Uri(e.Request.Uri);
            if (!string.Equals(uri.AbsolutePath, CanonFramePath, StringComparison.OrdinalIgnoreCase))
            {
                e.Response = webView.Environment.CreateWebResourceResponse(null, 404, "Not Found", CorsHeaders(origin!));
                return;
            }

            var frame = await _canon.LiveView.WaitForNewerAsync(ReadAfter(uri), WaitTimeout);
            if (frame is null)
            {
                // Chưa có khung mới (Live View chưa bật hoặc máy ảnh chậm): trình duyệt hỏi lại.
                e.Response = webView.Environment.CreateWebResourceResponse(null, 204, "No Content", CorsHeaders(origin!));
                return;
            }

            var headers = string.Join("\r\n",
                "Content-Type: image/jpeg",
                "Cache-Control: no-store",
                string.Create(CultureInfo.InvariantCulture, $"X-Frame-Sequence: {frame.Sequence}"),
                CorsHeaders(origin!));
            e.Response = webView.Environment.CreateWebResourceResponse(
                new MemoryStream(frame.Jpeg, writable: false), 200, "OK", headers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi phục vụ khung Live View");
            try
            {
                e.Response = webView.Environment.CreateWebResourceResponse(null, 500, "Internal Server Error", string.Empty);
            }
            catch (Exception responseError) when (responseError is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // WebView đang đóng: không còn gì để trả lời.
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static long ReadAfter(Uri uri)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "after" && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var after))
            {
                return after;
            }
        }

        return 0;
    }

    private static string CorsHeaders(string origin) =>
        $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Expose-Headers: X-Frame-Sequence";
}
