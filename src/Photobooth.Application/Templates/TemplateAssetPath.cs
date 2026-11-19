namespace Photobooth.Application.Templates;

/// <summary>Kiểm tra đường dẫn asset trong template (thuần chuỗi, không truy cập ổ đĩa).</summary>
public static class TemplateAssetPath
{
    /// <summary>
    /// Chỉ chấp nhận đường dẫn tương đối nằm trong thư mục template, ví dụ "assets/logo.png".
    /// Từ chối: đường dẫn tuyệt đối, ổ đĩa (C:), đường dẫn mạng (\\server), URI (http:), và ".." (path traversal).
    /// </summary>
    public static bool IsSafeRelative(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)
            || assetPath.Contains(':', StringComparison.Ordinal)
            || assetPath.StartsWith('/')
            || assetPath.StartsWith('\\')
            || Path.IsPathRooted(assetPath)
            || assetPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        var segments = assetPath.Split('/', '\\');
        return segments.All(s => s.Length > 0 && s != "." && s != "..");
    }
}
