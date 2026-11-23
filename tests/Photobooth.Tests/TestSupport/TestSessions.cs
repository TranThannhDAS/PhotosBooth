using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Export;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;

namespace Photobooth.Tests.TestSupport;

/// <summary>Dựng PhotoSessionService với Event Workspace thật (thư mục tạm) cho kiểm thử.</summary>
internal static class TestSessions
{
    public static EventWorkspaceService CreateWorkspaceService(PhotoboothStoragePaths paths, TimeProvider? timeProvider = null) =>
        new(paths, TestDatabase.For(paths), timeProvider ?? TimeProvider.System, NullLogger<EventWorkspaceService>.Instance);

    /// <summary>Mở (tạo nếu cần) thư mục sự kiện và trả về service đang dùng sự kiện đó.</summary>
    public static EventWorkspaceService OpenWorkspace(PhotoboothStoragePaths paths, string folder, string? name = null)
    {
        Directory.CreateDirectory(folder);
        var workspaces = CreateWorkspaceService(paths);
        var status = workspaces.OpenAsync(folder, name).GetAwaiter().GetResult();
        Assert.True(status.IsReady, status.Problem);
        return workspaces;
    }

    public static EventSessionArchive CreateArchive(PhotoboothStoragePaths paths, EventWorkspaceService workspaces) =>
        new(workspaces, TestDatabase.For(paths), paths, TimeProvider.System, NullLogger<EventSessionArchive>.Instance);

    public static PhotoSessionService Create(
        PhotoboothStoragePaths paths,
        EventWorkspaceService workspaces,
        CaptureOptions? options = null,
        IOriginalPhotoStore? store = null,
        IPhotoComposer? composer = null,
        TimeProvider? timeProvider = null,
        IPrinterProvider? printers = null,
        PrintOptions? printOptions = null,
        ShareLinkFactory? shareLinks = null,
        ICloudUploadQueue? uploads = null) =>
        new(
            store ?? new LocalOriginalPhotoStore(paths),
            composer ?? new SkiaPhotoComposer(paths, NullLogger<SkiaPhotoComposer>.Instance),
            new SkiaImagePreviewRenderer(paths),
            new ComposedPhotoExporter(new ExportOptions(), NullLogger<ComposedPhotoExporter>.Instance),
            CreateArchive(paths, workspaces),
            printers ?? new TestPrinterProvider(),
            options ?? new CaptureOptions(),
            printOptions ?? new PrintOptions(),
            shareLinks ?? new ShareLinkFactory(new CloudShareOptions()),
            uploads ?? new RecordingUploadQueue(),
            timeProvider ?? FakeTimeProvider.Immediate(),
            NullLogger<PhotoSessionService>.Instance);
}

/// <summary>Hàng đợi upload kiểm thử: chỉ ghi lại các yêu cầu (không gọi mạng).</summary>
internal sealed class RecordingUploadQueue : ICloudUploadQueue
{
    private readonly List<ShareUploadItem> _items = [];

    public event EventHandler? StateChanged;

    public IReadOnlyList<ShareUploadItem> Items
    {
        get
        {
            lock (_items)
            {
                return [.. _items];
            }
        }
    }

    public void Enqueue(ShareUploadItem item)
    {
        lock (_items)
        {
            _items.Add(item);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ShareUploadState? GetState(string token) => null;
}

internal sealed class TestPrinterProvider : IPrinterProvider
{
    public IPrinter? Current { get; set; }
}

/// <summary>Máy in kiểm thử: ghi lại yêu cầu, trả kết quả cấu hình được, có thể chặn để mô phỏng in chậm.</summary>
internal sealed class RecordingPrinter(bool isPhysical, PrintJobStatus result = PrintJobStatus.Submitted) : IPrinter
{
    private readonly List<PrintRequest> _requests = [];

    public string Name => isPhysical ? "Máy in kiểm thử" : "Máy in giả lập kiểm thử";

    public bool IsPhysical => isPhysical;

    public TaskCompletionSource? Gate { get; set; }

    public Exception? Throw { get; set; }

    public IReadOnlyList<PrintRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public async Task<PrintSubmission> SubmitAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (Throw is { } ex)
        {
            throw ex;
        }

        return new PrintSubmission(result, result == PrintJobStatus.Failed ? "Lỗi máy in giả lập." : null);
    }
}
