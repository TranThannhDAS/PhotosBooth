using Microsoft.EntityFrameworkCore;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Persistence;

/// <summary>SQLite là nguồn metadata duy nhất của Desktop: quan hệ, ràng buộc, đường dẫn tương đối, Share Token bất biến.</summary>
public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly string _eventFolder;
    private readonly EventWorkspaceService _workspaces;
    private readonly FakeCameraDevice _camera = new();
    private readonly CloudShareOptions _cloud = new() { Enabled = true, BaseUrl = "https://photo.example.com" };

    public SqlitePersistenceTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _eventFolder = Path.Combine(_temp.Path, "Sự kiện SQLite");
        _workspaces = TestSessions.OpenWorkspace(_paths, _eventFolder);
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    private PhotoSessionService CreateService() =>
        TestSessions.Create(_paths, _workspaces, shareLinks: new ShareLinkFactory(_cloud));

    private async Task<PhotoSession> CompleteAsync(PhotoSessionService service)
    {
        Assert.NotNull(service.StartNewSession());
        Assert.True(service.SelectTemplate(TestTemplates.TwoSlots()));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        Assert.True(await service.CompleteSessionAsync());
        await service.FlushAsync();
        return service.Current!;
    }

    [Fact]
    public async Task CompletedSession_IsStoredWithRelationships_AndRelativePaths()
    {
        var session = await CompleteAsync(CreateService());

        await using var db = TestDatabase.Open(_paths);
        var row = await db.Sessions.Include(s => s.Event).Include(s => s.Photos).Include(s => s.Versions).SingleAsync();
        Assert.Equal(session.SessionId, row.Id);
        Assert.Equal(session.EventId, row.EventId);
        Assert.Equal(_eventFolder, row.Event!.FolderPath, ignoreCase: true);
        Assert.Equal("completed", row.Status);
        Assert.Equal(2, row.Photos.Count);

        var version = Assert.Single(row.Versions);
        Assert.Equal(row.DirectoryPath + "/Outputs/final-v1.png", version.FilePath);
        Assert.Equal(row.DirectoryPath + "/Outputs/final-v1.thumb.jpg", version.ThumbnailPath);
        Assert.Equal(session.CompletedOutput!.Share!.Token, version.ShareToken);
        Assert.All(row.Photos.Select(p => p.FilePath).Append(version.FilePath), p => Assert.False(Path.IsPathRooted(p)));

        // Ảnh vẫn là file trong Event Workspace, đúng vị trí đường dẫn tương đối chỉ tới.
        Assert.All(row.Photos, p => Assert.True(File.Exists(Path.Combine(_eventFolder, p.FilePath))));
        Assert.True(File.Exists(Path.Combine(_eventFolder, version.FilePath)));

        // Không còn file metadata JSON nghiệp vụ trong thư mục phiên.
        Assert.Empty(Directory.GetFiles(_eventFolder, "session.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_eventFolder, "cloud-upload.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ShareToken_NeverChanges_AfterRestart_EvenWhenAStaleSnapshotIsSaved()
    {
        var service = CreateService();
        var session = await CompleteAsync(service);
        var token = session.CompletedOutput!.Share!.Token;

        // Lưu một ảnh chụp trạng thái cũ (phiên bản chưa có token): token đã lưu không bị xóa.
        var stale = session with { Versions = session.Versions.Select(v => v with { Share = null }).ToArray() };
        await TestSessions.CreateArchive(_paths, _workspaces).SaveAsync(stale);

        // Hoặc một token khác: cũng không được đổi.
        var other = session with { Versions = session.Versions.Select(v => v with { Share = v.Share! with { Token = ShareLinkFactory.NewToken() } }).ToArray() };
        await TestSessions.CreateArchive(_paths, _workspaces).SaveAsync(other);

        // "Khởi động lại": đọc lại từ database.
        var restartedPaths = new PhotoboothStoragePaths(_paths.RootDirectory);
        var restarted = TestSessions.OpenWorkspace(restartedPaths, _eventFolder);
        var archived = await TestSessions.CreateArchive(restartedPaths, restarted).LoadSessionAsync(Path.GetFileName(session.Directory!));
        Assert.Equal(token, Assert.Single(archived.Session.Versions).Share!.Token);
        Assert.Equal(session.CompletedOutput.Share, archived.Session.Versions[0].Share);
    }

    [Fact]
    public async Task ShareToken_IsUnique_InDatabase()
    {
        var first = await CompleteAsync(CreateService());
        var second = await CompleteAsync(CreateService());

        await using var db = TestDatabase.Open(_paths);
        var versions = await db.OutputVersions.OrderBy(v => v.Id).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.NotEqual(versions[0].ShareToken, versions[1].ShareToken);

        versions[1].ShareToken = versions[0].ShareToken;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task CloudUpload_IsLinkedOneToOne_WithTheSharedVersion()
    {
        var session = await CompleteAsync(CreateService());

        await using var db = TestDatabase.Open(_paths);
        var version = await db.OutputVersions.SingleAsync();
        db.CloudUploads.Add(new CloudUploadRecord { OutputVersionId = version.Id, Status = "pending" });
        await db.SaveChangesAsync();

        // Không thể có hai bản ghi upload cho cùng một phiên bản, hay bản ghi upload trỏ tới phiên bản không tồn tại.
        await using (var second = TestDatabase.Open(_paths))
        {
            second.CloudUploads.Add(new CloudUploadRecord { OutputVersionId = version.Id, Status = "pending" });
            await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        }

        await using (var third = TestDatabase.Open(_paths))
        {
            third.CloudUploads.Add(new CloudUploadRecord { OutputVersionId = 999_999, Status = "pending" });
            await Assert.ThrowsAsync<DbUpdateException>(() => third.SaveChangesAsync());
        }

        Assert.Equal(session.CompletedOutput!.Share!.Token, (await db.OutputVersions.Include(v => v.CloudUpload).SingleAsync()).ShareToken);
    }

    [Fact]
    public async Task SessionCannotReferenceUnknownEvent_AndBusinessDataIsNeverCascadeDeleted()
    {
        await CompleteAsync(CreateService());

        await using var db = TestDatabase.Open(_paths);
        db.Sessions.Add(new SessionRecord { Id = Guid.NewGuid(), EventId = Guid.NewGuid(), DirectoryPath = "Sessions/x" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var db2 = TestDatabase.Open(_paths);
        db2.Events.Remove(await db2.Events.SingleAsync());
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task FileOutsideEventFolder_IsNeverStored()
    {
        var service = CreateService();
        var session = await CompleteAsync(service);
        var outside = Path.Combine(_temp.Path, "Ngoài sự kiện.jpg");
        var tampered = session with { Photos = [.. session.Photos, session.Photos[0] with { PhotoId = Guid.NewGuid(), Sequence = 99, OriginalFilePath = outside }] };

        await Assert.ThrowsAsync<IOException>(() => TestSessions.CreateArchive(_paths, _workspaces).SaveAsync(tampered));

        await using var db = TestDatabase.Open(_paths);
        Assert.Equal(2, await db.Photos.CountAsync());
    }

    [Fact]
    public async Task DatabaseLivesInAppDataFolder_SchemaCreatedByMigrations()
    {
        await CompleteAsync(CreateService());

        Assert.True(File.Exists(_paths.DatabaseFilePath));
        await using var db = TestDatabase.Open(_paths);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains("20261123040036_InitialCreate", await db.Database.GetAppliedMigrationsAsync());
    }
}
