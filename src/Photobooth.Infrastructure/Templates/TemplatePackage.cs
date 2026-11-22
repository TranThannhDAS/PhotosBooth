using System.IO.Compression;

namespace Photobooth.Infrastructure.Templates;

/// <summary>
/// Gói .zip của một template: template.json (+ thumbnail.png, assets/…) ở gốc gói,
/// hoặc nằm trong MỘT thư mục con duy nhất (gói nén nguyên thư mục).
/// </summary>
internal static class TemplatePackage
{
    public static async Task WriteAsync(Stream destination, IReadOnlyList<(string EntryName, string FullPath)> files, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (entryName, fullPath) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            await using var output = entry.Open();
            await using var input = File.OpenRead(fullPath);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Giải nén vào <paramref name="targetDirectory"/> (thư mục mới). Từ chối mục có đường dẫn tuyệt đối, ổ đĩa, "..",
    /// hoặc trỏ ra ngoài thư mục đích (Zip Slip); giới hạn tổng dung lượng giải nén và số mục để tránh "zip bomb".
    /// </summary>
    /// <exception cref="InvalidDataException">Gói hỏng hoặc không an toàn.</exception>
    public static void ExtractSafely(string zipPath, string targetDirectory, long maxTotalBytes, int maxEntries)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > maxEntries)
        {
            throw new InvalidDataException($"gói có quá nhiều tệp (tối đa {maxEntries}).");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            if (!Application.Templates.TemplateAssetPath.IsSafeRelative(name))
            {
                throw new InvalidDataException($"đường dẫn không an toàn trong gói: '{entry.FullName}'.");
            }

            var destination = Path.GetFullPath(Path.Combine(root, name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"tệp nằm ngoài thư mục đích: '{entry.FullName}'.");
            }

            total += entry.Length;
            if (entry.Length < 0 || total > maxTotalBytes)
            {
                throw new InvalidDataException("dung lượng sau giải nén quá lớn.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            // Không tin Length khai báo trong gói: đếm số byte thật khi chép.
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length || total - entry.Length + written > maxTotalBytes)
                {
                    throw new InvalidDataException("dung lượng sau giải nén không khớp hoặc quá lớn.");
                }

                output.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>template.json ở gốc, hoặc trong thư mục con duy nhất. Không có → null.</summary>
    public static string? FindTemplateFile(string directory)
    {
        var direct = Path.Combine(directory, JsonTemplateCatalog.TemplateFileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var children = Directory.GetDirectories(directory);
        if (children.Length == 1 && Directory.GetFiles(directory).Length == 0)
        {
            var nested = Path.Combine(children[0], JsonTemplateCatalog.TemplateFileName);
            return File.Exists(nested) ? nested : null;
        }

        return null;
    }
}
