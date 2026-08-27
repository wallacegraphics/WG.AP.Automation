using Microsoft.Extensions.Logging;

namespace WG.AP.Processor.Logging;

/// <summary>
/// Appends log lines to a daily text file. A failure to write here (disk full, permission denied,
/// bad path) must never propagate into application code — logging infrastructure breaking must
/// not take down mailbox processing — so all file I/O is swallowed.
/// </summary>
internal sealed class FileLogger(string categoryName, FileLoggerOptions options, object writeLock) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= options.MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {categoryName}: {formatter(state, exception)}";

        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        try
        {
            Directory.CreateDirectory(options.Directory);
            var path = Path.Combine(options.Directory, $"WG.AP.Processing-{now:yyyy-MM-dd}.log");

            lock (writeLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Swallow: the log file itself must never be a new source of failure.
        }
    }
}
