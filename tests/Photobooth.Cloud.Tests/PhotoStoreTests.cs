using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Cloud.Data;
using Photobooth.Cloud.Storage;

namespace Photobooth.Cloud.Tests;

/// <summary>PhotoStore: file trên ổ đĩa + metadata trong PostgreSQL (database kiểm thử riêng cho từng test).</summary>
public sealed class PhotoStoreTests : IDisposable
{
    private readonly TempFolder _root = new();

    public void Dispose() => _root.Dispose();

    private PhotoStore CreateStore(long maxBytes = 1024 * 1024, Func<string>? ids = null, IDbContextFactory<CloudDbContext>? db = null) =>
        new(_root.Path, maxBytes, db ?? TestPostgres.Factory(_root.Path), NullLogger<PhotoStore>.Instance, ids);

    private CloudDbContext OpenDb() => TestPostgres.Factory(_root.Path).CreateDbContext();

    [Fact]
    public void NewId_IsRandomLowercaseBase32()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => PhotoStore.NewId()).ToHashSet();

        Assert.Equal(1000, ids.Count);
        Assert.All(ids, id => Assert.True(PhotoStore.IsValidId(id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("abcdefghijklmnopqrstuvwxy1")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    [InlineData("ab\\defghijklmnopqrstuvwxyz")]
    public void IsValidId_RejectsMalformed(string? id) => Assert.False(PhotoStore.IsValidId(id));

    [PostgresFact]
    public async Task Save_NeverOverwritesExistingPhoto_WhenIdCollides()
    {
        var existingId = PhotoStore.NewId();
        var freshId = PhotoStore.NewId();
        var queue = new Queue<string>([existingId, existingId, freshId]);
        var store = CreateStore(ids: () => queue.Dequeue());
        var existingPath = Path.Combine(_root.Path, existingId[..2], existingId + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
        await File.WriteAllBytesAsync(existingPath, SampleImages.Png);

        var saved = await store.SaveAsync(new MemoryStream(SampleImages.Jpeg), CancellationToken.None);

        Assert.Equal(freshId, saved.Id);
        Assert.Equal(SampleImages.Png, await File.ReadAllBytesAsync(existingPath));
        Assert.Equal(SampleImages.Jpeg, await File.ReadAllBytesAsync(saved.FullPath));
    }

    [PostgresFact]
    public async Task Save_TooLarge_ThrowsAndLeavesNoTempFileOrMetadata()
    {
        var store = CreateStore(maxBytes: 1000);

        var ex = await Assert.ThrowsAsync<PhotoRejectedException>(
            () => store.SaveAsync(new MemoryStream(SampleImages.BuildJpeg(1001)), CancellationToken.None));

        Assert.Equal(PhotoRejectReason.TooLarge, ex.Reason);
        Assert.Empty(Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories));
        await using var db = OpenDb();
        Assert.Empty(await db.Photos.ToListAsync());
    }

    [PostgresFact]
    public async Task SharedPhoto_MetadataStoredInPostgres_WithRelativePathAndHash()
    {
        var store = CreateStore();
        var token = ShareApiTests.NewToken();

        var saved = await store.SaveSharedAsync(token, new MemoryStream(SampleImages.Png), CancellationToken.None);

        await using var db = OpenDb();
        var row = await db.Photos.SingleAsync();
        Assert.True(saved.Created);
        Assert.Equal(token, row.ShareToken);
        Assert.True(PhotoStore.IsValidId(row.Id));
        Assert.StartsWith("shares/", row.StoragePath, StringComparison.Ordinal);
        Assert.DoesNotContain(token, row.StoragePath, StringComparison.Ordinal);
        Assert.False(Path.IsPathRooted(row.StoragePath));
        Assert.Equal("png", row.Format);
        Assert.Equal("image/png", row.ContentType);
        Assert.Equal(SampleImages.Png.Length, row.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(SampleImages.Png)), row.Sha256);
        Assert.Equal(SampleImages.Png, await File.ReadAllBytesAsync(Path.Combine(_root.Path, row.StoragePath)));
    }

    [PostgresFact]
    public async Task ReuploadSameContent_CreatesNoNewMetadata()
    {
        var store = CreateStore();
        var token = ShareApiTests.NewToken();

        var first = await store.SaveSharedAsync(token, new MemoryStream(SampleImages.Jpeg), CancellationToken.None);
        var second = await store.SaveSharedAsync(token, new MemoryStream(SampleImages.Jpeg), CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Photo.FullPath, second.Photo.FullPath);
        await using var db = OpenDb();
        Assert.Single(await db.Photos.ToListAsync());
    }

    [PostgresFact]
    public async Task MetadataWriteFailure_RemovesPlacedFile_NoOrphan()
    {
        TestPostgres.Factory(_root.Path); // Tạo schema trước.
        var store = CreateStore(db: new FailingSaveFactory(TestPostgres.For(_root.Path)));

        await Assert.ThrowsAsync<PhotoMetadataUnavailableException>(
            () => store.SaveSharedAsync(ShareApiTests.NewToken(), new MemoryStream(SampleImages.Jpeg), CancellationToken.None));
        await Assert.ThrowsAsync<PhotoMetadataUnavailableException>(
            () => store.SaveAsync(new MemoryStream(SampleImages.Png), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories));
        await using var db = OpenDb();
        Assert.Empty(await db.Photos.ToListAsync());
    }

    [PostgresFact]
    public async Task MetadataWithoutFile_IsReportedMissing_AndSameReuploadRestoresIt()
    {
        var store = CreateStore();
        var token = ShareApiTests.NewToken();
        var saved = await store.SaveSharedAsync(token, new MemoryStream(SampleImages.Jpeg), CancellationToken.None);
        File.Delete(saved.Photo.FullPath);

        Assert.Equal(PhotoAvailability.FileMissing, (await store.FindAsync(token, CancellationToken.None)).Availability);

        // Upload lại ảnh khác → vẫn 409 (không ghi đè); upload lại đúng ảnh → khôi phục file, không thêm metadata.
        await Assert.ThrowsAsync<ShareConflictException>(
            () => store.SaveSharedAsync(token, new MemoryStream(SampleImages.Png), CancellationToken.None));
        var restored = await store.SaveSharedAsync(token, new MemoryStream(SampleImages.Jpeg), CancellationToken.None);

        Assert.False(restored.Created);
        Assert.Equal(PhotoAvailability.Available, (await store.FindAsync(token, CancellationToken.None)).Availability);
        Assert.Equal(SampleImages.Jpeg, await File.ReadAllBytesAsync(saved.Photo.FullPath));
        await using var db = OpenDb();
        Assert.Single(await db.Photos.ToListAsync());
    }

    [PostgresFact]
    public async Task FileWithoutMetadata_IsNotPublished()
    {
        var store = CreateStore();
        var id = PhotoStore.NewId();
        var orphan = Path.Combine(_root.Path, id[..2], id + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, SampleImages.Jpeg);

        Assert.Equal(PhotoAvailability.NotFound, (await store.FindAsync(id, CancellationToken.None)).Availability);
    }

    [PostgresFact]
    public async Task TamperedStoragePath_NeverEscapesRoot()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(new MemoryStream(SampleImages.Jpeg), CancellationToken.None);
        await using (var db = OpenDb())
        {
            (await db.Photos.SingleAsync()).StoragePath = "../../../Windows/win.ini";
            await db.SaveChangesAsync();
        }

        var lookup = await store.FindAsync(saved.Id, CancellationToken.None);

        Assert.Equal(PhotoAvailability.FileMissing, lookup.Availability);
        Assert.Null(lookup.Photo);
    }

    [PostgresFact]
    public async Task ShareToken_IsUniqueInDatabase()
    {
        var store = CreateStore();
        var token = ShareApiTests.NewToken();
        await store.SaveSharedAsync(token, new MemoryStream(SampleImages.Jpeg), CancellationToken.None);

        await using var db = OpenDb();
        db.Photos.Add(new CloudPhoto
        {
            Id = PhotoStore.NewId(),
            ShareToken = token,
            StoragePath = "x/y.jpg",
            Format = "jpeg",
            ContentType = "image/jpeg",
            Sha256 = new string('0', 64),
            UploadedAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Detect_RecognisesRealFormatsOnly()
    {
        Assert.Equal(PhotoFormat.Jpeg, PhotoFormats.Detect(SampleImages.Jpeg.AsSpan(0, PhotoFormats.HeaderLength)));
        Assert.Equal(PhotoFormat.Png, PhotoFormats.Detect(SampleImages.Png.AsSpan(0, PhotoFormats.HeaderLength)));
        Assert.Null(PhotoFormats.Detect("%PDF-1.7 not an image......"u8));
        Assert.Null(PhotoFormats.Detect([0xFF, 0xD8]));

        // Chữ ký PNG nhưng không có IHDR hợp lệ.
        var brokenPng = SampleImages.Png.ToArray();
        brokenPng[12] = (byte)'X';
        Assert.Null(PhotoFormats.Detect(brokenPng.AsSpan(0, PhotoFormats.HeaderLength)));
    }

    /// <summary>Database đọc được nhưng mọi lần ghi đều lỗi (mô phỏng mất kết nối đúng lúc ghi metadata).</summary>
    private sealed class FailingSaveFactory(string connection) : IDbContextFactory<CloudDbContext>
    {
        public CloudDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CloudDbContext>();
            CloudDatabase.Configure(options, connection);
            options.AddInterceptors(new FailingSave());
            return new CloudDbContext(options.Options);
        }
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Mô phỏng lỗi ghi metadata.");
    }
}
