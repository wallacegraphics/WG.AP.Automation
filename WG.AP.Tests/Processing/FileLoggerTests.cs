using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Processor.Logging;

namespace WG.AP.Tests.Processing;

public class FileLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _today = DateTimeOffset.Now.ToString("yyyy-MM-dd");

    private string ExpectedLogFilePath => Path.Combine(_directory, $"WG.AP.Processing-{_today}.log");

    private FileLoggerProvider CreateProvider(LogLevel minLevel = LogLevel.Information, int logFilesRetentionDays = 60) =>
        new(Options.Create(new FileLoggerOptions { Directory = _directory, MinLevel = minLevel, LogFilesRetentionDays = logFilesRetentionDays }));

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

    [Fact]
    public void Construction_DeletesLogFilesOlderThanRetentionDays_AndKeepsRecentOnes()
    {
        Directory.CreateDirectory(_directory);
        var oldFile = Path.Combine(_directory, "WG.AP.Processing-2020-01-01.log");
        var recentFile = Path.Combine(_directory, "WG.AP.Processing-2020-06-01.log");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(recentFile, "recent");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-90));
        File.SetLastWriteTime(recentFile, DateTime.Now.AddDays(-10));

        CreateProvider(logFilesRetentionDays: 60);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public void Construction_DoesNotDeleteOldLogFiles_WhenRetentionDaysIsZero()
    {
        Directory.CreateDirectory(_directory);
        var oldFile = Path.Combine(_directory, "WG.AP.Processing-2020-01-01.log");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-90));

        CreateProvider(logFilesRetentionDays: 0);

        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public void Construction_DoesNotDeleteOldFiles_ThatDoNotMatchTheLogFileNamingPattern()
    {
        Directory.CreateDirectory(_directory);
        var unrelatedFile = Path.Combine(_directory, "some-other-file.txt");
        File.WriteAllText(unrelatedFile, "old");
        File.SetLastWriteTime(unrelatedFile, DateTime.Now.AddDays(-90));

        CreateProvider(logFilesRetentionDays: 60);

        Assert.True(File.Exists(unrelatedFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
