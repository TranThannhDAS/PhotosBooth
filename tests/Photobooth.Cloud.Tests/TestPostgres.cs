using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Photobooth.Cloud.Data;

namespace Photobooth.Cloud.Tests;

/// <summary>
/// PostgreSQL thật cho kiểm thử. Kết nối lấy từ biến môi trường <c>PHOTOBOOTH_TEST_POSTGRES</c>, nếu không có thì từ
/// User Secrets của Photobooth.Cloud (<c>ConnectionStrings:DefaultConnection</c>) — mật khẩu không nằm trong source.
/// Mỗi thư mục lưu trữ tạm của test dùng một database riêng <c>photobooth_test_*</c> (tạo bằng migrations, xóa khi xong),
/// không đụng dữ liệu của database phát triển.
/// </summary>
public static class TestPostgres
{
    private const string UserSecretsId = "photobooth-cloud-dev";
    private static readonly ConcurrentDictionary<string, string> Created = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<string?> Base = new(ResolveBase);

    public static bool IsAvailable => Base.Value is not null;

    public const string SkipReason = "Chưa cấu hình PostgreSQL cho kiểm thử (PHOTOBOOTH_TEST_POSTGRES hoặc User Secrets của Photobooth.Cloud).";

    /// <summary>Connection string tới database kiểm thử dành riêng cho <paramref name="rootPath"/> (cùng thư mục → cùng database, để thử khởi động lại).</summary>
    public static string For(string rootPath)
    {
        var builder = new NpgsqlConnectionStringBuilder(Base.Value ?? throw new InvalidOperationException(SkipReason))
        {
            Database = "photobooth_test_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rootPath)))[..16],
        };
        var connection = builder.ConnectionString;
        Created.TryAdd(rootPath, connection);
        return connection;
    }

    /// <summary>DbContext factory đã migrate tới database kiểm thử của <paramref name="rootPath"/>.</summary>
    public static IDbContextFactory<CloudDbContext> Factory(string rootPath)
    {
        var factory = new ContextFactory(For(rootPath));
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        return factory;
    }

    /// <summary>Xóa database kiểm thử đã tạo cho thư mục này (nếu có).</summary>
    public static void Drop(string rootPath)
    {
        if (!Created.TryRemove(rootPath, out var connection))
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        using var db = new ContextFactory(connection).CreateDbContext();
        db.Database.EnsureDeleted();
    }

    private static string? ResolveBase()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("PHOTOBOOTH_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var configuration = new ConfigurationBuilder().AddUserSecrets(UserSecretsId).Build();
        var connection = configuration.GetConnectionString(CloudDatabase.ConnectionName);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return null;
        }

        try
        {
            using var probe = new NpgsqlConnection(connection);
            probe.Open();
            return connection;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return null;
        }
    }

    private sealed class ContextFactory(string connection) : IDbContextFactory<CloudDbContext>
    {
        public CloudDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CloudDbContext>();
            CloudDatabase.Configure(options, connection);
            return new CloudDbContext(options.Options);
        }
    }
}

/// <summary>[Fact] cần PostgreSQL: tự bỏ qua (kèm lý do) khi máy kiểm thử không có PostgreSQL.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!TestPostgres.IsAvailable)
        {
            Skip = TestPostgres.SkipReason;
        }
    }
}

/// <summary>[Theory] cần PostgreSQL.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!TestPostgres.IsAvailable)
        {
            Skip = TestPostgres.SkipReason;
        }
    }
}
