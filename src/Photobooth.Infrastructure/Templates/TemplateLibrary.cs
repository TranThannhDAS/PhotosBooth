using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Application.Templates.Editing;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Storage;
using SkiaSharp;

namespace Photobooth.Infrastructure.Templates;

/// <summary>
/// Template Library: thư viện template DÙNG CHUNG cho mọi sự kiện, mặc định %LOCALAPPDATA%\Photobooth\Templates.
/// <code>
/// Templates/
///   library.json            trạng thái bật/tắt trên Kiosk (không thuộc cấu trúc template)
///   {id}/template.json      đúng cấu trúc của Template Engine (docs/TEMPLATE_SCHEMA.md)
///   {id}/thumbnail.png      ảnh đại diện (tạo lại khi lưu từ Editor)
///   {id}/assets/...         ảnh trang trí, đường dẫn tương đối
/// </code>
/// Lần chạy đầu tiên, thư viện được tạo bằng cách chép các template đi kèm ứng dụng.
/// Phiên chụp luôn dùng bản snapshot riêng (Event Workspace), nên sửa/xóa template ở đây không làm đổi phiên cũ.
/// Template lỗi vẫn được liệt kê để quản trị viên xử lý nhưng không hiện trên Kiosk.
/// </summary>
public sealed class TemplateLibrary : ITemplateCatalog
{
    public const string StateFileName = "library.json";
    public const string AssetsFolderName = "assets";
    public const long MaxAssetBytes = 30L * 1024 * 1024;
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    public const int MaxPackageEntries = 500;
    private const int ThumbnailWidthPixels = 600;
    private const string StagingFolderName = ".staging";

