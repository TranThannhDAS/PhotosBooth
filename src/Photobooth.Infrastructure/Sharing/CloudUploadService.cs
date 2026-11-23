using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Sharing;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Persistence;

namespace Photobooth.Infrastructure.Sharing;

/// <summary>
/// Upload ảnh thành phẩm đã có link chia sẻ lên Photobooth Cloud, chạy nền và độc lập với UI.
/// <list type="bullet">
/// <item>Không có message queue: trạng thái nằm ở bảng SQLite <c>CloudUploads</c> (1-1 với phiên bản thành phẩm),
/// token/URL nằm ở <c>OutputVersions</c>. Khởi động lại → truy vấn mọi ảnh đã chia sẻ chưa upload xong để upload tiếp.</item>
/// <item>Upload lại luôn dùng nguyên Share Token; Cloud nhận diện ảnh trùng (200) nên không tạo bản ghi trùng.</item>
/// <item>Lỗi tạm thời (mất mạng, Cloud lỗi 5xx, hết thời gian) → Pending, thử lại với khoảng nghỉ tăng dần.
/// Lỗi không tự hết (mất file local, 409, ảnh bị từ chối) → Failed, không thử lại vô hạn.</item>
/// <item>Không log token, URL hay đường dẫn file.</item>
/// </list>
/// </summary>
public sealed class CloudUploadService : ICloudUploadQueue, IDisposable
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
    ];

    private readonly CloudShareOptions _options;
    private readonly EventWorkspaceService _workspaces;
    private readonly IDbContextFactory<PhotoboothDbContext> _db;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CloudUploadService> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile bool _rescanRequested = true;
    private bool _includeFailedOnScan;
    private DateTimeOffset? _lastScanAt;
    private bool _disposed;

    public CloudUploadService(
        CloudShareOptions options,
        EventWorkspaceService workspaces,
        IDbContextFactory<PhotoboothDbContext> db,
        TimeProvider timeProvider,
        ILogger<CloudUploadService> logger,
        HttpMessageHandler? handler = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _options = options;
        Configuration = options.Describe();
        _workspaces = workspaces;
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
        _retryDelays = retryDelays is { Count: > 0 } ? retryDelays : DefaultRetryDelays;
        _ownsHttp = true;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            : new HttpClient(handler, disposeHandler: false);

        // Thời gian chờ từng lần upload do CancellationToken quản lý (cloud.uploadTimeoutSeconds).
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _workspaces.Changed += OnWorkspaceChanged;
    }

    public event EventHandler? StateChanged;

    public CloudShareConfiguration Configuration { get; }

    /// <summary>Bắt đầu vòng upload nền (gọi một lần khi ứng dụng khởi động). Lần đầu sẽ quét khôi phục ảnh chưa upload.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    public void Enqueue(ShareUploadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ShareLinkFactory.IsValidToken(item.Link.Token))
        {
            throw new InvalidOperationException("Share Token không hợp lệ.");
        }

        Entry? created = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(item.Link.Token, out var existing))
            {
                if (existing.Status == CloudUploadStatus.Pending)
                {
                    // Gọi lại khi đang chờ thử lại (ví dụ quản trị viên mở lại phiên): thử ngay, vẫn cùng token.
                    existing.NextAttemptAt = _timeProvider.GetUtcNow();
                }
            }
            else
            {
                created = new Entry(item) { NextAttemptAt = _timeProvider.GetUtcNow() };
                _entries[item.Link.Token] = created;
            }
        }

        if (created is not null)
        {
            // Ghi "pending" vào SQLite (token đã nằm trong OutputVersions nên dù lần ghi này lỗi, lần quét sau vẫn tìm thấy ảnh).
            _ = PersistAsync(created.Snapshot());
            RaiseStateChanged();
        }

        _signal.Release();
    }

    public ShareUploadState? GetState(string token)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(token, out var entry) ? entry.ToState() : null;
        }
    }

    public ShareUploadSummary GetSummary()
    {
        lock (_gate)
        {
            var values = _entries.Values;
            var lastError = values.Where(e => e.LastError is not null && e.Status != CloudUploadStatus.Uploaded)
                .OrderByDescending(e => e.LastAttemptAt)
                .Select(e => e.LastError)
                .FirstOrDefault();
            return new ShareUploadSummary(
                values.Count(e => e.Status == CloudUploadStatus.Pending),
                values.Count(e => e.Status == CloudUploadStatus.Uploading),
                values.Count(e => e.Status == CloudUploadStatus.Uploaded),
                values.Count(e => e.Status == CloudUploadStatus.Failed),
                lastError,
                _lastScanAt);
        }
    }

    /// <summary>Quản trị viên yêu cầu đồng bộ ngay: quét lại các sự kiện, thử lại cả ảnh lỗi, bỏ qua thời gian chờ.</summary>
    public void RetryNow()
    {
        lock (_gate)
        {
            _includeFailedOnScan = true;
            var now = _timeProvider.GetUtcNow();
            foreach (var entry in _entries.Values.Where(e => e.Status is CloudUploadStatus.Pending or CloudUploadStatus.Failed))
            {
                entry.Status = CloudUploadStatus.Pending;
                entry.NextAttemptAt = now;
            }
        }

        _rescanRequested = true;
        _signal.Release();
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- Vòng nền

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_rescanRequested)
                {
                    _rescanRequested = false;
                    await ScanAsync(cancellationToken).ConfigureAwait(false);
                }

                TimeSpan? wait = null;
                Entry? due = null;
                if (Configuration.IsReady)
                {
                    (due, wait) = NextDue();
                }

                if (due is null)
                {
                    // Chưa cấu hình Cloud: không upload, chỉ chờ (ảnh vẫn ở trạng thái Pending và được giữ nguyên).
                    await _signal.WaitAsync(wait ?? Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await UploadAsync(due, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Vòng nền không bao giờ được chết: ghi log, nghỉ ngắn rồi tiếp tục.
                _logger.LogError(ex, "Lỗi ngoài dự kiến trong vòng upload Cloud");
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private (Entry? Due, TimeSpan? Wait) NextDue()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var next = _entries.Values
                .Where(e => e.Status == CloudUploadStatus.Pending)
                .OrderBy(e => e.NextAttemptAt)
                .FirstOrDefault();
            if (next is null)
            {
                return (null, null);
            }

            return next.NextAttemptAt <= now ? (next, null) : (null, next.NextAttemptAt - now);
        }
    }

    private async Task UploadAsync(Entry entry, CancellationToken cancellationToken)
    {
        var item = entry.Item;
        if (!Directory.Exists(item.SessionDirectory))
        {
            // Thư mục sự kiện/phiên tạm thời không truy cập được (ổ USB chưa cắm...): chờ thử lại, KHÔNG coi là mất ảnh.
            await CompleteAsync(entry, CloudUploadStatus.Pending, "Không truy cập được thư mục sự kiện — sẽ thử lại.", offline: false).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(item.FilePath))
        {
            await CompleteAsync(entry, CloudUploadStatus.Failed, "Không tìm thấy file ảnh thành phẩm trên máy.", offline: false).ConfigureAwait(false);
            _logger.LogWarning("Không upload được phiên {SessionId} v{Version}: mất file ảnh thành phẩm", item.SessionId, item.Version);
            return;
        }

        lock (_gate)
        {
            entry.Status = CloudUploadStatus.Uploading;
            entry.Attempts++;
            entry.LastAttemptAt = _timeProvider.GetUtcNow();
        }

        await PersistAsync(entry.Snapshot()).ConfigureAwait(false);
        RaiseStateChanged();

        Outcome outcome;
        try
        {
            outcome = await SendAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ứng dụng đang đóng: lần chạy sau thử lại (trạng thái "đang upload" trên đĩa được hiểu là chờ upload).
            lock (_gate)
            {
                entry.Status = CloudUploadStatus.Pending;
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = Outcome.Retry("Hết thời gian chờ upload — sẽ thử lại.", offline: true);
        }
        catch (HttpRequestException)
        {
            outcome = Outcome.Retry("Chưa kết nối được Cloud (có thể mất Internet) — sẽ tự thử lại.", offline: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = Outcome.Retry("Không đọc được file ảnh thành phẩm — sẽ thử lại.", offline: false);
        }

        await CompleteAsync(entry, outcome.Status, outcome.Error, outcome.Offline).ConfigureAwait(false);
        _logger.LogInformation(
            "Upload phiên {SessionId} v{Version} lần {Attempt}: {Status}",
            item.SessionId, item.Version, entry.Attempts, outcome.Status);
    }

    private async Task<Outcome> SendAsync(ShareUploadItem item, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.UploadTimeoutSeconds));

        await using var file = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            Path.GetExtension(item.FilePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");

        // Tên file gửi đi chỉ là "final-v{N}.png" (Cloud không dùng tên này), token đi kèm để Cloud lưu đúng QR đã phát.
        form.Add(content, "file", Path.GetFileName(item.FilePath));
        form.Add(new StringContent(item.Link.Token), "shareToken");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration.BaseUrl!, "api/photos")) { Content = form };
        request.Headers.ExpectContinue = true;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Add("X-Api-Key", _options.ApiKey.Trim());
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.Created => await VerifyAsync(response, item.Link.Token, timeout.Token).ConfigureAwait(false),
            HttpStatusCode.Conflict => Outcome.Fail("Cloud đã có ảnh khác gắn với mã chia sẻ này; ảnh trên Cloud được giữ nguyên."),
            HttpStatusCode.RequestEntityTooLarge => Outcome.Fail("Ảnh vượt giới hạn dung lượng của Cloud."),
            HttpStatusCode.UnsupportedMediaType => Outcome.Fail("Cloud không nhận định dạng ảnh này."),
            HttpStatusCode.BadRequest => Outcome.Fail("Cloud từ chối dữ liệu tải lên."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Outcome.Retry("Cloud từ chối API Key (kiểm tra cloud.apiKey) — sẽ thử lại sau.", offline: false),
            HttpStatusCode.NotFound => Outcome.Retry("Không tìm thấy API trên Cloud (kiểm tra cloud.baseUrl) — sẽ thử lại sau.", offline: false),
            _ => Outcome.Retry($"Cloud tạm thời lỗi (HTTP {(int)response.StatusCode}) — sẽ thử lại.", offline: false),
        };
    }

    /// <summary>Cloud phải trả đúng token đã gửi (Cloud bản cũ không hỗ trợ token sẽ trả ID khác → không coi là đã chia sẻ).</summary>
    private static async Task<Outcome> VerifyAsync(HttpResponseMessage response, string token, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<UploadResponse>(cancellationToken).ConfigureAwait(false);
            return body?.Id == token
                ? Outcome.Done()
                : Outcome.Fail("Cloud không hỗ trợ mã chia sẻ (cần cập nhật Photobooth.Cloud).");
        }
        catch (JsonException)
        {
            return Outcome.Retry("Phản hồi của Cloud không hợp lệ — sẽ thử lại.", offline: false);
        }
    }

    private async Task CompleteAsync(Entry entry, CloudUploadStatus status, string? error, bool offline)
    {
        lock (_gate)
        {
            entry.Status = status;
            entry.LastError = error;
            entry.IsOffline = offline;
            if (status == CloudUploadStatus.Uploaded)
            {
                entry.UploadedAt = _timeProvider.GetUtcNow();
            }
            else if (status == CloudUploadStatus.Pending)
            {
                var delay = _retryDelays[Math.Clamp(entry.Attempts - 1, 0, _retryDelays.Count - 1)];
                entry.NextAttemptAt = _timeProvider.GetUtcNow() + delay;
            }
        }

        // Ghi trạng thái xuống đĩa trước khi báo cho giao diện.
        await PersistAsync(entry.Snapshot()).ConfigureAwait(false);
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- Khôi phục sau khi khởi động lại

    /// <summary>Truy vấn SQLite: mọi phiên bản đã có Share Token (mọi sự kiện đã biết) cùng trạng thái upload đã lưu.</summary>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        bool includeFailed;
        lock (_gate)
        {
            includeFailed = _includeFailedOnScan;
            _includeFailedOnScan = false;
        }

        List<(ShareUploadItem Item, CloudUploadRecord? Record)> found;
        try
        {
            found = await FindSharesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or InvalidOperationException)
        {
            _logger.LogError(ex, "Không đọc được danh sách ảnh chia sẻ từ cơ sở dữ liệu");
            return;
        }

        var added = 0;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var (item, record) in found)
            {
                if (_entries.TryGetValue(item.Link.Token, out var existing))
                {
                    if (includeFailed && existing.Status == CloudUploadStatus.Failed)
                    {
                        existing.Status = CloudUploadStatus.Pending;
                        existing.NextAttemptAt = now;
                    }

                    continue;
                }

                var status = ParseStatus(record?.Status);
                if (status == CloudUploadStatus.Uploading || (includeFailed && status == CloudUploadStatus.Failed))
                {
                    status = CloudUploadStatus.Pending;
                }

                _entries[item.Link.Token] = new Entry(item)
                {
                    Status = status,
                    Attempts = record?.Attempts ?? 0,
                    LastAttemptAt = record?.LastAttemptAt,
                    UploadedAt = record?.UploadedAt,
                    LastError = record?.LastError,
                    NextAttemptAt = now,
                };
                added++;
            }

            _lastScanAt = now;
        }

        if (added > 0)
        {
            _logger.LogInformation("Khôi phục {Count} ảnh chia sẻ từ cơ sở dữ liệu", added);
            RaiseStateChanged();
        }
    }

    private async Task<List<(ShareUploadItem Item, CloudUploadRecord? Record)>> FindSharesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.OutputVersions
            .AsNoTracking()
            .Where(v => v.ShareToken != null)
            .Select(v => new
            {
                Version = v,
                v.CloudUpload,
                SessionDirectory = db.Sessions.Where(s => s.Id == v.SessionId).Select(s => s.DirectoryPath).First(),
                EventFolder = db.Sessions.Where(s => s.Id == v.SessionId).Select(s => s.Event!.FolderPath).First(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<(ShareUploadItem, CloudUploadRecord?)>();
        foreach (var row in rows)
        {
            // Sự kiện mất thư mục (đã gán cho sự kiện khác): không xác định được file → bỏ qua, dữ liệu vẫn giữ nguyên.
            if (row.EventFolder is not { } root
                || EventSessionArchive.ToShareLink(row.Version.ShareToken, row.Version.ShareUrl, row.Version.ShareCreatedAt) is not { } link
                || EventSessionArchive.Resolve(root, row.SessionDirectory) is not { } sessionDirectory
                || EventSessionArchive.Resolve(root, row.Version.FilePath) is not { } file)
            {
                continue;
            }

            result.Add((new ShareUploadItem(row.Version.SessionId, sessionDirectory, row.Version.Version, file, link), row.CloudUpload));
        }

        return result;
    }

    // ---------------------------------------------------------------- Bảng CloudUploads

    /// <summary>Ghi trạng thái upload (upsert 1-1 theo phiên bản). Chỉ bảng CloudUploads — không đụng metadata phiên.</summary>
    private async Task PersistAsync(EntrySnapshot snapshot)
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await _db.CreateDbContextAsync().ConfigureAwait(false);
            var version = await db.OutputVersions
                .Include(v => v.CloudUpload)
                .FirstOrDefaultAsync(v => v.SessionId == snapshot.Item.SessionId && v.Version == snapshot.Item.Version)
                .ConfigureAwait(false);
            if (version is null || version.ShareToken != snapshot.Item.Link.Token)
            {
                _logger.LogWarning("Không tìm thấy phiên bản đã chia sẻ để ghi trạng thái upload (phiên {SessionId} v{Version})", snapshot.Item.SessionId, snapshot.Item.Version);
                return;
            }

            var record = version.CloudUpload ??= new CloudUploadRecord { OutputVersionId = version.Id };
            record.Status = JsonNamingPolicy.CamelCase.ConvertName(snapshot.Status.ToString());
            record.Attempts = snapshot.Attempts;
            record.LastAttemptAt = snapshot.LastAttemptAt;
            record.UploadedAt = snapshot.UploadedAt;
            record.LastError = snapshot.LastError;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or InvalidOperationException)
        {
            // Trạng thái chỉ để khôi phục nhanh; token đã nằm trong OutputVersions nên ảnh vẫn được quét lại khi khởi động.
            _logger.LogWarning(ex, "Không ghi được trạng thái upload của phiên {SessionId}", snapshot.Item.SessionId);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static CloudUploadStatus ParseStatus(string? value) =>
        Enum.TryParse<CloudUploadStatus>(value, ignoreCase: true, out var status) && Enum.IsDefined(status) ? status : CloudUploadStatus.Pending;

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        // Mở/đổi sự kiện: quét thêm sự kiện mới (có thể có ảnh chưa upload từ lần trước).
        _rescanRequested = true;
        _signal.Release();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi xử lý sự kiện trạng thái upload");
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
        }

        _workspaces.Changed -= OnWorkspaceChanged;
        _stop.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        if (_ownsHttp)
        {
            _http.Dispose();
        }

        _stop.Dispose();
    }

    // ---------------------------------------------------------------- Kiểu nội bộ

    private sealed class Entry(ShareUploadItem item)
    {
        public ShareUploadItem Item { get; } = item;

        public CloudUploadStatus Status { get; set; } = CloudUploadStatus.Pending;

        public int Attempts { get; set; }

        public DateTimeOffset? LastAttemptAt { get; set; }

        public DateTimeOffset? UploadedAt { get; set; }

        public string? LastError { get; set; }

        public bool IsOffline { get; set; }

        public DateTimeOffset NextAttemptAt { get; set; }

        public ShareUploadState ToState() => new(Status, Attempts, LastAttemptAt, UploadedAt, LastError, IsOffline);

        public EntrySnapshot Snapshot() => new(Item, Status, Attempts, LastAttemptAt, UploadedAt, LastError);
    }

    private sealed record EntrySnapshot(
        ShareUploadItem Item,
        CloudUploadStatus Status,
        int Attempts,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? UploadedAt,
        string? LastError);

    private sealed record Outcome(CloudUploadStatus Status, string? Error, bool Offline)
    {
        public static Outcome Done() => new(CloudUploadStatus.Uploaded, null, false);

        public static Outcome Fail(string error) => new(CloudUploadStatus.Failed, error, false);

        public static Outcome Retry(string error, bool offline) => new(CloudUploadStatus.Pending, error, offline);
    }

    private sealed record UploadResponse(string? Id);
}
