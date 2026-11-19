using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Photobooth.Infrastructure.Logging;

/// <summary>
/// Logger ghi file văn bản theo ngày (Logs/photobooth-yyyyMMdd.log), không cần thư viện ngoài.
/// Lỗi ghi log không bao giờ làm dừng ứng dụng.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disabled;
    private bool _disposed;

    public FileLoggerProvider(string logsDirectory, LogLevel minimumLevel, TimeProvider timeProvider)
    {
        _logsDirectory = logsDirectory;
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    internal void Write(LogLevel logLevel, string category, string message, Exception? exception)
    {
        var now = _timeProvider.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [").Append(ToShortName(logLevel)).Append("] ")
            .Append(category).Append(": ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            if (_disabled || _disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(DateOnly.FromDateTime(now.DateTime));
                _writer!.WriteLine(line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Không ghi được log ra file: tắt file logger thay vì làm hỏng luồng chính.
                _disabled = true;
                Debug.WriteLine($"FileLoggerProvider bị vô hiệu hóa: {ex.Message}");
            }
        }
    }

    private void EnsureWriter(DateOnly date)
    {
        if (_writer is not null && _writerDate == date)
        {
            return;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(_logsDirectory);
        var path = Path.Combine(_logsDirectory, $"photobooth-{date:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _writerDate = date;
    }

    private static string ToShortName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

