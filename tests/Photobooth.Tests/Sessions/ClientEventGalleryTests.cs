using Photobooth.Application.Admin;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Events;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Sessions;

/// <summary>
/// Thư viện sự kiện chỉ-xem cho chế độ khách (Client Mode): chỉ đọc sự kiện hiện tại, không cần PIN,
/// không tác động phiên đang hoạt động, chặn truy cập chéo sự kiện/giả mạo/đổi sự kiện giữa await,
/// trong khi thư viện quản trị (<see cref="EventGalleryService"/>) vẫn bị khóa với Kiosk.
/// </summary>
public sealed class ClientEventGalleryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly EventWorkspaceService _workspaces;
    private readonly EventSessionArchive _archive;
    private readonly SkiaImagePreviewRenderer _previews;
    private readonly ClientEventGalleryService _clientGallery;
    private readonly FakeCameraDevice _camera = new();
    private readonly string _eventA;
    private readonly string _eventB;

    public ClientEventGalleryTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _eventA = Path.Combine(_temp.Path, "Sự kiện A");
        _eventB = Path.Combine(_temp.Path, "Sự kiện B");
        Directory.CreateDirectory(_eventA);
        Directory.CreateDirectory(_eventB);

        _workspaces = TestSessions.CreateWorkspaceService(_paths);
        _archive = TestSessions.CreateArchive(_paths, _workspaces);
        _previews = new SkiaImagePreviewRenderer(_paths);
        _clientGallery = new ClientEventGalleryService(_archive, _previews);
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    private async Task OpenEventAsync(string folder) => Assert.True((await _workspaces.OpenAsync(folder)).IsReady);

    private static string SessionKeyOf(PhotoSession session) => Path.GetFileName(session.Directory!);

    private async Task<PhotoSession> CompleteSessionAsync(PhotoSessionService sessions)
    {
        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync());
        Assert.True(await sessions.CompleteSessionAsync());
        var completed = sessions.Current!;
        await sessions.FlushAsync();
        return completed;
    }

    private async Task<(PhotoSession SessionA1, PhotoSession SessionA2, PhotoSession SessionB1, string KeyA, string KeyB)> SeedTwoEventsAsync(PhotoSessionService sessions)
    {
        await OpenEventAsync(_eventA);
        var a1 = await CompleteSessionAsync(sessions);
        await Task.Delay(20);
        var a2 = await CompleteSessionAsync(sessions);
        var keyA = _workspaces.Current!.Info.EventId.ToString("N");

        await Task.Delay(20);
        await OpenEventAsync(_eventB);
        var b1 = await CompleteSessionAsync(sessions);
        var keyB = _workspaces.Current!.Info.EventId.ToString("N");

        return (a1, a2, b1, keyA, keyB);
    }

    [Fact]
    public async Task Detail_UsesPersistedCompletedState_WithoutChangingAdminEditingState()
    {
        await OpenEventAsync(_eventA);
        var sessions = TestSessions.Create(_paths, _workspaces);
        var completed = await CompleteSessionAsync(sessions);

        var archived = await _archive.LoadSessionAsync(SessionKeyOf(completed));
        var detail = await _clientGallery.GetSessionDetailAsync(SessionKeyOf(completed));

        Assert.Equal(PhotoSessionState.Completed, archived.PersistedState);
        Assert.Equal(PhotoSessionState.Completed, detail.State);
        Assert.Equal(PhotoSessionState.Ready, archived.Session.State);
        Assert.Same(completed, sessions.Current);
    }

    [Fact]
    public async Task Detail_UsesPersistedCancelledState_AndKeepsUnfinishedPhotosVisible()
    {
        await OpenEventAsync(_eventA);
        var sessions = TestSessions.Create(_paths, _workspaces);
        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(_camera, null)).Succeeded);
        sessions.EndCurrentSession();
        await sessions.FlushAsync();
        var cancelled = sessions.Current!;

        var detail = await _clientGallery.GetSessionDetailAsync(SessionKeyOf(cancelled));

        Assert.Equal(PhotoSessionState.Cancelled, detail.State);
        Assert.Single(detail.Photos);
        Assert.Empty(detail.Versions);
        Assert.Same(cancelled, sessions.Current);
    }

    [Fact]
    public async Task KioskWithoutPin_CanBrowseCurrentEvent_WhileAdminGalleryRemainsLocked()
    {
        var sessions = TestSessions.Create(_paths, _workspaces);
        await OpenEventAsync(_eventA);
        var completed = await CompleteSessionAsync(sessions);

        var admin = new AdminAccessService();
        admin.StartKiosk();
        Assert.False(admin.IsUnlocked);

        var adminGallery = new EventGalleryService(_archive, admin, sessions, _previews);

        // Client gallery đọc được sự kiện hiện tại mà không cần PIN.
        var current = _clientGallery.GetCurrentEvent();
        Assert.NotNull(current);
        Assert.Equal("Sự kiện A", current.Name);

        var list = await _clientGallery.ListSessionsAsync();
        var item = Assert.Single(list.Sessions);
        Assert.Equal(completed.SessionId, item.SessionId);
        Assert.True(item.HasThumbnail);
        Assert.NotNull(await _clientGallery.GetSessionThumbnailAsync(item, 240));

        var detail = await _clientGallery.GetSessionDetailAsync(item.SessionKey);
        Assert.Equal(completed.SessionId, detail.SessionId);
        Assert.Single(detail.Versions);
        Assert.Single(detail.Photos);
        Assert.NotNull(await _clientGallery.GetVersionPreviewAsync(item.SessionKey, 1, 400));
        Assert.NotNull(await _clientGallery.GetOriginalPhotoPreviewAsync(item.SessionKey, detail.Photos[0].PhotoId, 400));

        // Thư viện quản trị (Admin) vẫn bị khóa với Kiosk.
        Assert.Throws<UnauthorizedAccessException>(() => adminGallery.ListEvents());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adminGallery.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adminGallery.OpenAsync(item.SessionKey));
        var adminSummary = (await _archive.ListSessionsAsync()).Single();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adminGallery.GetThumbnailAsync(adminSummary, 240));
    }

    [Fact]
    public async Task EventsAAndB_FilterStrictlyToCurrentEvent_WithoutChangingSessionCurrent()
    {
        var sessions = TestSessions.Create(_paths, _workspaces);
        var (a1, a2, b1, keyA, keyB) = await SeedTwoEventsAsync(sessions);
        var currentBefore = sessions.Current;

        var listB = await _clientGallery.ListSessionsAsync();
        Assert.Equal(keyB, listB.Event.EventKey);
        Assert.Equal("Sự kiện B", listB.Event.Name);
        var onlyB = Assert.Single(listB.Sessions);
        Assert.Equal(b1.SessionId, onlyB.SessionId);
        Assert.Equal(currentBefore, sessions.Current);

        // Đổi sự kiện hiện tại sang A: chỉ thấy 2 phiên của A, mới nhất trước.
        await OpenEventAsync(_eventA);
        var listA = await _clientGallery.ListSessionsAsync();
        Assert.Equal(keyA, listA.Event.EventKey);
        Assert.Equal("Sự kiện A", listA.Event.Name);
        Assert.Equal([a2.SessionId, a1.SessionId], listA.Sessions.Select(s => s.SessionId));
        Assert.Equal(currentBefore, sessions.Current);
    }

    [Fact]
    public async Task CrossEvent_ForgedInputs_AndPathTraversal_AreRejected()
    {
        var sessions = TestSessions.Create(_paths, _workspaces);
        var (a1, _, b1, keyA, keyB) = await SeedTwoEventsAsync(sessions);
        var sessionKeyA = SessionKeyOf(a1);
        var sessionKeyB = SessionKeyOf(b1);
        var photoIdA = a1.Photos[0].PhotoId;

        // Đang ở sự kiện B: mọi truy cập tới session/ảnh của sự kiện A phải bị chặn.
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _clientGallery.GetSessionDetailAsync(sessionKeyA));
        Assert.Null(await _clientGallery.GetSessionThumbnailAsync(sessionKeyA, 240));
        Assert.Null(await _clientGallery.GetVersionPreviewAsync(sessionKeyA, 1, 400));
        Assert.Null(await _clientGallery.GetOriginalPhotoPreviewAsync(sessionKeyA, photoIdA, 400));

        // PhotoId của sự kiện A ghép với SessionKey của sự kiện B → không đọc được.
        Assert.Null(await _clientGallery.GetOriginalPhotoPreviewAsync(sessionKeyB, photoIdA, 400));
        // Version không tồn tại → null.
        Assert.Null(await _clientGallery.GetVersionPreviewAsync(sessionKeyB, 99, 400));
        Assert.Null(await _clientGallery.GetVersionPreviewAsync(sessionKeyB, 0, 400));

        // Summary giả mạo (đổi EventKey hoặc gắn SessionKey của sự kiện khác) → null.
        var forgedWrongEvent = new ClientSessionSummary(sessionKeyA, a1.SessionId, keyA, "Sự kiện A", a1.CreatedAt, "Mẫu", 1, 1, 1, 0, true, null);
        var forgedMatchingEvent = forgedWrongEvent with { EventKey = keyB, EventName = "Sự kiện B" };
        Assert.Null(await _clientGallery.GetSessionThumbnailAsync(forgedWrongEvent, 240));
        Assert.Null(await _clientGallery.GetSessionThumbnailAsync(forgedMatchingEvent, 240));

        // Path traversal / khóa phiên không hợp lệ.
        string[] badKeys = ["", " ", ".", "..", @"..\..\AppData", "../Sự kiện A", "C:", "a/b", @"a\b"];
        foreach (var bad in badKeys)
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _clientGallery.GetSessionDetailAsync(bad));
            Assert.Null(await _clientGallery.GetSessionThumbnailAsync(bad, 240));
            Assert.Null(await _clientGallery.GetVersionPreviewAsync(bad, 1, 400));
            Assert.Null(await _clientGallery.GetOriginalPhotoPreviewAsync(bad, photoIdA, 400));
        }
    }

    [Fact]
    public async Task EventChangedMidAwait_DoesNotReturnStaleListDetailOrImages()
    {
        var sessions = TestSessions.Create(_paths, _workspaces);
        var (a1, _, _, _, _) = await SeedTwoEventsAsync(sessions);
        await OpenEventAsync(_eventA);
        var sessionKeyA = SessionKeyOf(a1);
        var photoIdA = a1.Photos[0].PhotoId;

        // 1. Đổi sự kiện giữa lúc ListSessionsAsync / LoadSessionAsync đang await.
        var switchingArchive = new SwitchingArchive(_archive, async () => await OpenEventAsync(_eventB));
        var galleryWithArchiveSwitch = new ClientEventGalleryService(switchingArchive, _previews);

        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => galleryWithArchiveSwitch.ListSessionsAsync());

        await OpenEventAsync(_eventA);
        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => galleryWithArchiveSwitch.GetSessionDetailAsync(sessionKeyA));

        await OpenEventAsync(_eventA);
        Assert.Null(await galleryWithArchiveSwitch.GetSessionThumbnailAsync(sessionKeyA, 240));

        await OpenEventAsync(_eventA);
        Assert.Null(await galleryWithArchiveSwitch.GetVersionPreviewAsync(sessionKeyA, 1, 400));

        await OpenEventAsync(_eventA);
        Assert.Null(await galleryWithArchiveSwitch.GetOriginalPhotoPreviewAsync(sessionKeyA, photoIdA, 400));

        // 2. Đổi sự kiện giữa lúc RenderPreviewAsync đang await (sau khi đã đọc metadata xong).
        var switchingPreviews = new SwitchingPreviewRenderer(_previews, async () => await OpenEventAsync(_eventB));
        var galleryWithPreviewSwitch = new ClientEventGalleryService(_archive, switchingPreviews);

        await OpenEventAsync(_eventA);
        Assert.Null(await galleryWithPreviewSwitch.GetSessionThumbnailAsync(sessionKeyA, 240));

        await OpenEventAsync(_eventA);
        Assert.Null(await galleryWithPreviewSwitch.GetVersionPreviewAsync(sessionKeyA, 1, 400));

        await OpenEventAsync(_eventA);
        Assert.Null(await galleryWithPreviewSwitch.GetOriginalPhotoPreviewAsync(sessionKeyA, photoIdA, 400));
    }

    [Fact]
    public async Task BrowsingGallery_NeverModifiesCompletedOrActiveSession()
    {
        var sessions = TestSessions.Create(_paths, _workspaces);
        await OpenEventAsync(_eventA);
        var first = await CompleteSessionAsync(sessions);
        await Task.Delay(20);
        var doneSession = await CompleteSessionAsync(sessions);

        // Trường hợp 1: đang ở màn Done (Completed) của phiên thứ hai, mở xem chi tiết phiên thứ nhất.
        var detailFirst = await _clientGallery.GetSessionDetailAsync(SessionKeyOf(first));
        Assert.NotNull(await _clientGallery.GetVersionPreviewAsync(detailFirst.SessionKey, 1, 300));
        Assert.NotNull(await _clientGallery.GetOriginalPhotoPreviewAsync(detailFirst.SessionKey, detailFirst.Photos[0].PhotoId, 300));
        Assert.Equal(doneSession.SessionId, sessions.Current!.SessionId);
        Assert.Equal(PhotoSessionState.Completed, sessions.Current.State);
        Assert.False(sessions.Current.IsArchived);

        // Trường hợp 2: có phiên mới đang hoạt động (Previewing).
        var active = sessions.StartNewSession()!;
        Assert.True(sessions.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        sessions.MarkPreviewReady();

        await _clientGallery.ListSessionsAsync();
        await _clientGallery.GetSessionDetailAsync(SessionKeyOf(first));

        Assert.Equal(active.SessionId, sessions.Current!.SessionId);
        Assert.Equal(PhotoSessionState.Previewing, sessions.Current.State);
        Assert.False(sessions.Current.IsArchived);
    }

    [Fact]
    public async Task Detail_IncludesRetakeOriginalsWithMirror_AllVersions_Exports_PrintJobs_AndExistingQrOnly()
    {
        await OpenEventAsync(_eventA);
        var uploads = new RecordingUploadQueue();
        var shareLinks = new ShareLinkFactory(new CloudShareOptions { Enabled = true, BaseUrl = "https://share.example.test" });
        var printer = new RecordingPrinter(isPhysical: false, result: PrintJobStatus.Simulated);
        var printers = new TestPrinterProvider { Current = printer };
        var sessions = TestSessions.Create(_paths, _workspaces, printers: printers, shareLinks: shareLinks, uploads: uploads);

        // Lần chụp 1: Mirror = true; lần chụp lại 2: Mirror = false (cùng ảnh đỏ trái / xanh dương phải).
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);

        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        sessions.MarkPreviewReady();
        Assert.True(sessions.SetMirror(true));
        Assert.True((await sessions.CaptureSequenceAsync(_camera, null)).Succeeded);

        // Chụp lại vị trí 0 với Mirror = false để có cả ảnh cũ (retake) lẫn ảnh mới.
        Assert.True(sessions.BeginRetake(0));
        Assert.True(sessions.SetMirror(false));
        Assert.True((await sessions.CaptureSequenceAsync(_camera, null)).Succeeded);

        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync());
        var exportDir = Directory.CreateDirectory(Path.Combine(_temp.Path, "exports")).FullName;
        var exported = await sessions.ExportAsync([ExportFormat.Png], exportDir);
        Assert.True(exported.Single().Result.Succeeded);

        var printJob = await sessions.RequestPrintAsync(copies: 2);
        Assert.NotNull(printJob);

        Assert.True(await sessions.CompleteSessionAsync());
        var completed = sessions.Current!;
        var shareTokenV1 = completed.CompletedOutput?.Share?.Token;
        Assert.False(string.IsNullOrWhiteSpace(shareTokenV1));
        await sessions.FlushAsync();

        // Tạo thêm phiên bản v2 qua luồng Admin để kiểm tra đa phiên bản (v2 không có QR).
        var admin = new AdminAccessService();
        admin.TryUnlock(AdminAccessService.Pin);
        var adminSessions = TestSessions.Create(_paths, _workspaces, shareLinks: shareLinks, uploads: uploads);
        var adminGallery = new EventGalleryService(_archive, admin, adminSessions, _previews);
        await adminGallery.OpenAsync(SessionKeyOf(completed));
        Assert.True(adminSessions.BeginEditFromVersion(1));
        Assert.Equal(CompositionOutcome.Succeeded, await adminSessions.ComposeAsync(ColorFilter.Sepia));
        var v2 = await adminSessions.SaveEditsAsync();
        Assert.NotNull(v2);
        await adminSessions.FlushAsync();
        adminGallery.Close();

        var uploadCountBeforeBrowse = uploads.Items.Count;

        // Xem chi tiết qua ClientEventGalleryService.
        var detail = await _clientGallery.GetSessionDetailAsync(SessionKeyOf(completed));

        Assert.Equal("Sự kiện A", detail.EventName);
        Assert.Equal(1, detail.RequiredPhotoCount);
        Assert.Equal(2, detail.CapturedPhotoCount);
        Assert.Equal([1, 2], detail.Photos.Select(p => p.Sequence));
        Assert.False(detail.Photos[0].IsSelectedInCurrentSlots);
        Assert.True(detail.Photos[0].Mirrored);
        Assert.True(detail.Photos[1].IsSelectedInCurrentSlots);
        Assert.False(detail.Photos[1].Mirrored);

        // Kiểm tra preview ảnh gốc lật gương đúng theo photo.Mirrored.
        var mirroredPreview = await _clientGallery.GetOriginalPhotoPreviewAsync(detail.SessionKey, detail.Photos[0].PhotoId, 200);
        var unmirroredPreview = await _clientGallery.GetOriginalPhotoPreviewAsync(detail.SessionKey, detail.Photos[1].PhotoId, 200);
        Assert.NotNull(mirroredPreview);
        Assert.NotNull(unmirroredPreview);
        var (mw, mh) = TestImages.SizeOf(mirroredPreview);
        Assert.True(TestImages.IsClose(TestImages.PixelAt(mirroredPreview, mw / 4, mh / 2), TestImages.Right));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(mirroredPreview, (mw * 3) / 4, mh / 2), TestImages.Left));
        var (uw, uh) = TestImages.SizeOf(unmirroredPreview);
        Assert.True(TestImages.IsClose(TestImages.PixelAt(unmirroredPreview, uw / 4, uh / 2), TestImages.Left));
        Assert.True(TestImages.IsClose(TestImages.PixelAt(unmirroredPreview, (uw * 3) / 4, uh / 2), TestImages.Right));

        // Đủ cả 2 phiên bản, v1 giữ QR cũ, v2 không tự sinh QR hay enqueue upload mới.
        Assert.Equal([1, 2], detail.Versions.Select(v => v.Version));
        Assert.Equal(shareTokenV1, detail.Versions[0].Share?.Token);
        Assert.Null(detail.Versions[1].Share);
        Assert.Equal(uploadCountBeforeBrowse, uploads.Items.Count);
        Assert.NotNull(await _clientGallery.GetVersionPreviewAsync(detail.SessionKey, 1, 300));
        Assert.NotNull(await _clientGallery.GetVersionPreviewAsync(detail.SessionKey, 2, 300));

        // Thông tin xuất và lịch sử in dạng chỉ đọc có đủ.
        var exportItem = Assert.Single(detail.Exports);
        Assert.Equal(ExportFormat.Png, exportItem.Format);
        var printItem = Assert.Single(detail.PrintJobs);
        Assert.Equal(2, printItem.Copies);
        Assert.Equal(PrintJobStatus.Simulated, printItem.Status);
    }

    [Fact]
    public async Task EmptyEvent_UnfinishedSession_AndMissingFiles_AreHandledGracefully()
    {
        await OpenEventAsync(_eventA);

        // 1. Sự kiện trống.
        var emptyList = await _clientGallery.ListSessionsAsync();
        Assert.Equal("Sự kiện A", emptyList.Event.Name);
        Assert.Empty(emptyList.Sessions);

        // 2. Phiên mới chỉ chụp ảnh gốc, chưa có ảnh thành phẩm.
        var sessions = TestSessions.Create(_paths, _workspaces);
        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(_camera, null)).Succeeded);
        var unfinished = sessions.Current!;
        await sessions.FlushAsync();

        var listWithUnfinished = await _clientGallery.ListSessionsAsync();
        var unfinishedSummary = Assert.Single(listWithUnfinished.Sessions);
        Assert.Null(unfinishedSummary.LatestVersionNumber);
        Assert.False(unfinishedSummary.HasThumbnail);
        Assert.Null(await _clientGallery.GetSessionThumbnailAsync(unfinishedSummary, 240));

        var unfinishedDetail = await _clientGallery.GetSessionDetailAsync(unfinishedSummary.SessionKey);
        Assert.Empty(unfinishedDetail.Versions);
        Assert.Single(unfinishedDetail.Photos);
        Assert.NotNull(await _clientGallery.GetOriginalPhotoPreviewAsync(unfinishedSummary.SessionKey, unfinishedDetail.Photos[0].PhotoId, 300));

        // 3. Phiên hoàn tất bị xóa file thành phẩm + thumbnail: vẫn mở xem được ảnh gốc; xóa ảnh gốc thì trả null không crash.
        await Task.Delay(20);
        var completed = await CompleteSessionAsync(sessions);
        File.Delete(completed.LatestVersion!.FilePath);
        File.Delete(completed.LatestVersion.ThumbnailPath!);

        var listAfterDelete = await _clientGallery.ListSessionsAsync();
        var missingSummary = listAfterDelete.Sessions.Single(s => s.SessionId == completed.SessionId);
        Assert.False(missingSummary.HasThumbnail);
        Assert.NotNull(missingSummary.Problem);
        Assert.Null(await _clientGallery.GetSessionThumbnailAsync(missingSummary, 240));
        Assert.Null(await _clientGallery.GetVersionPreviewAsync(missingSummary.SessionKey, 1, 300));

        var missingDetail = await _clientGallery.GetSessionDetailAsync(missingSummary.SessionKey);
        Assert.False(missingDetail.Versions[0].IsFileAvailable);
        Assert.NotNull(missingDetail.Notice);
        Assert.True(missingDetail.Photos[0].IsFileAvailable);
        Assert.NotNull(await _clientGallery.GetOriginalPhotoPreviewAsync(missingSummary.SessionKey, missingDetail.Photos[0].PhotoId, 300));

        // Xóa tiếp ảnh gốc: không crash, báo IsFileAvailable = false và preview trả null.
        File.Delete(completed.Photos[0].OriginalFilePath);
        var detailNoOriginal = await _clientGallery.GetSessionDetailAsync(missingSummary.SessionKey);
        Assert.False(detailNoOriginal.Photos[0].IsFileAvailable);
        Assert.Null(await _clientGallery.GetOriginalPhotoPreviewAsync(missingSummary.SessionKey, detailNoOriginal.Photos[0].PhotoId, 300));
    }

    [Fact]
    public async Task TamperedPathsInDatabase_NeverEscapeCurrentEventFolder()
    {
        var sessions = TestSessions.Create(_paths, _workspaces);
        await OpenEventAsync(_eventA);
        var completed = await CompleteSessionAsync(sessions);
        var key = SessionKeyOf(completed);

        await using (var db = TestDatabase.Open(_paths))
        {
            db.OutputVersions.Single(v => v.SessionId == completed.SessionId).FilePath = "../../AppData/photobooth.db";
            await db.SaveChangesAsync();
        }

        var list = await _clientGallery.ListSessionsAsync();
        var summary = Assert.Single(list.Sessions);
        Assert.False(summary.HasThumbnail);
        Assert.Null(await _clientGallery.GetSessionThumbnailAsync(summary, 240));
        Assert.Null(await _clientGallery.GetVersionPreviewAsync(key, 1, 300));

        await using (var db = TestDatabase.Open(_paths))
        {
            db.Sessions.Single(s => s.Id == completed.SessionId).DirectoryPath = "../Ngoài sự kiện/" + key;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _clientGallery.GetSessionDetailAsync(key));
        Assert.Null(await _clientGallery.GetOriginalPhotoPreviewAsync(key, completed.Photos[0].PhotoId, 300));
    }

    /// <summary>Archive bọc dùng cho test: kích hoạt đổi sự kiện ngay giữa lúc chờ kết quả async.</summary>
    private sealed class SwitchingArchive(ISessionArchive inner, Func<Task> onAwait) : ISessionArchive
    {
        public SessionLocation CreateSessionLocation(Guid sessionId, DateTimeOffset createdAt) =>
            inner.CreateSessionLocation(sessionId, createdAt);

        public Task SaveAsync(PhotoSession session, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(session, cancellationToken);

        public Task<OutputVersion> CreateOutputVersionAsync(PhotoSession session, ComposedResult composed, int version, CancellationToken cancellationToken = default) =>
            inner.CreateOutputVersionAsync(session, composed, version, cancellationToken);

        public Task DeleteDraftsAsync(PhotoSession session, CancellationToken cancellationToken = default) =>
            inner.DeleteDraftsAsync(session, cancellationToken);

        public IReadOnlyList<KnownEvent> ListKnownEvents() => inner.ListKnownEvents();

        public async Task<IReadOnlyList<ArchivedSessionSummary>> ListSessionsAsync(string? eventKey = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.ListSessionsAsync(eventKey, cancellationToken).ConfigureAwait(false);
            await onAwait().ConfigureAwait(false);
            return result;
        }

        public async Task<ArchivedSession> LoadSessionAsync(string sessionKey, string? eventKey = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.LoadSessionAsync(sessionKey, eventKey, cancellationToken).ConfigureAwait(false);
            await onAwait().ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>Renderer bọc dùng cho test: kích hoạt đổi sự kiện ngay giữa lúc render ảnh.</summary>
    private sealed class SwitchingPreviewRenderer(IImagePreviewRenderer inner, Func<Task> onAwait) : IImagePreviewRenderer
    {
        public async Task<byte[]> RenderPreviewAsync(string imageFilePath, bool mirror, int maxWidthPixels, CancellationToken cancellationToken = default)
        {
            var bytes = await inner.RenderPreviewAsync(imageFilePath, mirror, maxWidthPixels, cancellationToken).ConfigureAwait(false);
            await onAwait().ConfigureAwait(false);
            return bytes;
        }

        public async Task<byte[]> RenderPreviewAsync(string imageFilePath, bool mirror, int maxWidthPixels, ColorFilter filter, CancellationToken cancellationToken = default)
        {
            var bytes = await inner.RenderPreviewAsync(imageFilePath, mirror, maxWidthPixels, filter, cancellationToken).ConfigureAwait(false);
            await onAwait().ConfigureAwait(false);
            return bytes;
        }
    }
}
