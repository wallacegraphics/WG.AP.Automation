using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.DataAccess;

namespace WG.AP.Processor.Logging;

/// <summary>
/// Buffers log events and flushes them to <c>dbo.ApplicationLog</c>.
/// </summary>
/// <remarks>
/// Follows the same contract as <see cref="FileLoggerProvider"/>: logging must never be able to take
/// down mailbox processing, so every failure here is swallowed. It is additive rather than a
/// replacement — the file log stays, because it is the only sink that still works when the database
/// is what is broken, and the only one that cannot vanish with a rolled-back transaction.
/// <para>
/// Defaults to Warning while the file log stays at Information. That split is deliberate: the file
/// keeps the full narrative for debugging one run, and the database keeps the small set of events
/// worth querying across runs — which also keeps a year of retention comfortable without a purge job.
/// </para>
/// <para>
/// Buffered and flushed rather than written per line, because the process is a single-threaded
/// scheduled task: one round trip per log line would add real time to a run for no benefit. Errors
/// flush immediately, so the events that matter survive a crash that never reaches
/// <see cref="Dispose"/>.
/// </para>
/// </remarks>
[ProviderAlias("Sql")]
public sealed class SqlLoggerProvider : ILoggerProvider
{
    private const int MaxBufferedEntries = 500;

    private readonly ApplicationLogRepository _repository;
    private readonly SqlLoggerOptions _options;
    private readonly ConcurrentDictionary<string, SqlLogger> _loggers = new();
    private readonly ConcurrentQueue<ApplicationLogEntry> _buffer = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public SqlLoggerProvider(ApplicationLogRepository repository, IOptions<SqlLoggerOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SqlLogger(name, this));

    public void Dispose()
    {
        Flush();
        _loggers.Clear();
        _flushLock.Dispose();
    }

    internal bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _options.MinLevel;

    internal void Enqueue(ApplicationLogEntry entry, bool flushNow)
    {
        _buffer.Enqueue(entry);

        // A runaway loop must not turn a log buffer into a memory leak. Dropping the oldest lines is
        // the right trade: the file log still has them, and an OutOfMemoryException would take the run
        // down - which is exactly what a log sink must never do.
        while (_buffer.Count > MaxBufferedEntries && _buffer.TryDequeue(out _))
        {
        }

        if (flushNow || _buffer.Count >= _options.BatchSize)
        {
            Flush();
        }
    }

    private void Flush()
    {
        if (_buffer.IsEmpty)
        {
            return;
        }

        if (!_flushLock.Wait(TimeSpan.FromSeconds(5)))
        {
            return;
        }

        try
        {
            var batch = new List<ApplicationLogEntry>();

            while (_buffer.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return;
            }

            // GetAwaiter().GetResult() rather than async: ILogger.Log is synchronous by contract, and
            // there is no async logging seam to hand this off to.
            _repository.WriteAsync(batch, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Swallow, and do not report through ILogger - that would recurse straight back into here.
            // A broken database sink shows up as an absence of rows plus the failure in the file log.
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private sealed class SqlLogger(string categoryName, SqlLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var entry = new ApplicationLogEntry
                {
                    LoggedOn = DateTime.UtcNow,
                    LogLevel = (byte)logLevel,
                    Category = Truncate(categoryName, 256),
                    Message = formatter(state, exception),
                    Exception = exception?.ToString(),
                    ProcessingRunId = ProcessingRunContext.CurrentRunId,
                    MailMessageId = ProcessingRunContext.CurrentMailMessageId
                };

                provider.Enqueue(entry, flushNow: logLevel >= LogLevel.Error);
            }
            catch
            {
                // Swallow, for the same reason as Flush.
            }
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}

/// <summary>Settings for the database log sink.</summary>
public sealed class SqlLoggerOptions
{
    public const string SectionName = "SqlLogging";

    /// <summary>
    /// Warning by default, while the file log stays at Information — see the note on
    /// <see cref="SqlLoggerProvider"/>.
    /// </summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Warning;

    public int BatchSize { get; init; } = 50;
}

/// <summary>
/// Carries the current run and message ids so log rows can be correlated without every call site
/// having to pass them.
/// </summary>
/// <remarks>
/// These are correlation ids only, which is why <c>dbo.ApplicationLog</c> has no foreign keys on
/// them: an FK would make the log insert fail exactly when the referenced row was rolled back, i.e.
/// exactly when the line mattered.
/// </remarks>
public static class ProcessingRunContext
{
    private static readonly AsyncLocal<long?> RunId = new();
    private static readonly AsyncLocal<long?> MailMessageId = new();

    public static long? CurrentRunId
    {
        get => RunId.Value;
        set => RunId.Value = value;
    }

    public static long? CurrentMailMessageId
    {
        get => MailMessageId.Value;
        set => MailMessageId.Value = value;
    }
}
