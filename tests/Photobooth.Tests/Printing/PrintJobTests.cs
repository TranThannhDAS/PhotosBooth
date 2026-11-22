using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Admin;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Printing;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Printing;

/// <summary>Chọn số bản in, Print Job, in lại, chống in trùng, máy in giả lập không báo in thật.</summary>
public sealed class PrintJobTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly FakeCameraDevice _camera = new();
    private readonly EventWorkspaceService _workspaces;
    private readonly TestPrinterProvider _printers = new();

    public PrintJobTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "Sự kiện in"));
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<PhotoSessionService> CreateReadyAsync(IPrinter? printer, int maxCopies = PrintOptions.DefaultMaxCopies)
    {
        _printers.Current = printer;
        var service = TestSessions.Create(_paths, _workspaces, printers: _printers, printOptions: new PrintOptions { MaxCopies = maxCopies });
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        return service;
    }

    [Fact]
    public void Defaults_OneCopyMinimum_TenMaximum()
    {
        Assert.Equal(10, new PrintOptions().MaxCopies);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrintOptions { MaxCopies = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrintOptions { MaxCopies = PrintOptions.AbsoluteMaxCopies + 1 }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(11)]
    public async Task InvalidCopies_AreRejected_WithoutCreatingJob(int copies)
    {
        var printer = new RecordingPrinter(isPhysical: true);
        var service = await CreateReadyAsync(printer);

        var result = await service.RequestPrintAsync(copies);

        Assert.Equal(PrintOutcome.InvalidCopies, result.Outcome);
        Assert.Empty(service.Current!.PrintJobs);
        Assert.Empty(printer.Requests);
    }

    [Fact]
    public async Task MaxCopies_IsConfigurable_AndCopiesAreIndependentOfPhotoCount()
    {
        var printer = new RecordingPrinter(isPhysical: true);
        var service = await CreateReadyAsync(printer, maxCopies: 5);

        Assert.Equal(PrintOutcome.InvalidCopies, (await service.RequestPrintAsync(6)).Outcome);
        var result = await service.RequestPrintAsync(5);

        // Template 2 ảnh nguồn, in 5 bản: một file thành phẩm, 5 bản.
        Assert.Equal(PrintOutcome.Submitted, result.Outcome);
        var request = Assert.Single(printer.Requests);
        Assert.Equal(5, request.Copies);
        Assert.Equal(service.Current!.LatestVersion!.FilePath, request.ImageFilePath);
    }

    [Fact]
    public async Task NoPrinterConfigured_ReportsNotConfigured()
    {
        var service = await CreateReadyAsync(printer: null);

        Assert.Equal(PrintOutcome.NotConfigured, (await service.RequestPrintAsync(1)).Outcome);
        Assert.Empty(service.Current!.PrintJobs);
    }

    [Fact]
    public async Task OneSession_CanHaveManyPrintJobs_PersistedInEventFolder()
    {
        var service = await CreateReadyAsync(new RecordingPrinter(isPhysical: true));

        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(3)).Outcome);
        Assert.Equal(PrintOutcome.Submitted, (await service.RequestPrintAsync(2)).Outcome);

        var session = service.Current!;
        Assert.Equal([3, 2], session.PrintJobs.Select(j => j.Copies));
        Assert.All(session.PrintJobs, j => Assert.Equal(PrintJobStatus.Submitted, j.Status));
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(session.Directory!, EventSessionArchive.SessionFileName)));
        var jobs = json.RootElement.GetProperty("printJobs");
        Assert.Equal(2, jobs.GetArrayLength());
        Assert.Equal("Outputs/final-v1.png", jobs[0].GetProperty("file").GetString());
    }

    [Fact]
    public async Task Reprint_UsesExactSavedVersionFile_WithoutRerendering()
    {
        var printer = new RecordingPrinter(isPhysical: true);
        var service = await CreateReadyAsync(printer);
        var v1 = await service.ConfirmOutputAsync();
        await service.ComposeAsync(ColorFilter.Sepia);
        var v2 = await service.ConfirmOutputAsync();
        var draftsBefore = Directory.GetFiles(_paths.GetDraftsDirectory(service.Current!.SessionId)).Length;

        var result = await service.RequestPrintAsync(2, version: 1);

        Assert.Equal(PrintOutcome.Submitted, result.Outcome);
        Assert.Equal(1, result.Job!.OutputVersion);
        Assert.Equal(v1!.FilePath, printer.Requests.Single().ImageFilePath);
        Assert.NotEqual(v2!.FilePath, v1.FilePath);
        Assert.Equal(draftsBefore, Directory.GetFiles(_paths.GetDraftsDirectory(service.Current.SessionId)).Length);
    }

    [Fact]
    public async Task RapidPresses_CreateOnlyOnePrintJob()
    {
        var printer = new RecordingPrinter(isPhysical: true) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = await CreateReadyAsync(printer);
        await service.ConfirmOutputAsync();

        var first = service.RequestPrintAsync(3);
        var others = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.RequestPrintAsync(3)));
        printer.Gate.SetResult();
        var firstResult = await first;

        Assert.All(others, r => Assert.Equal(PrintOutcome.Busy, r.Outcome));
        Assert.Equal(PrintOutcome.Submitted, firstResult.Outcome);

        // Bấm lại ngay với cùng yêu cầu: coi là bấm trùng.
        Assert.Equal(PrintOutcome.Busy, (await service.RequestPrintAsync(3)).Outcome);
        Assert.Single(service.Current!.PrintJobs);
        Assert.Single(printer.Requests);
    }

    [Fact]
    public async Task MockPrinter_NeverReportsPhysicalPrint()
    {
        var service = await CreateReadyAsync(new MockPrinter(NullLogger<MockPrinter>.Instance));

        var result = await service.RequestPrintAsync(4);

        Assert.Equal(PrintOutcome.Simulated, result.Outcome);
        Assert.Equal(PrintJobStatus.Simulated, result.Job!.Status);
        Assert.False(result.Job.IsPhysicalPrinter);

        // Không thể "xác nhận đã in xong" một lệnh giả lập.
        Assert.False(await service.UpdatePrintJobStatusAsync(result.Job.PrintJobId, PrintJobStatus.Completed));
    }

    [Fact]
    public async Task PrinterClaimingCompleted_IsDowngraded_CompletedOnlyByOperator()
    {
        var physical = await CreateReadyAsync(new RecordingPrinter(isPhysical: true, PrintJobStatus.Completed));
        var job = (await physical.RequestPrintAsync(1)).Job!;
        Assert.Equal(PrintJobStatus.Submitted, job.Status);
        Assert.True(await physical.UpdatePrintJobStatusAsync(job.PrintJobId, PrintJobStatus.Completed));
        Assert.Equal(PrintJobStatus.Completed, physical.Current!.PrintJobs.Single().Status);

        var fake = await CreateReadyAsync(new RecordingPrinter(isPhysical: false, PrintJobStatus.Completed));
        Assert.Equal(PrintJobStatus.Simulated, (await fake.RequestPrintAsync(1)).Job!.Status);
    }

    [Fact]
    public async Task FailedPrint_KeepsAllData_AndRetryNeedsConfirmation()
    {
        var printer = new RecordingPrinter(isPhysical: true, PrintJobStatus.Failed);
        var service = await CreateReadyAsync(printer);

        var failed = await service.RequestPrintAsync(2);

        Assert.Equal(PrintOutcome.Failed, failed.Outcome);
        var session = service.Current!;
        Assert.True(File.Exists(session.LatestVersion!.FilePath));
        Assert.All(session.Photos, p => Assert.True(File.Exists(p.OriginalFilePath)));
        Assert.Equal(PrintJobStatus.Failed, session.PrintJobs.Single().Status);

        // Không tự gửi lại: phải xác nhận.
        var retry = await service.RequestPrintAsync(1);
        Assert.Equal(PrintOutcome.ConfirmationRequired, retry.Outcome);
        Assert.Single(printer.Requests);

        var confirmed = await service.RequestPrintAsync(1, confirmRetry: true);
        Assert.Equal(PrintOutcome.Failed, confirmed.Outcome);
        Assert.Equal(2, service.Current!.PrintJobs.Count);
    }

    [Fact]
    public async Task PrinterException_IsRecordedAsFailed()
    {
        var printer = new RecordingPrinter(isPhysical: true) { Throw = new IOException("spooler") };
        var service = await CreateReadyAsync(printer);

        var result = await service.RequestPrintAsync(1);

        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal(PrintJobStatus.Failed, service.Current!.PrintJobs.Single().Status);
        Assert.True(File.Exists(service.Current.LatestVersion!.FilePath));
    }

    [Fact]
    public async Task UnclearJobAfterRestart_IsNotResentAutomatically()
    {
        // Mô phỏng ứng dụng dừng đột ngột khi lệnh in còn Pending: sửa trạng thái trong session.json.
        var service = await CreateReadyAsync(new RecordingPrinter(isPhysical: true));
        await service.RequestPrintAsync(2);
        Assert.True(await service.CompleteSessionAsync());
        var session = service.Current!;
        await service.FlushAsync();
        var file = Path.Combine(session.Directory!, EventSessionArchive.SessionFileName);
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"Submitted\"", "\"Pending\"", StringComparison.Ordinal));

        var admin = new AdminAccessService();
        admin.TryUnlock(AdminAccessService.Pin);
        var printer = new RecordingPrinter(isPhysical: true);
        _printers.Current = printer;
        var reopened = TestSessions.Create(_paths, _workspaces, printers: _printers);
        var gallery = new EventGalleryService(TestSessions.CreateArchive(_paths, _workspaces), admin, reopened, new SkiaImagePreviewRenderer(_paths));
        await gallery.OpenAsync(Path.GetFileName(session.Directory!));

        Assert.Equal(PrintJobStatus.Pending, reopened.Current!.PrintJobs.Single().Status);
        Assert.Equal(PrintOutcome.ConfirmationRequired, (await reopened.RequestPrintAsync(2, version: 1)).Outcome);
        Assert.Empty(printer.Requests);

        Assert.True(await reopened.UpdatePrintJobStatusAsync(reopened.Current.PrintJobs.Single().PrintJobId, PrintJobStatus.Cancelled));
        Assert.Equal(PrintOutcome.Submitted, (await reopened.RequestPrintAsync(2, version: 1)).Outcome);
    }
}
