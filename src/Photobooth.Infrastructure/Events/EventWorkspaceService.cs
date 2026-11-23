using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Events;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Infrastructure.Events;

/// <summary>Một Event Workspace đã mở: thư mục gốc của sự kiện và thông tin sự kiện (từ SQLite).</summary>
public sealed record EventWorkspace(string RootDirectory, EventInfo Info)
{
    public string SessionsDirectory => Path.Combine(RootDirectory, EventWorkspaceService.SessionsFolderName);
}

/// <summary>Kết quả kiểm tra/mở Event Workspace. <see cref="Problem"/> là thông điệp tiếng Việt cho người vận hành.</summary>
public sealed record WorkspaceStatus(EventWorkspace? Workspace, string? Folder, string? Problem, long? FreeBytes = null)
{
    public bool IsReady => Workspace is not null && Problem is null;

    /// <summary>Thư mục chưa có event.json (dấu nhận diện): mở sẽ tạo sự kiện mới.</summary>
    public bool IsNewEvent { get; init; }
}

/// <summary>
/// Quản lý sự kiện hiện tại (Event Workspace): chọn/mở/tạo, kiểm tra thư mục còn tồn tại và ghi được,
/// ghi nhớ sau khi khởi động lại. Không bao giờ tự tạo lại thư mục đã mất hay lưu sang thư mục khác.
/// <list type="bullet">
/// <item>SQLite (bảng Events) là nguồn dữ liệu chính: tên, thư mục, lần mở gần nhất, danh bạ sự kiện.</item>
/// <item><c>event.json</c> trong thư mục chỉ là DẤU NHẬN DIỆN (mã sự kiện) để biết thư mục thuộc sự kiện nào,
/// kể cả khi thư mục được di chuyển. Thư mục cũ có event.json đầy đủ (bản JSON trước đây) vẫn được nhận ra;
/// dữ liệu phiên cũ trong đó KHÔNG được chuyển sang database.</item>
/// </list>
/// </summary>
public sealed class EventWorkspaceService
{
    public const string EventFileName = "event.json";
    public const string SessionsFolderName = "Sessions";

    /// <summary>Dưới mức này không cho bắt đầu phục vụ (một phiên có thể cần vài chục MB).</summary>
    public const long MinimumFreeBytes = 200L * 1024 * 1024;

    private readonly PhotoboothStoragePaths _paths;
    private readonly IDbContextFactory<PhotoboothDbContext> _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventWorkspaceService> _logger;
    private readonly Lock _gate = new();
    private WorkspaceStatus _status;

    public EventWorkspaceService(PhotoboothStoragePaths paths, IDbContextFactory<PhotoboothDbContext> db, TimeProvider timeProvider, ILogger<EventWorkspaceService> logger)
    {
        _paths = paths;
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
        _status = new WorkspaceStatus(null, null, "Chưa chọn thư mục sự kiện.");
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
        var folder = LoadEvents().Where(e => e.FolderPath is not null).MaxBy(e => e.LastOpenedAt)?.FolderPath;
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

        var (marker, error) = await ReadEventFileAsync(folder, cancellationToken).ConfigureAwait(false);
        return error is not null
            ? new WorkspaceStatus(null, folder, error, freeBytes)
            : new WorkspaceStatus(null, folder, null, freeBytes) { IsNewEvent = marker is null };
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
            await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Events.FirstOrDefaultAsync(e => e.Id == info.EventId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return Status;
            }

            row.Name = trimmed;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            _logger.LogError(ex, "Không lưu được tên sự kiện vào cơ sở dữ liệu");
            return SetStatus(Status with { Problem = DatabaseProblem });
        }

