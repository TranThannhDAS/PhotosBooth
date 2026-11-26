using Microsoft.Extensions.Logging;

namespace Photobooth.Infrastructure.Logging;

/// <summary>
/// Giữ tạm log phát sinh trước khi logger thật được dựng (ví dụ khi đọc appsettings.json, vốn quyết định nơi ghi log),
/// rồi ghi lại vào logger thật bằng <see cref="ReplayTo"/>.
/// </summary>
public sealed class StartupLogBuffer : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    public void ReplayTo(ILogger logger)
    {
        lock (_gate)
        {
            foreach (var (level, message, exception) in _entries)
            {
#pragma warning disable CA2254 // Thông điệp đã được định dạng sẵn khi ghi vào bộ đệm.
                logger.Log(level, exception, "{Message}", message);
#pragma warning restore CA2254
            }

            _entries.Clear();
        }
    }
}
