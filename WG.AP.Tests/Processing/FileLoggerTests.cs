using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Processor.Logging;

namespace WG.AP.Tests.Processing;

public class FileLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));

    private string ExpectedLogFilePath => Path.Combine(_directory, $"WG.AP.Processing-{DateTime.Now:yyyy-MM-dd}.log");

    private FileLoggerProvider CreateProvider(LogLevel minLevel = LogLevel.Information) =>
        new(Options.Create(new FileLoggerOptions { Directory = _directory, MinLevel = minLevel }));

    [Fact]
    public void LogInformation_AppendsALineToTodaysLogFile()
    {
        var provider = CreateProvider();
        var logger = provider.CreateLogger("Test.Category");

        logger.LogInformation("Something happened: {Detail}.", "detail-value");

        var contents = File.ReadAllText(ExpectedLogFilePath);
        Assert.Contains("Test.Category", contents);
        Assert.Contains("Something happened: detail-value.", contents);
        Assert.Contains("[Information]", contents);
    }

    [Fact]
    public void LogError_IncludesTheExceptionDetails()
    {
        var provider = CreateProvider();
        var logger = provider.CreateLogger("Test.Category");

        logger.LogError(new InvalidOperationException("boom"), "Failed while doing {Thing}.", "the thing");

        var contents = File.ReadAllText(ExpectedLogFilePath);
        Assert.Contains("[Error]", contents);
        Assert.Contains("Failed while doing the thing.", contents);
        Assert.Contains("InvalidOperationException", contents);
        Assert.Contains("boom", contents);
    }

    [Fact]
    public void LogInformation_IsSuppressed_WhenMinLevelIsHigherThanError()
    {
        var provider = CreateProvider(minLevel: LogLevel.Error);
        var logger = provider.CreateLogger("Test.Category");

        logger.LogInformation("This should not be written.");

        Assert.False(File.Exists(ExpectedLogFilePath));
    }

    [Fact]
    public void Log_DoesNotThrow_WhenTheDirectoryCannotBeCreated()
    {
        // A file path used as a "directory" can never be created as one, simulating an
        // unwritable/invalid configured location without needing OS-level permission tricks.
        var blockingFilePath = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", $"{Guid.NewGuid():N}-blocker");
        Directory.CreateDirectory(Path.GetDirectoryName(blockingFilePath)!);
        File.WriteAllText(blockingFilePath, "not a directory");

        try
        {
            var provider = new FileLoggerProvider(Options.Create(new FileLoggerOptions { Directory = blockingFilePath }));
            var logger = provider.CreateLogger("Test.Category");

            var exception = Record.Exception(() => logger.LogInformation("This write should fail silently."));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blockingFilePath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
