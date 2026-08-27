using Microsoft.Extensions.Logging;

namespace WG.AP.Processor.Logging;

public sealed class FileLoggerOptions
{
    public const string SectionName = "FileLogging";

    public string Directory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WG.AP.Automation",
        "Logs");

    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    public int LogFilesRetentionDays { get; init; }
}
