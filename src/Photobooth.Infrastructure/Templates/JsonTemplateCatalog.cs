using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Templates;
using SkiaSharp;

namespace Photobooth.Infrastructure.Templates;

/// <summary>
/// Nạp template từ <c>Templates/{thư mục}/template.json</c> (mỗi template một thư mục, kèm asset của nó).
/// Thêm template = thêm thư mục, không cần sửa code. Không bao giờ sửa/ghi lại file JSON khi đọc.
/// Template lỗi bị bỏ qua và báo cáo rõ nguyên nhân, không làm dừng ứng dụng.
/// </summary>
public sealed class JsonTemplateCatalog : ITemplateCatalog
{
    public const string TemplateFileName = "template.json";

    /// <summary>
    /// Cấu trúc JSON nghiêm ngặt: thuộc tính lạ (gõ sai tên, thuộc tính cũ như "layer") và giá trị null
    /// cho thuộc tính bắt buộc đều bị báo lỗi thay vì âm thầm bỏ qua.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _templatesDirectory;
    private readonly ILogger<JsonTemplateCatalog> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private (IReadOnlyList<PhotoTemplate> Templates, IReadOnlyList<TemplateFileDiagnostics> Diagnostics)? _loaded;

    public JsonTemplateCatalog(string templatesDirectory, ILogger<JsonTemplateCatalog> logger)
    {
        _templatesDirectory = templatesDirectory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PhotoTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default) =>
        (await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false)).Templates;

    public async Task<IReadOnlyList<TemplateFileDiagnostics>> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        (await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false)).Diagnostics;

    /// <summary>
    /// Đọc và kiểm tra một file template.json: JSON, nội dung (TemplateValidator) và asset trên ổ đĩa.
    /// Trả về template chỉ khi không có lỗi (có thể có cảnh báo).
    /// </summary>
    public static (PhotoTemplate? Template, TemplateValidationResult Validation) LoadTemplateFile(string filePath)
    {
        PhotoTemplate? template;
        try
        {
            template = JsonSerializer.Deserialize<PhotoTemplate>(File.ReadAllText(filePath), SerializerOptions);
        }
        catch (JsonException ex)
        {
            var position = ex.LineNumber is { } line
                ? $" (dòng {line + 1}, cột {(ex.BytePositionInLine ?? 0) + 1})"
                : string.Empty;
            return (null, Fail(ex.Path ?? "$", $"JSON không hợp lệ{position}: {ex.Message}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, Fail("$", $"Không đọc được file: {ex.Message}"));
        }

        if (template is null)
        {
            return (null, Fail("$", "File template rỗng."));
        }

        template = template with { AssetDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) };
        var validation = ValidateWithFiles(template);
        return (validation.IsValid ? template : null, validation);
    }

    /// <summary>Kiểm tra nội dung (TemplateValidator) và file asset/font trong <see cref="PhotoTemplate.AssetDirectory"/>.</summary>
    public static TemplateValidationResult ValidateWithFiles(PhotoTemplate template) =>
        TemplateValidator.Validate(template).Concat(CheckFiles(template));

    private static TemplateValidationResult Fail(string path, string message) =>
        new([new TemplateValidationIssue(ValidationSeverity.Error, path, message)]);

    /// <summary>Kiểm tra asset (tồn tại, an toàn, đọc được như ảnh) và font (cảnh báo nếu máy không có).</summary>
    private static IEnumerable<TemplateValidationIssue> CheckFiles(PhotoTemplate template)
    {
        HashSet<string>? installedFonts = null;
        for (var i = 0; i < template.Decorations.Count; i++)
        {
            var decoration = template.Decorations[i];
            var path = decoration.Id is null ? $"decorations[{i}]" : $"decorations[{i}]({decoration.Id})";

            if (decoration.Type == DecorationKind.Image && TemplateAssetPath.IsSafeRelative(decoration.AssetPath))
            {
                if (!TemplateAssetResolver.TryResolve(template.AssetDirectory, decoration.AssetPath, out var fullPath, out var error))
                {
                    yield return new(ValidationSeverity.Error, $"{path}.assetPath", error);
                    continue;
                }

                using var codec = SKCodec.Create(fullPath);
                if (codec is null)
                {
                    yield return new(ValidationSeverity.Error, $"{path}.assetPath", $"File '{decoration.AssetPath}' hỏng hoặc không phải ảnh được hỗ trợ.");
                }
            }

            if (decoration.Type == DecorationKind.Text && !string.IsNullOrWhiteSpace(decoration.FontFamily))
            {
                installedFonts ??= SKFontManager.Default.GetFontFamilies().ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!installedFonts.Contains(decoration.FontFamily))
                {
                    yield return new(ValidationSeverity.Warning, $"{path}.fontFamily",
                        $"Máy không có font '{decoration.FontFamily}', sẽ dùng font thay thế.");
                }
            }
        }
    }

    private async Task<(IReadOnlyList<PhotoTemplate> Templates, IReadOnlyList<TemplateFileDiagnostics> Diagnostics)> EnsureLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (_loaded is { } loaded)
        {
            return loaded;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Đọc file nhỏ nhưng có giải mã kiểm tra asset: chạy ngoài UI thread.
            _loaded ??= await Task.Run(Load, cancellationToken).ConfigureAwait(false);
            return _loaded.Value;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private (IReadOnlyList<PhotoTemplate>, IReadOnlyList<TemplateFileDiagnostics>) Load()
    {
        var templates = new List<PhotoTemplate>();
        var diagnostics = new List<TemplateFileDiagnostics>();

        if (!Directory.Exists(_templatesDirectory))
        {
            _logger.LogError("Không tìm thấy thư mục template");
            return (templates, diagnostics);
        }

        // File JSON đặt trực tiếp trong Templates/ (cấu trúc cũ): không nạp, không sửa, chỉ báo.
        foreach (var file in Directory.EnumerateFiles(_templatesDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new TemplateFileDiagnostics(Path.GetFileName(file), null, false,
            [
                new(ValidationSeverity.Warning, "$",
                    $"Bỏ qua: mỗi template phải nằm trong thư mục riêng (Templates/<tên>/{TemplateFileName})."),
            ]));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(_templatesDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(directory);
            var relativeFile = $"{folderName}/{TemplateFileName}";
            var file = Path.Combine(directory, TemplateFileName);
            if (!File.Exists(file))
            {
                diagnostics.Add(new TemplateFileDiagnostics(relativeFile, null, false,
                    [new(ValidationSeverity.Warning, "$", $"Thư mục không có {TemplateFileName}.")]));
                continue;
            }

            var (template, validation) = LoadTemplateFile(file);
            var issues = validation.Issues.ToList();
            if (template is not null)
            {
                if (!ids.Add(template.Id))
                {
                    issues.Add(new(ValidationSeverity.Error, "id", $"Id '{template.Id}' trùng với template khác."));
                    template = null;
                }
                else if (!string.Equals(template.Id, folderName, StringComparison.Ordinal))
                {
                    issues.Add(new(ValidationSeverity.Warning, "id", $"Id '{template.Id}' khác tên thư mục '{folderName}'."));
                }
            }

            if (template is not null)
            {
                templates.Add(template);
            }

            diagnostics.Add(new TemplateFileDiagnostics(relativeFile, template?.Id, template is not null, issues));
        }

        foreach (var diagnostic in diagnostics.Where(d => d.Issues.Count > 0))
        {
            var details = string.Join(" | ", diagnostic.Issues);
            if (diagnostic.Loaded)
            {
                _logger.LogInformation("Template {TemplateFile} có cảnh báo: {Issues}", diagnostic.TemplateFile, details);
            }
            else
            {
                _logger.LogWarning("Không nạp template {TemplateFile}: {Issues}", diagnostic.TemplateFile, details);
            }
        }

        _logger.LogInformation("Đã nạp {Count} template", templates.Count);
        return (templates, diagnostics);
    }
}
