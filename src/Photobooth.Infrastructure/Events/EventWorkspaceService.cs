using System.Text.Json;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Events;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Infrastructure.Events;

/// <summary>Một Event Workspace đã mở: thư mục gốc của sự kiện và thông tin trong event.json.</summary>
public sealed record EventWorkspace(string RootDirectory, EventInfo Info)
{
    public string SessionsDirectory => Path.Combine(RootDirectory, EventWorkspaceService.SessionsFolderName);
}

/// <summary>Kết quả kiểm tra/mở Event Workspace. <see cref="Problem"/> là thông điệp tiếng Việt cho người vận hành.</summary>
public sealed record WorkspaceStatus(EventWorkspace? Workspace, string? Folder, string? Problem, long? FreeBytes = null)
{
    public bool IsReady => Workspace is not null && Problem is null;

    /// <summary>Thư mục chưa có event.json: mở sẽ tạo sự kiện mới.</summary>
    public bool IsNewEvent { get; init; }
}

/// <summary>
/// Quản lý sự kiện hiện tại (Event Workspace): chọn/mở/tạo, kiểm tra thư mục còn tồn tại và ghi được,
/// ghi nhớ sau khi khởi động lại. Không bao giờ tự tạo lại thư mục đã mất hay lưu sang thư mục khác.
/// </summary>
public sealed class EventWorkspaceService
{
    public const string EventFileName = "event.json";
    public const string SessionsFolderName = "Sessions";

    /// <summary>Dưới mức này không cho bắt đầu phục vụ (một phiên có thể cần vài chục MB).</summary>
    public const long MinimumFreeBytes = 200L * 1024 * 1024;

    private readonly PhotoboothStoragePaths _paths;
    private readonly AppStateStore _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventWorkspaceService> _logger;
    private readonly Lock _gate = new();
    private WorkspaceStatus _status;

    public EventWorkspaceService(PhotoboothStoragePaths paths, AppStateStore state, TimeProvider timeProvider, ILogger<EventWorkspaceService> logger)
    {
        _paths = paths;
        _state = state;
        _timeProvider = timeProvider;
        _logger = logger;
        _status = new WorkspaceStatus(null, state.Current.LastEventFolder, "Chưa chọn thư mục sự kiện.");
    }

    public event EventHandler? Changed;

    public WorkspaceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Sự kiện đang dùng (null nếu chưa chọn hoặc đang lỗi).</summary>
    public EventWorkspace? Current => Status is { IsReady: true, Workspace: { } workspace } ? workspace : null;

    /// <summary>Đã thử mở lại sự kiện lần trước trong lần chạy này.</summary>
    public bool RestoreAttempted { get; private set; }

    /// <summary>Mở lại sự kiện dùng lần trước (nếu còn hợp lệ). Thư mục đã mất → báo lỗi, KHÔNG tạo lại.</summary>
    public Task<WorkspaceStatus> RestoreLastAsync(CancellationToken cancellationToken = default)
    {
        RestoreAttempted = true;
        var folder = _state.Current.LastEventFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return Task.FromResult(Status);
        }

