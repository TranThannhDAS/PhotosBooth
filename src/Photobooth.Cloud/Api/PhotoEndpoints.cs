using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;
using Photobooth.Cloud.Storage;

namespace Photobooth.Cloud.Api;

/// <summary>
/// Minimal API cho ảnh.
/// Upload chỉ được bảo vệ khi cấu hình <c>Upload:ApiKey</c>; không có khóa thì KHÔNG được mở
/// <c>POST /api/photos</c> ra Internet công khai (xem README).
/// </summary>
public static class PhotoEndpoints
{
    public const string FormFieldName = "file";

    /// <summary>Trường multipart tùy chọn: Share Token do Desktop sinh (QR đã phát cho khách).</summary>
    public const string ShareTokenFieldName = "shareToken";

    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>Phần dư cho boundary/header multipart ngoài nội dung file.</summary>
    public const long MultipartOverheadBytes = 64 * 1024;

    // Ảnh không đổi sau khi lưu, nhưng link là "khóa" truy cập nên chỉ cho trình duyệt cache, không cho proxy chung.
    private const string PhotoCacheControl = "private, max-age=86400";

    public static IEndpointRouteBuilder MapPhotoApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/photos");

        group.MapPost("/", UploadAsync).DisableAntiforgery();
        group.MapGet("/{id}/link", GetLink);
        group.MapGet("/{id}/file", GetFile);
        group.MapGet("/{id}/download", Download);

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        PhotoStore store,
        PhotoLinkBuilder links,
        CloudSettings settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(PhotoEndpoints));

        if (!IsUploadAuthorized(request, settings))
        {
            logger.LogWarning("Từ chối upload: thiếu hoặc sai API Key");
            return Problem(StatusCodes.Status401Unauthorized, "Thiếu hoặc sai API Key của thiết bị.");
        }

        if (!request.HasFormContentType
            || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Cần gửi multipart/form-data có trường 'file'.");
        }

        if (request.ContentLength > store.MaxFileBytes + MultipartOverheadBytes)
        {
            return TooLarge(store);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(store);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            logger.LogInformation("Từ chối upload: dữ liệu multipart không hợp lệ ({Error})", ex.GetType().Name);
            return Problem(StatusCodes.Status400BadRequest, "Dữ liệu tải lên không hợp lệ.");
        }

        var file = form.Files.GetFile(FormFieldName);
        if (form.Files.Count != 1 || file is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Cần gửi đúng một file trong trường 'file'.");
        }

        // shareToken (tùy chọn): có trường này thì phải hợp lệ — không âm thầm chuyển sang sinh ID mới.
        string? shareToken = null;
        if (form.TryGetValue(ShareTokenFieldName, out var tokenValues))
        {
            shareToken = tokenValues.Count == 1 ? tokenValues[0] : null;
            if (!PhotoStore.IsValidShareToken(shareToken))
            {
                return Problem(StatusCodes.Status400BadRequest, "Share Token không hợp lệ (cần đúng 32 ký tự Base64URL).");
            }
        }

        if (file.Length > store.MaxFileBytes)
        {
            return TooLarge(store);
        }

        StoredPhoto photo;
        var created = true;
        try
        {
            await using var content = file.OpenReadStream();
            if (shareToken is null)
            {
                photo = await store.SaveAsync(content, cancellationToken);
            }
            else
            {
                var saved = await store.SaveSharedAsync(shareToken, content, cancellationToken);
                (photo, created) = (saved.Photo, saved.Created);
            }
        }
        catch (PhotoRejectedException ex)
        {
            // Không log tên file client gửi (có thể chứa thông tin cá nhân).
            logger.LogInformation("Từ chối upload: {Reason}", ex.Reason);
            return ex.Reason switch
            {
                PhotoRejectReason.TooLarge => TooLarge(store),
                PhotoRejectReason.Empty => Problem(StatusCodes.Status400BadRequest, "File rỗng."),
                _ => Problem(StatusCodes.Status415UnsupportedMediaType, "Chỉ chấp nhận ảnh JPEG hoặc PNG."),
            };
        }
        catch (ShareConflictException)
        {
            return Problem(StatusCodes.Status409Conflict, "Mã chia sẻ này đã gắn với một ảnh khác. Ảnh đã chia sẻ được giữ nguyên.");
        }
        catch (PhotoMetadataUnavailableException)
        {
            // File vừa lưu đã được dọn; client (Desktop) sẽ thử lại sau với cùng token.
            return Problem(StatusCodes.Status503ServiceUnavailable, "Máy chủ tạm thời chưa lưu được ảnh. Vui lòng thử lại sau.");
        }

        var result = links.For(request, photo.Id);

        // Upload lại đúng ảnh đã có (cùng token, cùng nội dung): 200 với đúng các link cũ, không tạo gì mới.
        return created ? Results.Created(result.FileUrl, result) : Results.Ok(result);
    }

    /// <summary>
    /// Bảo vệ upload bằng API Key của thiết bị (header <c>X-Api-Key</c>) khi Cloud có cấu hình <c>Upload:ApiKey</c>.
    /// Không phải hệ thống đăng nhập người dùng; so sánh thời gian hằng để không lộ khóa qua thời gian phản hồi.
    /// </summary>
    private static bool IsUploadAuthorized(HttpRequest request, CloudSettings settings)
    {
        if (settings.UploadApiKey is not { } expected)
        {
            return true;
        }

        var provided = request.Headers[ApiKeyHeaderName].ToString();
        return provided.Length > 0
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static Task<IResult> GetLink(string id, HttpRequest request, PhotoStore store, PhotoLinkBuilder links, CancellationToken cancellationToken) =>
        WithPhotoAsync(store, id, photo => Results.Ok(links.For(request, photo.Id)), cancellationToken);

    private static Task<IResult> GetFile(string id, HttpResponse response, PhotoStore store, CancellationToken cancellationToken) =>
        WithPhotoAsync(store, id, photo => PhotoFile(photo, response, downloadName: null), cancellationToken);

    private static Task<IResult> Download(string id, HttpResponse response, PhotoStore store, CancellationToken cancellationToken) =>
        WithPhotoAsync(store, id, photo => PhotoFile(photo, response, downloadName: $"photobooth-{photo.Id}{photo.Format.Extension()}"), cancellationToken);

    /// <summary>
    /// Tra metadata rồi kiểm tra file: không có → 404; metadata còn nhưng file mất → 410 (không bao giờ báo tải thành công);
    /// database không sẵn sàng → 503.
    /// </summary>
    private static async Task<IResult> WithPhotoAsync(PhotoStore store, string id, Func<StoredPhoto, IResult> found, CancellationToken cancellationToken)
    {
        PhotoLookup lookup;
        try
        {
            lookup = await store.FindAsync(id, cancellationToken);
        }
        catch (PhotoMetadataUnavailableException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Máy chủ tạm thời không truy cập được dữ liệu ảnh. Vui lòng thử lại sau.");
        }

        return lookup switch
        {
            { Availability: PhotoAvailability.Available, Photo: { } photo } => found(photo),
            { Availability: PhotoAvailability.FileMissing } => Problem(StatusCodes.Status410Gone, "File ảnh không còn trên máy chủ."),
            _ => PhotoNotFound(),
        };
    }

    /// <summary>Trả file gốc nguyên vẹn; có tên tải xuống thì kèm Content-Disposition: attachment.</summary>
    private static IResult PhotoFile(StoredPhoto photo, HttpResponse response, string? downloadName)
    {
        response.Headers.CacheControl = PhotoCacheControl;
        return TypedResults.PhysicalFile(
            photo.FullPath,
            photo.Format.ContentType(),
            fileDownloadName: downloadName,
            lastModified: photo.LastModified,
            entityTag: new EntityTagHeaderValue($"\"{photo.Id}\""),
            enableRangeProcessing: true);
    }

    private static IResult PhotoNotFound() => Problem(StatusCodes.Status404NotFound, "Không tìm thấy ảnh.");

    private static IResult TooLarge(PhotoStore store) =>
        Problem(StatusCodes.Status413PayloadTooLarge, $"Ảnh vượt quá giới hạn {store.MaxFileBytes / (1024 * 1024)} MB.");

    private static IResult Problem(int statusCode, string title) => Results.Problem(title: title, statusCode: statusCode);
}
