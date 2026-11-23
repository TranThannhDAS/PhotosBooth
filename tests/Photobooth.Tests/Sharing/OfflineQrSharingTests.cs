using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Cloud;
using Photobooth.Cloud.Tests;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Persistence;
using Photobooth.Infrastructure.Sharing;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Sharing;

/// <summary>
/// Offline-First QR Sharing end-to-end, toàn hệ thống: Desktop (SQLite) → Upload API → Photobooth.Cloud THẬT
/// (Kestrel trên 127.0.0.1, PostgreSQL kiểm thử riêng) → File Storage → trang /s/{token} → Download.
/// "Mất Internet" được mô phỏng bằng handler HTTP ném lỗi kết nối như khi không có mạng.
/// </summary>
public sealed class OfflineQrSharingTests : IAsyncLifetime
{
    private static readonly TimeSpan[] FastRetry = [TimeSpan.FromMilliseconds(50)];
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private readonly TempDirectory _temp = new();
    private readonly FakeCameraDevice _camera = new();
    private readonly NetworkSwitch _network = new();
    private readonly List<IDisposable> _disposables = [];
    private PhotoboothStoragePaths _paths = default!;
    private string _eventFolder = default!;
    private string _cloudRoot = default!;
    private WebApplication _cloud = default!;
    private Uri _cloudUrl = default!;
    private HttpClient _phone = default!;

