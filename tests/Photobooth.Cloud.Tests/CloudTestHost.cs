using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Photobooth.Cloud.Tests;

/// <summary>Chạy ứng dụng Cloud THẬT (Kestrel, cổng ngẫu nhiên trên 127.0.0.1) với thư mục lưu trữ tạm.</summary>
public sealed class CloudTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CloudTestHost(WebApplication app, string rootPath, Uri baseAddress)
    {
        _app = app;
        RootPath = rootPath;
        Client = new HttpClient { BaseAddress = baseAddress };
    }

    public string RootPath { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<CloudTestHost> StartAsync(string rootPath, params string[] extraArgs)
    {
        string[] args =
        [
            "--urls=http://127.0.0.1:0",
            $"--Storage:RootPath={rootPath}",
            $"--ConnectionStrings:DefaultConnection={TestPostgres.For(rootPath)}",
            "--Database:MigrateOnStartup=true",
            "--Logging:LogLevel:Default=Warning",
            .. extraArgs,
        ];
        var app = CloudApp.Build(args);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new CloudTestHost(app, rootPath, new Uri(address));
    }

    public async Task<HttpResponseMessage> UploadAsync(
        byte[] content,
        string fileName = "photo.jpg",
        string contentType = "image/jpeg",
        string fieldName = "file",
        bool expectContinue = false,
        string? shareToken = null,
        string? apiKey = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, fieldName, fileName);
        if (shareToken is not null)
        {
            form.Add(new StringContent(shareToken), "shareToken");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/photos") { Content = form };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        // Với Expect: 100-continue, server từ chối file quá lớn bằng 413 trước khi client gửi phần thân.
        request.Headers.ExpectContinue = expectContinue;
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Thư mục tạm riêng cho mỗi test, xóa khi xong.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photobooth-cloud-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // Database kiểm thử tạo cho thư mục này (nếu có) được xóa cùng thư mục.
        TestPostgres.Drop(Path);
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class SampleImages
{
    /// <summary>PNG 1×1 hợp lệ.</summary>
    public static byte[] Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>JPEG nhỏ (SOI + APP0 JFIF + dữ liệu đệm + EOI) — đủ để nhận dạng định dạng.</summary>
    public static byte[] Jpeg { get; } = BuildJpeg(4096);

    public static byte[] BuildJpeg(int size)
    {
        var bytes = new byte[size];
        byte[] header = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];
        header.CopyTo(bytes, 0);
        for (var i = header.Length; i < size - 2; i++)
        {
            bytes[i] = (byte)(i * 31);
        }

        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }
}
