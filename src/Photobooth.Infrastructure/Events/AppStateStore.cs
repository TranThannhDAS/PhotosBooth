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
/// Webcam dùng cho Kiosk (chọn trong Admin). <see cref="WebcamDeviceId"/> null = camera mặc định của Windows.
/// <see cref="WebcamLabel"/> dùng để hiển thị và tìm lại camera khi mã thiết bị thay đổi.
/// </summary>
public sealed record CameraSelection
{
    public string? WebcamDeviceId { get; init; }

    public string? WebcamLabel { get; init; }

    [JsonIgnore]
    public bool IsDefault => string.IsNullOrEmpty(WebcamDeviceId);
}

/// <summary>
/// Trạng thái máy (không phải cấu hình người dùng sửa tay): máy in và webcam đã chọn.
/// Lưu ở %LOCALAPPDATA%\Photobooth\app-state.json, tách khỏi settings.json để ứng dụng không ghi đè file cấu hình.
/// Sự kiện (đang dùng, danh bạ) nay nằm trong SQLite (bảng Events).
/// </summary>
public sealed record AppState
{
    /// <summary>KHÔNG CÒN DÙNG (bản JSON cũ). Chỉ giữ lại để đọc/ghi lại nguyên vẹn file cũ, không phải nguồn dữ liệu.</summary>
    public string? LastEventFolder { get; init; }

    public PrinterSelection Printer { get; init; } = new();

    /// <summary>Webcam dùng cho Kiosk; file cũ không có mục này → camera mặc định.</summary>
    public CameraSelection Camera { get; init; } = new();

    /// <summary>
    /// Thiết lập hướng dẫn bố cục trên Live View Kiosk.
    /// app-state.json cũ không có mục này → mặc định làm tối bên ngoài 65%.
    /// </summary>
    public CaptureGuideSettings CaptureGuide { get; init; } = new();

    /// <summary>KHÔNG CÒN DÙNG (bản JSON cũ). Chỉ giữ lại để đọc/ghi lại nguyên vẹn file cũ, không phải nguồn dữ liệu.</summary>
    public IReadOnlyList<KnownEventEntry> KnownEvents { get; init; } = [];
}

/// <summary>Một sự kiện đã mở trên máy (đọc từ bảng Events): mã, tên, thư mục tuyệt đối, lần mở gần nhất.</summary>
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
