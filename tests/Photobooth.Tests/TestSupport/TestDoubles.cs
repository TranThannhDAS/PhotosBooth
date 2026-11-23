using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Photos;
using Photobooth.Application.Templates;
using SkiaSharp;

namespace Photobooth.Tests.TestSupport;

/// <summary>Tạo dữ liệu JPEG tối thiểu (SOI + APP0 + SOF0 + EOI) đủ để đọc kích thước, không giải mã được.</summary>
internal static class TestJpeg
{
    public static byte[] CreateHeaderOnly(int widthPixels = 640, int heightPixels = 480)
    {
        byte[] app0 = [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];
        byte[] sof0 =
        [
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(heightPixels >> 8), (byte)heightPixels,
            (byte)(widthPixels >> 8), (byte)widthPixels,
            0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        ];
        return [0xFF, 0xD8, .. app0, .. sof0, 0xFF, 0xD9];
    }
}

/// <summary>Ảnh thật (giải mã được) để kiểm thử ghép/xuất/lật ảnh.</summary>
internal static class TestImages
{
    public static readonly SKColor Left = new(220, 20, 20);
    public static readonly SKColor Right = new(20, 20, 220);

    /// <summary>JPEG nửa trái màu đỏ, nửa phải màu xanh dương — dùng để phát hiện ảnh bị lật.</summary>
    public static byte[] CreateSplitJpeg(int width = 640, int height = 480, SKColor? leftColor = null, SKColor? rightColor = null) =>
        Encode(width, height, SKEncodedImageFormat.Jpeg, canvas =>
        {
            using var left = new SKPaint { Color = leftColor ?? Left };
            using var right = new SKPaint { Color = rightColor ?? Right };
            canvas.DrawRect(SKRect.Create(0, 0, width / 2f, height), left);
            canvas.DrawRect(SKRect.Create(width / 2f, 0, width / 2f, height), right);
        });

    public static readonly SKColor TopLeft = new(220, 20, 20);
    public static readonly SKColor TopRight = new(20, 160, 20);
    public static readonly SKColor BottomLeft = new(20, 20, 220);
    public static readonly SKColor BottomRight = new(230, 200, 20);

    /// <summary>Ảnh 4 góc phần tư khác màu — kiểm tra hướng kéo/zoom và lật gương của vùng cắt.</summary>
    public static byte[] CreateQuadrants(int width = 640, int height = 480, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg) =>
        Encode(width, height, format, canvas =>
        {
            void Fill(float x, float y, SKColor color)
            {
                using var paint = new SKPaint { Color = color };
                canvas.DrawRect(SKRect.Create(x, y, width / 2f, height / 2f), paint);
            }

            Fill(0, 0, TopLeft);
            Fill(width / 2f, 0, TopRight);
            Fill(0, height / 2f, BottomLeft);
            Fill(width / 2f, height / 2f, BottomRight);
        });

    public static byte[] CreateSolid(int width, int height, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Png) =>
        Encode(width, height, format, canvas => canvas.Clear(color));

    /// <summary>PNG trong suốt ở nửa trái, nửa phải là <paramref name="right"/> (có thể bán trong suốt).</summary>
    public static byte[] CreateHalfTransparentPng(int width, int height, SKColor right) =>
        Encode(width, height, SKEncodedImageFormat.Png, canvas =>
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = right };
            canvas.DrawRect(SKRect.Create(width / 2f, 0, width / 2f, height), paint);
        });

    public static SKColor PixelAt(string filePath, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(filePath) ?? throw new InvalidOperationException("Không giải mã được ảnh.");
        return bitmap.GetPixel(x, y);
    }

    public static SKColor PixelAt(byte[] data, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidOperationException("Không giải mã được ảnh.");
        return bitmap.GetPixel(x, y);
    }

    public static (int Width, int Height) SizeOf(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidOperationException("Không giải mã được ảnh.");
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>So màu có dung sai (JPEG nén mất dữ liệu).</summary>
    public static bool IsClose(SKColor actual, SKColor expected, int tolerance = 40) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance
        && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Blue - expected.Blue) <= tolerance;

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }
}

