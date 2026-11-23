using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Photobooth.Cloud.Data;

namespace Photobooth.Cloud.Storage;

/// <summary>Ảnh đã lưu trên server. <see cref="Id"/> là ID ảnh (upload thường) hoặc Share Token (ảnh chia sẻ qua QR).</summary>
public sealed record StoredPhoto(string Id, PhotoFormat Format, string FullPath, long Length, DateTimeOffset LastModified);

public enum PhotoRejectReason
{
    Empty,
    TooLarge,
    UnsupportedFormat,
}

public sealed class PhotoRejectedException(PhotoRejectReason reason) : Exception($"Ảnh bị từ chối: {reason}")
{
    public PhotoRejectReason Reason { get; } = reason;
}

/// <summary>Share Token đã gắn với một ảnh có nội dung KHÁC — không bao giờ ghi đè ảnh đã chia sẻ.</summary>
public sealed class ShareConflictException() : Exception("Share Token đã được dùng cho một ảnh khác.");

/// <summary>Không đọc/ghi được metadata (PostgreSQL không sẵn sàng...). Ảnh không được công bố khi metadata chưa ghi được.</summary>
public sealed class PhotoMetadataUnavailableException(Exception inner) : Exception("Không truy cập được cơ sở dữ liệu metadata ảnh.", inner);

/// <summary>Kết quả lưu ảnh theo Share Token.</summary>
/// <param name="Created">true = vừa lưu mới; false = ảnh giống hệt đã có sẵn (upload lại), không tạo dữ liệu mới.</param>
public sealed record SharedSaveResult(StoredPhoto Photo, bool Created);

public enum PhotoAvailability
{
    /// <summary>Không có metadata (hoặc ID/token sai định dạng).</summary>
    NotFound,

    /// <summary>Có metadata và file.</summary>
    Available,

    /// <summary>Có metadata nhưng file trên ổ đĩa đã mất — KHÔNG được báo như ảnh có sẵn.</summary>
    FileMissing,

    /// <summary>Không truy cập được database metadata (chỉ dùng cho giao diện; API trả 503).</summary>
    MetadataUnavailable,
}

public sealed record PhotoLookup(PhotoAvailability Availability, StoredPhoto? Photo)
{
    public static readonly PhotoLookup NotFound = new(PhotoAvailability.NotFound, null);
}

/// <summary>
/// Lưu ảnh: FILE trên chính server (Storage:RootPath), METADATA trong PostgreSQL (bảng photos).
/// <list type="bullet">
/// <item>ID ngẫu nhiên 130 bit (26 ký tự base32 chữ thường), không phân biệt hoa/thường nên an toàn trên mọi hệ thống file.</item>
/// <item>Share Token (do Desktop sinh, 32 ký tự Base64URL = 192 bit): file lưu theo SHA-256 của token, unique trong database.</item>
/// <item>Tên/đường dẫn file chỉ do server đặt; tên file client gửi không bao giờ được dùng; đường dẫn đọc từ database
/// luôn được kiểm tra nằm trong Storage:RootPath.</item>
/// <item>Ghi file tạm (cùng ổ đĩa) → đổi tên KHÔNG ghi đè → ghi metadata. Ghi metadata lỗi thì xóa file vừa đặt vào chỗ
/// (không để file mồ côi). Ảnh chỉ được coi là công bố khi có metadata và file.</item>
/// </list>
/// </summary>
public sealed class PhotoStore
{
    public const int IdLength = 26;
    public const int ShareTokenLength = 32;
    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const string ShareTokenAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const string IncomingFolderName = ".incoming";
    private const string SharesFolderName = "shares";
    private const int CopyBufferSize = 81920;
    private const int MaxIdAttempts = 5;

    private static readonly SearchValues<char> IdChars = SearchValues.Create(IdAlphabet);
    private static readonly SearchValues<char> ShareTokenChars = SearchValues.Create(ShareTokenAlphabet);

    private readonly IDbContextFactory<CloudDbContext> _db;
    private readonly ILogger<PhotoStore> _logger;
    private readonly Func<string> _newId;

    // Tuần tự hóa phần "kiểm tra metadata → đặt file → ghi metadata" của ảnh chia sẻ trong một tiến trình
    // (nhiều instance song song vẫn được chặn bởi unique constraint của share_token).
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    public PhotoStore(string rootPath, long maxFileBytes, IDbContextFactory<CloudDbContext> db, ILogger<PhotoStore> logger, Func<string>? idGenerator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        MaxFileBytes = maxFileBytes;
        _db = db;
        _logger = logger;
        _newId = idGenerator ?? NewId;
    }

