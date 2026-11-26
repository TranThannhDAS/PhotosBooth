using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace Photobooth.Camera.Canon;

/// <summary>
/// Thư viện bộ thông số máy ảnh Canon — cùng mô hình với Template Library:
/// <list type="bullet">
/// <item>Thư viện đang dùng (ghi được, không mất khi build lại): mặc định <c>%LOCALAPPDATA%\Photobooth\CameraPresets</c>, mỗi bộ một file <c>{id}.json</c>.</item>
/// <item>Bộ thông số đi kèm ứng dụng (<c>CameraPresets</c> cạnh Photobooth.exe, chép từ <c>src/Photobooth.Desktop/CameraPresets</c>)
/// chỉ dùng để khởi tạo thư viện lần đầu; thư viện đã có thì không bao giờ bị chép đè.</item>
/// <item>Nhập/xuất file JSON để chia sẻ giữa các máy hoặc đưa vào bộ đi kèm ứng dụng.</item>
/// </list>
/// Ghi file tạm rồi đổi tên để không để lại file dở khi mất điện/lỗi ổ đĩa.
/// </summary>
public sealed class CanonPresetStore
{
    public const string FileExtension = ".json";

    /// <summary>File bộ thông số rất nhỏ (vài KB); giới hạn để không đọc nhầm file lớn khi nhập.</summary>
    public const long MaxFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,