internal static class TestTemplates
{
    /// <summary>Template 2 ảnh: hai ô cạnh nhau, có một khối trang trí bất đối xứng ở góc trái dưới.</summary>
    public static PhotoTemplate TwoSlots(int photoCount = 2) => new()
    {
        Id = "test-two",
        Name = "Kiểm thử",
        Canvas = new TemplateCanvas { WidthPixels = 400, HeightPixels = 300, Dpi = 100, BackgroundColor = "#FFFFFF" },
        Slots = Enumerable.Range(0, photoCount)
            .Select(i => new TemplateSlot { PhotoIndex = i, X = 10 + (i * 200), Y = 10, Width = 180, Height = 180 })
            .ToArray(),
        Decorations =
        [
            new TemplateDecoration { Type = DecorationKind.Rectangle, X = 10, Y = 250, Width = 40, Height = 40, Color = "#00FF00" },
        ],
    };

    public static PhotoTemplate FourSlots() => new()
    {
        Id = "test-four",
        Name = "Kiểm thử 4 ảnh",
        Canvas = new TemplateCanvas { WidthPixels = 600, HeightPixels = 900, Dpi = 150, BackgroundColor = "#FFFFFF" },
        Slots =
        [
            new TemplateSlot { PhotoIndex = 0, X = 20, Y = 20, Width = 270, Height = 200 },
            new TemplateSlot { PhotoIndex = 1, X = 310, Y = 20, Width = 270, Height = 200 },
            new TemplateSlot { PhotoIndex = 2, X = 20, Y = 240, Width = 270, Height = 200 },
            new TemplateSlot { PhotoIndex = 3, X = 310, Y = 240, Width = 270, Height = 200 },
        ],
        Decorations = [new TemplateDecoration { Type = DecorationKind.Text, X = 20, Y = 500, Width = 560, Height = 80, Text = "Photobooth {date}" }],
    };

    /// <summary>
    /// Template có một ô cho mỗi phần tử của <paramref name="photoIndexes"/> (lưới 4 cột, ô 180×180, bước 190 px).
    /// Ô thứ i có Id "s{i}" và nằm tại (10 + (i % 4) × 190, 10 + (i / 4) × 190).
    /// </summary>
    public static PhotoTemplate WithPhotoIndexes(params int[] photoIndexes) => new()
    {
        Id = "test-indexes",
        Name = "Kiểm thử photoIndex",
        Canvas = new TemplateCanvas { WidthPixels = 800, HeightPixels = 800, Dpi = 100, BackgroundColor = "#FFFFFF" },
        Slots = photoIndexes
            .Select((index, i) => new TemplateSlot { Id = $"s{i}", PhotoIndex = index, X = 10 + ((i % 4) * 190), Y = 10 + ((i / 4) * 190), Width = 180, Height = 180 })
            .ToList(),
    };

    /// <summary>Tâm ô thứ <paramref name="slot"/> của <see cref="WithPhotoIndexes"/>.</summary>
    public static (int X, int Y) SlotCenter(int slot) => (100 + ((slot % 4) * 190), 100 + ((slot / 4) * 190));

    public static string ShippedTemplatesDirectory =>Path.Combine(AppContext.BaseDirectory, "Templates");
}

/// <summary>Tạo thư mục template tạm (Templates/{folder}/template.json) cho test.</summary>
internal static class TestTemplateFiles
{
    public static string ValidJson(string id) => $$"""
        {
          "id": "{{id}}", "name": "Mẫu kiểm thử",
          "canvas": { "widthPixels": 200, "heightPixels": 200 },
          "slots": [ { "photoIndex": 0, "x": 0, "y": 0, "width": 100, "height": 100 } ],
          "decorations": []
        }
        """;

