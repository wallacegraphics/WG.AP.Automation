namespace WG.AP.Tests.Invoice;

/// <summary>
/// Guards <see cref="FixturePaths"/> against resolving relative to the caller.
/// </summary>
/// <remarks>
/// This file lives under <c>Invoice/</c> on purpose. The bug it covers only appeared when the helper
/// was called from a subfolder — <c>[CallerFilePath]</c> is filled in at the call site, so a caller in
/// <c>Invoice/</c> got <c>Invoice/Invoice/Fixtures.local</c> — which meant the one test that checks
/// extraction against the client's own numbers silently found no fixtures and reported a pass. A test
/// sitting next to <c>FixturePaths.cs</c> would have resolved the right path and proved nothing.
/// </remarks>
public class FixturePathsTests
{
    [Fact]
    public void ResolveRealInvoiceDirectory_PointsAtTheDocumentedLocation_WhenCalledFromASubfolder()
    {
        var resolved = FixturePaths.ResolveRealInvoiceDirectory();

        Assert.Equal(
            Path.Combine("WG.AP.Tests", "Invoice", "Fixtures.local"),
            Path.Combine(
                Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(resolved))!)!,
                Path.GetFileName(Path.GetDirectoryName(resolved))!,
                Path.GetFileName(resolved)));
    }

    [Fact]
    public void RepositoryRoot_IsTheRepositoryRoot_WhenCalledFromASubfolder()
    {
        // Asserted by a landmark rather than by name, so renaming the checkout directory cannot make
        // this pass for the wrong reason.
        Assert.True(
            Directory.Exists(Path.Combine(FixturePaths.RepositoryRoot(), "WG.AP.Database")),
            $"Expected the repository root to contain WG.AP.Database, got {FixturePaths.RepositoryRoot()}.");
    }

    [Fact]
    public void ExtractionPromptSeedScript_ResolvesToTheRealSeedFile()
    {
        Assert.True(
            File.Exists(FixturePaths.ExtractionPromptSeedScript()),
            $"Expected the prompt seed script at {FixturePaths.ExtractionPromptSeedScript()}.");
    }
}
