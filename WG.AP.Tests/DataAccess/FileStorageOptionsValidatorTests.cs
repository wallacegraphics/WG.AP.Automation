using WG.AP.DataAccess;

namespace WG.AP.Tests.DataAccess;

/// <summary>
/// The point of this validator is that it fails at startup rather than mid-run, so the cases worth
/// pinning are the ones a plain "is the string empty" check waved through: a path that does not
/// exist, and one that exists but cannot be written to.
/// </summary>
public class FileStorageOptionsValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));

    private static ValidateOptionsResultAssertion Validate(string rootDirectory) =>
        new(new FileStorageOptionsValidator().Validate(null, new FileStorageOptions { RootDirectory = rootDirectory }));

    [Fact]
    public void Succeeds_WhenTheRootExistsAndIsWritable()
    {
        Directory.CreateDirectory(_root);

        Validate(_root).ShouldSucceed();
    }

    [Fact]
    public void Fails_WhenTheRootDoesNotExist()
    {
        // Not created - this is the mistyped-path and share-not-provisioned-yet case.
        Validate(_root).ShouldFailContaining("does not exist or is unreachable");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenTheRootIsBlank(string rootDirectory) =>
        Validate(rootDirectory).ShouldFailContaining("is required");

    [Fact]
    public void LeavesNoProbeFileBehind_WhenTheRootIsWritable()
    {
        Directory.CreateDirectory(_root);

        Validate(_root).ShouldSucceed();

        // The write check creates a temp file and deletes it. If that delete ever regresses, every run
        // of the processor would drop another file into the attachment share.
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Fails_WhenTheRootIsAFileRatherThanADirectory()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a directory");

        Validate(_root).ShouldFailContaining("does not exist or is unreachable");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            else if (File.Exists(_root))
            {
                File.Delete(_root);
            }
        }
        catch
        {
            // Test cleanup only.
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Small readability wrapper so each test reads as one assertion.</summary>
internal sealed class ValidateOptionsResultAssertion(Microsoft.Extensions.Options.ValidateOptionsResult result)
{
    public void ShouldSucceed() =>
        Assert.True(result.Succeeded, $"Expected success but got: {result.FailureMessage}");

    public void ShouldFailContaining(string expectedFragment)
    {
        Assert.True(result.Failed, "Expected validation to fail, but it succeeded.");
        Assert.Contains(expectedFragment, result.FailureMessage ?? string.Empty, StringComparison.Ordinal);
    }
}
