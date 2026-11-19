using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Photobooth.Application.Sessions;
using Photobooth.Desktop.Hosting;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Desktop;

/// <summary>
/// Điểm khởi động WPF: dựng DI, đăng ký xử lý ngoại lệ toàn cục, kiểm tra WebView2 Runtime
/// rồi mới mở cửa sổ chính.
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = PhotoboothStoragePaths.CreateDefault();
        _services = ServiceRegistration.BuildServiceProvider(paths);
        _logger = _services.GetRequiredService<ILogger<App>>();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _logger.LogInformation("Khởi động Photobooth {Version}", typeof(App).Assembly.GetName().Version);

        if (!IsWebView2RuntimeAvailable())
        {
            MessageBox.Show(
                "Máy tính chưa cài Microsoft Edge WebView2 Runtime nên không thể hiển thị giao diện.\n\n" +
                "Vui lòng cài đặt WebView2 Runtime (Evergreen) rồi mở lại ứng dụng.",
                "Photobooth",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(_services);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Kết thúc phiên (không xóa ảnh gốc) để trạng thái được ghi nhận trong log.
            _services?.GetService<PhotoSessionService>()?.EndCurrentSession();
            _logger?.LogInformation("Đóng ứng dụng (mã thoát {ExitCode})", e.ApplicationExitCode);
        }
        finally
        {
            // Dispose đồng bộ: các service singleton hiện tại không cần dispose bất đồng bộ.
            _services?.Dispose();
            base.OnExit(e);
        }
    }

    private bool IsWebView2RuntimeAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            _logger?.LogInformation("WebView2 Runtime {WebView2Version}", version);
            return !string.IsNullOrEmpty(version);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            _logger?.LogCritical(ex, "Không tìm thấy WebView2 Runtime");
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Ngoại lệ không được xử lý trên UI thread");

        // Giữ ứng dụng chạy: ảnh gốc và trạng thái phiên nằm ở service singleton, không bị mất.
        e.Handled = true;
        MessageBox.Show(
            "Đã xảy ra lỗi không mong muốn. Ảnh đã chụp vẫn được giữ nguyên.\n" +
            "Nếu lỗi lặp lại, vui lòng khởi động lại ứng dụng.",
            "Photobooth",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Ngoại lệ không được xử lý (IsTerminating={IsTerminating})", e.IsTerminating);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Task lỗi không được quan sát");
        e.SetObserved();
    }
}
