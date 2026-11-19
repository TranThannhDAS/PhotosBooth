using Photobooth.Application.Templates;

namespace Photobooth.Infrastructure.Templates;

/// <summary>Chuyển <c>assetPath</c> tương đối thành đường dẫn tuyệt đối, bảo đảm nằm trong thư mục template.</summary>
public static class TemplateAssetResolver
{
    /// <summary>Trả về false kèm lý do nếu đường dẫn không an toàn hoặc file không tồn tại.</summary>
    public static bool TryResolve(string? assetDirectory, string? assetPath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(assetDirectory))
        {
            error = "Template không có thư mục tài nguyên.";
            return false;
        }

        if (!TemplateAssetPath.IsSafeRelative(assetPath))
        {
            error = $"Đường dẫn asset không an toàn: '{assetPath}'.";
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetDirectory)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, assetPath!));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Asset nằm ngoài thư mục template: '{assetPath}'.";
            return false;
        }

        if (!File.Exists(candidate))
        {
            error = $"Không tìm thấy asset '{assetPath}'.";
            return false;
        }

        fullPath = candidate;
        error = string.Empty;
        return true;
    }
}