    /// <summary>Ghi template.json vào {root}/{folder}/ và trả về đường dẫn file.</summary>
    public static string Write(string root, string folder, string json)
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "template.json");
        File.WriteAllText(file, json);
        return file;
    }

    public static void WritePng(string root, string relativePath, byte[]? png = null)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, png ?? TestImages.CreateSolid(20, 20, SKColors.Orange));
    }
}

/// <summary>Camera giả cho test nghiệp vụ. Không thay thế kiểm thử webcam thật.</summary>
internal sealed class FakeCameraDevice : ICameraDevice
{
    public bool IsActive { get; set; } = true;

    public int CaptureCount { get; private set; }

    /// <summary>Nhận số thứ tự lần chụp (1-based) và trả về khung hình.</summary>
    public Func<int, CapturedFrame> NextFrame { get; set; } = _ => new CapturedFrame(TestImages.CreateSplitJpeg(), JpegImageInfo.ContentType);

    public event EventHandler<CameraError>? Faulted;

    public Task<CameraStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        IsActive = true;
        return Task.FromResult(CameraStartResult.Success());
    }

    public Task StopAsync()
    {
        IsActive = false;
        return Task.CompletedTask;
    }

    public Task<CapturedFrame> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        CaptureCount++;
        return Task.FromResult(NextFrame(CaptureCount));
    }

    public void RaiseFaulted(CameraError error) => Faulted?.Invoke(this, error);
}

internal sealed class FailingOriginalPhotoStore : IOriginalPhotoStore
{
    public Task<StoredOriginal> SaveOriginalAsync(Guid sessionId, Guid photoId, int sequence, ReadOnlyMemory<byte> jpegData, CancellationToken cancellationToken = default) =>
        throw new IOException("Ổ đĩa giả lập bị lỗi.");

    public Task<byte[]> ReadOriginalAsync(string filePath, CancellationToken cancellationToken = default) =>
        throw new FileNotFoundException();
}

internal sealed class FailingComposer : IPhotoComposer
{
    public Task<ComposedImage> ComposeAsync(CompositionRequest request, CancellationToken cancellationToken = default) =>
        throw new CompositionException("Lỗi ghép giả lập.");

    public Task<byte[]> RenderTemplatePreviewAsync(PhotoTemplate template, int maxWidthPixels, CancellationToken cancellationToken = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> RenderTemplateSampleAsync(PhotoTemplate template, TemplateSampleContent content, int maxWidthPixels, CancellationToken cancellationToken = default) =>
        throw new CompositionException("Lỗi ghép giả lập.");
}

/// <summary>
/// TimeProvider giả: bộ hẹn giờ kích hoạt ngay (bỏ qua thời gian chờ đếm ngược) hoặc không bao giờ kích hoạt.
/// </summary>
internal sealed class FakeTimeProvider(bool timersFire) : TimeProvider
{
    public static FakeTimeProvider Immediate() => new(timersFire: true);

    public static FakeTimeProvider Frozen() => new(timersFire: false);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state, timersFire);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, bool fire) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (fire && dueTime != Timeout.InfiniteTimeSpan)
            {
                // Kích hoạt bất đồng bộ như timer thật.
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>IProgress đồng bộ (Progress&lt;T&gt; gửi bất đồng bộ nên không phù hợp để kiểm tra thứ tự).</summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly List<T> _values = [];

    public Action<T>? OnReport { get; set; }

    public IReadOnlyList<T> Values
    {
        get
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }

    public void Report(T value)
    {
        lock (_values)
        {
            _values.Add(value);
        }

        OnReport?.Invoke(value);
    }
}

/// <summary>Thư mục tạm riêng cho mỗi test, tự xóa khi kết thúc.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoboothTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // Đóng các kết nối SQLite đang được pool giữ để xóa được file photobooth.db.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Bỏ qua: thư mục tạm của test, hệ điều hành sẽ dọn.
        }
    }
}
