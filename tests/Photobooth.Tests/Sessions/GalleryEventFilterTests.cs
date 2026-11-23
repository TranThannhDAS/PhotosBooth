using Photobooth.Application.Admin;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Events;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Sessions;

/// <summary>Bộ lọc theo sự kiện trong Thư viện ảnh (Admin): xem phiên của sự kiện khác mà không đổi sự kiện của Kiosk.</summary>
public sealed class GalleryEventFilterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly EventWorkspaceService _workspaces;
    private readonly PhotoSessionService _sessions;
    private readonly EventGalleryService _gallery;
    private readonly FakeCameraDevice _camera = new();
    private readonly string _eventA;
    private readonly string _eventB;

    public GalleryEventFilterTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _eventA = Path.Combine(_temp.Path, "Đám cưới A");
        _eventB = Path.Combine(_temp.Path, "Sinh nhật B");
        Directory.CreateDirectory(_eventA);
        Directory.CreateDirectory(_eventB);
        _workspaces = TestSessions.CreateWorkspaceService(_paths);
        _sessions = TestSessions.Create(_paths, _workspaces);
        var admin = new AdminAccessService();
        admin.TryUnlock(AdminAccessService.Pin);
        _gallery = new EventGalleryService(TestSessions.CreateArchive(_paths, _workspaces), admin, _sessions, new SkiaImagePreviewRenderer(_paths));
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    private async Task OpenEventAsync(string folder) => Assert.True((await _workspaces.OpenAsync(folder)).IsReady);

    private async Task<PhotoSession> CompleteSessionAsync()
    {
        Assert.NotNull(_sessions.StartNewSession());
        Assert.True(_sessions.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        _sessions.MarkPreviewReady();
        Assert.True((await _sessions.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await _sessions.ComposeAsync());
        Assert.True(await _sessions.CompleteSessionAsync());
        var completed = _sessions.Current!;
        await _sessions.FlushAsync();
        return completed;
    }

    /// <summary>Sự kiện A có 2 phiên, sự kiện B có 1 phiên; sự kiện hiện tại là B.</summary>
    private async Task<(string KeyA, string KeyB)> SeedAsync()
    {
        await OpenEventAsync(_eventA);
        await CompleteSessionAsync();
        await Task.Delay(20);
        await CompleteSessionAsync();
        var keyA = _workspaces.Current!.Info.EventId.ToString("N");
        await Task.Delay(20);
        await OpenEventAsync(_eventB);
        await CompleteSessionAsync();
        return (keyA, _workspaces.Current!.Info.EventId.ToString("N"));
    }

    [Fact]
    public async Task KnownEvents_AreRemembered_CurrentFirst_AndSurviveRestart()
    {
        var (keyA, keyB) = await SeedAsync();

        var events = _gallery.ListEvents();
        Assert.Equal([keyB, keyA], events.Select(e => e.EventKey));
        Assert.Equal(["Sinh nhật B", "Đám cưới A"], events.Select(e => e.Name));
        Assert.True(events[0].IsCurrent);
        Assert.All(events, e => Assert.True(e.IsAvailable));

        // Khởi động lại: danh bạ sự kiện đọc lại từ SQLite (bảng Events).
        var restarted = TestSessions.CreateWorkspaceService(new PhotoboothStoragePaths(_paths.RootDirectory));
        Assert.Equal([keyA, keyB], restarted.KnownEvents.OrderBy(e => e.Name).Select(e => e.EventId.ToString("N")));
    }

    [Fact]
    public async Task Filter_ShowsOnlySelectedEvent_WithoutChangingKioskEvent()
    {
        var (keyA, keyB) = await SeedAsync();

        Assert.Single(await _gallery.ListAsync());
        var sessionsA = await _gallery.ListAsync(keyA);

        Assert.Equal(2, sessionsA.Count);
        Assert.All(sessionsA, s => Assert.Equal(("Đám cưới A", keyA), (s.EventName, s.EventKey)));
        Assert.Equal(keyB, _workspaces.Current!.Info.EventId.ToString("N"));

        // Phiên mới của Kiosk vẫn lưu vào sự kiện hiện tại (B).
        await CompleteSessionAsync();
        Assert.Equal(2, (await _gallery.ListAsync(keyB)).Count);
        Assert.Equal(2, (await _gallery.ListAsync(keyA)).Count);
    }

    [Fact]
    public async Task AllEvents_AggregatesNewestFirst_WithEventNames()
    {
        await SeedAsync();

        var all = await _gallery.ListAsync(EventGalleryService.AllEvents);

        Assert.Equal(3, all.Count);
        Assert.Equal(all.OrderByDescending(s => s.CreatedAt).Select(s => s.SessionId), all.Select(s => s.SessionId));
        Assert.Equal(["Sinh nhật B", "Đám cưới A", "Đám cưới A"], all.Select(s => s.EventName));
    }

    [Fact]
    public async Task OpenSessionOfOtherEvent_ThumbnailsAndReprintWork_InThatEventFolder()
    {
        var (keyA, _) = await SeedAsync();
        var summary = (await _gallery.ListAsync(keyA))[0];

        Assert.NotNull(await _gallery.GetThumbnailAsync(summary, 200));
        var archived = await _gallery.OpenAsync(summary.SessionKey, summary.EventKey);

        Assert.StartsWith(_eventA, archived.Session.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.True(archived.Session.CanEdit, archived.EditUnavailableReason);
        _gallery.Close();
    }

    [Fact]
    public async Task UnknownOrPathLikeEventKeys_AreRejected()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => _gallery.ListAsync(Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => _gallery.ListAsync(_eventA));
        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => _gallery.ListAsync(@"..\..\Windows"));
        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => _gallery.OpenAsync("x", _eventA));
    }

    [Fact]
    public async Task MissingEventFolder_IsFlagged_AndSkippedByAllEvents()
    {
        var (keyA, _) = await SeedAsync();
        Directory.Move(_eventA, _eventA + "-moved");

        var events = _gallery.ListEvents();
        var missing = Assert.Single(events, e => e.EventKey == keyA);

        Assert.False(missing.IsAvailable);
        await Assert.ThrowsAsync<EventWorkspaceUnavailableException>(() => _gallery.ListAsync(keyA));
        Assert.Single(await _gallery.ListAsync(EventGalleryService.AllEvents));
    }

    [Fact]
    public async Task Kiosk_CannotListEvents()
    {
        var gallery = new EventGalleryService(TestSessions.CreateArchive(_paths, _workspaces), new AdminAccessService(), _sessions, new SkiaImagePreviewRenderer(_paths));

        Assert.Throws<UnauthorizedAccessException>(() => gallery.ListEvents());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gallery.ListAsync(EventGalleryService.AllEvents));
    }
}
