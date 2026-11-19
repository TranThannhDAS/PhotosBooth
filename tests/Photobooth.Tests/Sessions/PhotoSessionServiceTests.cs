using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Infrastructure.Export;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Sessions;

public sealed class PhotoSessionServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCameraDevice _camera = new();
    private readonly PhotoboothStoragePaths _paths;

    public PhotoSessionServiceTests()
    {
        _paths = new PhotoboothStoragePaths(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private PhotoSessionService CreateService(
        TimeProvider? timeProvider = null,
        IOriginalPhotoStore? store = null,
        CaptureOptions? options = null,
        IPhotoComposer? composer = null) =>
        new(
            store ?? new LocalOriginalPhotoStore(_paths),
            composer ?? new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance),
            new SkiaImagePreviewRenderer(_paths),
            new ComposedPhotoExporter(new ExportOptions(), NullLogger<ComposedPhotoExporter>.Instance),
            options ?? new CaptureOptions(),
            timeProvider ?? FakeTimeProvider.Immediate(),
            NullLogger<PhotoSessionService>.Instance);

    /// <summary>Phiên đã chọn template 4 ảnh và camera sẵn sàng.</summary>
    private PhotoSessionService CreatePreviewingService(TimeProvider? timeProvider = null, IOriginalPhotoStore? store = null, IPhotoComposer? composer = null)
    {
        var service = CreateService(timeProvider, store, composer: composer);
        service.StartNewSession();
        Assert.True(service.SelectTemplate(TestTemplates.FourSlots()));
        Assert.True(service.MarkPreviewReady());
        return service;
    }

    private async Task<PhotoSessionService> CreateReviewingServiceAsync(IPhotoComposer? composer = null)
    {
        var service = CreatePreviewingService(composer: composer);
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
        return service;
    }

    // ------------------------------------------------------------ Chụp nhiều ảnh

    [Fact]
    public async Task CaptureSequence_CapturesAllPhotosInOrderThenReviews()
    {
        var service = CreatePreviewingService();
        var progress = new RecordingProgress<CaptureProgress>();

        var result = await service.CaptureSequenceAsync(_camera, progress);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.CapturedCount);
        var session = service.Current!;
        Assert.Equal(PhotoSessionState.Reviewing, session.State);
        Assert.Equal([0, 1, 2, 3], session.Slots.Select(p => p!.Position));
        Assert.Equal([1, 2, 3, 4], session.Slots.Select(p => p!.Sequence));
        Assert.All(session.Slots, p => Assert.True(File.Exists(p!.OriginalFilePath)));
        Assert.Equal(4, session.Slots.Select(p => p!.OriginalFilePath).Distinct().Count());
        Assert.Empty(session.PendingPositions);

        // Mỗi ảnh: đếm ngược 3-2-1, chụp, đã lưu; hiển thị "Ảnh X/4".
        var forFirst = progress.Values.Where(p => p.Position == 0).Select(p => (p.Stage, p.SecondsRemaining));
        Assert.Equal([(CaptureStage.Countdown, 3), (CaptureStage.Countdown, 2), (CaptureStage.Countdown, 1), (CaptureStage.Shooting, 0), (CaptureStage.Saved, 0)], forFirst);
        Assert.All(progress.Values, p => Assert.Equal((4, 4), (p.PhotoCount, p.ShotCount)));
        Assert.Equal([1, 2, 3, 4], progress.Values.Where(p => p.Stage == CaptureStage.Saved).Select(p => p.ShotNumber));
    }

    [Fact]
    public async Task CaptureSequence_UsesConfiguredCountdownAndPhotoCount()
    {
        var service = CreateService(options: new CaptureOptions { CountdownSeconds = 5, PhotosPerSession = 2 });
        service.StartNewSession();
        Assert.False(service.SelectTemplate(TestTemplates.FourSlots()));
        Assert.True(service.SelectTemplate(TestTemplates.TwoSlots()));
        service.MarkPreviewReady();
        var progress = new RecordingProgress<CaptureProgress>();

        var result = await service.CaptureSequenceAsync(_camera, progress);

        Assert.Equal(2, result.CapturedCount);
        Assert.Equal([5, 4, 3, 2, 1], progress.Values.Where(p => p.Position == 0 && p.Stage == CaptureStage.Countdown).Select(p => p.SecondsRemaining));
    }

    [Fact]
    public async Task CaptureSequence_WithoutTemplateOrPreview_IsNotReady()
    {
        var service = CreateService();
        service.StartNewSession();
        Assert.False(service.MarkPreviewReady());

        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(CaptureFailureReason.NotReady, result.Failure);
        Assert.Equal(0, _camera.CaptureCount);
    }

    [Fact]
    public async Task CaptureSequence_WhileRunning_SecondCallIsBusy_AndCancelKeepsState()
    {
        var service = CreatePreviewingService(FakeTimeProvider.Frozen());
        using var cts = new CancellationTokenSource();

        var first = service.CaptureSequenceAsync(_camera, null, cts.Token);
        var second = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(CaptureFailureReason.Busy, second.Failure);
        Assert.Equal(PhotoSessionState.Capturing, service.Current!.State);

        await cts.CancelAsync();
        var firstResult = await first;

        Assert.Equal(CaptureFailureReason.Cancelled, firstResult.Failure);
        Assert.Equal(PhotoSessionState.Previewing, service.Current!.State);
        Assert.Equal([0, 1, 2, 3], service.Current.PendingPositions);
        Assert.Equal(0, _camera.CaptureCount);
    }

    [Fact]
    public async Task CaptureSequence_FailureOnThirdShot_KeepsFirstTwo_ThenContinues()
    {
        var service = CreatePreviewingService();
        _camera.NextFrame = n => n == 3
            ? throw new CameraException(CameraError.Disconnected, "mất camera")
            : new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);

        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(CaptureFailureReason.CameraFailed, result.Failure);
        Assert.Equal(CameraError.Disconnected, result.CameraError);
        Assert.Equal(2, result.CapturedCount);
        var session = service.Current!;
        Assert.Equal(PhotoSessionState.Previewing, session.State);
        Assert.Equal(2, session.CapturedCount);
        Assert.Equal([2, 3], session.PendingPositions);
        Assert.False(session.IsRetake);

        var resumed = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(2, resumed.CapturedCount);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
    }

    [Fact]
    public async Task CaptureSequence_InvalidImage_IsNotCounted()
    {
        var service = CreatePreviewingService();
        _camera.NextFrame = _ => new CapturedFrame([1, 2, 3, 4, 5], JpegImageInfo.ContentType);

        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(CaptureFailureReason.InvalidImage, result.Failure);
        Assert.Equal(0, service.Current!.CapturedCount);
        Assert.False(Directory.Exists(_paths.SessionsDirectory));
    }

    [Fact]
    public async Task CaptureSequence_StorageFails_ReturnsStorageFailed()
    {
        var service = CreatePreviewingService(store: new FailingOriginalPhotoStore());

        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(CaptureFailureReason.StorageFailed, result.Failure);
        Assert.Equal(PhotoSessionState.Previewing, service.Current!.State);
        Assert.Equal(0, service.Current.CapturedCount);
    }

    // ------------------------------------------------------------ Gương

    [Fact]
    public async Task Mirror_DefaultsFromOptions_AndToggleAppliesToNextPhotosOnly()
    {
        var service = CreatePreviewingService();
        Assert.True(service.Current!.Mirror);
        _camera.NextFrame = n => n == 2
            ? throw new CameraException(CameraError.CaptureFailed, "dừng")
            : new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);

        await service.CaptureSequenceAsync(_camera, null);
        Assert.True(service.SetMirror(false));
        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);
        await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal([true, false, false, false], service.Current!.Slots.Select(p => p!.Mirrored));
    }

    [Fact]
    public async Task SlotPreview_MatchesLivePreviewOrientation()
    {
        var service = await CreateReviewingServiceAsync();

        var preview = await service.GetSlotPreviewAsync(0, 320);

        // Gương bật mặc định: nửa trái ảnh xem lại là nửa phải của khung hình camera.
        Assert.True(TestImages.IsClose(TestImages.PixelAt(preview!, 40, 120), TestImages.Right));
        Assert.Null(await service.GetSlotPreviewAsync(9, 320));
    }

    // ------------------------------------------------------------ Chụp lại

    [Fact]
    public async Task RetakeSingle_ReplacesOnlyThatPositionAndKeepsOldOriginal()
    {
        var service = await CreateReviewingServiceAsync();
        var before = service.Current!.Slots.ToArray();

        Assert.True(service.BeginRetake(1));
        Assert.True(service.Current!.IsRetake);
        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(1, result.CapturedCount);
        var session = service.Current!;
        Assert.Equal(PhotoSessionState.Reviewing, session.State);
        Assert.Equal(before[0], session.Slots[0]);
        Assert.Equal(before[2], session.Slots[2]);
        Assert.NotEqual(before[1], session.Slots[1]);
        Assert.Equal(1, session.Slots[1]!.Position);
        Assert.Equal(5, session.Photos.Count);
        Assert.True(File.Exists(before[1]!.OriginalFilePath));
    }

    [Fact]
    public async Task RetakeAll_CancelledMidway_KeepsUncapturedOldPhotos_AndCanReturnToReview()
    {
        var service = await CreateReviewingServiceAsync();
        var before = service.Current!.Slots.ToArray();
        Assert.True(service.BeginRetake(null));
        _camera.NextFrame = n => n == 6
            ? throw new CameraException(CameraError.CaptureFailed, "lỗi")
            : new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);

        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(1, result.CapturedCount);
        var session = service.Current!;
        Assert.NotEqual(before[0], session.Slots[0]);
        Assert.Equal(before[1..], session.Slots.Skip(1));
        Assert.True(session.IsRetake);

        Assert.True(service.ReturnToReview());
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
        Assert.Empty(service.Current.PendingPositions);
    }

    [Fact]
    public void BeginRetake_InvalidStateOrPosition_ReturnsFalse()
    {
        var service = CreatePreviewingService();

        Assert.False(service.BeginRetake(0));
        Assert.False(service.ReturnToReview());
    }

    [Fact]
    public async Task ReturnToTemplateSelection_OnlyBeforeAnyPhoto()
    {
        var service = CreatePreviewingService();
        Assert.True(service.ReturnToTemplateSelection());
        Assert.Null(service.Current!.Template);
        Assert.Equal(PhotoSessionState.Created, service.Current.State);

        service.SelectTemplate(TestTemplates.FourSlots());
        service.MarkPreviewReady();
        _camera.NextFrame = n => n == 2 ? throw new CameraException(CameraError.CaptureFailed, "x") : new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);
        await service.CaptureSequenceAsync(_camera, null);

        Assert.False(service.ReturnToTemplateSelection());
        Assert.False(service.SelectTemplate(TestTemplates.FourSlots() with { Id = "other" }));
    }

    // ------------------------------------------------------------ Ghép ảnh

    [Fact]
    public async Task Compose_CreatesResult_AndReusesItForSameInputs()
    {
        var service = await CreateReviewingServiceAsync();

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));
        var first = service.Current!.Composed!;
        Assert.Equal(PhotoSessionState.Ready, service.Current.State);
        Assert.True(File.Exists(first.FilePath));
        Assert.Equal(service.Current.Slots.Select(p => p!.PhotoId), first.PhotoIds);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));
        Assert.Equal(first.FilePath, service.Current!.Composed!.FilePath);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.Sepia));
        Assert.NotEqual(first.FilePath, service.Current!.Composed!.FilePath);
        Assert.Equal(ColorFilter.Sepia, service.Current.Composed.Filter);
        Assert.True(File.Exists(first.FilePath));
    }

    [Fact]
    public async Task Compose_BeforeAllPhotos_IsNotReady()
    {
        var service = CreatePreviewingService();

        Assert.Equal(CompositionOutcome.NotReady, await service.ComposeAsync(ColorFilter.None));
    }

    [Fact]
    public async Task Compose_Failure_ReturnsToReviewAndKeepsPhotos()
    {
        var service = await CreateReviewingServiceAsync(new FailingComposer());
        var slots = service.Current!.Slots.ToArray();

        Assert.Equal(CompositionOutcome.Failed, await service.ComposeAsync(ColorFilter.None));

        var session = service.Current!;
        Assert.Equal(PhotoSessionState.Reviewing, session.State);
        Assert.True(session.LastCompositionFailed);
        Assert.Equal(slots, session.Slots);
        Assert.All(slots, p => Assert.True(File.Exists(p!.OriginalFilePath)));
    }

    [Fact]
    public async Task Retake_AfterCompose_InvalidatesComposedResult()
    {
        var service = await CreateReviewingServiceAsync();
        await service.ComposeAsync(ColorFilter.None);

        Assert.True(service.BeginRetake(2));
        await service.CaptureSequenceAsync(_camera, null);

        Assert.Null(service.Current!.Composed);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current.State);
    }

    // ------------------------------------------------------------ Xuất file

    [Fact]
    public async Task Export_AllFormats_ThenComplete()
    {
        var service = await CreateReviewingServiceAsync();
        await service.ComposeAsync(ColorFilter.Warm);
        var output = Path.Combine(_temp.Path, "export");
        Directory.CreateDirectory(output);

        var results = await service.ExportAsync([ExportFormat.Pdf, ExportFormat.Jpeg, ExportFormat.Png], output);

        Assert.Equal([ExportFormat.Jpeg, ExportFormat.Png, ExportFormat.Pdf], results.Select(r => r.Format));
        Assert.All(results, r => Assert.True(r.Result.Succeeded));
        Assert.Equal(3, Directory.GetFiles(output).Length);
        Assert.Equal(3, service.Current!.Exports.Count);

        Assert.True(service.CompleteSession());
        Assert.Equal(PhotoSessionState.Completed, service.Current!.State);
        Assert.All(service.Current.Photos, p => Assert.True(File.Exists(p.OriginalFilePath)));
    }

    [Fact]
    public async Task Export_Twice_DoesNotOverwrite()
    {
        var service = await CreateReviewingServiceAsync();
        await service.ComposeAsync(ColorFilter.None);

        await service.ExportAsync([ExportFormat.Jpeg], _temp.Path);
        await service.ExportAsync([ExportFormat.Jpeg], _temp.Path);

        Assert.Equal(2, Directory.GetFiles(_temp.Path, "Photobooth_*.jpg").Length);
    }

    [Fact]
    public async Task Export_Failure_KeepsReadyState()
    {
        var service = await CreateReviewingServiceAsync();
        await service.ComposeAsync(ColorFilter.None);

        var results = await service.ExportAsync([ExportFormat.Jpeg], Path.Combine(_temp.Path, "missing"));

        Assert.Equal(ExportError.DirectoryNotFound, results.Single().Result.Error);
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);
        Assert.Empty(service.Current.Exports);
        Assert.False(service.CompleteSession());
    }

    [Fact]
    public async Task Export_BeforeCompose_ReturnsNothingToSave()
    {
        var service = await CreateReviewingServiceAsync();

        var results = await service.ExportAsync([ExportFormat.Png], _temp.Path);

        Assert.Equal(ExportError.NothingToSave, results.Single().Result.Error);
    }

    // ------------------------------------------------------------ Vòng đời phiên

    [Fact]
    public async Task StartNewSession_EndsPreviousWithoutDeletingPhotos()
    {
        var service = await CreateReviewingServiceAsync();
        var previous = service.Current!;

        var next = service.StartNewSession();

        Assert.NotEqual(previous.SessionId, next.SessionId);
        Assert.Equal(PhotoSessionState.Created, next.State);
        Assert.Equal(0, next.CapturedCount);
        Assert.All(previous.Photos, p => Assert.True(File.Exists(p.OriginalFilePath)));
    }

    [Fact]
    public void EndCurrentSession_WithoutExport_IsCancelledAndRaisesEvent()
    {
        var service = CreatePreviewingService();
        var raised = 0;
        service.SessionChanged += (_, _) => raised++;

        service.EndCurrentSession();

        Assert.Equal(PhotoSessionState.Cancelled, service.Current!.State);
        Assert.Equal(1, raised);
        Assert.False(service.MarkPreviewReady());
    }

    [Fact]
    public void Constructor_InvalidOptions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options: new CaptureOptions { CountdownSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options: new CaptureOptions { PhotosPerSession = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options: new CaptureOptions { JpegQuality = 1.5 }));
    }
}
