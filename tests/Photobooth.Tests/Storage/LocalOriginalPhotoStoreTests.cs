using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Storage;

public sealed class LocalOriginalPhotoStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly LocalOriginalPhotoStore _store;

    public LocalOriginalPhotoStoreTests()
    {
        _paths = new PhotoboothStoragePaths(_temp.Path);
        _store = new LocalOriginalPhotoStore(_paths);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SaveOriginalAsync_WritesFileInSessionOriginalsFolder()
    {
        var sessionId = Guid.NewGuid();
        var data = TestJpeg.CreateHeaderOnly();

        var stored = await _store.SaveOriginalAsync(sessionId, Guid.NewGuid(), 1, data);

        Assert.StartsWith(_paths.GetOriginalsDirectory(sessionId), stored.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(data, await File.ReadAllBytesAsync(stored.FilePath));
        Assert.Equal(data.Length, stored.SizeBytes);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(stored.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task SaveOriginalAsync_SamePhotoTwice_DoesNotOverwrite()
    {
        var sessionId = Guid.NewGuid();
        var photoId = Guid.NewGuid();
        var first = TestJpeg.CreateHeaderOnly(640, 480);
        var stored = await _store.SaveOriginalAsync(sessionId, photoId, 1, first);

        await Assert.ThrowsAnyAsync<IOException>(() => _store.SaveOriginalAsync(sessionId, photoId, 1, TestJpeg.CreateHeaderOnly(800, 600)));

        Assert.Equal(first, await File.ReadAllBytesAsync(stored.FilePath));
    }

    [Fact]
    public async Task SaveOriginalAsync_EmptyData_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveOriginalAsync(Guid.NewGuid(), Guid.NewGuid(), 1, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task ReadOriginalAsync_OutsideDataRoot_IsRejected()
    {
        using var other = new TempDirectory();
        var outside = Path.Combine(other.Path, "secret.jpg");
        await File.WriteAllBytesAsync(outside, TestJpeg.CreateHeaderOnly());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _store.ReadOriginalAsync(outside));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _store.ReadOriginalAsync(Path.Combine(_paths.RootDirectory, "..", "x.jpg")));
    }
}
