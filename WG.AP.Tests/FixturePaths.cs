using System.Runtime.CompilerServices;

namespace WG.AP.Tests;

/// <summary>
/// Resolves paths inside the repository from this file's own location.
/// </summary>
/// <remarks>
/// Uses <see cref="CallerFilePathAttribute"/> rather than the test binary's directory, because the
/// binary sits under <c>bin/Debug/net10.0</c> and the number of levels back to the repository root is
/// a build-configuration detail that would silently break.
/// <para>
/// The caller path is captured once, here, rather than through a public parameter defaulting to
/// <see cref="CallerFilePathAttribute"/>. That parameter is filled in at the <em>call site</em>, so it
/// silently meant "wherever the calling test happens to live" — which made
/// <see cref="ResolveRealInvoiceDirectory"/> append <c>Invoice/</c> to a caller already inside
/// <c>Invoice/</c>, and would have made <see cref="RepositoryRoot"/> return the test project rather
/// than the repository root for the same caller. Both now resolve identically from anywhere.
/// </para>
/// </remarks>
internal static class FixturePaths
{
    /// <summary>The <c>WG.AP.Tests</c> project directory — this file's own.</summary>
    private static readonly string TestProjectDirectory = ResolveTestProjectDirectory();

    // Called from inside this file, so the injected path is always FixturePaths.cs.
    private static string ResolveTestProjectDirectory([CallerFilePath] string sourceFilePath = "") =>
        Path.GetDirectoryName(sourceFilePath)!;

    /// <summary>The repository root.</summary>
    internal static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(TestProjectDirectory, ".."));

    /// <summary>
    /// Where real invoice PDFs and their manifest live for the opt-in local integration test. Never
    /// committed — these are real client documents.
    /// </summary>
    internal static string ResolveRealInvoiceDirectory()
    {
        var envOverride = Environment.GetEnvironmentVariable("AP_REAL_INVOICE_FIXTURES_DIR");

        return string.IsNullOrWhiteSpace(envOverride)
            ? Path.Combine(TestProjectDirectory, "Invoice", "Fixtures.local")
            : envOverride;
    }

    /// <summary>The seed script that is the master copy of the Ollama prompt.</summary>
    internal static string ExtractionPromptSeedScript() =>
        Path.Combine(RepositoryRoot(), "WG.AP.Database", "Scripts", "Seed", "ExtractionPrompt.sql");
}
