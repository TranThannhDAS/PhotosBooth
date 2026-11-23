namespace Photobooth.Cloud;

/// <summary>
/// Cấu hình đã kiểm tra của Cloud (đọc từ appsettings, biến môi trường hoặc tham số dòng lệnh).
/// Cấu hình sai thì dừng khởi động ngay với thông báo rõ ràng, không chạy với giá trị đoán.
/// </summary>
public sealed class CloudSettings
{
    public const int DefaultMaxFileSizeMB = 20;
    public const int MaxAllowedFileSizeMB = 100;
    public const int MinApiKeyLength = 32;

    private CloudSettings(string rootPath, int maxFileSizeMB, Uri? publicBaseUrl, string? uploadApiKey, string connectionString, bool migrateOnStartup)
    {
        RootPath = rootPath;
        MaxFileSizeMB = maxFileSizeMB;
        PublicBaseUrl = publicBaseUrl;
        UploadApiKey = uploadApiKey;
        ConnectionString = connectionString;
        MigrateOnStartup = migrateOnStartup;
    }

    /// <summary>Thư mục gốc lưu ảnh (đường dẫn tuyệt đối, nằm ngoài thư mục publish).</summary>
    public string RootPath { get; }

    public int MaxFileSizeMB { get; }

    public long MaxFileBytes => MaxFileSizeMB * 1024L * 1024L;

    /// <summary>Địa chỉ công khai dùng để tạo link (null = lấy theo request).</summary>
    public Uri? PublicBaseUrl { get; }

    /// <summary>
    /// API Key của thiết bị cho <c>POST /api/photos</c> (header <c>X-Api-Key</c>). null = không bảo vệ upload —
    /// chỉ dùng khi phát triển hoặc trong mạng được kiểm soát. Không ghi khóa vào source code: đặt bằng biến môi trường/file cấu hình của server.
    /// </summary>
    public string? UploadApiKey { get; }

    /// <summary>
    /// <c>ConnectionStrings:DefaultConnection</c> tới PostgreSQL (metadata ảnh). Mật khẩu đặt bằng User Secrets (phát triển)
    /// hoặc biến môi trường <c>ConnectionStrings__DefaultConnection</c> (triển khai) — không nằm trong appsettings/source.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// <c>Database:MigrateOnStartup</c>: áp dụng EF Core Migrations khi khởi động (bật trong appsettings.Development.json).
    /// Production nên tắt và cập nhật schema chủ động bằng <c>dotnet ef database update</c> hoặc migration bundle.
    /// </summary>
    public bool MigrateOnStartup { get; }

    public static CloudSettings Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var rootPath = ResolveRootPath(configuration["Storage:RootPath"], environment);
        var maxFileSizeMB = ReadMaxFileSize(configuration["Storage:MaxFileSizeMB"]);
        var publicBaseUrl = ReadPublicBaseUrl(configuration["PublicBaseUrl"]);
        var apiKey = ReadApiKey(configuration["Upload:ApiKey"]);
        var connectionString = configuration.GetConnectionString(Data.CloudDatabase.ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Chưa cấu hình ConnectionStrings:DefaultConnection (PostgreSQL). Khi phát triển dùng dotnet user-secrets; khi triển khai dùng biến môi trường ConnectionStrings__DefaultConnection.");
        }

        var migrate = bool.TryParse(configuration["Database:MigrateOnStartup"], out var flag) && flag;
        return new CloudSettings(rootPath, maxFileSizeMB, publicBaseUrl, apiKey, connectionString, migrate);
    }

    private static string? ReadApiKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var key = configured.Trim();
        if (key.Length < MinApiKeyLength)
        {
            throw new InvalidOperationException($"Upload:ApiKey phải dài ít nhất {MinApiKeyLength} ký tự (nên sinh ngẫu nhiên).");
        }

        return key;
    }

    private static string ResolveRootPath(string? configured, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Chưa cấu hình Storage:RootPath. Hãy đặt đường dẫn tuyệt đối tới thư mục lưu ảnh, nằm ngoài thư mục publish.");
            }

            // Chỉ khi phát triển: dùng thư mục dữ liệu của người dùng hiện tại.
            configured = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Photobooth.Cloud",
                "Photos");
        }

        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException("Storage:RootPath phải là đường dẫn tuyệt đối.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));

        // Ảnh không được nằm trong thư mục publish/mã nguồn: deploy lại có thể xóa hoặc ghi đè thư mục đó,
        // và thư mục web có thể bị phục vụ trực tiếp ra ngoài.
        foreach (var protectedFolder in new[] { AppContext.BaseDirectory, environment.ContentRootPath })
        {
            if (!string.IsNullOrEmpty(protectedFolder) && IsSameOrInside(root, protectedFolder))
            {
                throw new InvalidOperationException(
                    "Storage:RootPath không được nằm trong thư mục ứng dụng/publish. Hãy dùng một thư mục dữ liệu riêng.");
            }
        }

        return root;
    }

    private static int ReadMaxFileSize(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultMaxFileSizeMB;
        }

        if (!int.TryParse(configured, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < 1
            || value > MaxAllowedFileSizeMB)
        {
            throw new InvalidOperationException(
                $"Storage:MaxFileSizeMB phải là số nguyên từ 1 đến {MaxAllowedFileSizeMB}.");
        }

        return value;
    }

    private static Uri? ReadPublicBaseUrl(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "PublicBaseUrl phải là địa chỉ http/https tuyệt đối, không có query hoặc fragment (ví dụ https://photo.example.com).");
        }

        return uri;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, parent, comparison)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
}