    public async Task InitializeAsync()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _eventFolder = Path.Combine(_temp.Path, "Sự kiện QR");
        _cloudRoot = Path.Combine(_temp.Path, "CloudData");
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);

        _cloud = CloudApp.Build(
        [
            "--urls=http://127.0.0.1:0",
            $"--Storage:RootPath={_cloudRoot}",
            $"--ConnectionStrings:DefaultConnection={TestPostgres.For(_cloudRoot)}",
            "--Database:MigrateOnStartup=true",
            "--Logging:LogLevel:Default=Warning",
        ]);
        await _cloud.StartAsync();
        _cloudUrl = new Uri(_cloud.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");

        // "Điện thoại của khách": luôn có Internet, mở đúng URL trong QR.
        _phone = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _phone.Dispose();
        await _cloud.StopAsync();
        await _cloud.DisposeAsync();
        _network.Dispose();
        TestPostgres.Drop(_cloudRoot);
        _temp.Dispose();
    }

    // ------------------------------------------------------------ Dựng "máy Desktop"

    private sealed record Desktop(PhotoSessionService Sessions, CloudUploadService Uploads, EventWorkspaceService Workspaces);

    /// <summary>Khởi động (hoặc khởi động lại) ứng dụng Desktop trên cùng thư mục dữ liệu và sự kiện.</summary>
    private Desktop StartDesktop(CloudShareOptions? options = null, IPrinterProvider? printers = null, bool startUploads = true)
    {
        options ??= new CloudShareOptions { Enabled = true, BaseUrl = _cloudUrl.AbsoluteUri, UploadTimeoutSeconds = 10 };
        var paths = new PhotoboothStoragePaths(_paths.RootDirectory);
        var workspaces = TestSessions.OpenWorkspace(paths, _eventFolder);
        var uploads = new CloudUploadService(options, workspaces, TestDatabase.For(paths), TimeProvider.System, NullLogger<CloudUploadService>.Instance, _network, FastRetry);
        _disposables.Add(uploads);
        var sessions = TestSessions.Create(paths, workspaces, printers: printers, shareLinks: new ShareLinkFactory(options), uploads: uploads);
        if (startUploads)
        {
            uploads.Start();
        }

        return new Desktop(sessions, uploads, workspaces);
    }

    private async Task<PhotoSession> CompleteSessionAsync(PhotoSessionService sessions, Func<PhotoSessionService, Task>? beforeComplete = null)
    {
        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(TestTemplates.TwoSlots()));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync());
        if (beforeComplete is not null)
        {
            await beforeComplete(sessions);
        }

        Assert.True(await sessions.CompleteSessionAsync());
        return sessions.Current!;
    }

    private static async Task<ShareUploadState> WaitForStatusAsync(CloudUploadService uploads, string token, CloudUploadStatus expected, Func<ShareUploadState, bool>? extra = null)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (true)
        {
            if (uploads.GetState(token) is { } state && state.Status == expected && (extra is null || extra(state)))
            {
                return state;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Hết thời gian chờ trạng thái {expected}; hiện tại: {uploads.GetState(token)}");
            await Task.Delay(25);
        }
    }

    /// <summary>Trạng thái upload trong SQLite (bảng CloudUploads) được ghi ngay sau khi đổi trạng thái; chờ tối đa vài giây.</summary>
    private async Task<CloudUploadRecord> WaitForStoredStatusAsync(PhotoSession session, string expected)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (true)
        {
            await using (var db = TestDatabase.Open(_paths))
            {
                var record = await db.CloudUploads.AsNoTracking()
                    .Include(u => u.OutputVersion)
                    .FirstOrDefaultAsync(u => u.OutputVersion!.SessionId == session.SessionId && u.OutputVersion.Version == session.CompletedOutput!.Version);
                if (record?.Status == expected)
                {
                    return record;
                }

                Assert.True(DateTime.UtcNow < deadline, $"CloudUploads chưa có trạng thái {expected}: {record?.Status}");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Phiên bản đã lưu trong SQLite (bảng OutputVersions).</summary>
    private List<OutputVersionRecord> StoredVersions(PhotoSession session)
    {
        using var db = TestDatabase.Open(_paths);
        return db.OutputVersions.AsNoTracking().Where(v => v.SessionId == session.SessionId).OrderBy(v => v.Version).ToList();
    }

    private async Task<string> OpenSharePageAsync(ShareLink link)
    {
        using var response = await _phone.GetAsync(link.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<byte[]> DownloadAsync(ShareLink link)
    {
        using var response = await _phone.GetAsync(new Uri(_cloudUrl, $"api/photos/{link.Token}/download"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private string[] CloudSharedFiles()
    {
        var shares = Path.Combine(_cloudRoot, "shares");
        return Directory.Exists(shares) ? Directory.GetFiles(shares, "*", SearchOption.AllDirectories) : [];
    }

    // ------------------------------------------------------------ Case 1 — Internet hoạt động

    [PostgresFact]
    public async Task Case1_Online_QrImmediately_UploadSucceeds_PhoneSeesAndDownloadsPhoto()
    {
        var desktop = StartDesktop();

        var session = await CompleteSessionAsync(desktop.Sessions);

        // QR có ngay khi hoàn tất, token đã nằm trong SQLite (trước khi màn hoàn tất hiển thị).
        var output = session.CompletedOutput!;
        var link = output.Share!;
        Assert.Equal(new Uri(_cloudUrl, $"s/{link.Token}").AbsoluteUri, link.Url);
        Assert.True(ShareLinkFactory.IsValidToken(link.Token));
        var stored = Assert.Single(StoredVersions(session));
        Assert.Equal(link.Token, stored.ShareToken);
        Assert.Equal(link.Url, stored.ShareUrl);

        await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Uploaded);

        var page = await OpenSharePageAsync(link);
        Assert.Contains("Tải ảnh xuống", page);
        Assert.Equal(await File.ReadAllBytesAsync(output.FilePath), await DownloadAsync(link));

        // Cloud: metadata trong PostgreSQL, file trên ổ đĩa server.
        await using (var cloudDb = TestPostgres.Factory(_cloudRoot).CreateDbContext())
        {
            var row = await cloudDb.Photos.SingleAsync();
            Assert.Equal(link.Token, row.ShareToken);
            Assert.Equal(new FileInfo(output.FilePath).Length, row.SizeBytes);
            Assert.True(File.Exists(Path.Combine(_cloudRoot, row.StoragePath)));
        }

        // QR không đổi sau khi upload; trạng thái upload được ghi vào SQLite, liên kết đúng phiên bản đã chia sẻ.
        Assert.Equal(link, desktop.Sessions.Current!.CompletedOutput!.Share);
        var status = await WaitForStoredStatusAsync(session, "uploaded");
        Assert.NotNull(status.UploadedAt);
        Assert.Equal(link.Token, status.OutputVersion!.ShareToken);
        Assert.EndsWith("/Outputs/final-v1.png", status.OutputVersion.FilePath, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ Case 2 + 3 — mất Internet rồi có lại

    [PostgresFact]
    public async Task Case2And3_OfflineThenRestored_SameQrShowsWaitingThenPhoto()
    {
        _network.Online = false;
        var desktop = StartDesktop();

        var session = await CompleteSessionAsync(desktop.Sessions);
        var link = session.CompletedOutput!.Share!;

        // Case 2: không có Internet vẫn có QR; upload chờ thử lại; khách quét mã thấy trang chờ.
        Assert.True(File.Exists(session.CompletedOutput.FilePath));
        var pending = await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Pending, s => s.Attempts >= 1 && s.IsOffline);
        Assert.NotNull(pending.LastError);
        var waiting = await OpenSharePageAsync(link);
        Assert.Contains("Ảnh của bạn đang được chuẩn bị.", waiting);
        Assert.DoesNotContain("Tải ảnh xuống", waiting);

        // Case 3: có Internet trở lại → tự upload với token cũ; đúng QR cũ mở được ảnh.
        _network.Online = true;
        await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Uploaded);
        Assert.Contains("Tải ảnh xuống", await OpenSharePageAsync(link));
        Assert.Equal(await File.ReadAllBytesAsync(session.CompletedOutput.FilePath), await DownloadAsync(link));
        Assert.Equal(link, desktop.Sessions.Current!.CompletedOutput!.Share);
        Assert.Single(CloudSharedFiles());
    }

    // ------------------------------------------------------------ Case 4 — khởi động lại Desktop

    [PostgresFact]
    public async Task Case4_RestartWhileOffline_RecoversPendingUpload_WithSameToken()
    {
        _network.Online = false;
        var first = StartDesktop();
        var session = await CompleteSessionAsync(first.Sessions);
        var link = session.CompletedOutput!.Share!;
        await WaitForStatusAsync(first.Uploads, link.Token, CloudUploadStatus.Pending, s => s.Attempts >= 1);
        await first.Sessions.FlushAsync();
        first.Uploads.Dispose(); // "Đóng Desktop".

        // "Mở lại Desktop" khi đã có mạng: quét sự kiện, upload tiếp đúng token đã lưu, không tạo token mới.
        _network.Online = true;
        var second = StartDesktop();
        await WaitForStatusAsync(second.Uploads, link.Token, CloudUploadStatus.Uploaded);
        Assert.Contains("Tải ảnh xuống", await OpenSharePageAsync(link));

        var archived = await TestSessions.CreateArchive(new PhotoboothStoragePaths(_paths.RootDirectory), second.Workspaces)
            .LoadSessionAsync(Path.GetFileName(session.Directory!));
        Assert.Equal(link, Assert.Single(archived.Session.Versions).Share);
        Assert.Single(CloudSharedFiles());
    }

    // ------------------------------------------------------------ Case 5 — upload trùng

    [PostgresFact]
    public async Task Case5_DuplicateUpload_CloudKeepsOnePhoto_UrlUnchanged()
    {
        var desktop = StartDesktop();
        var session = await CompleteSessionAsync(desktop.Sessions);
        var link = session.CompletedOutput!.Share!;
        await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Uploaded);

        // Ép upload lại cùng ảnh + token (ví dụ trạng thái trên máy bị mất): Cloud trả 200, không tạo file mới.
        await using (var db = TestDatabase.Open(_paths))
        {
            db.CloudUploads.RemoveRange(db.CloudUploads);
            await db.SaveChangesAsync();
        }

        var restarted = StartDesktop();
        restarted.Uploads.RetryNow();
        await WaitForStatusAsync(restarted.Uploads, link.Token, CloudUploadStatus.Uploaded, s => s.Attempts >= 1);

        // Gọi lại khi giao diện render lại / mở lại phiên: vẫn đúng link cũ.
        Assert.Equal(link, await desktop.Sessions.EnsureShareLinkAsync(session.CompletedOutput.Version));
        Assert.Single(CloudSharedFiles());
        Assert.Contains("Tải ảnh xuống", await OpenSharePageAsync(link));
    }

    // ------------------------------------------------------------ Case 6 — token trùng, ảnh khác

    [PostgresFact]
    public async Task Case6_SameTokenDifferentPhoto_Returns409_FailedWithoutRetry_CloudPhotoKept()
    {
        _network.Online = false;
        var desktop = StartDesktop();
        var session = await CompleteSessionAsync(desktop.Sessions);
        var link = session.CompletedOutput!.Share!;

        // Ai đó đã upload ảnh KHÁC với đúng token này lên Cloud.
        var other = TestImages.CreateQuadrants();
        using (var form = new MultipartFormDataContent { { new ByteArrayContent(other), "file", "x.jpg" }, { new StringContent(link.Token), "shareToken" } })
        using (var response = await _phone.PostAsync(new Uri(_cloudUrl, "api/photos"), form))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        _network.Online = true;
        desktop.Uploads.RetryNow();
        var failed = await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Failed);
        await Task.Delay(300);

        Assert.Equal(failed.Attempts, desktop.Uploads.GetState(link.Token)!.Attempts);
        Assert.Equal(other, await DownloadAsync(link));
        Assert.Single(CloudSharedFiles());
    }

    // ------------------------------------------------------------ Case 7 — hai phiên khác nhau

    [PostgresFact]
    public async Task Case7_TwoSessions_DifferentTokensAndQr_NoMixedPhotos()
    {
        var desktop = StartDesktop();
        var first = await CompleteSessionAsync(desktop.Sessions);
        var second = await CompleteSessionAsync(desktop.Sessions);
        var a = first.CompletedOutput!;
        var b = second.CompletedOutput!;

        Assert.NotEqual(a.Share!.Token, b.Share!.Token);
        Assert.NotEqual(a.Share.Url, b.Share.Url);

        // Màn hoàn tất chỉ thấy QR của phiên hiện tại.
        Assert.Equal(b.Share, desktop.Sessions.Current!.CompletedOutput!.Share);

        await WaitForStatusAsync(desktop.Uploads, a.Share.Token, CloudUploadStatus.Uploaded);
        await WaitForStatusAsync(desktop.Uploads, b.Share.Token, CloudUploadStatus.Uploaded);
        Assert.Equal(await File.ReadAllBytesAsync(a.FilePath), await DownloadAsync(a.Share));
        Assert.Equal(await File.ReadAllBytesAsync(b.FilePath), await DownloadAsync(b.Share));
        Assert.Equal(2, CloudSharedFiles().Length);
    }

    // ------------------------------------------------------------ Case 8 — Cloud không hoạt động

    [PostgresFact]
    public async Task Case8_CloudDown_CaptureSavePrintAndQrStillWork_ErrorRecorded()
    {
        var deadCloud = new CloudShareOptions { Enabled = true, BaseUrl = $"http://127.0.0.1:{FreePort()}/", UploadTimeoutSeconds = 5 };
        var printer = new RecordingPrinter(isPhysical: false, PrintJobStatus.Simulated);
        var desktop = StartDesktop(deadCloud, new TestPrinterProvider { Current = printer });

        var session = await CompleteSessionAsync(desktop.Sessions, async s =>
            Assert.Equal(PrintOutcome.Simulated, (await s.RequestPrintAsync(1)).Outcome));

        var link = session.CompletedOutput!.Share!;
        Assert.StartsWith(deadCloud.BaseUrl, link.Url);
        Assert.Single(printer.Requests);
        var state = await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Pending, s => s.Attempts >= 2);
        Assert.True(state.IsOffline);
        await WaitForStoredStatusAsync(session, "pending");
    }

    // ------------------------------------------------------------ Quy tắc token / cấu hình

    [PostgresTheory]
    [InlineData(false, "https://photo.example.com")]
    [InlineData(true, "")]
    [InlineData(true, "photo.example.com")]
    [InlineData(true, "ftp://photo.example.com")]
    [InlineData(true, "https://photo.example.com/?a=1")]
    public async Task CloudDisabledOrInvalid_NoQr_SessionStillCompletes(bool enabled, string baseUrl)
    {
        var options = new CloudShareOptions { Enabled = enabled, BaseUrl = baseUrl };
        var desktop = StartDesktop(options);

        var session = await CompleteSessionAsync(desktop.Sessions);

        Assert.False(desktop.Sessions.ShareConfiguration.IsReady);
        Assert.NotNull(desktop.Sessions.ShareConfiguration.Problem);
        Assert.Equal(PhotoSessionState.Completed, session.State);
        Assert.Null(session.CompletedOutput!.Share);
        Assert.All(StoredVersions(session), v => Assert.Null(v.ShareToken));
    }

    [PostgresFact]
    public async Task PrintAgainAndReopen_KeepToken_NewEditedVersionGetsNewToken()
    {
        var printer = new RecordingPrinter(isPhysical: false, PrintJobStatus.Simulated);
        _network.Online = false;
        var desktop = StartDesktop(printers: new TestPrinterProvider { Current = printer });
        var session = await CompleteSessionAsync(desktop.Sessions);
        var v1 = session.CompletedOutput!;
        await desktop.Sessions.FlushAsync();

        // Mở lại phiên trong Admin: token giữ nguyên; in lại phiên bản đó không tạo token mới.
        var archive = TestSessions.CreateArchive(new PhotoboothStoragePaths(_paths.RootDirectory), desktop.Workspaces);
        var archived = await archive.LoadSessionAsync(Path.GetFileName(session.Directory!));
        Assert.True(desktop.Sessions.OpenArchivedSession(archived));
        Assert.Equal(v1.Share, desktop.Sessions.Current!.FindVersion(v1.Version)!.Share);
        Assert.Equal(PrintOutcome.Simulated, (await desktop.Sessions.RequestPrintAsync(1, v1.Version, confirmRetry: true)).Outcome);
        Assert.Equal(v1.Share, desktop.Sessions.Current!.FindVersion(v1.Version)!.Share);

        // Chỉnh sửa lại → phiên bản mới; tạo QR cho phiên bản mới là token MỚI, QR cũ không bị ghi đè.
        Assert.True(desktop.Sessions.BeginEditFromVersion(v1.Version));
        Assert.Equal(CompositionOutcome.Succeeded, await desktop.Sessions.ComposeAsync(ColorFilter.Grayscale));
        var v2 = await desktop.Sessions.SaveEditsAsync();
        Assert.NotNull(v2);
        Assert.Null(v2.Share);
        var link2 = await desktop.Sessions.EnsureShareLinkAsync(v2.Version);
        Assert.NotNull(link2);
        Assert.NotEqual(v1.Share!.Token, link2.Token);
        Assert.Equal(v1.Share, desktop.Sessions.Current!.FindVersion(v1.Version)!.Share);

        var stored = StoredVersions(session);
        Assert.Equal([v1.Share.Token, link2.Token], stored.Select(v => v.ShareToken));
    }

    [PostgresFact]
    public async Task MissingLocalFile_MarkedFailed_NotRetriedForever()
    {
        _network.Online = false;
        var desktop = StartDesktop();
        var session = await CompleteSessionAsync(desktop.Sessions);
        var link = session.CompletedOutput!.Share!;
        await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Pending, s => s.Attempts >= 1);

        File.Delete(session.CompletedOutput.FilePath);
        _network.Online = true;
        var failed = await WaitForStatusAsync(desktop.Uploads, link.Token, CloudUploadStatus.Failed);
        await Task.Delay(300);

        Assert.Contains("Không tìm thấy file", failed.LastError);
        Assert.Equal(failed.Attempts, desktop.Uploads.GetState(link.Token)!.Attempts);
        Assert.Empty(CloudSharedFiles());
    }

    [PostgresFact]
    public void ShareTokens_Are192BitBase64Url_AndUnique()
    {
        var factory = new ShareLinkFactory(new CloudShareOptions { Enabled = true, BaseUrl = "https://photo.example.com/booth" });
        var links = Enumerable.Range(0, 2000).Select(_ => factory.Create(DateTimeOffset.UnixEpoch)!).ToArray();

        Assert.Equal(2000, links.Select(l => l.Token).Distinct(StringComparer.Ordinal).Count());
        Assert.All(links, l =>
        {
            Assert.True(ShareLinkFactory.IsValidToken(l.Token));
            Assert.Equal(24, System.Buffers.Text.Base64Url.DecodeFromChars(l.Token).Length);
            Assert.Equal($"https://photo.example.com/booth/s/{l.Token}", l.Url);
        });
    }

    [PostgresFact]
    public async Task CorruptedShareInSessionJson_IsIgnored_NoQrFromUntrustedData()
    {
        var desktop = StartDesktop();
        var session = await CompleteSessionAsync(desktop.Sessions);
        await desktop.Sessions.FlushAsync();
        await using (var db = TestDatabase.Open(_paths))
        {
            db.OutputVersions.Single(v => v.SessionId == session.SessionId).ShareToken = "../../khong-hop-le";
            await db.SaveChangesAsync();
        }

        var archived = await TestSessions.CreateArchive(new PhotoboothStoragePaths(_paths.RootDirectory), desktop.Workspaces)
            .LoadSessionAsync(Path.GetFileName(session.Directory!));

        Assert.Null(Assert.Single(archived.Session.Versions).Share);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>"Công tắc Internet" của máy Desktop: tắt thì mọi request lỗi kết nối như khi mất mạng.</summary>
    private sealed class NetworkSwitch : DelegatingHandler
    {
        public NetworkSwitch()
            : base(new SocketsHttpHandler())
        {
        }

        public volatile bool Online = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Online
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException("Mô phỏng mất Internet.", null, HttpStatusCode.ServiceUnavailable);
    }
}
