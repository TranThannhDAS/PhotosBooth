using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Events;

/// <summary>Tạo sự kiện mới = chọn nơi lưu + tên → thư mục con mang tên sự kiện; mở lại chỉ nhận thư mục đã là sự kiện.</summary>
public sealed class EventFolderCreationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly string _storage;

    public EventFolderCreationTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _storage = Path.Combine(_temp.Path, "Nơi lưu");
        Directory.CreateDirectory(_storage);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("Đám cưới Lan & Minh", "Đám cưới Lan & Minh")]
    [InlineData("  Tiệc   công ty  2026 ", "Tiệc công ty 2026")]
    [InlineData("Sinh nhật: 10/12 \"VIP\"?", "Sinh nhật- 10-12 -VIP--")]
    [InlineData("Hội chợ...", "Hội chợ")]
    public void FolderName_KeepsVietnamese_ReplacesInvalidCharacters(string eventName, string expected)
    {
        Assert.True(EventFolderName.TryCreate(eventName, out var folder));
        Assert.Equal(expected, folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("CON")]
    [InlineData("lpt1.txt")]
    public void FolderName_Unusable_IsRejected(string eventName) =>
        Assert.False(EventFolderName.TryCreate(eventName, out _));

    [Fact]
    public void FolderName_IsLimitedInLength() =>
        Assert.True(EventFolderName.TryCreate(new string('a', 300), out var folder) && folder.Length == EventFolderName.MaxLength);

    [Fact]
    public async Task CreateEvent_CreatesSubfolderNamedAfterEvent_AndUsesIt()
    {
        var workspaces = TestSessions.CreateWorkspaceService(_paths);

        var status = await workspaces.CreateEventAsync(_storage, "Đám cưới Lan & Minh");

        Assert.True(status.IsReady, status.Problem);
        var expected = Path.Combine(_storage, "Đám cưới Lan & Minh");
        Assert.Equal(expected, workspaces.Current!.RootDirectory);
        Assert.Equal("Đám cưới Lan & Minh", workspaces.Current.Info.Name);
        Assert.True(File.Exists(Path.Combine(expected, "event.json")));
        Assert.Equal(_storage, workspaces.SuggestedStorageFolder);

        // Khởi động lại: gợi ý đúng nơi lưu lần trước.
        Assert.Equal(_storage, TestSessions.CreateWorkspaceService(_paths).SuggestedStorageFolder);
    }

    [Fact]
    public async Task CreateEvent_ExistingFolder_IsRefused_AndCurrentEventUnchanged()
    {
        var workspaces = TestSessions.CreateWorkspaceService(_paths);
        Assert.True((await workspaces.CreateEventAsync(_storage, "Sự kiện A")).IsReady);
        var current = workspaces.Current;
        Directory.CreateDirectory(Path.Combine(_storage, "Sự kiện B"));

        var duplicate = await workspaces.CreateEventAsync(_storage, "Sự kiện A");
        var existingPlain = await workspaces.CreateEventAsync(_storage, "Sự kiện B");

        Assert.False(duplicate.IsReady);
        Assert.Contains("đã có thư mục", duplicate.Problem, StringComparison.Ordinal);
        Assert.False(existingPlain.IsReady);
        Assert.False(File.Exists(Path.Combine(_storage, "Sự kiện B", "event.json")));
        Assert.Same(current, workspaces.Current);
    }

    [Fact]
    public async Task CreateEvent_InvalidNameOrMissingStorage_IsRefused_WithoutCreatingFolders()
    {
        var workspaces = TestSessions.CreateWorkspaceService(_paths);

        Assert.False((await workspaces.CreateEventAsync(_storage, "  ")).IsReady);
        Assert.False((await workspaces.CreateEventAsync(_storage, "CON")).IsReady);
        Assert.False((await workspaces.CreateEventAsync(Path.Combine(_temp.Path, "Không tồn tại"), "Sự kiện")).IsReady);
        Assert.False((await workspaces.CreateEventAsync("tương-đối", "Sự kiện")).IsReady);

        Assert.Null(workspaces.Current);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_storage));
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "Không tồn tại")));
    }

    [Fact]
    public async Task OpenExisting_ReopensCreatedEvent_ButRefusesPlainFolder()
    {
        var first = TestSessions.CreateWorkspaceService(_paths);
        Assert.True((await first.CreateEventAsync(_storage, "Hội nghị")).IsReady);
        var eventId = first.Current!.Info.EventId;
        var plain = Path.Combine(_storage, "Thư mục thường");
        Directory.CreateDirectory(plain);

        var workspaces = TestSessions.CreateWorkspaceService(_paths);
        var refused = await workspaces.OpenExistingAsync(plain);
        var reopened = await workspaces.OpenExistingAsync(Path.Combine(_storage, "Hội nghị"));

        Assert.False(refused.IsReady);
        Assert.Contains("chưa phải là sự kiện", refused.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(plain, "event.json")));
        Assert.True(reopened.IsReady, reopened.Problem);
        Assert.Equal(eventId, workspaces.Current!.Info.EventId);
    }
}
