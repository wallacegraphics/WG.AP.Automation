using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WG.AP.Processor.Logging;

[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly IOptions<FileLoggerOptions> _options;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _writeLock = new();

    public FileLoggerProvider(IOptions<FileLoggerOptions> options)
    {
        _options = options;
        PurgeOldLogs();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _options.Value, _writeLock));

    public void Dispose() => _loggers.Clear();

    private void PurgeOldLogs()
    {
        var retentionDays = _options.Value.LogFilesRetentionDays;
        if (retentionDays <= 0)
        {
            return;
        }

        var directory = _options.Value.Directory;

        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTimeOffset.Now - TimeSpan.FromDays(retentionDays);

            foreach (var file in Directory.EnumerateFiles(directory, "WG.AP.Processing-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Swallow: one bad file must not stop the rest of the purge.
                }
            }
        }
        catch
        {
            // Swallow: cleanup failing must never block startup or take down mailbox processing.
        }
    }
}
