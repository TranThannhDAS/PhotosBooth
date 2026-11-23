using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Admin;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Application.Templates;
using Photobooth.Desktop.Camera;
using Photobooth.Desktop.Platform;
using Photobooth.Infrastructure.Configuration;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Export;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Logging;
using Photobooth.Infrastructure.Printing;
using Photobooth.Infrastructure.Sharing;
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

            // Không ghi từng câu SQL của EF Core vào log (chỉ cảnh báo/lỗi).
            builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
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
        services.AddSingleton(sp => sp.GetRequiredService<PhotoboothSettings>().Print);
        services.AddSingleton(sp => sp.GetRequiredService<PhotoboothSettings>().Cloud);

        // Chia sẻ QR (Offline-First): link sinh trên máy; upload lên Cloud chạy nền, trạng thái lưu trong thư mục phiên.
        services.AddSingleton<ShareLinkFactory>();
        services.AddSingleton<CloudUploadService>();
        services.AddSingleton<ICloudUploadQueue>(sp => sp.GetRequiredService<CloudUploadService>());
        services.AddSingleton<QrCodeRenderer>();

        // Metadata nghiệp vụ: SQLite %LOCALAPPDATA%\Photobooth\photobooth.db (schema tạo bằng EF Core Migrations lúc khởi động).
        // Ảnh vẫn là file trong Event Workspace. Factory: mỗi thao tác một DbContext ngắn (service đều là singleton).
        Directory.CreateDirectory(paths.RootDirectory);
        services.AddDbContextFactory<PhotoboothDbContext>(options => PhotoboothDatabase.Configure(options, paths.DatabaseFilePath));

        // Sự kiện (Event Workspace) và lưu trữ phiên lâu dài theo sự kiện.
        // app-state.json chỉ còn giữ lựa chọn máy in (cấu hình máy, không phải dữ liệu nghiệp vụ).
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<EventWorkspaceService>();
        services.AddSingleton<ISessionArchive, EventSessionArchive>();
        services.AddSingleton<AdminAccessService>();
        services.AddSingleton<EventGalleryService>();

        // In: máy in giả lập hoặc máy in Windows (chọn trong Admin).
        services.AddSingleton<MockPrinter>();
        services.AddSingleton<PrinterSelectionService>();
        services.AddSingleton<IPrinterProvider>(sp => sp.GetRequiredService<PrinterSelectionService>());

        // Template, ghép ảnh, xuất file.
        // Template Library dùng chung: %LOCALAPPDATA%\Photobooth\Templates (ghi được); lần đầu chép từ Templates/ đi kèm ứng dụng.
        // Kiosk đọc qua ITemplateCatalog (chỉ template hợp lệ và đang bật).
        services.AddSingleton(sp => new TemplateLibrary(
            Path.Combine(paths.RootDirectory, "Templates"),
            Path.Combine(AppContext.BaseDirectory, "Templates"),
            sp.GetRequiredService<IPhotoComposer>(),
            sp.GetRequiredService<ILogger<TemplateLibrary>>()));
        services.AddSingleton<ITemplateCatalog>(sp => sp.GetRequiredService<TemplateLibrary>());
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