        return SetStatus(Status with { Workspace = workspace with { Info = info } });
    }

    /// <summary>Các sự kiện đã mở trên máy này, sự kiện hiện tại trước, rồi theo lần mở gần nhất.</summary>
    public IReadOnlyList<KnownEventEntry> KnownEvents
    {
        get
        {
            var currentId = Current?.Info.EventId;
            return LoadEvents()
                .Where(e => e.FolderPath is not null)
                .Select(e => new KnownEventEntry(e.Id, e.Name, e.FolderPath!, e.LastOpenedAt))
                .OrderByDescending(e => e.EventId == currentId)
                .ThenByDescending(e => e.LastOpenedAt)
                .ToArray();
        }
    }

    /// <summary>Thư mục hiện tại của một sự kiện theo mã (null nếu không biết) — dùng để dựng đường dẫn tuyệt đối từ đường dẫn tương đối.</summary>
    public string? GetEventFolder(Guid eventId)
    {
        if (Current is { } current && current.Info.EventId == eventId)
        {
            return current.RootDirectory;
        }

        return LoadEvents().FirstOrDefault(e => e.Id == eventId)?.FolderPath;
    }

    private EventRecord[] LoadEvents()
    {
        try
        {
            using var db = _db.CreateDbContext();
            return db.Events.AsNoTracking().ToArray();
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            _logger.LogError(ex, "Không đọc được danh sách sự kiện từ cơ sở dữ liệu");
            return [];
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

        var entry = LoadEvents().FirstOrDefault(e => e.Id == eventId)
            ?? throw new EventWorkspaceUnavailableException("Sự kiện này chưa từng được mở trên máy.");
        if (entry.FolderPath is not { } folder || !Directory.Exists(folder))
        {
            throw new EventWorkspaceUnavailableException("Không tìm thấy thư mục sự kiện (đã bị đổi tên, di chuyển hoặc ổ đĩa chưa cắm).");
        }

        var (marker, error) = await ReadEventFileAsync(folder, cancellationToken).ConfigureAwait(false);
        if (error is not null || marker is null || marker.EventId != eventId)
        {
            throw new EventWorkspaceUnavailableException(error ?? "Thư mục không còn chứa sự kiện này.");
        }

        _paths.AllowRoot(folder);
        return new EventWorkspace(folder, new EventInfo(entry.Id, entry.Name, entry.CreatedAt));
    }

    /// <summary>Kiểm tra nhanh (không đọc ảnh) để hiển thị trạng thái trong bộ lọc.</summary>
    public string? DescribeProblem(KnownEventEntry entry) =>
        Current?.Info.EventId == entry.EventId
            || (Directory.Exists(entry.Folder) && File.Exists(Path.Combine(entry.Folder, EventFileName)))
            ? null
            : "Không truy cập được thư mục sự kiện.";

    /// <summary>
    /// Ghi sự kiện vào SQLite: tạo mới hoặc cập nhật thư mục và lần mở gần nhất. Một thư mục chỉ thuộc một sự kiện:
    /// sự kiện khác đang trỏ tới thư mục này (dữ liệu cũ) được gỡ thư mục.
    /// </summary>
    private async Task<EventRecord> RememberAsync(string root, EventMarker marker, string fallbackName, CancellationToken cancellationToken)
    {
        await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var rows = await db.Events.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var other in rows.Where(e => e.Id != marker.EventId && string.Equals(e.FolderPath, root, StringComparison.OrdinalIgnoreCase)))
        {
            other.FolderPath = null;
        }

        // Gỡ thư mục cũ trước, rồi mới gán cho sự kiện này (tránh vi phạm ràng buộc duy nhất trong cùng một lần lưu).
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var row = rows.FirstOrDefault(e => e.Id == marker.EventId);
        if (row is null)
        {
            row = new EventRecord
            {
                Id = marker.EventId,
                Name = string.IsNullOrWhiteSpace(marker.Name) ? fallbackName : marker.Name.Trim(),
                CreatedAt = marker.CreatedAt ?? now,
            };
            db.Events.Add(row);
        }

        row.FolderPath = root;
        row.LastOpenedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
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

        var (marker, error) = await ReadEventFileAsync(root, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return SetStatus(new WorkspaceStatus(null, root, error, problem.Free));
        }

        var name = string.IsNullOrWhiteSpace(newEventName) ? Path.GetFileName(root) : newEventName.Trim();
        name = string.IsNullOrWhiteSpace(name) ? "Sự kiện" : name;
        if (marker is null)
        {
            if (!createIfMissingEventFile)
            {
                return SetStatus(new WorkspaceStatus(null, root, "Thư mục không còn thông tin sự kiện (event.json). Vui lòng chọn lại.", problem.Free));
            }

            marker = new EventMarker(Guid.NewGuid(), null, _timeProvider.GetUtcNow());
            try
            {
                await WriteEventFileAsync(root, marker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Không tạo được event.json");
                return SetStatus(new WorkspaceStatus(null, root, DescribeWriteError(ex), problem.Free));
            }

            _logger.LogInformation("Tạo sự kiện mới {EventId}", marker.EventId);
        }

        EventRecord row;
        try
        {
            row = await RememberAsync(root, marker, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            // Không ghi được metadata sự kiện → không cho phục vụ (phiên chụp phải liên kết được với sự kiện).
            _logger.LogError(ex, "Không ghi được sự kiện vào cơ sở dữ liệu");
            return SetStatus(new WorkspaceStatus(null, root, DatabaseProblem, problem.Free));
        }

        _paths.AllowRoot(root);
        var workspace = new EventWorkspace(root, new EventInfo(row.Id, row.Name, row.CreatedAt));
        var status = SetStatus(new WorkspaceStatus(workspace, root, null, problem.Free));
        _logger.LogInformation("Đang dùng sự kiện {EventId}", row.Id);
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

    internal const string DatabaseProblem = "Không ghi được dữ liệu vào cơ sở dữ liệu của ứng dụng (photobooth.db). Vui lòng kiểm tra ổ đĩa rồi thử lại.";

    internal static bool IsDatabaseError(Exception ex) => ex is DbUpdateException or SqliteException or InvalidOperationException;

    /// <summary>Đọc dấu nhận diện event.json (chấp nhận cả định dạng đầy đủ của bản JSON cũ: có thêm name).</summary>
    private static async Task<(EventMarker? Marker, string? Error)> ReadEventFileAsync(string folder, CancellationToken cancellationToken)
    {
        var file = Path.Combine(folder, EventFileName);
        if (!File.Exists(file))
        {
            return (null, null);
        }

        try
        {
            await using var stream = File.OpenRead(file);
            var marker = await JsonSerializer.DeserializeAsync<EventMarker>(stream, AppStateStore.SerializerOptions, cancellationToken).ConfigureAwait(false);
            return marker is { EventId: var id } && id != Guid.Empty
                ? (marker, null)
                : (null, "File event.json không hợp lệ. Ứng dụng không tự sửa file này; vui lòng chọn thư mục khác.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, "Không đọc được event.json trong thư mục này. Ứng dụng không tự sửa file; vui lòng chọn thư mục khác.");
        }
    }

    /// <summary>Tạo dấu nhận diện cho sự kiện mới (không ghi đè). Tên sự kiện nằm trong SQLite, không ghi vào đây.</summary>
    private static Task WriteEventFileAsync(string folder, EventMarker marker, CancellationToken cancellationToken) =>
        SafeFileWriter.WriteAsync(
            Path.Combine(folder, EventFileName),
            (stream, token) => JsonSerializer.SerializeAsync(stream, marker with { Name = null }, MarkerOptions, token),
            overwrite: false,
            cancellationToken);

    private static readonly JsonSerializerOptions MarkerOptions = new(AppStateStore.SerializerOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Nội dung event.json: mã sự kiện (+ tên/ngày tạo nếu là thư mục của bản JSON cũ).</summary>
    private sealed record EventMarker(Guid EventId, string? Name, DateTimeOffset? CreatedAt);

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
