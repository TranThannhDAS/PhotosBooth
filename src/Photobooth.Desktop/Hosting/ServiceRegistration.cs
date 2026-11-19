using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Desktop.Camera;
using Photobooth.Desktop.Platform;
using Photobooth.Infrastructure.Configuration;
using Photobooth.Infrastructure.Export;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Logging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;

namespace Photobooth.Desktop.Hosting;

/// <summary>Composition root: nơi duy nhất đăng ký service của ứng dụng.</summary>
public static class ServiceRegistration
{
    public static ServiceProvider BuildServiceProvider(PhotoboothStoragePaths paths)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogsDirectory, LogLevel.Information, TimeProvider.System));
        });

        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);

        // Cấu hình: %LOCALAPPDATA%\Photobooth\settings.json (không bắt buộc), lỗi → mặc định.
        services.AddSingleton(sp => PhotoboothSettings.Load(
            paths.SettingsFilePath, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Photobooth.Settings")));
        services.AddSingleton(sp => sp.GetRequiredService<PhotoboothSettings>().Capture);
        services.AddSingleton(sp => sp.GetRequiredService<PhotoboothSettings>().Export);

        // Template, ghép ảnh, xuất file.
        services.AddSingleton<ITemplateCatalog>(sp => new JsonTemplateCatalog(
            Path.Combine(AppContext.BaseDirectory, "Templates"), sp.GetRequiredService<ILogger<JsonTemplateCatalog>>()));
        services.AddSingleton<IPhotoComposer, SkiaPhotoComposer>();
        services.AddSingleton<IImagePreviewRenderer, SkiaImagePreviewRenderer>();
        services.AddSingleton<IComposedPhotoExporter, ComposedPhotoExporter>();

        // Nghiệp vụ: singleton để trạng thái phiên sống ngoài vòng đời component/WebView.
        services.AddSingleton<IOriginalPhotoStore, LocalOriginalPhotoStore>();
        services.AddSingleton<PhotoSessionService>();

        // Tích hợp nền tảng Windows.
        services.AddSingleton<ExportFolderService>();
        services.AddSingleton<WindowsShellService>();

        // Camera WebView2 phụ thuộc IJSRuntime của BlazorWebView → scoped theo WebView.
        services.AddScoped<WebViewCameraDevice>();
        services.AddScoped<ICameraDevice>(sp => sp.GetRequiredService<WebViewCameraDevice>());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