        // Giữ tiếng Việt có dấu trong file để dễ đọc khi mở bằng tay.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string? _bundledDirectory;
    private readonly ILogger<CanonPresetStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="libraryDirectory">Thư mục thư viện (phải ghi được).</param>
    /// <param name="bundledDirectory">Bộ thông số đi kèm ứng dụng, chỉ dùng để khởi tạo thư viện lần đầu (có thể null).</param>
    public CanonPresetStore(string libraryDirectory, string? bundledDirectory, ILogger<CanonPresetStore> logger)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory) || !Path.IsPathFullyQualified(libraryDirectory))
        {
            throw new ArgumentException("Thư mục thư viện bộ thông số phải là đường dẫn tuyệt đối.", nameof(libraryDirectory));
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDirectory));
        _bundledDirectory = bundledDirectory;
        _logger = logger;
    }

    public string RootDirectory { get; }

    /// <summary>Danh sách bộ thông số, sắp theo tên. File hỏng bị bỏ qua (ghi log), không làm hỏng cả danh sách.</summary>
    public async Task<IReadOnlyList<CanonPreset>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var presets = new List<CanonPreset>();
            foreach (var file in Directory.EnumerateFiles(RootDirectory, "*" + FileExtension))
            {
                if (await TryReadAsync(file, cancellationToken).ConfigureAwait(false) is { } preset)
                {
                    presets.Add(preset);
                }
            }

            return presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tạo mới hoặc ghi đè (theo <see cref="CanonPreset.Id"/>). Ném IOException/UnauthorizedAccessException khi không ghi được.</summary>
    public async Task SaveAsync(CanonPreset preset, CancellationToken cancellationToken = default)
    {
        Validate(preset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            await WriteAtomicAsync(LibraryFile(preset.Id), preset, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Đã lưu bộ thông số máy ảnh \"{PresetName}\" ({ValueCount} thông số)", preset.Name, preset.Values.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(LibraryFile(id));
            _logger.LogInformation("Đã xóa bộ thông số máy ảnh {PresetId}", id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Xuất một bộ thông số ra file JSON (cùng định dạng thư viện) để chia sẻ hoặc đưa vào bộ đi kèm ứng dụng.</summary>
    public async Task ExportAsync(CanonPreset preset, string targetFile, CancellationToken cancellationToken = default)
    {
        Validate(preset);
        if (!Path.IsPathFullyQualified(targetFile))
        {
            throw new ArgumentException("Đường dẫn xuất phải là đường dẫn tuyệt đối.", nameof(targetFile));
        }

        await WriteAtomicAsync(targetFile, preset, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Đã xuất bộ thông số máy ảnh \"{PresetName}\"", preset.Name);
    }

    /// <summary>
    /// Nhập bộ thông số từ file JSON vào thư viện. Luôn tạo bộ mới (mã mới) để không ghi đè bộ đang có;
    /// trùng tên thì thêm hậu tố "(2)", "(3)"... Ném <see cref="CanonPresetImportException"/> khi file không hợp lệ.
    /// </summary>
    public async Task<CanonPreset> ImportAsync(string sourceFile, DateTimeOffset importedAt, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(sourceFile);
        if (!info.Exists)
        {
            throw new CanonPresetImportException("Không tìm thấy file.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new CanonPresetImportException("File quá lớn, không phải file bộ thông số máy ảnh.");
        }

        var source = await TryReadAsync(sourceFile, cancellationToken).ConfigureAwait(false)
            ?? throw new CanonPresetImportException("File không đúng định dạng bộ thông số máy ảnh Canon.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var existingNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(RootDirectory, "*" + FileExtension))
            {
                if (await TryReadAsync(file, cancellationToken).ConfigureAwait(false) is { } existing)
                {
                    existingNames.Add(existing.Name);
                }
            }

            var imported = source with { Id = Guid.NewGuid(), Name = UniqueName(source.Name, existingNames), UpdatedAt = importedAt };
            await WriteAtomicAsync(LibraryFile(imported.Id), imported, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Đã nhập bộ thông số máy ảnh \"{PresetName}\"", imported.Name);
            return imported;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Chép các bộ đi kèm ứng dụng mà thư viện CHƯA có (theo mã) — dùng khi bản build mới mang thêm bộ mẫu.
    /// Không ghi đè bộ đang có; bộ đi kèm đã bị xóa khỏi thư viện cũng sẽ được chép lại (quản trị viên chủ động bấm).
    /// Trả về các bộ vừa thêm.
    /// </summary>
    public async Task<IReadOnlyList<CanonPreset>> AddMissingBundledAsync(CancellationToken cancellationToken = default)
    {
        if (_bundledDirectory is null || !Directory.Exists(_bundledDirectory))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var added = new List<CanonPreset>();
            foreach (var file in Directory.EnumerateFiles(_bundledDirectory, "*" + FileExtension))
            {
                if (await TryReadAsync(file, cancellationToken).ConfigureAwait(false) is not { } bundled
                    || File.Exists(LibraryFile(bundled.Id)))
                {
                    continue;
                }

                await WriteAtomicAsync(LibraryFile(bundled.Id), bundled, cancellationToken).ConfigureAwait(false);
                added.Add(bundled);
            }

            if (added.Count > 0)
            {
                _logger.LogInformation("Đã thêm {Count} bộ thông số đi kèm ứng dụng vào thư viện", added.Count);
            }

            return added;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lần đầu: tạo thư viện và chép bộ thông số đi kèm ứng dụng. Thư viện đã có thì không bao giờ chép đè.</summary>
    private void EnsureInitialized()
    {
        if (Directory.Exists(RootDirectory))
        {
            return;
        }

        var staging = RootDirectory + ".init-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        try
        {
            var copied = 0;
            if (_bundledDirectory is not null && Directory.Exists(_bundledDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_bundledDirectory, "*" + FileExtension))
                {
                    File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
                    copied++;
                }
            }

            Directory.Move(staging, RootDirectory);
            _logger.LogInformation("Đã khởi tạo thư viện bộ thông số máy ảnh ({Count} bộ đi kèm ứng dụng)", copied);
        }
        catch
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(cleanup, "Không dọn được thư mục tạm khi khởi tạo thư viện bộ thông số");
            }

            throw;
        }
    }

    private async Task<CanonPreset?> TryReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            var preset = await JsonSerializer.DeserializeAsync<CanonPreset>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (preset is { Values: not null } && preset.Id != Guid.Empty && CanonPreset.ValidateName(preset.Name) is null
                && preset.Values.All(v => Enum.IsDefined(v.Setting) && v.Setting != CanonSetting.ExposureMode))
            {
                return preset;
            }

            _logger.LogWarning("Bỏ qua file bộ thông số không hợp lệ {FileName}", Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Không đọc được file bộ thông số {FileName}", Path.GetFileName(file));
        }

        return null;
    }

    private static async Task WriteAtomicAsync(string path, CanonPreset preset, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, preset, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void Validate(CanonPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (CanonPreset.ValidateName(preset.Name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(preset));
        }

        if (preset.Id == Guid.Empty)
        {
            throw new ArgumentException("Bộ thông số thiếu mã định danh.", nameof(preset));
        }
    }

    private static string UniqueName(string name, HashSet<string> existing)
    {
        if (!existing.Contains(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var suffix = $" ({i})";
            var candidate = (name.Length + suffix.Length > CanonPreset.MaxNameLength
                ? name[..(CanonPreset.MaxNameLength - suffix.Length)]
                : name) + suffix;
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private string LibraryFile(Guid id) => Path.Combine(RootDirectory, $"{id:N}{FileExtension}");
}

/// <summary>File nhập không phải bộ thông số hợp lệ; thông báo hiển thị được cho quản trị viên.</summary>
public sealed class CanonPresetImportException(string message) : Exception(message);
