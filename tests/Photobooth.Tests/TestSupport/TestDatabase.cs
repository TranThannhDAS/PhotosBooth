using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Tests.TestSupport;

/// <summary>
/// SQLite thật (file photobooth.db trong thư mục dữ liệu tạm của test), schema tạo bằng đúng EF Core Migrations của ứng dụng.
/// Cùng thư mục dữ liệu → cùng database, nên "khởi động lại ứng dụng" trong test đọc lại được dữ liệu đã ghi.
/// </summary>
internal static class TestDatabase
{
    private static readonly ConcurrentDictionary<string, Lazy<Factory>> Factories = new(StringComparer.OrdinalIgnoreCase);

    public static IDbContextFactory<PhotoboothDbContext> For(PhotoboothStoragePaths paths) =>
        Factories.GetOrAdd(paths.DatabaseFilePath, file => new Lazy<Factory>(() => new Factory(file))).Value;

    /// <summary>Đọc trực tiếp database (kiểm tra dữ liệu đã lưu).</summary>
    public static PhotoboothDbContext Open(PhotoboothStoragePaths paths) => For(paths).CreateDbContext();

    private sealed class Factory : IDbContextFactory<PhotoboothDbContext>
    {
        private readonly DbContextOptions<PhotoboothDbContext> _options;

        public Factory(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var builder = new DbContextOptionsBuilder<PhotoboothDbContext>();
            PhotoboothDatabase.Configure(builder, file);
            _options = builder.Options;
            PhotoboothDatabase.Migrate(this);
        }

        public PhotoboothDbContext CreateDbContext() => new(_options);
    }
}