        return OpenCoreAsync(folder, newEventName: null, createIfMissingEventFile: false, cancellationToken);
    }

    /// <summary>Xem trước một thư mục: đã là sự kiện (có event.json) hay sẽ tạo sự kiện mới.</summary>
    public async Task<WorkspaceStatus> InspectAsync(string folder, CancellationToken cancellationToken = default)
    {
        var problem = CheckFolder(folder, out var freeBytes);
        if (problem is not null)
        {
            return new WorkspaceStatus(null, folder, problem, freeBytes);
        }

        var (info, error) = await ReadEventFileAsync(folder, cancellationToken).ConfigureAwait(false);
        return error is not null
            ? new WorkspaceStatus(null, folder, error, freeBytes)
            : new WorkspaceStatus(null, folder, null, freeBytes) { IsNewEvent = info is null };
    }

    /// <summary>
    /// Chọn thư mục làm sự kiện hiện tại. Thư mục đã có event.json → mở lại sự kiện cũ;
    /// chưa có → tạo sự kiện mới tên <paramref name="newEventName"/> (mặc định = tên thư mục).
    /// </summary>
    public Task<WorkspaceStatus> OpenAsync(string folder, string? newEventName = null, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(folder, newEventName, createIfMissingEventFile: true, cancellationToken);

    /// <summary>Đổi tên hiển thị của sự kiện hiện tại (không đổi tên thư mục).</summary>
    public async Task<WorkspaceStatus> RenameAsync(string name, CancellationToken cancellationToken = default)
    {
        var workspace = Current;
        var trimmed = name?.Trim();
        if (workspace is null || string.IsNullOrEmpty(trimmed))
        {
            return Status;
        }

        var info = workspace.Info with { Name = trimmed };
        try
        {
            await WriteEventFileAsync(workspace.RootDirectory, info, overwrite: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Không ghi được event.json khi đổi tên sự kiện");
            return SetStatus(Status with { Problem = DescribeWriteError(ex) });
        }

        await RememberAsync(workspace.RootDirectory, info, cancellationToken).ConfigureAwait(false);
        return SetStatus(Status with { Workspace = workspace with { Info = info } });
    }

    /// <summary>Các sự kiện đã mở trên máy này, sự kiện hiện tại trước, rồi theo lần mở gần nhất.</summary>
    public IReadOnlyList<KnownEventEntry> KnownEvents
    {
        get
        {
            var currentId = Current?.Info.EventId;
            return _state.Current.KnownEvents
                .OrderByDescending(e => e.EventId == currentId)
                .ThenByDescending(e => e.LastOpenedAt)
                .ToArray();
        }
    }

    /// <summary>
    /// Thư mục của một sự kiện đã biết, sau khi kiểm tra thư mục còn tồn tại và event.json vẫn đúng mã sự kiện.
    /// Chỉ để XEM (không cần ghi được, không đổi sự kiện hiện tại); cho phép đọc ảnh trong thư mục đó.
    /// </summary>
    /// <exception cref="EventWorkspaceUnavailableException">Mã lạ, thư mục mất hoặc không còn là sự kiện đó.</exception>
    public async Task<EventWorkspace> OpenForBrowsingAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        if (Current is { } current && current.Info.EventId == eventId)
        {
            return current;
        }

        var entry = _state.Current.KnownEvents.FirstOrDefault(e => e.EventId == eventId)
            ?? throw new EventWorkspaceUnavailableException("Sự kiện này chưa từng được mở trên máy.");
        if (!Directory.Exists(entry.Folder))
        {
            throw new EventWorkspaceUnavailableException("Không tìm thấy thư mục sự kiện (đã bị đổi tên, di chuyển hoặc ổ đĩa chưa cắm).");
        }

        var (info, error) = await ReadEventFileAsync(entry.Folder, cancellationToken).ConfigureAwait(false);
        if (error is not null || info is null || info.EventId != eventId)
        {
            throw new EventWorkspaceUnavailableException(error ?? "Thư mục không còn chứa sự kiện này.");
        }

        _paths.AllowRoot(entry.Folder);
        return new EventWorkspace(entry.Folder, info);
    }

    /// <summary>Kiểm tra nhanh (không đọc ảnh) để hiển thị trạng thái trong bộ lọc.</summary>
    public string? DescribeProblem(KnownEventEntry entry) =>
        Current?.Info.EventId == entry.EventId
            || (Directory.Exists(entry.Folder) && File.Exists(Path.Combine(entry.Folder, EventFileName)))
            ? null
            : "Không truy cập được thư mục sự kiện.";

    /// <summary>Ghi nhớ sự kiện vào danh bạ (một mục cho mỗi mã sự kiện và mỗi thư mục; lần mở mới nhất thắng).</summary>
    private async Task RememberAsync(string root, EventInfo info, CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await _state.UpdateAsync(
                    s => s with
                    {
                        KnownEvents = s.KnownEvents
                            .Where(e => e.EventId != info.EventId && !string.Equals(e.Folder, root, StringComparison.OrdinalIgnoreCase))
                            .Append(new KnownEventEntry(info.EventId, info.Name, root, now))
                            .ToArray(),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không ghi nhớ được danh sách sự kiện");
        }
    }

    /// <summary>Kiểm tra lại sự kiện hiện tại (thư mục còn tồn tại, ghi được, còn dung lượng).</summary>
    public Task<WorkspaceStatus> RecheckAsync(CancellationToken cancellationToken = default)
    {
        var folder = Status.Workspace?.RootDirectory ?? Status.Folder;
        return string.IsNullOrWhiteSpace(folder)
            ? Task.FromResult(Status)
            : OpenCoreAsync(folder, null, createIfMissingEventFile: false, cancellationToken);
    }

    private async Task<WorkspaceStatus> OpenCoreAsync(string folder, string? newEventName, bool createIfMissingEventFile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return SetStatus(new WorkspaceStatus(null, folder, "Đường dẫn thư mục sự kiện không hợp lệ."));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var problem = await Task.Run(
                () =>
                {
                    var found = CheckFolder(root, out var free);
                    return (Problem: found, Free: free);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (problem.Problem is not null)
        {
            _logger.LogWarning("Event Workspace không dùng được: {Problem}", problem.Problem);
            return SetStatus(new WorkspaceStatus(null, root, problem.Problem, problem.Free));
        }

        var (info, error) = await ReadEventFileAsync(root, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return SetStatus(new WorkspaceStatus(null, root, error, problem.Free));
        }

        if (info is null)
        {
            if (!createIfMissingEventFile)
            {
                return SetStatus(new WorkspaceStatus(null, root, "Thư mục không còn thông tin sự kiện (event.json). Vui lòng chọn lại.", problem.Free));
            }

            var name = string.IsNullOrWhiteSpace(newEventName) ? Path.GetFileName(root) : newEventName.Trim();
            info = new EventInfo(Guid.NewGuid(), string.IsNullOrWhiteSpace(name) ? "Sự kiện" : name, _timeProvider.GetUtcNow());
            try
            {
                await WriteEventFileAsync(root, info, overwrite: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Không tạo được event.json");
                return SetStatus(new WorkspaceStatus(null, root, DescribeWriteError(ex), problem.Free));
            }

            _logger.LogInformation("Tạo sự kiện mới {EventId}", info.EventId);
        }

        _paths.AllowRoot(root);
        var workspace = new EventWorkspace(root, info);
        var status = SetStatus(new WorkspaceStatus(workspace, root, null, problem.Free));
        try
        {
            await _state.UpdateAsync(s => s with { LastEventFolder = root }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không chặn phục vụ: chỉ là ghi nhớ cho lần mở sau.
            _logger.LogWarning(ex, "Không ghi nhớ được sự kiện đang chọn");
        }

        await RememberAsync(root, info, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Đang dùng sự kiện {EventId}", info.EventId);
        return status;
    }

    /// <summary>Kiểm tra thư mục tồn tại, ghi được và còn dung lượng. Trả về null nếu dùng được.</summary>
    private static string? CheckFolder(string folder, out long? freeBytes)
    {
        freeBytes = null;
        if (!Directory.Exists(folder))
        {
            return "Không tìm thấy thư mục sự kiện. Thư mục có thể đã bị đổi tên, di chuyển hoặc ổ đĩa chưa được cắm.";
        }

        var probe = Path.Combine(folder, $".photobooth-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DescribeWriteError(ex);
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                freeBytes = new DriveInfo(root).AvailableFreeSpace;
                if (freeBytes < MinimumFreeBytes)
                {
                    return $"Ổ đĩa gần đầy (còn {freeBytes / (1024 * 1024)} MB). Vui lòng giải phóng dung lượng hoặc chọn ổ khác.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Không đọc được dung lượng (ổ mạng...): vẫn cho dùng vì đã ghi thử thành công.
        }

        return null;
    }

    internal static string DescribeWriteError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Không có quyền ghi vào thư mục sự kiện. Vui lòng chọn thư mục khác hoặc cấp quyền ghi.",
        IOException io when IsDiskFull(io) => "Ổ đĩa đã đầy. Vui lòng giải phóng dung lượng hoặc chọn ổ khác.",
        _ => "Không ghi được vào thư mục sự kiện. Kiểm tra ổ đĩa và quyền truy cập.",
    };

    internal static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;

    private static async Task<(EventInfo? Info, string? Error)> ReadEventFileAsync(string folder, CancellationToken cancellationToken)
    {
        var file = Path.Combine(folder, EventFileName);
        if (!File.Exists(file))
        {
            return (null, null);
        }

        try
        {
            await using var stream = File.OpenRead(file);
            var info = await JsonSerializer.DeserializeAsync<EventInfo>(stream, AppStateStore.SerializerOptions, cancellationToken).ConfigureAwait(false);
            return info is { EventId: var id, Name: { Length: > 0 } } && id != Guid.Empty
                ? (info, null)
                : (null, "File event.json không hợp lệ. Ứng dụng không tự sửa file này; vui lòng chọn thư mục khác.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, "Không đọc được event.json trong thư mục này. Ứng dụng không tự sửa file; vui lòng chọn thư mục khác.");
        }
    }

    private static Task WriteEventFileAsync(string folder, EventInfo info, bool overwrite, CancellationToken cancellationToken) =>
        SafeFileWriter.WriteAsync(
            Path.Combine(folder, EventFileName),
            (stream, token) => JsonSerializer.SerializeAsync(stream, info, AppStateStore.SerializerOptions, token),
            overwrite,
            cancellationToken);

    private WorkspaceStatus SetStatus(WorkspaceStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return status;
    }
}
