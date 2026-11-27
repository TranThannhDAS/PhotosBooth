using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Photobooth.Infrastructure.Persistence;

/// <summary>
/// SQLite của Desktop: %LOCALAPPDATA%\Photobooth\photobooth.db. Nguồn dữ liệu chính cho cấu hình thiết bị, sự kiện, phiên chụp, ảnh,
/// phiên bản thành phẩm, xuất/in, link chia sẻ và trạng thái upload. Không lưu nội dung ảnh.
/// Không xóa dây chuyền (cascade): dữ liệu nghiệp vụ chỉ được thêm/cập nhật, không bao giờ bị xóa kèm theo.
/// </summary>
public sealed class PhotoboothDbContext(DbContextOptions<PhotoboothDbContext> options) : DbContext(options)
{
    public DbSet<EventRecord> Events => Set<EventRecord>();

    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<PhotoRecord> Photos => Set<PhotoRecord>();

    public DbSet<OutputVersionRecord> OutputVersions => Set<OutputVersionRecord>();

    public DbSet<CloudUploadRecord> CloudUploads => Set<CloudUploadRecord>();

    public DbSet<ExportRecord> Exports => Set<ExportRecord>();

    public DbSet<PrintJobRecord> PrintJobs => Set<PrintJobRecord>();

    public DbSet<DeviceSettingsRecord> DeviceSettings => Set<DeviceSettingsRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceSettingsRecord>(e =>
        {
            e.ToTable("DeviceSettings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PrinterKind).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.WindowsPrinterName).HasMaxLength(260);
            e.Property(x => x.WebcamDeviceId).HasMaxLength(1024);
            e.Property(x => x.WebcamLabel).HasMaxLength(260);
            e.Property(x => x.GuideMode).HasConversion<string>().HasMaxLength(24).IsRequired();
        });

        modelBuilder.Entity<EventRecord>(e =>
        {
            e.ToTable("Events");
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);

            // Mỗi thư mục chỉ thuộc một sự kiện (không phân biệt hoa/thường như đường dẫn Windows).
            e.Property(x => x.FolderPath).HasMaxLength(1024).UseCollation("NOCASE");
            e.HasIndex(x => x.FolderPath).IsUnique();
            e.HasMany(x => x.Sessions).WithOne(x => x.Event).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SessionRecord>(e =>
        {
            e.ToTable("Sessions");
            e.Property(x => x.DirectoryPath).IsRequired().HasMaxLength(512).UseCollation("NOCASE");
            e.HasIndex(x => new { x.EventId, x.DirectoryPath }).IsUnique();
            e.Property(x => x.Status).IsRequired().HasMaxLength(20);
            e.Property(x => x.Filter).IsRequired().HasMaxLength(40);
            e.HasMany(x => x.Photos).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Exports).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.PrintJobs).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PhotoRecord>(e =>
        {
            e.ToTable("Photos");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FilePath).IsRequired().HasMaxLength(512);
            e.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
        });

        modelBuilder.Entity<OutputVersionRecord>(e =>
        {
            e.ToTable("OutputVersions");
            e.Property(x => x.FilePath).IsRequired().HasMaxLength(512);
            e.Property(x => x.ThumbnailPath).HasMaxLength(512);
            e.Property(x => x.TemplateId).IsRequired().HasMaxLength(200);
            e.Property(x => x.Filter).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.SessionId, x.Version }).IsUnique();

            // Share Token: duy nhất toàn máy, phân biệt hoa/thường (Base64URL).
            e.Property(x => x.ShareToken).HasMaxLength(64);
            e.Property(x => x.ShareUrl).HasMaxLength(1024);
            e.HasIndex(x => x.ShareToken).IsUnique();
            e.HasOne(x => x.CloudUpload).WithOne(x => x.OutputVersion)
                .HasForeignKey<CloudUploadRecord>(x => x.OutputVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudUploadRecord>(e =>
        {
            e.ToTable("CloudUploads");
            e.HasKey(x => x.OutputVersionId);
            e.Property(x => x.OutputVersionId).ValueGeneratedNever();
            e.Property(x => x.Status).IsRequired().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ExportRecord>(e =>
        {
            e.ToTable("Exports");
            e.Property(x => x.Format).IsRequired().HasMaxLength(10);
            e.Property(x => x.FilePath).IsRequired().HasMaxLength(1024);
            e.HasIndex(x => x.SessionId);
        });

        modelBuilder.Entity<PrintJobRecord>(e =>
        {
            e.ToTable("PrintJobs");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FilePath).IsRequired().HasMaxLength(512);
            e.Property(x => x.PrinterName).IsRequired().HasMaxLength(260);
            e.Property(x => x.Status).IsRequired().HasMaxLength(20);
            e.Property(x => x.ErrorMessage).HasMaxLength(500);
            e.HasIndex(x => x.SessionId);
        });
    }
}

/// <summary>Cấu hình và khởi tạo database (áp dụng migration khi ứng dụng khởi động).</summary>
public static class PhotoboothDatabase
{
    public const string FileName = "photobooth.db";

    public static void Configure(DbContextOptionsBuilder options, string databaseFile) =>
        options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
        }.ToString());

    /// <summary>
    /// Tạo/cập nhật schema bằng EF Core Migrations (không chuyển dữ liệu JSON cũ). Bật WAL để đọc không chặn ghi
    /// và an toàn hơn khi ứng dụng dừng đột ngột.
    /// </summary>
    public static void Migrate(IDbContextFactory<PhotoboothDbContext> factory)
    {
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>Dùng cho <c>dotnet ef migrations</c> (không chạy lúc ứng dụng hoạt động).</summary>
public sealed class PhotoboothDbContextDesignFactory : IDesignTimeDbContextFactory<PhotoboothDbContext>
{
    public PhotoboothDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PhotoboothDbContext>();
        PhotoboothDatabase.Configure(options, Path.Combine(Path.GetTempPath(), "photobooth-design.db"));
        return new PhotoboothDbContext(options.Options);
    }
}
