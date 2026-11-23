using System.Security.Cryptography;
using Photobooth.Application.Camera;
using Photobooth.Application.Admin;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Sessions;

/// <summary>Thư viện ảnh thành phẩm (Admin) và chỉnh sửa lại phiên đã lưu.</summary>
public sealed class GalleryAndReeditTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly FakeCameraDevice _camera = new();
    private readonly string _eventFolder;
    private readonly EventWorkspaceService _workspaces;
    private readonly AdminAccessService _admin = new();

    public GalleryAndReeditTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _eventFolder = Path.Combine(_temp.Path, "Đám cưới");
        _workspaces = TestSessions.OpenWorkspace(_paths, _eventFolder);
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>"Khởi động lại ứng dụng": service mới trên cùng thư mục sự kiện.</summary>
    private (PhotoSessionService Sessions, EventGalleryService Gallery) Restart()
    {
        var paths = new PhotoboothStoragePaths(_paths.RootDirectory);
        var workspaces = TestSessions.OpenWorkspace(paths, _eventFolder);
        var sessions = TestSessions.Create(paths, workspaces);
        _admin.TryUnlock(AdminAccessService.Pin);
        return (sessions, new EventGalleryService(TestSessions.CreateArchive(paths, workspaces), _admin, sessions, new SkiaImagePreviewRenderer(paths)));
    }

    /// <summary>Phiên Kiosk hoàn chỉnh: chụp, chỉnh crop/bộ lọc, hoàn tất (lưu final-v1).</summary>
    private async Task<PhotoSession> CompleteKioskSessionAsync(PhotoSessionService service, PhotoTemplate? template = null, SlotCrop? crop = null, ColorFilter filter = ColorFilter.None)
    {
        Assert.NotNull(service.StartNewSession());
        Assert.True(service.SelectTemplate(template ?? TestTemplates.WithPhotoIndexes(0, 0)));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(filter));
        if (crop is not null)
        {
            Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, crop));
        }

        Assert.True(await service.CompleteSessionAsync());
        var completed = service.Current!;
        await service.FlushAsync();
        return completed;
    }

    private static string Key(PhotoSession session) => Path.GetFileName(session.Directory!);

    // ------------------------------------------------------------ Gallery

    [Fact]
    public async Task Gallery_ListsFinishedPhotos_NewestFirst_AfterRestart()
    {
        var kiosk = TestSessions.Create(_paths, _workspaces);
        var first = await CompleteKioskSessionAsync(kiosk);
        await Task.Delay(20);
        var second = await CompleteKioskSessionAsync(kiosk);

        var (_, gallery) = Restart();
        var list = await gallery.ListAsync();

        Assert.Equal([second.SessionId, first.SessionId], list.Select(s => s.SessionId));
        Assert.All(list, s =>
        {
            Assert.Null(s.Problem);
            Assert.Equal(1, s.LatestVersion!.Version);
            Assert.True(File.Exists(s.LatestVersion.FilePath));
            Assert.True(File.Exists(s.LatestVersion.ThumbnailPath));
        });
        Assert.NotNull(await gallery.GetThumbnailAsync(list[0], 200));
    }

    [Fact]
    public async Task Gallery_OpensSessionByKey_WithFinishedVersion()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        var (sessions, gallery) = Restart();

        var archived = await gallery.OpenAsync(Key(completed));

        var session = sessions.Current!;
        Assert.True(session.IsArchived);
        Assert.True(session.CanEdit);
        Assert.Equal(PhotoSessionState.Ready, session.State);
        Assert.Equal(session.LatestVersion!.FilePath, session.Composed!.FilePath);
        Assert.Null(archived.EditUnavailableReason);
        Assert.NotNull(await sessions.GetVersionPreviewAsync(1, 300));
    }

    [Fact]
    public async Task Gallery_MissingFinishedFile_IsReported_AndDoesNotCrash()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        File.Delete(completed.LatestVersion!.FilePath);
        File.Delete(completed.LatestVersion.ThumbnailPath!);

        var (sessions, gallery) = Restart();
        var summary = Assert.Single(await gallery.ListAsync());
        Assert.Contains("Thiếu file", summary.Problem, StringComparison.Ordinal);
        Assert.Null(await gallery.GetThumbnailAsync(summary, 200));

        await gallery.OpenAsync(summary.SessionKey);
        Assert.Null(await sessions.GetVersionPreviewAsync(1, 300));
    }

    [Fact]
    public async Task Gallery_TamperedPathsInDatabase_NeverEscapeEventFolder()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));

        // Đường dẫn thành phẩm bị sửa thành đường dẫn ra ngoài thư mục sự kiện: không được dùng.
        await using (var db = TestDatabase.Open(_paths))
        {
            db.OutputVersions.Single(v => v.SessionId == completed.SessionId).FilePath = "../../AppData/photobooth.db";
            await db.SaveChangesAsync();
        }

        var (sessions, gallery) = Restart();
        var summary = Assert.Single(await gallery.ListAsync());
        Assert.NotNull(summary.Problem);
        Assert.Null(summary.LatestVersion);
        await gallery.OpenAsync(summary.SessionKey);
        Assert.Empty(sessions.Current!.Versions);
        gallery.Close();

        // Thư mục phiên bị sửa ra ngoài: từ chối mở.
        await using (var db = TestDatabase.Open(_paths))
        {
            db.Sessions.Single(s => s.Id == completed.SessionId).DirectoryPath = "../Ngoài sự kiện/" + summary.SessionKey;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => gallery.OpenAsync(summary.SessionKey));
    }

    [Fact]
    public async Task Kiosk_CannotAccessGallery()
    {
        await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        var admin = new AdminAccessService();
        var sessions = TestSessions.Create(_paths, _workspaces);
        var gallery = new EventGalleryService(TestSessions.CreateArchive(_paths, _workspaces), admin, sessions, new SkiaImagePreviewRenderer(_paths));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gallery.ListAsync());
        Assert.False(admin.TryUnlock("0000"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gallery.OpenAsync("bất kỳ"));

        // Đang ở Kiosk: lối vào của màn khởi động bị khóa; chỉ vào lại được bằng nút Quản trị + PIN đúng.
        admin.StartKiosk();
        Assert.False(admin.TryUnlock(AdminAccessService.Pin));
        Assert.False(admin.TryReturnFromKiosk("0000"));
        Assert.True(admin.KioskStarted);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gallery.ListAsync());
    }

    [Fact]
    public async Task Kiosk_ReturnToAdminWithPin_ThenBackToKioskViaStartup()
    {
        await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        var admin = new AdminAccessService();
        var gallery = new EventGalleryService(TestSessions.CreateArchive(_paths, _workspaces), admin, TestSessions.Create(_paths, _workspaces), new SkiaImagePreviewRenderer(_paths));
        admin.StartKiosk();

        Assert.True(admin.TryReturnFromKiosk(AdminAccessService.Pin));
        Assert.False(admin.KioskStarted);
        Assert.True(admin.IsUnlocked);
        Assert.Single(await gallery.ListAsync());

        // Thoát Admin → màn khởi động → bắt đầu phục vụ khách lại: Admin khóa như cũ.
        admin.Lock();
        admin.StartKiosk();
        Assert.False(admin.IsUnlocked);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gallery.ListAsync());

        // Không ở Kiosk thì lối vào này không có tác dụng.
        admin.Lock();
        Assert.True(admin.TryReturnFromKiosk(AdminAccessService.Pin));
        Assert.False(admin.TryReturnFromKiosk(AdminAccessService.Pin));
    }

    [Fact]
    public async Task Gallery_RejectsSessionKeysOutsideEvent()
    {
        var (_, gallery) = Restart();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => gallery.OpenAsync(@"..\..\AppData"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => gallery.OpenAsync("C:"));
    }

    [Fact]
    public async Task OpenArchived_IsRefused_WhileKioskSessionIsActive()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        var (sessions, gallery) = Restart();
        sessions.StartNewSession();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gallery.OpenAsync(Key(completed)));
    }

    // ------------------------------------------------------------ Chỉnh sửa lại

    [Fact]
    public async Task Reopen_RestoresManualCropFilterAndMirror()
    {
        var crop = new SlotCrop(2, 0.75, 0.25);
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces), crop: crop, filter: ColorFilter.Grayscale);
        var (sessions, gallery) = Restart();

        await gallery.OpenAsync(Key(completed));

        var session = sessions.Current!;
        Assert.Equal([null, crop], session.SlotCrops);
        Assert.Equal(ColorFilter.Grayscale, session.Filter);
        Assert.Equal(completed.Photos.Select(p => p.Mirrored), session.Photos.Select(p => p.Mirrored));
        Assert.Equal(completed.Slots.Select(p => p!.PhotoId), session.Slots.Select(p => p!.PhotoId));
    }

    [Fact]
    public async Task Reedit_RendersFromSources_AndSaveCreatesNewVersion_KeepingOldOne()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        var v1Bytes = await File.ReadAllBytesAsync(completed.LatestVersion!.FilePath);
        var (sessions, gallery) = Restart();
        await gallery.OpenAsync(Key(completed));
        Assert.True(sessions.BeginEditFromVersion(1));

        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ApplySlotCropAsync(0, new SlotCrop(3, 0, 0)));
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync(ColorFilter.Sepia));
        Assert.True(sessions.Current!.HasUnsavedEdits);
        Assert.StartsWith(_paths.GetDraftsDirectory(completed.SessionId), sessions.Current.Composed!.FilePath, StringComparison.OrdinalIgnoreCase);

        var v2 = await sessions.SaveEditsAsync();

        Assert.Equal(2, v2!.Version);
        Assert.EndsWith("final-v2.png", v2.FilePath, StringComparison.Ordinal);
        Assert.Equal(v1Bytes, await File.ReadAllBytesAsync(completed.LatestVersion.FilePath));
        Assert.False(sessions.Current!.HasUnsavedEdits);

        // Sau khởi động lại: có đủ 2 phiên bản, phiên bản mới mang đúng chỉnh sửa.
        await sessions.FlushAsync();
        var (reloaded, gallery2) = Restart();
        await gallery2.OpenAsync(Key(completed));
        Assert.Equal([1, 2], reloaded.Current!.Versions.Select(v => v.Version));
        Assert.Equal(ColorFilter.Sepia, reloaded.Current.Filter);
        Assert.Equal(new SlotCrop(3, 0, 0), reloaded.Current.SlotCrops[0]);
        Assert.Empty(Directory.GetFiles(_paths.GetDraftsDirectory(completed.SessionId), "composed-*.png"));
        await using var db = TestDatabase.Open(_paths);
        Assert.Equal("completed", db.Sessions.Single(s => s.Id == completed.SessionId).Status);
        Assert.Equal([1, 2], db.OutputVersions.Where(v => v.SessionId == completed.SessionId).Select(v => v.Version).OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task CancelEdits_RestoresConfirmedState_WithoutNewVersion()
    {
        var crop = new SlotCrop(2, 0.25, 0.25);
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces), crop: crop);
        var (sessions, gallery) = Restart();
        await gallery.OpenAsync(Key(completed));
        sessions.BeginEditFromVersion(1);
        await sessions.ApplySlotCropAsync(1, new SlotCrop(3, 1, 1));
        await sessions.ComposeAsync(ColorFilter.Warm);

        Assert.True(sessions.DiscardEdits());

        var session = sessions.Current!;
        Assert.Equal(completed.LatestVersion!.FilePath, session.Composed!.FilePath);
        Assert.Equal([null, crop], session.SlotCrops);
        Assert.Equal(ColorFilter.None, session.Filter);
        Assert.Single(session.Versions);
        Assert.False(session.HasUnsavedEdits);
        await sessions.FlushAsync();
        Assert.Equal(["final-v1.png"], Directory.GetFiles(Path.Combine(completed.Directory!, "Outputs"), "*.png").Select(Path.GetFileName));
    }

    [Fact]
    public async Task Reedit_DoesNotModifyOriginalsOrTemplateJson_AndSurvivesLibraryTemplateDeletion()
    {
        var library = Path.Combine(_temp.Path, "Templates");
        var file = TestTemplateFiles.Write(library, "logo-test", """
            {
              "id": "logo-test", "name": "Mẫu có logo",
              "canvas": { "widthPixels": 400, "heightPixels": 200 },
              "slots": [
                { "id": "a", "photoIndex": 0, "x": 0, "y": 0, "width": 200, "height": 200 },
                { "id": "b", "photoIndex": 0, "x": 200, "y": 0, "width": 200, "height": 200 }
              ],
              "decorations": [ { "type": "image", "assetPath": "assets/logo.png", "x": 10, "y": 10, "width": 50, "height": 50 } ]
            }
            """);
        TestTemplateFiles.WritePng(Path.GetDirectoryName(file)!, "assets/logo.png");
        var jsonBefore = await File.ReadAllBytesAsync(file);
        var (template, validation) = Photobooth.Infrastructure.Templates.JsonTemplateCatalog.LoadTemplateFile(file);
        Assert.True(validation.IsValid);

        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces), template!, new SlotCrop(1.5, 0.3, 0.3));
        var hashes = completed.Photos.Select(p => SHA256.HashData(File.ReadAllBytes(p.OriginalFilePath))).ToArray();
        Assert.True(File.Exists(Path.Combine(completed.Directory!, "Template", "assets", "logo.png")));

        // Thư viện template bị xóa sau sự kiện: phiên vẫn chỉnh sửa được nhờ snapshot.
        Assert.Equal(jsonBefore, await File.ReadAllBytesAsync(file));
        Directory.Delete(library, recursive: true);

        var (sessions, gallery) = Restart();
        await gallery.OpenAsync(Key(completed));
        Assert.True(sessions.Current!.CanEdit);
        sessions.BeginEditFromVersion(1);
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ApplySlotCropAsync(1, new SlotCrop(2.5, 0.9, 0.9)));
        Assert.NotNull(await sessions.SaveEditsAsync());

        Assert.Equal(hashes, sessions.Current!.Photos.Select(p => SHA256.HashData(File.ReadAllBytes(p.OriginalFilePath))));
    }

    [Fact]
    public async Task MissingOriginals_AllowViewExportAndPrint_ButNotEditing()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces));
        File.Delete(completed.Slots[0]!.OriginalFilePath);

        var (sessions, gallery) = Restart();
        var archived = await gallery.OpenAsync(Key(completed));

        Assert.False(sessions.Current!.CanEdit);
        Assert.Contains("Thiếu ảnh gốc", archived.EditUnavailableReason, StringComparison.Ordinal);
        Assert.False(sessions.BeginEditFromVersion(1));
        Assert.Equal(CompositionOutcome.NotReady, await sessions.ApplySlotCropAsync(0, new SlotCrop(2, 0.5, 0.5)));
        Assert.Equal(CompositionOutcome.NotReady, await sessions.ComposeAsync(ColorFilter.Sepia));

        var output = Directory.CreateDirectory(Path.Combine(_temp.Path, "out")).FullName;
        var results = await sessions.ExportAsync([ExportFormat.Png], output, version: 1);
        Assert.True(results.Single().Result.Succeeded);
        Assert.Equal(await File.ReadAllBytesAsync(completed.LatestVersion!.FilePath), await File.ReadAllBytesAsync(results.Single().Result.FilePath!));
    }

    [Fact]
    public async Task ReopenedSessionState_DoesNotLeakIntoNextSession()
    {
        var completed = await CompleteKioskSessionAsync(TestSessions.Create(_paths, _workspaces), crop: new SlotCrop(2, 0.5, 0.5), filter: ColorFilter.Sepia);
        var (sessions, gallery) = Restart();
        await gallery.OpenAsync(Key(completed));
        gallery.Close();

        var next = sessions.StartNewSession()!;
        Assert.NotEqual(completed.SessionId, next.SessionId);
        Assert.Equal(ColorFilter.None, next.Filter);
        Assert.Empty(next.SlotCrops);
        Assert.Empty(next.Versions);
        Assert.Empty(next.PrintJobs);
    }
}