    public string RootPath { get; }

    public long MaxFileBytes { get; }

    private string IncomingPath => Path.Combine(RootPath, IncomingFolderName);

    public static bool IsValidId(string? id) =>
        id is { Length: IdLength } && !id.AsSpan().ContainsAnyExcept(IdChars);

    /// <summary>Share Token hợp lệ về định dạng: đúng 32 ký tự Base64URL (không padding) = 192 bit.</summary>
    public static bool IsValidShareToken(string? token) =>
        token is { Length: ShareTokenLength } && !token.AsSpan().ContainsAnyExcept(ShareTokenChars);

    public static string NewId() => RandomNumberGenerator.GetString(IdAlphabet, IdLength);

    /// <summary>Tạo thư mục lưu trữ và kiểm tra ghi được — gọi lúc khởi động để báo lỗi cấu hình sớm.</summary>
    public void EnsureReady()
    {
        CreatePrivateDirectory(RootPath);
        CreatePrivateDirectory(IncomingPath);
        var probe = Path.Combine(IncomingPath, $"probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(probe, []);
        File.Delete(probe);
    }

    /// <summary>
    /// Tìm ảnh theo ID ảnh hoặc Share Token (hai định dạng không trùng nhau vì khác độ dài) qua metadata,
    /// rồi kiểm tra file thực tế. Giá trị sai định dạng được coi như không tồn tại.
    /// </summary>
    /// <exception cref="PhotoMetadataUnavailableException">Không truy cập được database.</exception>
    public async Task<PhotoLookup> FindAsync(string? idOrToken, CancellationToken cancellationToken)
    {
        var byId = IsValidId(idOrToken);
        if (!byId && !IsValidShareToken(idOrToken))
        {
            return PhotoLookup.NotFound;
        }

        CloudPhoto? row;
        try
        {
            await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            row = byId
                ? await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == idOrToken, cancellationToken).ConfigureAwait(false)
                : await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.ShareToken == idOrToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            throw new PhotoMetadataUnavailableException(ex);
        }

        if (row is null || ParseFormat(row.Format) is not { } format)
        {
            return PhotoLookup.NotFound;
        }

        var fullPath = ResolveStoragePath(row.StoragePath);
        if (fullPath is null || !File.Exists(fullPath))
        {
            _logger.LogError("Metadata ảnh {PhotoId} còn nhưng file trên ổ đĩa không còn", row.Id);
            return new PhotoLookup(PhotoAvailability.FileMissing, null);
        }

        // Ảnh chia sẻ luôn được gọi bằng token (đúng URL trong QR); ID nội bộ không lộ ra ngoài.
        var publicId = byId ? row.Id : row.ShareToken!;
        return new PhotoLookup(PhotoAvailability.Available, new StoredPhoto(publicId, format, fullPath, row.SizeBytes, row.UploadedAt));
    }

