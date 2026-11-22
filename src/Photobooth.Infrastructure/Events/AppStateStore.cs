using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Infrastructure.Events;

public enum PrinterKind
{
    /// <summary>Chưa cấu hình máy in: Kiosk báo "chưa thể in".</summary>
    None,

    /// <summary>Máy in giả lập để thử nghiệp vụ; không in thật.</summary>
    Mock,

    /// <summary>Máy in Windows đã cài (theo tên).</summary>
    Windows,
}

public sealed record PrinterSelection
{
    public PrinterKind Kind { get; init; } = PrinterKind.None;

    public string? WindowsPrinterName { get; init; }
}

/// <summary>
/// Trạng thái máy (không phải cấu hình người dùng sửa tay): sự kiện đang chọn và máy in đã chọn.
/// Lưu ở %LOCALAPPDATA%\Photobooth\app-state.json, tách khỏi settings.json để ứng dụng không ghi đè file cấu hình.
/// </summary>
public sealed record AppState
{
    /// <summary>Thư mục Event Workspace dùng lần trước (đường dẫn tuyệt đối).</summary>
    public string? LastEventFolder { get; init; }

    public PrinterSelection Printer { get; init; } = new();

    /// <summary>Các sự kiện đã mở trên máy này (cho bộ lọc Thư viện ảnh). Chỉ là danh bạ — không chứa ảnh.</summary>
    public IReadOnlyList<KnownEventEntry> KnownEvents { get; init; } = [];
}

/// <summary>Một sự kiện đã mở: mã (từ event.json), tên lần cuối thấy và thư mục tuyệt đối.</summary>
public sealed record KnownEventEntry(Guid EventId, string Name, string Folder, DateTimeOffset LastOpenedAt);

/// <summary>Đọc/ghi <see cref="AppState"/> an toàn (ghi tạm rồi đổi tên).</summary>
public sealed class AppStateStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,

        // Giữ nguyên tiếng Việt có dấu trong file (dễ đọc khi mở bằng tay), vẫn escape ký tự HTML nhạy cảm.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _filePath;
    private readonly ILogger<AppStateStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppState _current;

    public AppStateStore(PhotoboothStoragePaths paths, ILogger<AppStateStore> logger)
    {
        _filePath = Path.Combine(paths.RootDirectory, "app-state.json");
        _logger = logger;
        _current = Load();
    }

    public AppState Current => Volatile.Read(ref _current);

    public async Task UpdateAsync(Func<AppState, AppState> update, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = update(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await SafeFileWriter.WriteAsync(
                    _filePath,
                    (stream, token) => JsonSerializer.SerializeAsync(stream, next, SerializerOptions, token),
                    overwrite: true,
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _current, next);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private AppState Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(_filePath), SerializerOptions) ?? new AppState()
                : new AppState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không đọc được app-state.json, dùng trạng thái mặc định");
            return new AppState();
        }
    }
}
