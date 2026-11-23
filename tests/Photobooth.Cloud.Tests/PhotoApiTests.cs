using System.Net;
using System.Net.Http.Json;
using Photobooth.Cloud.Api;
using Photobooth.Cloud.Storage;

namespace Photobooth.Cloud.Tests;

public sealed class PhotoApiTests : IDisposable
{
    private readonly TempFolder _root = new();

    public void Dispose() => _root.Dispose();

    [PostgresTheory]
    [InlineData("jpeg")]
    [InlineData("png")]
    public async Task Upload_ValidImage_Returns201WithLinks_AndStoresOriginalBytes(string kind)
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var (bytes, contentType, extension) = kind == "png"
            ? (SampleImages.Png, "image/png", ".png")
            : (SampleImages.Jpeg, "image/jpeg", ".jpg");

        using var response = await host.UploadAsync(bytes, "anh" + extension, contentType);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var links = await response.Content.ReadFromJsonAsync<PhotoLinks>();
        Assert.NotNull(links);
        Assert.True(PhotoStore.IsValidId(links.Id));
        Assert.Equal(new Uri(host.Client.BaseAddress!, $"/photos/{links.Id}"), new Uri(links.ViewUrl));
        Assert.Equal(new Uri(host.Client.BaseAddress!, $"/api/photos/{links.Id}/download"), new Uri(links.DownloadUrl));
        Assert.Equal(new Uri(links.FileUrl), response.Headers.Location);