    /// <summary>
    /// Lưu nội dung ảnh từ stream với ID ngẫu nhiên do server sinh. Kiểm tra dung lượng trong lúc đọc
    /// (không tin Content-Length) và định dạng thực tế từ những byte đầu tiên.
    /// </summary>
    /// <exception cref="PhotoRejectedException">File rỗng, quá lớn hoặc không phải JPEG/PNG.</exception>
    /// <exception cref="PhotoMetadataUnavailableException">Không ghi được metadata (file đã được dọn).</exception>
    public async Task<StoredPhoto> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        var incoming = await ReceiveAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaxIdAttempts; attempt++)
            {
                var id = _newId();
                var relative = PhotoRelativePath(id, incoming.Format);
                if (!IsValidId(id) || await IdExistsAsync(id, cancellationToken).ConfigureAwait(false) || File.Exists(Full(relative)))
                {
                    // Trùng ID (gần như không thể với 130 bit) — sinh ID khác, không bao giờ ghi đè.
                    continue;
                }

                if (!TryPlace(incoming.TempPath, relative))
                {
                    continue;
                }

                var row = NewRow(id, shareToken: null, relative, incoming);
                await InsertOrRemoveFileAsync(row, relative, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Đã lưu ảnh {PhotoId} ({Format}, {Bytes} byte)", id, incoming.Format, incoming.Length);
                return ToStored(id, row, incoming.Format);
            }

            throw new IOException("Không sinh được ID ảnh không trùng lặp.");
        }
        finally
        {
            TryDelete(incoming.TempPath);
        }
    }

    /// <summary>
    /// Lưu ảnh theo Share Token do Desktop sinh. Idempotent: upload lại đúng nội dung cũ (so SHA-256 trong metadata) → trả ảnh
    /// đã có, không tạo dữ liệu mới (nếu file đã mất thì khôi phục từ lần upload này). Nội dung khác → <see cref="ShareConflictException"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Token sai định dạng.</exception>
    /// <exception cref="PhotoRejectedException">File rỗng, quá lớn hoặc không phải JPEG/PNG.</exception>
    /// <exception cref="ShareConflictException">Token đã gắn với ảnh có nội dung khác.</exception>
    /// <exception cref="PhotoMetadataUnavailableException">Không ghi được metadata (file vừa đặt đã được dọn).</exception>
    public async Task<SharedSaveResult> SaveSharedAsync(string shareToken, Stream content, CancellationToken cancellationToken)
    {
        if (!IsValidShareToken(shareToken))
        {
            throw new ArgumentException("Share Token không hợp lệ.", nameof(shareToken));
        }

        var incoming = await ReceiveAsync(content, cancellationToken).ConfigureAwait(false);
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await FindByTokenAsync(shareToken, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return CompareWithExisting(existing, incoming);
            }

            var relative = ShareRelativePath(shareToken, incoming.Format);
            var full = Full(relative);
            var placedHere = false;
            if (File.Exists(full))
            {
                // File không có metadata (ứng dụng dừng giữa chừng ở lần trước): chưa từng được công bố.
                if (!string.Equals(await HashFileAsync(full, cancellationToken).ConfigureAwait(false), incoming.Sha256Hex, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Thay file mồ côi (không có metadata) của một ảnh chia sẻ chưa từng được công bố");
                    File.Delete(full);
                    placedHere = TryPlace(incoming.TempPath, relative);
                }
            }
            else
            {
                placedHere = TryPlace(incoming.TempPath, relative);
            }

            if (!File.Exists(full))
            {
                throw new IOException("Không đặt được file ảnh vào thư mục lưu trữ.");
            }

            var row = NewRow(NewId(), shareToken, relative, incoming);
            try
            {
                await InsertOrRemoveFileAsync(row, placedHere ? relative : null, cancellationToken).ConfigureAwait(false);
            }
            catch (PhotoMetadataUnavailableException ex) when (ex.InnerException is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
            {
                // Instance khác vừa ghi cùng token: xử lý như upload lại.
                var raced = await FindByTokenAsync(shareToken, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                return CompareWithExisting(raced, incoming);
            }

            // Không log token (là "chìa khóa" xem ảnh).
            _logger.LogInformation("Đã lưu ảnh chia sẻ ({Format}, {Bytes} byte)", incoming.Format, incoming.Length);
            return new SharedSaveResult(ToStored(shareToken, row, incoming.Format), Created: true);
        }
        finally
        {
            _publishGate.Release();
            TryDelete(incoming.TempPath);
        }
    }

    private SharedSaveResult CompareWithExisting(CloudPhoto existing, IncomingFile incoming)
    {
        if (!string.Equals(existing.Sha256, incoming.Sha256Hex, StringComparison.Ordinal) || ParseFormat(existing.Format) != incoming.Format)
        {
            _logger.LogWarning("Từ chối upload: Share Token đã gắn với ảnh có nội dung khác");
            throw new ShareConflictException();
        }

        // Metadata còn mà file mất: lần upload lại đúng nội dung này khôi phục file (không đổi metadata).
        var full = ResolveStoragePath(existing.StoragePath);
        if (full is not null && !File.Exists(full) && TryPlace(incoming.TempPath, existing.StoragePath))
        {
            _logger.LogWarning("Khôi phục file của ảnh {PhotoId} từ lần upload lại", existing.Id);
        }

        _logger.LogInformation("Upload lại ảnh chia sẻ đã có (nội dung trùng khớp) — không tạo dữ liệu mới");
        return new SharedSaveResult(ToStored(existing.ShareToken!, existing, incoming.Format), Created: false);
    }

    private async Task<CloudPhoto?> FindByTokenAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.ShareToken == token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            throw new PhotoMetadataUnavailableException(ex);
        }
    }

    private async Task<bool> IdExistsAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Photos.AnyAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            throw new PhotoMetadataUnavailableException(ex);
        }
    }

    /// <summary>Ghi metadata; lỗi thì xóa file vừa đặt (<paramref name="placedRelative"/>) để không còn file mồ côi.</summary>
    private async Task InsertOrRemoveFileAsync(CloudPhoto row, string? placedRelative, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Photos.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseError(ex) || ex is OperationCanceledException)
        {
            if (placedRelative is not null)
            {
                TryDelete(Full(placedRelative));
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogError(ex, "Không ghi được metadata ảnh; đã dọn file vừa lưu");
            throw new PhotoMetadataUnavailableException(ex);
        }
    }

    private CloudPhoto NewRow(string id, string? shareToken, string relative, IncomingFile incoming) => new()
    {
        Id = id,
        ShareToken = shareToken,
        StoragePath = relative,
        Format = incoming.Format == PhotoFormat.Png ? "png" : "jpeg",
        ContentType = incoming.Format.ContentType(),
        SizeBytes = incoming.Length,
        Sha256 = incoming.Sha256Hex,
        UploadedAt = DateTimeOffset.UtcNow,
    };

    private StoredPhoto ToStored(string publicId, CloudPhoto row, PhotoFormat format) =>
        new(publicId, format, Full(row.StoragePath), row.SizeBytes, row.UploadedAt);

    private static PhotoFormat? ParseFormat(string? value) => value switch
    {
        "jpeg" => PhotoFormat.Jpeg,
        "png" => PhotoFormat.Png,
        _ => null,
    };

    /// <summary>Đổi tên file tạm thành file cuối, KHÔNG ghi đè (cùng ổ đĩa nên là thao tác nguyên tử).</summary>
    private bool TryPlace(string tempPath, string relative)
    {
        var destination = Full(relative);
        CreatePrivateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Move(tempPath, destination, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destination))
        {
            return false;
        }
    }

    /// <summary>Nhận nội dung vào file tạm trong RootPath (cùng ổ đĩa với file đích), đồng thời tính SHA-256.</summary>
    private async Task<IncomingFile> ReceiveAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        CreatePrivateDirectory(IncomingPath);
        var tempPath = Path.Combine(IncomingPath, $"{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            PhotoFormat? format = null;
            long length = 0;
            var header = new byte[PhotoFormats.HeaderLength];
            var headerCount = 0;

            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
            {
                RestrictFilePermissions(tempPath);
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)) > 0)
                {
                    length += read;
                    if (length > MaxFileBytes)
                    {
                        throw new PhotoRejectedException(PhotoRejectReason.TooLarge);
                    }

                    if (headerCount < header.Length)
                    {
                        var take = Math.Min(read, header.Length - headerCount);
                        buffer.AsSpan(0, take).CopyTo(header.AsSpan(headerCount));
                        headerCount += take;
                        if (headerCount == header.Length)
                        {
                            // Từ chối sớm, không đọc tiếp phần còn lại của file không hợp lệ.
                            format = PhotoFormats.Detect(header) ?? throw new PhotoRejectedException(PhotoRejectReason.UnsupportedFormat);
                        }
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (length == 0)
                {
                    throw new PhotoRejectedException(PhotoRejectReason.Empty);
                }

                format ??= PhotoFormats.Detect(header.AsSpan(0, headerCount)) ?? throw new PhotoRejectedException(PhotoRejectReason.UnsupportedFormat);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            return new IncomingFile(tempPath, format.Value, length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string PhotoRelativePath(string id, PhotoFormat format) => $"{id[..2]}/{id}{format.Extension()}";

    private static string ShareRelativePath(string token, PhotoFormat format)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
        return $"{SharesFolderName}/{hash[..2]}/{hash}{format.Extension()}";
    }

    /// <summary>Đường dẫn tuyệt đối từ đường dẫn tương đối do server tạo.</summary>
    private string Full(string relative) =>
        ResolveStoragePath(relative) ?? throw new InvalidOperationException("Đường dẫn ảnh nằm ngoài thư mục lưu trữ.");

    /// <summary>Đường dẫn đọc từ database: chỉ chấp nhận nếu nằm trong Storage:RootPath (không tin dữ liệu lưu trữ tuyệt đối).</summary>
    private string? ResolveStoragePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(RootPath + Path.DirectorySeparatorChar, comparison) ? path : null;
    }

    private static bool IsDatabaseError(Exception ex) =>
        ex is DbUpdateException or NpgsqlException or InvalidOperationException or TimeoutException;

    /// <summary>Linux/macOS: chỉ tài khoản chạy dịch vụ được đọc/ghi (700). Windows: phân quyền bằng ACL khi triển khai (xem README).</summary>
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không xóa được file tạm khi tải ảnh lên");
        }
    }

    private sealed record IncomingFile(string TempPath, PhotoFormat Format, long Length, string Sha256Hex);
}
