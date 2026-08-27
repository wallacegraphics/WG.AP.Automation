using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WG.AP.Processor.Logging;

[ProviderAlias("File")]
public sealed class FileLoggerProvider(IOptions<FileLoggerOptions> options) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _writeLock = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, options.Value, _writeLock));

    public void Dispose() => _loggers.Clear();
}
