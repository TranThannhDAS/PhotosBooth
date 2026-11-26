using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Export;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

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
        TestSessions.Create(
            _paths,
            TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "Sự kiện")),
            options,
            store,
            composer,
            timeProvider);

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
    public async Task CaptureSequence_UsesConfiguredCountdown_IndependentOfPhotoCount()
    {
        var service = CreateService(options: new CaptureOptions { CountdownSeconds = 5 });
        service.StartNewSession();
        Assert.True(service.SelectTemplate(TestTemplates.TwoSlots()));
        service.MarkPreviewReady();
        var progress = new RecordingProgress<CaptureProgress>();

        var result = await service.CaptureSequenceAsync(_camera, progress);

        Assert.Equal(2, result.CapturedCount);
        Assert.Equal([5, 4, 3, 2, 1], progress.Values.Where(p => p.Position == 0 && p.Stage == CaptureStage.Countdown).Select(p => p.SecondsRemaining));
    }

    // ------------------------------------------------------------ Số ảnh theo template (BR-T01..T07)

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task CaptureSequence_CapturesExactlyTheTemplatePhotoCount(int photoCount)
    {
        var service = CreateService();
        service.StartNewSession();
        Assert.Equal(0, service.Current!.RequiredPhotoCount);

        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes([.. Enumerable.Range(0, photoCount)])));
        Assert.Equal(photoCount, service.Current!.RequiredPhotoCount);
        Assert.Equal(Enumerable.Range(0, photoCount), service.Current.PendingPositions);
        Assert.True(service.MarkPreviewReady());
        var progress = new RecordingProgress<CaptureProgress>();

        var result = await service.CaptureSequenceAsync(_camera, progress);

        Assert.True(result.Succeeded);
        Assert.Equal(photoCount, result.CapturedCount);
        Assert.Equal(photoCount, _camera.CaptureCount);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
        Assert.True(service.Current.AllSlotsFilled);
        Assert.Equal(photoCount, service.Current.Photos.Count);

        // Tiến trình "Ảnh x/N" theo template.
        Assert.All(progress.Values, p => Assert.Equal((photoCount, photoCount), (p.PhotoCount, p.ShotCount)));
        Assert.Equal(Enumerable.Range(1, photoCount), progress.Values.Where(p => p.Stage == CaptureStage.Saved).Select(p => p.ShotNumber));
    }

    [Fact]
    public async Task SharedPhotoIndex_CapturesEachSourcePhotoOnce()
    {
        var service = CreateService();
        service.StartNewSession();
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 0, 1, 1)));
        service.MarkPreviewReady();

        var result = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(2, result.CapturedCount);
        Assert.Equal(2, _camera.CaptureCount);
        Assert.Equal(2, service.Current!.Slots.Count);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current.State);
    }

    [Fact]
    public void ChangingTemplateBeforeCapture_UpdatesPhotoCount()
    {
        var service = CreateService();
        service.StartNewSession();

        Assert.True(service.SelectTemplate(TestTemplates.FourSlots()));
        Assert.Equal(4, service.Current!.RequiredPhotoCount);
        Assert.True(service.MarkPreviewReady());

        // Đổi thẳng sang template khác khi chưa có ảnh.
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1, 2)));
        Assert.Equal(3, service.Current!.RequiredPhotoCount);
        Assert.Equal([0, 1, 2], service.Current.PendingPositions);

        // Quay lại màn chọn khung rồi chọn template 1 ảnh.
        Assert.True(service.ReturnToTemplateSelection());
        Assert.Equal(0, service.Current!.RequiredPhotoCount);
        Assert.Empty(service.Current.PendingPositions);
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0)));
        Assert.Equal(1, service.Current!.RequiredPhotoCount);
    }

    [Fact]
    public async Task ActiveSession_PhotoCountIsNotChangedByTemplateEdits()
    {
        var service = CreateService();
        service.StartNewSession();
        var template = TestTemplates.WithPhotoIndexes(0, 1, 2);
        Assert.True(service.SelectTemplate(template));
        service.MarkPreviewReady();

        // Đối tượng template gốc bị sửa sau khi chọn (ví dụ nạp lại/sửa file): phiên dùng snapshot.
        ((List<TemplateSlot>)template.Slots).Add(new TemplateSlot { PhotoIndex = 3, X = 10, Y = 400, Width = 100, Height = 100 });
        Assert.Equal(4, template.RequiredPhotoCount);
        Assert.Equal(3, service.Current!.RequiredPhotoCount);
        Assert.Equal(3, service.Current.Template!.Slots.Count);

        // Đã có ảnh: không được đổi sang template có cấu trúc khác.
        _camera.NextFrame = n => n == 2 ? throw new CameraException(CameraError.CaptureFailed, "x") : new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);
        await service.CaptureSequenceAsync(_camera, null);
        Assert.False(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1, 2, 3, 4, 5)));
        Assert.Equal(3, service.Current!.RequiredPhotoCount);
        Assert.Equal("test-indexes", service.Current.Template!.Id);
    }

    [Fact]
    public async Task MissingPhotos_SessionDoesNotComplete_UntilAllCaptured()
    {
        var service = CreateService();
        service.StartNewSession();
        service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1, 2));
        service.MarkPreviewReady();
        _camera.NextFrame = n => n == 3 ? throw new CameraException(CameraError.CaptureFailed, "x") : new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);

        var first = await service.CaptureSequenceAsync(_camera, null);

        Assert.False(first.Succeeded);
        Assert.Equal(2, first.CapturedCount);
        Assert.Equal(PhotoSessionState.Previewing, service.Current!.State);
        Assert.False(service.Current.AllSlotsFilled);
        Assert.Equal([2], service.Current.PendingPositions);
        Assert.Equal(CompositionOutcome.NotReady, await service.ComposeAsync(ColorFilter.None));

        _camera.NextFrame = _ => new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);
        var second = await service.CaptureSequenceAsync(_camera, null);

        Assert.Equal(1, second.CapturedCount);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
        Assert.True(service.Current.AllSlotsFilled);
    }

    [Fact]
    public void TemplatesOverSafetyLimitOrInvalid_CannotStartSession()
    {
        var service = CreateService(options: new CaptureOptions { MaxPhotosPerSession = 3 });
        service.StartNewSession();

        Assert.False(service.IsTemplateSupported(TestTemplates.FourSlots()));
        Assert.False(service.SelectTemplate(TestTemplates.FourSlots()));
        Assert.False(service.SelectTemplate(TestTemplates.WithPhotoIndexes()));
        Assert.False(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 2)));
        Assert.Null(service.Current!.Template);
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1, 2)));
    }

    [Fact]
    public async Task RetakeSharedPhoto_ReplacesOnlyThatPhoto_AndAllItsSlotsUseIt()
    {
        SKColor[] colors = [new(220, 20, 20), new(20, 160, 20), new(20, 20, 220)];
        _camera.NextFrame = n => new CapturedFrame(TestImages.CreateSolid(320, 240, colors[Math.Min(n, 3) - 1], SKEncodedImageFormat.Jpeg), JpegImageInfo.ContentType);
        var service = CreateService();
        service.StartNewSession();
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1, 0)));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        var oldSecond = service.Current!.Slots[1];

        // Chụp lại ảnh 1 (photoIndex 0) → ảnh thứ 3 (xanh dương) thay thế, ảnh 2 giữ nguyên.
        Assert.True(service.BeginRetake(0));
        Assert.Equal([0], service.Current!.PendingPositions);
        Assert.Equal(1, (await service.CaptureSequenceAsync(_camera, null)).CapturedCount);
        Assert.Same(oldSecond, service.Current!.Slots[1]);
        Assert.Equal(3, service.Current.Slots[0]!.Sequence);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));
        var composed = service.Current!.Composed!.FilePath;
        Assert.True(TestImages.IsClose(PixelAtSlot(composed, 0), colors[2]));
        Assert.True(TestImages.IsClose(PixelAtSlot(composed, 1), colors[1]));
        Assert.True(TestImages.IsClose(PixelAtSlot(composed, 2), colors[2]));

        static SKColor PixelAtSlot(string file, int slot)
        {
            var (x, y) = TestTemplates.SlotCenter(slot);
            return TestImages.PixelAt(file, x, y);
        }
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
    public async Task CancelDuringSave_WaitsForOriginalThenEndingSessionKeepsPhoto()
    {
        var store = new PausedOriginalPhotoStore(new LocalOriginalPhotoStore(_paths));
        var service = CreatePreviewingService(store: store);
        using var cts = new CancellationTokenSource();
        var capture = service.CaptureSequenceAsync(_camera, null, cts.Token);
        try
        {
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            Assert.False(capture.IsCompleted);
            Assert.Equal(PhotoSessionState.Capturing, service.Current!.State);
        }
        finally
        {
            store.Release.TrySetResult();
        }

        var result = await capture.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CaptureFailureReason.Cancelled, result.Failure);
        Assert.Equal(1, result.CapturedCount);
        var photo = Assert.Single(service.Current!.Photos);
        Assert.Equal(photo, service.Current.Slots[0]);
        Assert.True(File.Exists(photo.OriginalFilePath));

        service.EndCurrentSession();

        Assert.Equal(PhotoSessionState.Cancelled, service.Current.State);
        Assert.Equal(photo, Assert.Single(service.Current.Photos));
        Assert.True(File.Exists(photo.OriginalFilePath));
    }

    private sealed class PausedOriginalPhotoStore(IOriginalPhotoStore inner) : IOriginalPhotoStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StoredOriginal> SaveOriginalAsync(Guid sessionId, Guid photoId, int sequence,
            ReadOnlyMemory<byte> jpegData, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.SaveOriginalAsync(sessionId, photoId, sequence, jpegData, cancellationToken);
        }

        public Task<byte[]> ReadOriginalAsync(string filePath, CancellationToken cancellationToken = default) =>
            inner.ReadOriginalAsync(filePath, cancellationToken);
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
    public async Task RetakeSharedPhoto_FromReadyWithCrops_ResetsOnlyAffectedSlotsAndKeepsOtherCrops()
    {
        var service = CreateService();
        service.StartNewSession();
        var template = TestTemplates.WithPhotoIndexes(0, 1, 0);
        Assert.True(service.SelectTemplate(template));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));

        var cropSlot0 = new SlotCrop(1.5, 0.25, 0.4);
        var cropSlot1 = new SlotCrop(2.0, 0.6, 0.35);
        var cropSlot2 = new SlotCrop(1.8, 0.75, 0.65);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, cropSlot0));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, cropSlot1));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(2, cropSlot2));

        var oldPhoto0 = service.Current!.Slots[0]!;
        var oldPhoto1 = service.Current.Slots[1]!;
        var composedBefore = service.Current.Composed!;

        // Chọn chụp lại từ ô 2 (SlotIndex = 2 → PhotoIndex = 0, không truyền SlotIndex = 2).
        var photoIndex = service.Current.Template!.Slots[2].PhotoIndex;
        Assert.Equal(0, photoIndex);
        Assert.True(service.BeginRetake(photoIndex));

        // Trước khi ảnh mới lưu thành công: ảnh cũ, crop đã xác nhận và thành phẩm cũ vẫn được giữ.
        Assert.Equal(PhotoSessionState.Previewing, service.Current!.State);
        Assert.True(service.Current.IsRetake);
        Assert.Equal([0], service.Current.PendingPositions);
        Assert.Same(oldPhoto0, service.Current.Slots[0]);
        Assert.Equal([cropSlot0, cropSlot1, cropSlot2], service.Current.SlotCrops);
        Assert.Same(composedBefore, service.Current.Composed);

        // Chụp lại thành công rồi ghép lại: ô 0 và ô 2 (dùng ảnh 0) về Auto Crop, ô 1 giữ crop.
        var retake = await service.CaptureSequenceAsync(_camera, null);
        Assert.True(retake.Succeeded);
        Assert.Equal(1, retake.CapturedCount);
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
        Assert.NotEqual(oldPhoto0, service.Current.Slots[0]);
        Assert.Same(oldPhoto1, service.Current.Slots[1]);
        Assert.True(File.Exists(oldPhoto0.OriginalFilePath));
        Assert.Equal(3, service.Current.Photos.Count);
        Assert.Equal([null, cropSlot1, null], service.Current.SlotCrops);
        Assert.Null(service.Current.Composed);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);
        Assert.NotNull(service.Current.Composed);
        Assert.Equal([null, cropSlot1, null], service.Current.Composed!.SlotCrops);
        Assert.Equal([0, 1, 0], service.Current.Template!.Slots.Select(s => s.PhotoIndex));
    }

    [Fact]
    public async Task RetakeSingle_FailedOrKeptOld_PreservesOldPhotoAndConfirmedCrops()
    {
        var service = await CreateReviewingServiceAsync();
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));

        var crop0 = new SlotCrop(1.4, 0.3, 0.5);
        var crop1 = new SlotCrop(1.9, 0.7, 0.4);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, crop0));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(1, crop1));
        var oldPhoto1 = service.Current!.Slots[1]!;
        var oldComposed = service.Current.Composed!;

        Assert.True(service.BeginRetake(1));
        _camera.NextFrame = _ => throw new CameraException(CameraError.CaptureFailed, "lỗi camera");
        var failed = await service.CaptureSequenceAsync(_camera, null);

        Assert.False(failed.Succeeded);
        Assert.Equal(0, failed.CapturedCount);
        Assert.Equal(PhotoSessionState.Previewing, service.Current!.State);
        Assert.Same(oldPhoto1, service.Current.Slots[1]);
        Assert.Equal([crop0, crop1, null, null], service.Current.SlotCrops);

        // Khách chọn "Giữ ảnh cũ": giữ nguyên ảnh cũ, crop đã xác nhận và thành phẩm cũ.
        Assert.True(service.ReturnToReview());
        Assert.Equal(PhotoSessionState.Reviewing, service.Current!.State);
        Assert.Empty(service.Current.PendingPositions);
        Assert.Same(oldPhoto1, service.Current.Slots[1]);
        Assert.Equal([crop0, crop1, null, null], service.Current.SlotCrops);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);
        Assert.Equal(oldComposed.FilePath, service.Current.Composed!.FilePath);
    }

    [Fact]
    public async Task BeginRetake_RejectsOutOfRangePositionArchivedAndComposing()
    {
        var paused = new PausedComposer(new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance));
        var service = CreateService(composer: paused);
        service.StartNewSession();
        Assert.True(service.SelectTemplate(TestTemplates.WithPhotoIndexes(0, 1, 0)));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);

        // Khi đang ghép ảnh (Composing): từ chối BeginRetake.
        var composeTask = service.ComposeAsync(ColorFilter.None);
        try
        {
            await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(PhotoSessionState.Composing, service.Current!.State);
            Assert.False(service.BeginRetake(0));
        }
        finally
        {
            paused.Release.TrySetResult();
        }

        Assert.Equal(CompositionOutcome.Succeeded, await composeTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);

        // Vị trí âm hoặc vượt số ảnh nguồn (ví dụ truyền nhầm SlotIndex = 2 cho template chỉ có 2 ảnh nguồn).
        Assert.False(service.BeginRetake(-1));
        Assert.False(service.BeginRetake(2));
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);

        // Phiên Admin mở lại (IsArchived = true): không cho chụp lại.
        var archivedSnapshot = service.Current!;
        Assert.True(await service.CompleteSessionAsync());
        Assert.True(service.OpenArchivedSession(new ArchivedSession("archived-1", archivedSnapshot, null)));
        Assert.True(service.Current!.IsArchived);
        Assert.False(service.BeginRetake(0));
        Assert.Equal(PhotoSessionState.Ready, service.Current.State);
    }

    [Fact]
    public async Task RetakeContainSlotPhoto_UsesPhotoIndex_KeepsContainFitAndUnaffectedCoverCrops()
    {
        var baseTemplate = TestTemplates.WithPhotoIndexes(0, 1, 1);
        var mixedTemplate = baseTemplate with
        {
            Slots =
            [
                baseTemplate.Slots[0] with { Fit = ImageFit.Cover },
                baseTemplate.Slots[1] with { Fit = ImageFit.Contain },
                baseTemplate.Slots[2] with { Fit = ImageFit.Cover },
            ],
        };

        var service = CreateService();
        service.StartNewSession();
        Assert.True(service.SelectTemplate(mixedTemplate));
        service.MarkPreviewReady();
        Assert.True((await service.CaptureSequenceAsync(_camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync(ColorFilter.None));

        var cropCover0 = new SlotCrop(1.6, 0.3, 0.4);
        var cropCover2 = new SlotCrop(1.9, 0.7, 0.6);
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(0, cropCover0));
        Assert.Equal(CompositionOutcome.Succeeded, await service.ApplySlotCropAsync(2, cropCover2));

        // Ô contain không hỗ trợ Manual Crop: áp crop bị từ chối và không làm mất crop của các ô cover.
        var containSlot = service.Current!.Template!.Slots[1];
        Assert.False(SlotCrop.IsSupported(containSlot));
        Assert.Equal(CompositionOutcome.NotReady, await service.ApplySlotCropAsync(1, new SlotCrop(1.5, 0.5, 0.5)));
        Assert.Equal([cropCover0, null, cropCover2], service.Current.SlotCrops);

        var oldPhoto0 = service.Current.Slots[0]!;
        var oldPhoto1 = service.Current.Slots[1]!;

        // Chụp lại ảnh từ ô contain (SlotIndex = 1 → PhotoIndex = 1).
        Assert.True(service.BeginRetake(containSlot.PhotoIndex));
        Assert.Equal([1], service.Current!.PendingPositions);
        var retake = await service.CaptureSequenceAsync(_camera, null);
        Assert.True(retake.Succeeded);

        Assert.Same(oldPhoto0, service.Current!.Slots[0]);
        Assert.NotEqual(oldPhoto1, service.Current.Slots[1]);
        Assert.True(File.Exists(oldPhoto1.OriginalFilePath));
        Assert.Equal(ImageFit.Contain, service.Current.Template!.Slots[1].Fit);
        Assert.Equal([cropCover0, null, null], service.Current.SlotCrops);

        Assert.Equal(CompositionOutcome.Succeeded, await service.ComposeAsync());
        Assert.Equal(PhotoSessionState.Ready, service.Current!.State);
        Assert.Equal([cropCover0, null, null], service.Current.Composed!.SlotCrops);

        // Phiên cũ mở lại trong Admin không cho chụp lại ảnh của ô contain.
        var archivedSnapshot = service.Current;
        Assert.True(await service.CompleteSessionAsync());
        Assert.True(service.OpenArchivedSession(new ArchivedSession("archived-contain", archivedSnapshot, null)));
        Assert.False(service.BeginRetake(containSlot.PhotoIndex));
    }

    private sealed class PausedComposer(IPhotoComposer inner) : IPhotoComposer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ComposedImage> ComposeAsync(CompositionRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ComposeAsync(request, cancellationToken);
        }

        public Task<byte[]> RenderTemplatePreviewAsync(PhotoTemplate template, int maxWidthPixels, CancellationToken cancellationToken = default) =>
            inner.RenderTemplatePreviewAsync(template, maxWidthPixels, cancellationToken);

        public Task<byte[]> RenderTemplateSampleAsync(PhotoTemplate template, TemplateSampleContent content, int maxWidthPixels, CancellationToken cancellationToken = default) =>
            inner.RenderTemplateSampleAsync(template, content, maxWidthPixels, cancellationToken);
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

        Assert.True(await service.CompleteSessionAsync());
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

        // Xuất lỗi không ảnh hưởng: thành phẩm vẫn được lưu vào thư mục sự kiện khi hoàn tất.
        Assert.True(await service.CompleteSessionAsync());
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

        var next = service.StartNewSession()!;

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
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options: new CaptureOptions { MaxPhotosPerSession = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options: new CaptureOptions { MaxPhotosPerSession = CaptureOptions.AbsoluteMaxPhotosPerSession + 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options: new CaptureOptions { JpegQuality = 1.5 }));
    }
}
