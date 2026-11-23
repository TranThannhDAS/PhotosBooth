using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Photobooth.Cloud.Data;

/// <summary>
/// Metadata của một ảnh trên Cloud. Nội dung ảnh KHÔNG nằm trong database: file ở {Storage:RootPath}/{StoragePath}.
/// Một ảnh "đã công bố" khi có bản ghi này VÀ file tồn tại.
/// </summary>
public sealed class CloudPhoto
{
    /// <summary>ID ảnh do server sinh (26 ký tự base32 chữ thường), khóa chính.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Share Token do Desktop sinh (32 ký tự Base64URL), duy nhất; null với ảnh upload thường.</summary>
    public string? ShareToken { get; set; }

    /// <summary>Đường dẫn file, tương đối với Storage:RootPath (dấu "/"). Không lộ ra ngoài API.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>jpeg | png (định dạng thực tế nhận dạng từ nội dung).</summary>
    public string Format { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 (hex chữ thường) của nội dung — chống upload trùng/ghi đè bằng cùng token.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>PostgreSQL của Cloud: chỉ một bảng metadata ảnh (chưa có Event/User/Session vì Cloud chưa dùng).</summary>
public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    public DbSet<CloudPhoto> Photos => Set<CloudPhoto>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CloudPhoto>(e =>
        {
            e.ToTable("photos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasMaxLength(26).ValueGeneratedNever();
            e.Property(x => x.ShareToken).HasColumnName("share_token").HasMaxLength(32);
            e.HasIndex(x => x.ShareToken).IsUnique();
            e.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.StoragePath).IsUnique();
            e.Property(x => x.Format).HasColumnName("format").HasMaxLength(10).IsRequired();
            e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(50).IsRequired();
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
        });
    }
}

/// <summary>
/// Dùng cho <c>dotnet ef</c>: đọc <c>ConnectionStrings:DefaultConnection</c> từ biến môi trường hoặc User Secrets
/// (không ghi mật khẩu vào source). Không có cấu hình thì dùng chuỗi không mật khẩu — đủ cho <c>migrations add</c>.
/// </summary>
public sealed class CloudDbContextDesignFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<CloudDbContextDesignFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connection = configuration.GetConnectionString(CloudDatabase.ConnectionName) ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres";
        var options = new DbContextOptionsBuilder<CloudDbContext>();
        CloudDatabase.Configure(options, connection);
        return new CloudDbContext(options.Options);
    }
}

public static class CloudDatabase
{
    public const string ConnectionName = "DefaultConnection";

    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3));
}