        // Lưu đúng nội dung gốc, tên file do server đặt theo ID + định dạng thực tế.
        var stored = Path.Combine(_root.Path, links.Id[..2], links.Id + extension);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored));
    }

    [PostgresFact]
    public async Task Link_ReturnsIdViewAndDownloadUrls()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var id = await UploadAsync(host, SampleImages.Jpeg);

        var links = await host.Client.GetFromJsonAsync<PhotoLinks>($"/api/photos/{id}/link");

        Assert.NotNull(links);
        Assert.Equal(id, links.Id);
        Assert.EndsWith($"/photos/{id}", links.ViewUrl);
        Assert.EndsWith($"/api/photos/{id}/download", links.DownloadUrl);
        Assert.EndsWith($"/api/photos/{id}/file", links.FileUrl);
    }

    [PostgresFact]
    public async Task Link_UsesConfiguredPublicBaseUrl()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path, "--PublicBaseUrl=https://photo.example.com/booth/");
        var id = await UploadAsync(host, SampleImages.Png);

        var links = await host.Client.GetFromJsonAsync<PhotoLinks>($"/api/photos/{id}/link");

        Assert.Equal($"https://photo.example.com/booth/photos/{id}", links!.ViewUrl);
        Assert.Equal($"https://photo.example.com/booth/api/photos/{id}/download", links.DownloadUrl);
    }

    [PostgresTheory]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("png", "image/png")]
    public async Task File_ReturnsOriginalBytesWithCorrectContentType(string kind, string expectedType)
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var bytes = kind == "png" ? SampleImages.Png : SampleImages.Jpeg;
        // Client khai báo sai Content-Type: server vẫn trả theo định dạng thực tế.
        var id = await UploadAsync(host, bytes, contentType: "application/octet-stream");

        using var response = await host.Client.GetAsync($"/api/photos/{id}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedType, response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task Download_ReturnsAttachmentWithOriginalBytes()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var id = await UploadAsync(host, SampleImages.Jpeg, fileName: "Tên khách.jpg");

        using var response = await host.Client.GetAsync($"/api/photos/{id}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal($"photobooth-{id}.jpg", disposition.FileName?.Trim('"'));
        Assert.Equal(SampleImages.Jpeg, await response.Content.ReadAsByteArrayAsync());
    }

    [PostgresTheory]
    [InlineData("link")]
    [InlineData("file")]
    [InlineData("download")]
    public async Task UnknownOrMalformedId_Returns404(string action)
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var unknown = PhotoStore.NewId();
        var existing = await UploadAsync(host, SampleImages.Jpeg);

        foreach (var id in new[] { unknown, "abc", existing.ToUpperInvariant(), "..%2F..%2Fappsettings.json", "%2E%2E", existing + "x" })
        {
            using var response = await host.Client.GetAsync($"/api/photos/{id}/{action}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Upload_NonImageWithImageNameAndType_Returns415_AndStoresNothing()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var text = "Đây không phải là ảnh, chỉ là văn bản đổi đuôi thành .jpg"u8.ToArray();

        using var response = await host.UploadAsync(text, "fake.jpg", "image/jpeg");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        AssertNoPhotosStored();
    }

    [PostgresFact]
    public async Task Upload_Gif_Returns415()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

        using var response = await host.UploadAsync(gif, "anh.png", "image/png");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        AssertNoPhotosStored();
    }

    [PostgresFact]
    public async Task Upload_EmptyFile_Returns400()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);

        using var response = await host.UploadAsync([], "empty.jpg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoPhotosStored();
    }

    [PostgresFact]
    public async Task Upload_OverLimit_Returns413_AndStoresNothing()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path, "--Storage:MaxFileSizeMB=1");

        using var atLimit = await host.UploadAsync(SampleImages.BuildJpeg(1024 * 1024));
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);

        using var overLimit = await host.UploadAsync(SampleImages.BuildJpeg(1024 * 1024 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.StatusCode);

        // Content-Length vượt giới hạn: bị từ chối ngay, không đọc phần thân.
        using var wayOver = await host.UploadAsync(SampleImages.BuildJpeg(3 * 1024 * 1024), expectContinue: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, wayOver.StatusCode);

        Assert.Single(StoredPhotoFiles());
    }

    [PostgresFact]
    public async Task Upload_WrongFieldOrNotMultipart_IsRejected()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);

        using var wrongField = await host.UploadAsync(SampleImages.Jpeg, fieldName: "image");
        Assert.Equal(HttpStatusCode.BadRequest, wrongField.StatusCode);

        using var raw = await host.Client.PostAsync("/api/photos", new ByteArrayContent(SampleImages.Jpeg));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, raw.StatusCode);

        using var twoFiles = new MultipartFormDataContent
        {
            { new ByteArrayContent(SampleImages.Jpeg), "file", "a.jpg" },
            { new ByteArrayContent(SampleImages.Jpeg), "file", "b.jpg" },
        };
        using var twoResponse = await host.Client.PostAsync("/api/photos", twoFiles);
        Assert.Equal(HttpStatusCode.BadRequest, twoResponse.StatusCode);

        AssertNoPhotosStored();
    }

    [PostgresFact]
    public async Task Upload_ClientFileNameWithTraversal_IsIgnored()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);

        var id = await UploadAsync(host, SampleImages.Jpeg, fileName: "../../../evil.jpg");

        var files = StoredPhotoFiles();
        Assert.Single(files);
        Assert.Equal(id + ".jpg", Path.GetFileName(files[0]));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root.Path)!, "evil.jpg")));
    }

    [PostgresFact]
    public async Task Photos_SurviveServerRestart()
    {
        string id;
        await using (var first = await CloudTestHost.StartAsync(_root.Path))
        {
            id = await UploadAsync(first, SampleImages.Png);
        }

        await using var second = await CloudTestHost.StartAsync(_root.Path);
        using var response = await second.Client.GetAsync($"/api/photos/{id}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SampleImages.Png, await response.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task ViewPage_ShowsImageAndDownloadButton()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var id = await UploadAsync(host, SampleImages.Jpeg);

        using var response = await host.Client.GetAsync($"/photos/{id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"src=\"api/photos/{id}/file\"", html);
        Assert.Contains($"href=\"api/photos/{id}/download\"", html);
        Assert.Contains("Tải ảnh xuống", html);
        Assert.Contains("name=\"viewport\"", html);
        Assert.DoesNotContain("blazor.web.js", html);
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ViewPage_MissingPhoto_Returns404WithMessage(bool wellFormedId)
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var id = wellFormedId ? PhotoStore.NewId() : "khong-ton-tai";

        using var response = await host.Client.GetAsync($"/photos/{id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Không tìm thấy ảnh", html);
        Assert.DoesNotContain("Tải ảnh xuống", html);
    }

    private static async Task<string> UploadAsync(CloudTestHost host, byte[] bytes, string fileName = "photo.jpg", string contentType = "image/jpeg")
    {
        using var response = await host.UploadAsync(bytes, fileName, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var links = await response.Content.ReadFromJsonAsync<PhotoLinks>();
        return links!.Id;
    }

    private string[] StoredPhotoFiles() =>
        Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(_root.Path, path).StartsWith(".incoming", StringComparison.Ordinal))
            .ToArray();

    private void AssertNoPhotosStored()
    {
        Assert.Empty(StoredPhotoFiles());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.Path, ".incoming")));
    }
}
