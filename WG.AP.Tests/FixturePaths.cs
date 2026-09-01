using System.Runtime.CompilerServices;

namespace WG.AP.Tests;

/// <summary>
/// Resolves paths inside the repository from the compiled test's own source location.
/// </summary>
/// <remarks>
/// Uses <see cref="CallerFilePathAttribute"/> rather than the test binary's directory, because the
/// binary sits under <c>bin/Debug/net10.0</c> and the number of levels back to the repository root is
/// a build-configuration detail that would silently break.
/// </remarks>
internal static class FixturePaths
{
    /// <summary>The repository root.</summary>
    internal static string RepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".."));

    /// <summary>
    /// Where real invoice PDFs and their manifest live for the opt-in local integration test. Never
    /// committed — these are real client documents.
    /// </summary>
    internal static string ResolveRealInvoiceDirectory([CallerFilePath] string sourceFilePath = "")
    {
        var envOverride = Environment.GetEnvironmentVariable("AP_REAL_INVOICE_FIXTURES_DIR");

        return string.IsNullOrWhiteSpace(envOverride)
            ? Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Invoice", "Fixtures.local")
            : envOverride;
    }

    /// <summary>The seed script that is the master copy of the Ollama prompt.</summary>
    internal static string ExtractionPromptSeedScript() =>
        Path.Combine(RepositoryRoot(), "WG.AP.Database", "Scripts", "Seed", "ExtractionPrompt.sql");
}
