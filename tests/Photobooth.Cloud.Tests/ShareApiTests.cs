using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Photobooth.Cloud.Api;
using Photobooth.Cloud.Storage;

namespace Photobooth.Cloud.Tests;

/// <summary>Offline-First QR Sharing phía Cloud: upload theo Share Token do Desktop sinh và trang /s/{token}.</summary>
public sealed class ShareApiTests : IDisposable
{
    private const string ApiKey = "kiem-thu-api-key-0123456789-abcdefghij";
    private readonly TempFolder _root = new();

    public void Dispose() => _root.Dispose();

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    [PostgresFact]
    public async Task UploadWithShareToken_UsesThatToken_AndServesPhoto()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var token = NewToken();

        using var response = await host.UploadAsync(SampleImages.Png, "final-v1.png", "image/png", shareToken: token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var links = await response.Content.ReadFromJsonAsync<PhotoLinks>();
        Assert.Equal(token, links!.Id);
        Assert.Equal(new Uri(host.Client.BaseAddress!, $"/s/{token}"), new Uri(links.ViewUrl));

        using var file = await host.Client.GetAsync($"/api/photos/{token}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SampleImages.Png, await file.Content.ReadAsByteArrayAsync());

        using var link = await host.Client.GetAsync($"/api/photos/{token}/link");
        Assert.Equal(links, await link.Content.ReadFromJsonAsync<PhotoLinks>());

        // Token không nằm trong tên file trên đĩa.
        Assert.DoesNotContain(SharedFiles(), f => f.Contains(token, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task ReuploadSameContentAndToken_Returns200_SameLinks_NoNewFile()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var token = NewToken();

        using var first = await host.UploadAsync(SampleImages.Jpeg, shareToken: token);
        var firstLinks = await first.Content.ReadFromJsonAsync<PhotoLinks>();
        var stored = Assert.Single(SharedFiles());
        var writtenAt = File.GetLastWriteTimeUtc(stored);

        using var second = await host.UploadAsync(SampleImages.Jpeg, "ten-khac.jpg", shareToken: token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstLinks, await second.Content.ReadFromJsonAsync<PhotoLinks>());
        Assert.Equal(stored, Assert.Single(SharedFiles()));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(stored));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.Path, ".incoming")));
    }

    [PostgresFact]
    public async Task SameTokenDifferentContent_Returns409_AndKeepsOriginal()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var token = NewToken();
        using var first = await host.UploadAsync(SampleImages.Jpeg, shareToken: token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var differentJpeg = await host.UploadAsync(SampleImages.BuildJpeg(5000), shareToken: token);
        using var differentFormat = await host.UploadAsync(SampleImages.Png, "a.png", "image/png", shareToken: token);

        Assert.Equal(HttpStatusCode.Conflict, differentJpeg.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, differentFormat.StatusCode);
        Assert.Single(SharedFiles());
        using var file = await host.Client.GetAsync($"/api/photos/{token}/download");
        Assert.Equal(SampleImages.Jpeg, await file.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task ConcurrentDuplicateUploads_CreateExactlyOneFile()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var token = NewToken();
        var bytes = SampleImages.BuildJpeg(512 * 1024);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.UploadAsync(bytes, shareToken: token)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Single(SharedFiles());
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [PostgresTheory]
    [InlineData("")]
    [InlineData("ngan-qua")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 31 ký tự
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+/")] // Base64 thường, không phải Base64URL
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("../../../../../../../../etc/pass")]
    public async Task InvalidShareToken_Returns400_AndStoresNothing(string token)
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);

        using var response = await host.UploadAsync(SampleImages.Jpeg, shareToken: token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(SharedFiles());
        Assert.Empty(Directory.GetFiles(_root.Path, "*.jpg", SearchOption.AllDirectories));
    }

    [PostgresFact]
    public async Task SharePage_BeforeUpload_ShowsWaitingPage_NotCached_ThenShowsPhoto()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var token = NewToken();

        using (var waiting = await host.Client.GetAsync($"/s/{token}"))
        {
            var html = await waiting.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
            Assert.Contains("Ảnh của bạn đang được chuẩn bị.", html);
            Assert.Contains("Vui lòng đợi trong giây lát hoặc quay lại sau.", html);
            Assert.Contains("http-equiv=\"refresh\"", html);
            Assert.True(waiting.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain("Tải ảnh xuống", html);
        }

        using var upload = await host.UploadAsync(SampleImages.Jpeg, shareToken: token);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        using var ready = await host.Client.GetAsync($"/s/{token}");
        var readyHtml = await ready.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains($"src=\"api/photos/{token}/file\"", readyHtml);
        Assert.Contains($"href=\"api/photos/{token}/download\"", readyHtml);
        Assert.Contains("Tải ảnh xuống", readyHtml);
        Assert.DoesNotContain("http-equiv=\"refresh\"", readyHtml);
    }

    [PostgresTheory]
    [InlineData("khong-hop-le")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task SharePage_MalformedToken_Returns404(string token)
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);

        using var response = await host.Client.GetAsync($"/s/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("đang được chuẩn bị", await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task TokensDifferingOnlyByCase_NeverShareAPhoto()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);
        var token = "Ab" + NewToken()[2..];
        var other = "aB" + token[2..];

        using var upload = await host.UploadAsync(SampleImages.Jpeg, shareToken: token);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        using var otherFile = await host.Client.GetAsync($"/api/photos/{other}/file");
        Assert.Equal(HttpStatusCode.NotFound, otherFile.StatusCode);
        var otherPage = await host.Client.GetStringAsync($"/s/{other}");
        Assert.Contains("đang được chuẩn bị", otherPage);
    }

    [PostgresFact]
    public async Task ApiKeyConfigured_UploadRequiresKey_ViewingDoesNot()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path, $"--Upload:ApiKey={ApiKey}");
        var token = NewToken();

        using var missing = await host.UploadAsync(SampleImages.Jpeg, shareToken: token);
        using var wrong = await host.UploadAsync(SampleImages.Jpeg, shareToken: token, apiKey: ApiKey + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(SharedFiles());

        using var ok = await host.UploadAsync(SampleImages.Jpeg, shareToken: token, apiKey: ApiKey);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        using var view = await host.Client.GetAsync($"/s/{token}");
        Assert.Contains("Tải ảnh xuống", await view.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public void ShortApiKey_RefusesToStart()
    {
        Assert.Throws<InvalidOperationException>(
            () => CloudApp.Build([$"--Storage:RootPath={_root.Path}", "--Upload:ApiKey=ngan"]));
    }

    [PostgresFact]
    public async Task UploadWithoutToken_StillWorksAsBefore()
    {
        await using var host = await CloudTestHost.StartAsync(_root.Path);

        using var response = await host.UploadAsync(SampleImages.Jpeg);
        var links = await response.Content.ReadFromJsonAsync<PhotoLinks>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(PhotoStore.IsValidId(links!.Id));
        Assert.EndsWith($"/photos/{links.Id}", links.ViewUrl);
        Assert.Empty(SharedFiles());
    }

    private string[] SharedFiles()
    {
        var shares = Path.Combine(_root.Path, "shares");
        return Directory.Exists(shares) ? Directory.GetFiles(shares, "*", SearchOption.AllDirectories) : [];
    }
}