    public static readonly IReadOnlyList<string> SupportedImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>Ghi template.json: cùng cấu trúc Engine đọc, thụt lề, giữ tiếng Việt, bỏ thuộc tính null.</summary>
    internal static readonly JsonSerializerOptions TemplateWriteOptions = new(JsonTemplateCatalog.SerializerOptions)
    {
        WriteIndented = true,
        Encoder = PhotoboothJsonOptions.Options.Encoder,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions StateOptions = new(PhotoboothJsonOptions.Options);

    private readonly string? _bundledDirectory;
    private readonly IPhotoComposer _composer;
    private readonly ILogger<TemplateLibrary> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<TemplateLibraryEntry>? _entries;
    private IReadOnlyList<string>? _fonts;

    /// <param name="libraryDirectory">Thư mục thư viện (phải ghi được).</param>
    /// <param name="bundledDirectory">Template đi kèm ứng dụng, chỉ dùng để khởi tạo thư viện lần đầu (có thể null).</param>
    public TemplateLibrary(string libraryDirectory, string? bundledDirectory, IPhotoComposer composer, ILogger<TemplateLibrary> logger)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory) || !Path.IsPathFullyQualified(libraryDirectory))
        {
            throw new ArgumentException("Thư mục thư viện phải là đường dẫn tuyệt đối.", nameof(libraryDirectory));
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDirectory));
        _bundledDirectory = bundledDirectory;
        _composer = composer;
        _logger = logger;
    }

    public string RootDirectory { get; }

    /// <summary>Thư viện đã thay đổi (thêm/sửa/xóa/bật/tắt).</summary>
    public event EventHandler? Changed;

    // ================================================================ Đọc

    public async Task<IReadOnlyList<TemplateLibraryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (_entries is { } entries)
        {
            return entries;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Kiosk: chỉ template hợp lệ VÀ đang bật.</summary>
    public async Task<IReadOnlyList<PhotoTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(e => e.Enabled && e.Template is not null)
            .Select(e => e.Template!)
            .ToArray();

    public async Task<IReadOnlyList<TemplateFileDiagnostics>> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(e => new TemplateFileDiagnostics($"{e.Key}/{JsonTemplateCatalog.TemplateFileName}", e.Template?.Id, e.IsValid, e.Issues))
            .ToArray();

    public async Task<TemplateLibraryEntry?> FindAsync(string key, CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Key == key);

    /// <summary>Đọc lại thư viện từ ổ đĩa (khi quản trị viên sửa tay thư mục).</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Font cài trên máy (cho ô chọn font của Editor).</summary>
    public IReadOnlyList<string> GetInstalledFonts() =>
        _fonts ??= SKFontManager.Default.GetFontFamilies()
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    // ================================================================ Quản lý

    /// <summary>Tạo template mới hợp lệ tối thiểu: một ô ảnh (photoIndex 0) gần kín canvas. Mặc định TẮT trên Kiosk.</summary>
    public async Task<TemplateLibraryEntry> CreateAsync(NewTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new TemplateLibraryException("Vui lòng nhập tên template.");
        }

        string key = string.Empty;
        await MutateAsync(
            async () =>
            {
                key = TemplateIds.MakeUnique(request.Name, IsTaken);
                var margin = Math.Max(0, Math.Min(request.WidthPixels, request.HeightPixels) / 20);
                var template = new PhotoTemplate
                {
                    Id = key,
                    Name = request.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                    Canvas = new TemplateCanvas
                    {
                        WidthPixels = request.WidthPixels,
                        HeightPixels = request.HeightPixels,
                        Dpi = request.Dpi,
                        BackgroundColor = request.BackgroundColor,
                    },
                    Slots =
                    [
                        new TemplateSlot
                        {
                            Id = "photo-1",
                            PhotoIndex = 0,
                            X = margin,
                            Y = margin,
                            Width = Math.Max(1, request.WidthPixels - (2 * margin)),
                            Height = Math.Max(1, request.HeightPixels - (2 * margin)),
                        },
                    ],
                };

                var validation = TemplateValidator.Validate(template);
                if (!validation.IsValid)
                {
                    throw new TemplateLibraryException("Thông số canvas không hợp lệ: " + string.Join(" ", validation.Errors.Select(i => i.Message)));
                }

                var folder = Path.Combine(RootDirectory, key);
                Directory.CreateDirectory(folder);
                await WriteTemplateFileAsync(folder, template, cancellationToken).ConfigureAwait(false);
                await TryWriteThumbnailAsync(template with { AssetDirectory = folder }, cancellationToken).ConfigureAwait(false);
                await UpdateStateAsync(s => s.With(key, enabled: false), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Đã tạo template {TemplateId}", key);
            },
            cancellationToken).ConfigureAwait(false);

        return await RequireAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Nhân bản: id mới, chép template.json + thumbnail + asset (bản riêng, không dùng chung file). Bản gốc không đổi.</summary>
    public async Task<TemplateLibraryEntry> DuplicateAsync(string key, CancellationToken cancellationToken = default)
    {
        string newKey = string.Empty;
        await MutateAsync(
            async () =>
            {
                var source = RequireValid(key);
                var template = source.Template!;
                newKey = TemplateIds.MakeUnique(template.Id + "-ban-sao", IsTaken);
                var copy = template with { Id = newKey, Name = $"{template.Name} (bản sao)" };
                await CopyTemplateAsync(copy, ResolveFolder(key), Path.Combine(RootDirectory, newKey), cancellationToken).ConfigureAwait(false);
                await UpdateStateAsync(s => s.With(newKey, enabled: false), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Đã sao chép template {TemplateId} thành {NewTemplateId}", key, newKey);
            },
            cancellationToken).ConfigureAwait(false);

        return await RequireAsync(newKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Đổi tên hiển thị/mô tả (id và thư mục giữ nguyên để không ảnh hưởng dữ liệu khác).</summary>
    public async Task<TemplateLibraryEntry> RenameAsync(string key, string name, string? description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new TemplateLibraryException("Tên template không được để trống.");
        }

        await MutateAsync(
            async () =>
            {
                var entry = RequireValid(key);
                var renamed = entry.Template! with
                {
                    Name = name.Trim(),
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                };
                await WriteTemplateFileAsync(ResolveFolder(key), renamed, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return await RequireAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Xóa hẳn thư mục của template này (sau khi giao diện đã xác nhận). Asset của template khác và mọi phiên chụp
    /// trong Event Workspace không bị đụng tới (phiên dùng bản snapshot riêng).
    /// </summary>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        MutateAsync(
            async () =>
            {
                var folder = ResolveFolder(key);
                if (!Directory.Exists(folder))
                {
                    throw new TemplateLibraryException("Không tìm thấy template.");
                }

                Directory.Delete(folder, recursive: true);
                await UpdateStateAsync(s => s.Without(key), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Đã xóa template {TemplateId}", key);
            },
            cancellationToken);

    /// <summary>Bật/tắt hiển thị trên Kiosk. Không đổi template.json.</summary>
    public Task SetEnabledAsync(string key, bool enabled, CancellationToken cancellationToken = default) =>
        MutateAsync(
            async () =>
            {
                if (!Directory.Exists(ResolveFolder(key)))
                {
                    throw new TemplateLibraryException("Không tìm thấy template.");
                }

                await UpdateStateAsync(s => s.With(key, enabled), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    // ================================================================ Editor

    /// <summary>
    /// Lưu template từ Editor: kiểm tra đầy đủ (nội dung + asset) TRƯỚC khi ghi; không hợp lệ thì không ghi gì,
    /// bản đang có giữ nguyên. Ghi theo kiểu tạm-rồi-đổi-tên, sau đó tạo lại thumbnail.
    /// </summary>
    public async Task<TemplateOperationResult> SaveAsync(string key, PhotoTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        TemplateOperationResult? result = null;
        await MutateAsync(
            async () =>
            {
                var folder = ResolveFolder(key);
                if (!File.Exists(Path.Combine(folder, JsonTemplateCatalog.TemplateFileName)))
                {
                    result = TemplateOperationResult.Fail("Không tìm thấy template trong thư viện (có thể đã bị xóa).");
                    return;
                }

                var current = _entries?.FirstOrDefault(e => e.Key == key)?.Template;
                if (current is not null && current.Id != template.Id)
                {
                    result = TemplateOperationResult.Fail("Không được đổi mã (id) của template khi lưu.");
                    return;
                }

                var located = template with { AssetDirectory = folder };
                var validation = await Task.Run(() => JsonTemplateCatalog.ValidateWithFiles(located), cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    result = TemplateOperationResult.Fail("Template chưa hợp lệ, chưa được lưu.", validation.Issues);
                    return;
                }

                await WriteTemplateFileAsync(folder, located, cancellationToken).ConfigureAwait(false);
                await TryWriteThumbnailAsync(located, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Đã lưu template {TemplateId} từ Editor", key);
                result = new TemplateOperationResult(true, null, validation.Issues);
            },
            cancellationToken).ConfigureAwait(false);

        return result! with { Entry = result!.Succeeded ? await FindAsync(key, cancellationToken).ConfigureAwait(false) : null };
    }

    /// <summary>Chép một ảnh vào assets/ của template với tên mới không trùng (không ghi đè file nào). Trả về đường dẫn tương đối.</summary>
    public async Task<TemplateAsset> AddAssetAsync(string key, string sourceFile, CancellationToken cancellationToken = default)
    {
        var folder = ResolveFolder(key);
        if (!Directory.Exists(folder))
        {
            throw new TemplateLibraryException("Không tìm thấy template.");
        }

        var extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (!SupportedImageExtensions.Contains(extension))
        {
            throw new TemplateLibraryException("Chỉ hỗ trợ ảnh PNG, JPEG, WebP, BMP hoặc GIF.");
        }

        var info = new FileInfo(sourceFile);
        if (!info.Exists)
        {
            throw new TemplateLibraryException("Không tìm thấy file ảnh.");
        }

        if (info.Length > MaxAssetBytes)
        {
            throw new TemplateLibraryException("Ảnh quá lớn (tối đa 30 MB).");
        }

        var (width, height) = await Task.Run(() => ReadImageSize(sourceFile), cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateLibraryException("File hỏng hoặc không phải ảnh được hỗ trợ.");

        // Tên mới, không trùng: "logo-3f9a2c1b.png". Không bao giờ ghi đè asset đang có.
        var stem = TemplateIds.Slugify(Path.GetFileNameWithoutExtension(sourceFile));
        stem = stem.Length > 40 ? stem[..40].TrimEnd('-') : stem;
        var name = string.Create(CultureInfo.InvariantCulture, $"{stem}-{Guid.NewGuid().ToString("N")[..8]}");
        var relative = $"{AssetsFolderName}/{name}{extension}";
        var target = Path.Combine(folder, AssetsFolderName, name + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await CopyFileAsync(sourceFile, target, cancellationToken).ConfigureAwait(false);
        return new TemplateAsset(relative, width, height);
    }

    /// <summary>Đọc nội dung asset (để hiển thị trong Editor); đường dẫn phải nằm trong thư mục template.</summary>
    public async Task<byte[]> ReadAssetAsync(string key, string assetPath, CancellationToken cancellationToken = default)
    {
        if (!TemplateAssetResolver.TryResolve(ResolveFolder(key), assetPath, out var fullPath, out var error))
        {
            throw new TemplateLibraryException(error);
        }

        if (new FileInfo(fullPath).Length > MaxAssetBytes)
        {
            throw new TemplateLibraryException("Ảnh quá lớn để hiển thị.");
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dọn các asset do Editor vừa thêm (<paramref name="addedAssetPaths"/>) nhưng bản template ĐÃ LƯU không dùng
    /// (ví dụ hủy chỉnh sửa). Asset có sẵn từ trước không bao giờ bị xóa ở đây.
    /// </summary>
    public async Task RemoveUnusedAddedAssetsAsync(string key, IEnumerable<string> addedAssetPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addedAssetPaths);
        var folder = ResolveFolder(key);
        var file = Path.Combine(folder, JsonTemplateCatalog.TemplateFileName);
        if (!File.Exists(file))
        {
            return;
        }

        var (saved, _) = await Task.Run(() => JsonTemplateCatalog.LoadTemplateFile(file), cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            // Không đọc được bản đã lưu: không xóa gì cho an toàn.
            return;
        }

        var used = saved.Decorations.Where(d => d.AssetPath is not null)
            .Select(d => Path.GetFullPath(Path.Combine(folder, d.AssetPath!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assetsRoot = Path.Combine(folder, AssetsFolderName) + Path.DirectorySeparatorChar;
        foreach (var path in addedAssetPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TemplateAssetPath.IsSafeRelative(path))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(folder, path));
            if (full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && !used.Contains(full) && File.Exists(full))
            {
                TryDeleteFile(full);
            }
        }
    }

    // ================================================================ Nhập / xuất

    /// <summary>
    /// Nhập từ thư mục (chứa template.json) hoặc gói .zip. Kiểm tra JSON, ô ảnh, asset; lỗi → không ghi gì vào thư viện.
    /// Id trùng → tự đặt id mới (không ghi đè template đang có). Template nhập mặc định TẮT trên Kiosk.
    /// </summary>
    public async Task<TemplateOperationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
        {
            return TemplateOperationResult.Fail("Đường dẫn nhập không hợp lệ.");
        }

        TemplateOperationResult? result = null;
        string? importedKey = null;
        await MutateAsync(
            async () =>
            {
                string? staging = null;
                try
                {
                    string directory;
                    if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        staging = Path.Combine(RootDirectory, StagingFolderName, Guid.NewGuid().ToString("N"));
                        await Task.Run(() => TemplatePackage.ExtractSafely(sourcePath, staging, MaxPackageBytes, MaxPackageEntries), cancellationToken).ConfigureAwait(false);
                        directory = staging;
                    }
                    else if (Directory.Exists(sourcePath))
                    {
                        directory = sourcePath;
                    }
                    else
                    {
                        result = TemplateOperationResult.Fail("Không tìm thấy thư mục hoặc file .zip cần nhập.");
                        return;
                    }

                    var templateFile = TemplatePackage.FindTemplateFile(directory);
                    if (templateFile is null)
                    {
                        result = TemplateOperationResult.Fail($"Không tìm thấy {JsonTemplateCatalog.TemplateFileName} trong nội dung nhập.");
                        return;
                    }

                    var (template, validation) = await Task.Run(() => JsonTemplateCatalog.LoadTemplateFile(templateFile), cancellationToken).ConfigureAwait(false);
                    if (template is null)
                    {
                        result = TemplateOperationResult.Fail("Template không hợp lệ, chưa được nhập.", validation.Issues);
                        return;
                    }

                    var key = TemplateIds.MakeUnique(template.Id, IsTaken);
                    var message = key == template.Id
                        ? $"Đã nhập template “{template.Name}”."
                        : $"Đã nhập template “{template.Name}” với mã mới “{key}” vì mã “{template.Id}” đã tồn tại.";
                    await CopyTemplateAsync(template with { Id = key }, template.AssetDirectory!, Path.Combine(RootDirectory, key), cancellationToken).ConfigureAwait(false);
                    await UpdateStateAsync(s => s.With(key, enabled: false), cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Đã nhập template {TemplateId}", key);
                    importedKey = key;
                    result = new TemplateOperationResult(true, null, validation.Issues, message);
                }
                catch (InvalidDataException ex)
                {
                    result = TemplateOperationResult.Fail("Gói .zip không hợp lệ: " + ex.Message);
                }
                finally
                {
                    if (staging is not null)
                    {
                        TryDeleteDirectory(staging);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        return importedKey is not null
            ? result! with { Entry = await FindAsync(importedKey, cancellationToken).ConfigureAwait(false) }
            : result!;
    }

    /// <summary>Xuất template (template.json, thumbnail, asset đang dùng) thành gói .zip nhập lại được.</summary>
    public async Task ExportAsync(string key, string zipPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !Path.IsPathFullyQualified(zipPath))
        {
            throw new TemplateLibraryException("Đường dẫn xuất không hợp lệ.");
        }

        var entry = await FindAsync(key, cancellationToken).ConfigureAwait(false);
        if (entry?.Template is not { } template)
        {
            throw new TemplateLibraryException("Chỉ xuất được template hợp lệ.");
        }

        var folder = ResolveFolder(key);
        var files = new List<(string EntryName, string FullPath)>
        {
            (JsonTemplateCatalog.TemplateFileName, Path.Combine(folder, JsonTemplateCatalog.TemplateFileName)),
        };
        var thumbnail = Path.Combine(folder, Imaging.SkiaPhotoComposer.ThumbnailFileName);
        if (File.Exists(thumbnail))
        {
            files.Add((Imaging.SkiaPhotoComposer.ThumbnailFileName, thumbnail));
        }

        foreach (var assetPath in template.Decorations.Select(d => d.AssetPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TemplateAssetResolver.TryResolve(folder, assetPath, out var full, out var error))
            {
                throw new TemplateLibraryException(error);
            }

            files.Add((assetPath.Replace('\\', '/'), full));
        }

        await SafeFileWriter.WriteAsync(
                zipPath,
                (stream, token) => TemplatePackage.WriteAsync(stream, files, token),
                overwrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Đã xuất template {TemplateId}", key);
    }

    // ================================================================ Nội bộ

    private bool IsTaken(string id) =>
        Directory.Exists(Path.Combine(RootDirectory, id))
        || _entries?.Any(e => e.Template?.Id == id) == true;

    /// <summary>Thư mục của một template: chỉ một cấp dưới thư viện (chặn ".." và đường dẫn tuyệt đối).</summary>
    private string ResolveFolder(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith('.') || key.IndexOfAny(['/', '\\', ':']) >= 0
            || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new TemplateLibraryException("Mã template không hợp lệ.");
        }

        var folder = Path.GetFullPath(Path.Combine(RootDirectory, key));
        if (!string.Equals(Path.GetDirectoryName(folder), RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new TemplateLibraryException("Mã template không hợp lệ.");
        }

        return folder;
    }

    private TemplateLibraryEntry RequireValid(string key)
    {
        var entry = _entries?.FirstOrDefault(e => e.Key == key) ?? throw new TemplateLibraryException("Không tìm thấy template.");
        return entry.Template is null ? throw new TemplateLibraryException("Template đang bị lỗi, hãy sửa hoặc xóa.") : entry;
    }

    private async Task<TemplateLibraryEntry> RequireAsync(string key, CancellationToken cancellationToken) =>
        await FindAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new TemplateLibraryException("Không đọc lại được template vừa ghi.");

    /// <summary>Mọi thay đổi chạy tuần tự; danh sách được đọc lại từ ổ đĩa sau đó (kể cả khi lỗi).</summary>
    private async Task MutateAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Lỗi ghi Template Library");
                throw new TemplateLibraryException("Không ghi được thư viện template (ổ đĩa đầy, bị khóa hoặc không có quyền).", ex);
            }
            finally
            {
                RemoveEmptyStaging();
                _entries = null;
                await EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IReadOnlyList<TemplateLibraryEntry>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is { } entries)
        {
            return entries;
        }

        var loaded = await Task.Run(Load, cancellationToken).ConfigureAwait(false);
        _entries = loaded;
        return loaded;
    }

    private IReadOnlyList<TemplateLibraryEntry> Load()
    {
        EnsureInitialized();
        var state = ReadState();
        var entries = new List<TemplateLibraryEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var key = Path.GetFileName(directory);
            if (key.StartsWith('.'))
            {
                continue;
            }

            var file = Path.Combine(directory, JsonTemplateCatalog.TemplateFileName);
            if (!File.Exists(file))
            {
                entries.Add(new TemplateLibraryEntry(key, null, false,
                    [new(ValidationSeverity.Error, "$", $"Thư mục không có {JsonTemplateCatalog.TemplateFileName}.")]));
                continue;
            }

            var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(file);
            var issues = validation.Issues.ToList();
            if (template is not null && !ids.Add(template.Id))
            {
                issues.Add(new(ValidationSeverity.Error, "id", $"Id '{template.Id}' trùng với template khác."));
                template = null;
            }
            else if (template is not null && template.Id != key)
            {
                issues.Add(new(ValidationSeverity.Warning, "id", $"Id '{template.Id}' khác tên thư mục '{key}'."));
            }

            entries.Add(new TemplateLibraryEntry(key, template, !state.IsDisabled(key), issues));
        }

        foreach (var entry in entries.Where(e => !e.IsValid))
        {
            _logger.LogWarning("Template {TemplateFolder} không dùng được: {Issues}", entry.Key, string.Join(" | ", entry.Issues));
        }

        return entries;
    }

    /// <summary>Lần đầu: tạo thư viện và chép template đi kèm ứng dụng. Thư viện đã có thì không bao giờ chép đè.</summary>
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
            if (_bundledDirectory is not null && Directory.Exists(_bundledDirectory))
            {
                foreach (var source in Directory.EnumerateDirectories(_bundledDirectory))
                {
                    if (File.Exists(Path.Combine(source, JsonTemplateCatalog.TemplateFileName)))
                    {
                        CopyDirectory(source, Path.Combine(staging, Path.GetFileName(source)));
                    }
                }
            }

            Directory.Move(staging, RootDirectory);
            _logger.LogInformation("Đã khởi tạo Template Library từ template đi kèm ứng dụng");
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    /// <summary>
    /// Tạo thư mục template mới từ một template hợp lệ: ghi template.json (id mới), chép thumbnail và asset ĐANG DÙNG.
    /// Ghi vào thư mục tạm rồi đổi tên, nên lỗi giữa chừng không để lại template dở dang.
    /// </summary>
    private async Task CopyTemplateAsync(PhotoTemplate template, string sourceDirectory, string targetDirectory, CancellationToken cancellationToken)
    {
        if (Directory.Exists(targetDirectory))
        {
            throw new TemplateLibraryException("Thư mục template đích đã tồn tại.");
        }

        var staging = Path.Combine(RootDirectory, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var assetPath in template.Decorations.Select(d => d.AssetPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!TemplateAssetResolver.TryResolve(sourceDirectory, assetPath, out var source, out var error))
                {
                    throw new TemplateLibraryException(error);
                }

                var target = Path.GetFullPath(Path.Combine(staging, assetPath));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new TemplateLibraryException($"Đường dẫn asset không an toàn: '{assetPath}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyFileAsync(source, target, cancellationToken).ConfigureAwait(false);
            }

            var thumbnail = Path.Combine(sourceDirectory, Imaging.SkiaPhotoComposer.ThumbnailFileName);
            if (File.Exists(thumbnail) && ReadImageSize(thumbnail) is not null)
            {
                await CopyFileAsync(thumbnail, Path.Combine(staging, Imaging.SkiaPhotoComposer.ThumbnailFileName), cancellationToken).ConfigureAwait(false);
            }

            await WriteTemplateFileAsync(staging, template, cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, targetDirectory);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static Task WriteTemplateFileAsync(string folder, PhotoTemplate template, CancellationToken cancellationToken) =>
        SafeFileWriter.WriteAsync(
            Path.Combine(folder, JsonTemplateCatalog.TemplateFileName),
            (stream, token) => JsonSerializer.SerializeAsync(stream, template, TemplateWriteOptions, token),
            overwrite: true,
            cancellationToken);

    /// <summary>Ảnh đại diện = bố cục render bằng Photo Composer (ô ảnh đánh số). Lỗi chỉ ghi log, không làm hỏng việc lưu.</summary>
    private async Task TryWriteThumbnailAsync(PhotoTemplate template, CancellationToken cancellationToken)
    {
        try
        {
            var png = await _composer.RenderTemplateSampleAsync(template, TemplateSampleContent.Placeholders, ThumbnailWidthPixels, cancellationToken).ConfigureAwait(false);
            await SafeFileWriter.WriteAsync(
                    Path.Combine(template.AssetDirectory!, Imaging.SkiaPhotoComposer.ThumbnailFileName),
                    (stream, token) => stream.WriteAsync(png, token).AsTask(),
                    overwrite: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CompositionException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không tạo được ảnh đại diện cho template {TemplateId}", template.Id);
        }
    }

    private static Task CopyFileAsync(string source, string target, CancellationToken cancellationToken) =>
        SafeFileWriter.WriteAsync(
            target,
            async (stream, token) =>
            {
                await using var input = File.OpenRead(source);
                await input.CopyToAsync(stream, token).ConfigureAwait(false);
            },
            overwrite: false,
            cancellationToken);

    private static (int Width, int Height)? ReadImageSize(string file)
    {
        using var codec = SKCodec.Create(file);
        return codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ? null : (codec.Info.Width, codec.Info.Height);
    }

    private LibraryState ReadState()
    {
        var file = Path.Combine(RootDirectory, StateFileName);
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(file), StateOptions) ?? new LibraryState()
                : new LibraryState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không đọc được {StateFile}, coi mọi template là đang bật", StateFileName);
            return new LibraryState();
        }
    }

    private Task UpdateStateAsync(Func<LibraryState, LibraryState> update, CancellationToken cancellationToken)
    {
        var next = update(ReadState());
        return SafeFileWriter.WriteAsync(
            Path.Combine(RootDirectory, StateFileName),
            (stream, token) => JsonSerializer.SerializeAsync(stream, next, StateOptions, token),
            overwrite: true,
            cancellationToken);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không dọn được thư mục tạm của Template Library");
        }
    }

    /// <summary>Thư mục tạm .staging chỉ tồn tại trong lúc nhập/sao chép; xóa khi đã rỗng.</summary>
    private void RemoveEmptyStaging()
    {
        var staging = Path.Combine(RootDirectory, StagingFolderName);
        try
        {
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
            {
                Directory.Delete(staging);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Chưa dọn được thư mục tạm của Template Library");
        }
    }

    private void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không xóa được asset không dùng");
        }
    }

    /// <summary>Nội dung library.json: danh sách template bị tắt trên Kiosk (mặc định: bật).</summary>
    private sealed record LibraryState
    {
        public IReadOnlyList<string> DisabledTemplates { get; init; } = [];

        public bool IsDisabled(string key) => DisabledTemplates.Contains(key, StringComparer.Ordinal);

        public LibraryState With(string key, bool enabled) => this with
        {
            DisabledTemplates = enabled
                ? DisabledTemplates.Where(k => k != key).ToArray()
                : DisabledTemplates.Append(key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        };

        public LibraryState Without(string key) => With(key, enabled: true);
    }
}
