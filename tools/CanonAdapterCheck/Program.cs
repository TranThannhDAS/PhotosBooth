using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Camera;
using Photobooth.Application.Photos;
using Photobooth.Camera.Canon;

// Kiểm tra thủ công CanonCameraDevice (adapter thật của Photobooth) với máy ảnh Canon.
// Cách dùng: dotnet run -- [--shots N] [--reconnect]

var shots = 4;
var reconnect = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--shots" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n is >= 0 and <= 10:
            shots = n;
            i++;
            break;
        case "--reconnect":
            reconnect = true;
            break;
        default:
            Console.WriteLine("Cách dùng: dotnet run -- [--shots 0..10] [--reconnect]");
            return 2;
    }
}

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "adapter-output", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outputDirectory);
using var report = new StreamWriter(Path.Combine(outputDirectory, "report.txt")) { AutoFlush = true };
var logger = new ReportLogger(report);
var results = new List<string>();

void Result(string step, bool passed, string detail)
{
    var line = $"[{(passed ? "PASS" : "FAIL")}] {step} — {detail}";
    results.Add(line);
    logger.Write(line);
}

logger.Write($"Thư mục kết quả: {outputDirectory}; shots={shots}, reconnect={reconnect}; luồng gọi T{Environment.CurrentManagedThreadId}");

var host = new CanonSdkHost(logger.For<CanonSdkHost>());
var device = new CanonCameraDevice(host, new CanonCameraOptions(), logger.For<CanonCameraDevice>());
var faulted = new TaskCompletionSource<CameraError>(TaskCreationOptions.RunContinuationsAsynchronously);
device.Faulted += (_, error) => faulted.TrySetResult(error);

try
{
    var start = await device.StartAsync();
    Result("StartAsync", start.Succeeded, start.Succeeded ? $"{device.CameraName}" : $"Lỗi {start.Error}");
    if (!start.Succeeded)
    {
        return 1;
    }

    await CaptureSeriesAsync("Chụp", shots);

    if (reconnect)
    {
        logger.Write(">>> Hãy RÚT CÁP USB (hoặc tắt máy ảnh) trong vòng 60 giây...");
        var lost = await Task.WhenAny(faulted.Task, Task.Delay(TimeSpan.FromSeconds(60))) == faulted.Task;
        Result("Faulted khi mất kết nối", lost && faulted.Task.Result == CameraError.Disconnected,
            lost ? $"Faulted({faulted.Task.Result}), IsActive={device.IsActive}" : "Không nhận được Faulted trong 60 giây");

        if (lost)
        {
            logger.Write(">>> Hãy CẮM LẠI CÁP và bật máy ảnh; chương trình thử kết nối lại trong 60 giây...");
            var reconnected = false;
            var stopwatch = Stopwatch.StartNew();
            while (!reconnected && stopwatch.Elapsed < TimeSpan.FromSeconds(60))
            {
                reconnected = (await device.StartAsync()).Succeeded;
                if (!reconnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            Result("Kết nối lại", reconnected, reconnected ? $"sau {stopwatch.Elapsed.TotalSeconds:0} giây" : "Không kết nối lại được trong 60 giây");
            if (reconnected)
            {
                await CaptureSeriesAsync("Chụp sau kết nối lại", 1);
            }
        }
    }

    return 0;
}
catch (Exception ex)
{
    Result("Ngoại lệ không mong đợi", false, ex.ToString());
    return 1;
}
finally
{
    await device.DisposeAsync();
    host.Dispose();
    logger.Write(string.Empty);
    logger.Write("================ TÓM TẮT ================");
    results.ForEach(logger.Write);
}

async Task CaptureSeriesAsync(string label, int count)
{
    for (var shot = 1; shot <= count; shot++)
    {
        // Mô phỏng nhịp của phiên chụp Photobooth (đếm ngược ngắn giữa các ảnh).
        await Task.Delay(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var frame = await device.CaptureFrameAsync();
            JpegImageInfo.TryReadDimensions(frame.Data, out var width, out var height);
            var fileName = $"{label.Replace(' ', '-')}-{shot}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, fileName), frame.Data);
            Result($"{label} #{shot}", true, $"{width}x{height}, {frame.Data.Length / 1024} KB, {stopwatch.ElapsedMilliseconds} ms → {fileName}");
        }
        catch (CameraException ex)
        {
            Result($"{label} #{shot}", false, $"{ex.Error}: {ex.Message} ({stopwatch.ElapsedMilliseconds} ms)");
        }
    }
}

/// <summary>Logger tối giản ghi ra console và report.txt (kèm mã luồng).</summary>
internal sealed class ReportLogger(StreamWriter report)
{
    private readonly Lock _gate = new();

    public void Write(string message)
    {
        var line = string.IsNullOrEmpty(message) || message.StartsWith('[') || message.StartsWith('=')
            ? message
            : $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId,2}] {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            report.WriteLine(line);
        }
    }

    public ILogger<T> For<T>() => new Category<T>(this);

    private sealed class Category<T>(ReportLogger owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Write($"{logLevel,-11} {typeof(T).Name}: {formatter(state, exception)}" + (exception is null ? string.Empty : $" | {exception.Message}"));
    }
}
