using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Photobooth.Desktop.Components;
using Photobooth.Desktop.Hosting;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Desktop;

/// <summary>
/// Cửa sổ chính: chỉ chứa BlazorWebView và phần nối với WebView2 (quyền, điều hướng, sự cố renderer).
/// Giao diện và nghiệp vụ nằm ở Razor Components và Application Services.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly PhotoboothStoragePaths _paths;
    private readonly IServiceProvider _services;
    private CoreWebView2? _coreWebView;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();

        _services = services;
        _logger = services.GetRequiredService<ILogger<MainWindow>>();
        _paths = services.GetRequiredService<PhotoboothStoragePaths>();
        services.GetRequiredService<WindowFullscreenService>().Attach(this);

        BlazorHost.Services = services;
        BlazorHost.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(PhotoboothApp) });
        BlazorHost.BlazorWebViewInitializing += OnWebViewInitializing;
        BlazorHost.BlazorWebViewInitialized += OnWebViewInitialized;
        BlazorHost.UrlLoading += OnUrlLoading;
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.GetRequiredService<WindowFullscreenService>().Detach(this);
        base.OnClosed(e);
    }

    private void OnWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        // Hồ sơ WebView2 đặt trong %LOCALAPPDATA%\Photobooth, không cạnh file .exe (có thể nằm trong Program Files).
        e.UserDataFolder = _paths.WebViewDataDirectory;
    }

    private void OnWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        _coreWebView = e.WebView.CoreWebView2;
        _coreWebView.PermissionRequested += OnPermissionRequested;
        _coreWebView.ProcessFailed += OnProcessFailed;
        _services.GetRequiredService<LiveViewEndpoint>().Attach(_coreWebView);

        var settings = _coreWebView.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;

        // Màn cảm ứng: không cho chụm hai ngón phóng to cả trang, không cho vuốt ngang để "quay lại trang trước".
        // Cử chỉ chụm trong trình chỉnh vùng cắt vẫn hoạt động (do trang tự xử lý, touch-action: none).
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
#if !DEBUG
        // Bản phát hành: tắt menu chuột phải và phím tắt trình duyệt (F5, Ctrl+P...) để giao diện như ứng dụng kiosk.
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDevToolsEnabled = false;
#endif
        _logger.LogInformation("BlazorWebView đã khởi tạo");
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var decision = WebViewPermissionPolicy.Decide(e.PermissionKind, e.Uri);
        e.State = decision;
        _logger.LogInformation(
            "Yêu cầu quyền {PermissionKind} từ {Origin}: {Decision}",
            e.PermissionKind, WebViewPermissionPolicy.GetOriginForLog(e.Uri), decision);
    }

    private void OnUrlLoading(object? sender, UrlLoadingEventArgs e)
    {
        // Không mở trang ngoài (kể cả bằng trình duyệt hệ thống) từ giao diện kiosk.
        if (!WebViewPermissionPolicy.IsAppOrigin(e.Url))
        {
            e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
            _logger.LogWarning("Đã chặn điều hướng tới nguồn ngoài ứng dụng");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogError(
            "Tiến trình WebView2 gặp sự cố: {Kind}, lý do {Reason}, mã thoát {ExitCode}",
            e.ProcessFailedKind, e.Reason, e.ExitCode);

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowFailurePanel(
                    "Ảnh đã chụp vẫn được giữ nguyên. Nhấn “Tải lại giao diện” để tiếp tục.",
                    canReload: true);
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                ShowFailurePanel(
                    "Không thể khôi phục giao diện. Ảnh đã chụp vẫn được giữ nguyên.\nVui lòng đóng và mở lại ứng dụng.",
                    canReload: false);
                break;
            default:
                // Sự cố ở tiến trình phụ (GPU, tiện ích...): WebView2 tự khởi động lại, chỉ ghi log.
                break;
        }
    }

    private void ShowFailurePanel(string detail, bool canReload)
    {
        Dispatcher.Invoke(() =>
        {
            WebViewFailureDetail.Text = detail;
            ReloadButton.Visibility = canReload ? Visibility.Visible : Visibility.Collapsed;
            BlazorHost.Visibility = Visibility.Hidden;
            WebViewFailurePanel.Visibility = Visibility.Visible;
        });
    }

    private void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        if (_coreWebView is null)
        {
            return;
        }

        try
        {
            WebViewFailurePanel.Visibility = Visibility.Collapsed;
            BlazorHost.Visibility = Visibility.Visible;
            _coreWebView.Reload();
            _logger.LogInformation("Đã tải lại giao diện sau sự cố WebView2");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogError(ex, "Không tải lại được giao diện");
            ShowFailurePanel("Không thể tải lại giao diện. Vui lòng đóng và mở lại ứng dụng.", canReload: false);
        }
    }
}
