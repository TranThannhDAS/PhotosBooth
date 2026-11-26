using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Infrastructure.Configuration;
using Photobooth.Infrastructure.Logging;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Configuration;

public sealed class LoggingOptionsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string DataRoot => Path.Combine(_temp.Path, "data");

    private string DefaultLogs => Path.Combine(DataRoot, "Logs");

    // ---------------------------------------------------------------- Đọc từ appsettings.json

    [Fact]
    public void Load_LoggingSection_AppliesValues()
    {
        var file = WriteSettings("""{ "logging": { "directory": "D:\\PhotoboothLogs", "minimumLevel": "Warning" } }""");

        var settings = PhotoboothSettings.Load(file, NullLogger.Instance);

        Assert.Equal("D:\\PhotoboothLogs", settings.Logging.Directory);
        Assert.Equal(LogLevel.Warning, settings.Logging.ResolveMinimumLevel(out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Load_NoLoggingSection_UsesDefaults()
    {
        var file = WriteSettings("""{ "capture": { "countdownSeconds": 5 } }""");

        var settings = PhotoboothSettings.Load(file, NullLogger.Instance);

        Assert.Equal(DefaultLogs, settings.Logging.ResolveDirectory(DataRoot, DefaultLogs, out _));
        Assert.Equal(LogLevel.Information, settings.Logging.ResolveMinimumLevel(out _));
    }

    [Fact]
    public void Load_InvalidLoggingValues_DoNotResetOtherSettings()
    {
        var file = WriteSettings("""{ "capture": { "countdownSeconds": 5 }, "logging": { "directory": "C:logs", "minimumLevel": "Verbose" } }""");

        var settings = PhotoboothSettings.Load(file, NullLogger.Instance);

        Assert.Equal(5, settings.Capture.CountdownSeconds);
        Assert.Equal(LogLevel.Information, settings.Logging.ResolveMinimumLevel(out var levelProblem));
        Assert.NotNull(levelProblem);
        Assert.Equal(DefaultLogs, settings.Logging.ResolveDirectory(DataRoot, DefaultLogs, out var directoryProblem));
        Assert.NotNull(directoryProblem);
    }

    // ---------------------------------------------------------------- Mức log

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData(" Error ", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    public void ResolveMinimumLevel_ValidValues(string? value, LogLevel expected)
    {
        var level = new LoggingOptions { MinimumLevel = value }.ResolveMinimumLevel(out var problem);

        Assert.Equal(expected, level);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("Verbose")]
    [InlineData("None")]
    [InlineData("2")]
    [InlineData("99")]
    public void ResolveMinimumLevel_InvalidValues_FallBackToInformationWithProblem(string value)
    {
        var level = new LoggingOptions { MinimumLevel = value }.ResolveMinimumLevel(out var problem);

        Assert.Equal(LogLevel.Information, level);
        Assert.NotNull(problem);
    }

    // ---------------------------------------------------------------- Thư mục log

    [Fact]
    public void ResolveDirectory_Absolute_IsCreatedAndUsed()
    {
        var target = Path.Combine(_temp.Path, "custom", "logs");

        var directory = new LoggingOptions { Directory = target }.ResolveDirectory(DataRoot, DefaultLogs, out var problem);

        Assert.Equal(target, directory);
        Assert.Null(problem);
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void ResolveDirectory_Relative_IsUnderDataRoot_NotExecutableFolder()
    {
        var directory = new LoggingOptions { Directory = "MyLogs" }.ResolveDirectory(DataRoot, DefaultLogs, out var problem);

        Assert.Equal(Path.Combine(DataRoot, "MyLogs"), directory);
        Assert.Null(problem);
    }

    [Fact]
    public void ResolveDirectory_EnvironmentVariable_IsExpanded()
    {
        const string variable = "PHOTOBOOTH_TEST_LOG_ROOT";
        Environment.SetEnvironmentVariable(variable, _temp.Path);
        try
        {
            var directory = new LoggingOptions { Directory = $"%{variable}%\\env-logs" }.ResolveDirectory(DataRoot, DefaultLogs, out var problem);

            Assert.Equal(Path.Combine(_temp.Path, "env-logs"), directory);
            Assert.Null(problem);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("C:logs")]
    [InlineData("\\logs")]
    public void ResolveDirectory_AmbiguousPath_FallsBackToDefault(string value)
    {
        var directory = new LoggingOptions { Directory = value }.ResolveDirectory(DataRoot, DefaultLogs, out var problem);

        Assert.Equal(DefaultLogs, directory);
        Assert.NotNull(problem);
    }

    [Fact]
    public void ResolveDirectory_NotWritable_FallsBackToDefault()
    {
        // Một file chắn đúng chỗ thư mục cần tạo → không tạo được thư mục log.
        var blocker = Path.Combine(_temp.Path, "blocker");
        File.WriteAllText(blocker, "x");

        var directory = new LoggingOptions { Directory = Path.Combine(blocker, "logs") }.ResolveDirectory(DataRoot, DefaultLogs, out var problem);

        Assert.Equal(DefaultLogs, directory);
        Assert.Contains("mặc định", problem);
    }

    [Fact]
    public void FileLogger_WritesIntoConfiguredDirectory()
    {
        var target = new LoggingOptions { Directory = Path.Combine(_temp.Path, "custom") }.ResolveDirectory(DataRoot, DefaultLogs, out _);

        using (var provider = new FileLoggerProvider(target, LogLevel.Information, TimeProvider.System))
        {
            provider.CreateLogger("Test").LogInformation("xin chào");
        }

        var file = Assert.Single(Directory.GetFiles(target, "photobooth-*.log"));
        Assert.Contains("xin chào", File.ReadAllText(file));
    }

    // ---------------------------------------------------------------- Bộ đệm log khởi động

    [Fact]
    public void StartupLogBuffer_ReplaysEntriesInOrder_ThenClears()
    {
        var buffer = new StartupLogBuffer();
        buffer.LogWarning("Thứ nhất {Value}", 1);
        buffer.LogInformation("Thứ hai");
        var target = new RecordingLogger();

        buffer.ReplayTo(target);
        buffer.ReplayTo(target);

        Assert.Equal([(LogLevel.Warning, "Thứ nhất 1"), (LogLevel.Information, "Thứ hai")], target.Entries);
    }

    private string WriteSettings(string json)
    {
        var file = Path.Combine(_temp.Path, "appsettings.json");
        File.WriteAllText(file, json);
        return file;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel, string)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
