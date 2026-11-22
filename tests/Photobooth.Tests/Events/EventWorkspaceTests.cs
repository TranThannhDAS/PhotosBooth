using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Photobooth.Application.Sessions;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Events;

/// <summary>Event Workspace: chọn/tạo/mở lại sự kiện, lưu phiên đúng thư mục, không trộn/ghi đè dữ liệu.</summary>
public sealed class EventWorkspaceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly FakeCameraDevice _camera = new();

    public EventWorkspaceTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
    }

    public void Dispose() => _temp.Dispose();

    private string Folder(string name)
    {
        var folder = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<PhotoSession> CaptureSessionAsync(PhotoSessionService service)
    {
        var session = service.StartNewSession();
        Assert.NotNull(session);
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1)));
        Assert.True(service.MarkPreviewReady());
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        await service.FlushAsync();
        return service.Current!;
    }

    [Fact]
    public async Task SelectExistingFolder_CreatesEvent_AndIsRememberedAfterRestart()
    {
        var folder = Folder("Sinh nhật Minh Anh");
        var workspaces = TestSessions.CreateWorkspaceService(_paths);

        var status = await workspaces.OpenAsync(folder);

        Assert.True(status.IsReady);
        Assert.Equal("Sinh nhật Minh Anh", status.Workspace!.Info.Name);
        Assert.True(File.Exists(Path.Combine(folder, EventWorkspaceService.EventFileName)));

        // "Khởi động lại": service mới đọc app-state.json và mở lại đúng sự kiện.
        var restarted = TestSessions.CreateWorkspaceService(_paths);
        var restored = await restarted.RestoreLastAsync();
        Assert.True(restored.IsReady);
        Assert.Equal(status.Workspace.Info.EventId, restored.Workspace!.Info.EventId);
    }

    [Fact]
    public async Task CreateNewEvent_WithVietnameseName_IsStoredAsUtf8()
    {
        var folder = Folder("Đám cưới Ngọc Huyền");
        var workspaces = TestSessions.CreateWorkspaceService(_paths);

        Assert.True((await workspaces.InspectAsync(folder)).IsNewEvent);
        var status = await workspaces.OpenAsync(folder, "Đám cưới Ngọc Huyền & Quốc Bảo");

        Assert.Equal("Đám cưới Ngọc Huyền & Quốc Bảo", status.Workspace!.Info.Name);
        var json = await File.ReadAllTextAsync(Path.Combine(folder, EventWorkspaceService.EventFileName));
        Assert.Equal("Đám cưới Ngọc Huyền & Quốc Bảo", JsonDocument.Parse(json).RootElement.GetProperty("name").GetString());
        Assert.Contains("Đám cưới Ngọc Huyền", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenExistingEvent_KeepsItsIdAndName()
    {
        var folder = Folder("Hội nghị 2026");
        var first = await TestSessions.CreateWorkspaceService(_paths).OpenAsync(folder, "Hội nghị khách hàng");

        var other = TestSessions.CreateWorkspaceService(new PhotoboothStoragePaths(Path.Combine(_temp.Path, "OtherMachine")));
        Assert.False((await other.InspectAsync(folder)).IsNewEvent);
        var reopened = await other.OpenAsync(folder, "Tên khác bị bỏ qua");

        Assert.Equal(first.Workspace!.Info, reopened.Workspace!.Info);
    }

    [Fact]
    public async Task Session_IsSavedInsideCurrentEventFolder_WithRelativePaths()
    {
        var folder = Folder("Sự kiện A");
        var service = TestSessions.Create(_paths, TestSessions.OpenWorkspace(_paths, folder));

        var session = await CaptureSessionAsync(service);

        Assert.StartsWith(Path.Combine(folder, "Sessions") + Path.DirectorySeparatorChar, session.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.All(session.Photos, p => Assert.StartsWith(session.Directory!, p.OriginalFilePath, StringComparison.OrdinalIgnoreCase));
        var json = await File.ReadAllTextAsync(Path.Combine(session.Directory!, EventSessionArchive.SessionFileName));
        Assert.DoesNotContain(folder.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Originals/photo-", json, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(session.Directory!, "Template", "template.json")));
        Assert.False(Directory.Exists(_paths.SessionsDirectory));
    }

    [Fact]
    public void SessionLocations_AreUnique_EvenWithSameTimestamp()
    {
        var archive = TestSessions.CreateArchive(_paths, TestSessions.OpenWorkspace(_paths, Folder("Unique")));
        var timestamp = DateTimeOffset.UtcNow;

        var directories = Enumerable.Range(0, 50).Select(_ => archive.CreateSessionLocation(Guid.NewGuid(), timestamp).Directory).ToArray();

        Assert.Equal(directories.Length, directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task MissingFolder_IsReported_NotRecreated_AndNoSessionIsStarted()
    {
        var folder = Folder("Sẽ bị đổi tên");
        var service = TestSessions.Create(_paths, TestSessions.OpenWorkspace(_paths, folder));
        Directory.Move(folder, folder + " (đã đổi tên)");

        var restarted = TestSessions.CreateWorkspaceService(_paths);
        var status = await restarted.RestoreLastAsync();
        Assert.False(status.IsReady);
        Assert.Contains("Không tìm thấy", status.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));

        // Phiên không được tạo ở chỗ khác (không âm thầm lưu vào thư mục ứng dụng).
        Assert.Null(TestSessions.Create(_paths, restarted).StartNewSession());
        Assert.Null(service.StartNewSession());
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(_paths.SessionsDirectory));
    }

    [Fact]
    public async Task FolderWithoutWritePermission_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Folder("Chỉ đọc");
        var info = new DirectoryInfo(folder);
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.WriteData, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var status = await TestSessions.CreateWorkspaceService(_paths).OpenAsync(folder);

            Assert.False(status.IsReady);
            Assert.Contains("quyền ghi", status.Problem, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(folder, EventWorkspaceService.EventFileName)));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task CorruptEventFile_IsReported_AndNeverOverwritten()
    {
        var folder = Folder("Hỏng");
        var file = Path.Combine(folder, EventWorkspaceService.EventFileName);
        await File.WriteAllTextAsync(file, "{ không phải json");

        var status = await TestSessions.CreateWorkspaceService(_paths).OpenAsync(folder);

        Assert.False(status.IsReady);
        Assert.Equal("{ không phải json", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task NewSession_NeverOverwritesPreviousSessions()
    {
        var service = TestSessions.Create(_paths, TestSessions.OpenWorkspace(_paths, Folder("Không ghi đè")));
        var first = await CaptureSessionAsync(service);
        var firstPhotos = first.Photos.Select(p => (p.OriginalFilePath, Bytes: File.ReadAllBytes(p.OriginalFilePath))).ToArray();

        var second = await CaptureSessionAsync(service);

        Assert.NotEqual(first.Directory, second.Directory);
        Assert.All(firstPhotos, p => Assert.Equal(p.Bytes, File.ReadAllBytes(p.OriginalFilePath)));

        // Phiên cũ chỉ được cập nhật trạng thái kết thúc, dữ liệu ảnh giữ nguyên.
        Assert.Equal(first.Photos.Count, JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(first.Directory!, EventSessionArchive.SessionFileName))).RootElement.GetProperty("photos").GetArrayLength());
        Assert.Equal(PhotoSessionState.Cancelled.ToString().ToLowerInvariant(),
            JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(first.Directory!, EventSessionArchive.SessionFileName))).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TwoEvents_KeepSessionsSeparate_EvenWhenSwitchingDuringSession()
    {
        var eventA = Folder("Sự kiện A");
        var eventB = Folder("Sự kiện B");
        var workspaces = TestSessions.OpenWorkspace(_paths, eventA);
        var service = TestSessions.Create(_paths, workspaces);
        await CaptureSessionAsync(service);

        // Bắt đầu phiên ở A rồi đổi sự kiện giữa chừng: phiên này vẫn ghi vào A.
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.WithPhotoIndexes(0));
        service.MarkPreviewReady();
        Assert.True((await workspaces.OpenAsync(eventB)).IsReady);
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        var inA = service.Current!;

        await CaptureSessionAsync(service);
        await service.FlushAsync();

        Assert.StartsWith(eventA, inA.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(eventA, "Sessions")).Length);
        Assert.Single(Directory.GetDirectories(Path.Combine(eventB, "Sessions")));
        var listB = await TestSessions.CreateArchive(_paths, workspaces).ListSessionsAsync();
        Assert.Single(listB);
    }
}
