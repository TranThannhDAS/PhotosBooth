using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Photobooth.Cloud.Data;
using Photobooth.Cloud.Api;
using Photobooth.Cloud.Components;
using Photobooth.Cloud.Storage;

namespace Photobooth.Cloud;

/// <summary>Dựng ứng dụng Cloud (tách khỏi Program.cs để test khởi động được ứng dụng thật).</summary>
public static class CloudApp
{
    // Trang chỉ có HTML + CSS của chính site, không script: CSP chặt.
    private const string ContentSecurityPolicy =
        "default-src 'none'; img-src 'self'; style-src 'self'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(CloudApp).Assembly.GetName().Name,
        });

        var settings = CloudSettings.Load(builder.Configuration, builder.Environment);
        var maxRequestBytes = settings.MaxFileBytes + PhotoEndpoints.MultipartOverheadBytes;

        // Giới hạn kích thước ở tầng server (Kestrel) và tầng đọc form; PhotoStore còn đếm byte thực tế khi ghi.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxRequestBytes);
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = maxRequestBytes;
            form.ValueCountLimit = 16;
            form.MultipartHeadersCountLimit = 8;
        });

        builder.Services.AddSingleton(settings);

        // Metadata ảnh: PostgreSQL qua EF Core (factory: mỗi thao tác một DbContext ngắn; PhotoStore là singleton).
        builder.Services.AddDbContextFactory<CloudDbContext>(options => CloudDatabase.Configure(options, settings.ConnectionString));
        builder.Services.AddSingleton(services => new PhotoStore(
            settings.RootPath,
            settings.MaxFileBytes,
            services.GetRequiredService<IDbContextFactory<CloudDbContext>>(),
            services.GetRequiredService<ILogger<PhotoStore>>()));
        builder.Services.AddSingleton(new PhotoLinkBuilder(settings.PublicBaseUrl));
        builder.Services.AddProblemDetails();
        builder.Services.AddRazorComponents();

        var app = builder.Build();

        // Thư mục lưu trữ không tạo/ghi được thì dừng ngay, không chạy rồi mới lỗi khi khách tải ảnh.
        app.Services.GetRequiredService<PhotoStore>().EnsureReady();

        // Schema PostgreSQL bằng EF Core Migrations: tự áp dụng khi Database:MigrateOnStartup = true (mặc định khi phát triển).
        if (settings.MigrateOnStartup)
        {
            using var db = app.Services.GetRequiredService<IDbContextFactory<CloudDbContext>>().CreateDbContext();
            db.Database.Migrate();
        }

        if (settings.UploadApiKey is null && !app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning(
                "Upload chưa được bảo vệ (thiếu Upload:ApiKey). Chỉ chạy trong mạng được kiểm soát, không mở POST /api/photos ra Internet.");
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler();
            app.UseHsts();
        }

        app.UseStatusCodePages();
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            return next(context);
        });
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapPhotoApi();
        app.MapRazorComponents<App>();

        return app;
    }
}
