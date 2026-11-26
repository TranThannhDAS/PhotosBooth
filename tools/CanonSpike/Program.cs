namespace CanonSpike;

internal static class Program
{
    /// <summary>
    /// Luồng chính là STA: mọi lệnh EDSDK và việc xử lý callback đều chạy trên luồng này (giống sample WinForms của Canon).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SpikeOptions.Parse(args);
        if (options is null)
        {
            SpikeOptions.PrintUsage();
            return 2;
        }

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "spike-output", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputDirectory);

        using var log = new SpikeLog(Path.Combine(outputDirectory, "report.txt"));
        log.Info($"Thư mục kết quả: {outputDirectory}");
        log.Info($"Tùy chọn: pump={options.Pump}, shots={options.Shots}, shutter={options.Shutter}, " +
                 $"liveview={(options.SkipLiveView ? "tắt" : options.LiveViewSeconds + "s")}, waitDisconnect={options.WaitDisconnect}");

        return new SpikeRunner(options, outputDirectory, log).Run();
    }
}

internal enum PumpMode
{
    /// <summary>Chỉ xử lý Windows message (như sample WinForms).</summary>
    Win32,

    /// <summary>Chỉ gọi EdsGetEvent định kỳ.</summary>
    GetEvent,

    /// <summary>Cả hai.</summary>
    Both,
}

internal enum ShutterMode
{
    /// <summary>Nhấn hết nút chụp rồi nhả (có AF) — giống TakePictureCommand của sample.</summary>
    Full,

    /// <summary>Nhấn hết nút chụp không lấy nét lại (Completely_NonAF).</summary>
    NonAf,

    /// <summary>Nhấn nửa (lấy nét), chờ, rồi nhấn hết và nhả.</summary>
    HalfThenFull,
}

internal sealed record SpikeOptions(
    PumpMode Pump,
    int Shots,
    ShutterMode Shutter,
    int LiveViewSeconds,
    bool SkipLiveView,
    bool WaitDisconnect)
{
    public static SpikeOptions? Parse(string[] args)
    {
        var options = new SpikeOptions(PumpMode.Win32, Shots: 2, ShutterMode.Full, LiveViewSeconds: 5, SkipLiveView: false, WaitDisconnect: false);
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i].ToLowerInvariant())
            {
                case "--pump":
                    if (!Enum.TryParse<PumpMode>(Next(), ignoreCase: true, out var pump)) return null;
                    options = options with { Pump = pump };
                    break;
                case "--shots":
                    if (!int.TryParse(Next(), out var shots) || shots is < 0 or > 10) return null;
                    options = options with { Shots = shots };
                    break;
                case "--shutter":
                    if (!Enum.TryParse<ShutterMode>(Next(), ignoreCase: true, out var shutter)) return null;
                    options = options with { Shutter = shutter };
                    break;
                case "--liveview":
                    if (!int.TryParse(Next(), out var seconds) || seconds is < 1 or > 60) return null;
                    options = options with { LiveViewSeconds = seconds };
                    break;
                case "--no-liveview":
                    options = options with { SkipLiveView = true };
                    break;
                case "--wait-disconnect":
                    options = options with { WaitDisconnect = true };
                    break;
                default:
                    return null;
            }
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            CanonSpike — kiểm chứng Canon EDSDK trên .NET 10

            Tùy chọn:
              --pump win32|getevent|both        Cách nhận callback từ SDK (mặc định win32)
              --shots N                         Số ảnh chụp thử, 0..10 (mặc định 2)
              --shutter full|nonaf|halfthenfull Cách nhấn nút chụp (mặc định full)
              --liveview S                      Số giây đo Live View, 1..60 (mặc định 5)
              --no-liveview                     Bỏ qua Live View
              --wait-disconnect                 Cuối bài, chờ tối đa 60s để bạn rút cáp/tắt máy
            """);
    }
}

/// <summary>Ghi log ra console và file report.txt (kèm mã luồng để xác định callback chạy trên luồng nào).</summary>
internal sealed class SpikeLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly List<(string Step, bool Passed, string Detail)> _results = [];

    public SpikeLog(string path)
    {
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void Info(string message) => Write("    ", message);

    public void Warn(string message) => Write("WARN", message);

    public void Result(string step, bool passed, string detail)
    {
        _results.Add((step, passed, detail));
        Write(passed ? "PASS" : "FAIL", $"{step}: {detail}");
    }

    public void WriteSummary()
    {
        WriteRaw(string.Empty);
        WriteRaw("================ TÓM TẮT ================");
        foreach (var (step, passed, detail) in _results)
        {
            WriteRaw($"[{(passed ? "PASS" : "FAIL")}] {step} — {detail}");
        }
    }

    public void Dispose() => _writer.Dispose();

    private void Write(string level, string message) =>
        WriteRaw($"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId,2}] {level} {message}");

    private void WriteRaw(string line)
    {
        Console.WriteLine(line);
        _writer.WriteLine(line);
    }
}
