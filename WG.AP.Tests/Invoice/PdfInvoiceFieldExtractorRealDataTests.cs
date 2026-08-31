using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.Invoice.AI;
using WG.AP.Invoice.Excel;
using WG.AP.Invoice.Models;
using Xunit.Abstractions;

namespace WG.AP.Tests.Invoice;

// Opt-in local integration test: it needs real invoice PDFs plus their SanMar Excel manifest (never
// committed) and a locally running Ollama server, so it no-ops on CI and on any machine without
// both. See WG.AP.Tests/Invoice/Fixtures.local/ (gitignored) for how to add fixtures. Verification
// is against the real manifest via SanmarManifestVerifier - not hand-written expected-value files -
// so it automatically covers whichever PDF/manifest-row pairs happen to be present.
public class PdfInvoiceFieldExtractorRealDataTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ExtractAsync_OnRealInvoicePdfs_MatchesTheExcelManifest()
    {
        var fixturesDir = ResolveFixturesDirectory();

        if (!Directory.Exists(fixturesDir))
        {
            output.WriteLine($"Skipping: fixtures directory not found at {fixturesDir}. Drop real invoice PDFs and a manifest there to run this test.");
            return;
        }

        var verifier = new SanmarManifestVerifier(NullLogger<SanmarManifestVerifier>.Instance);

        var manifestRows = Directory.GetFiles(fixturesDir, "*.xlsx")
            .SelectMany(path => verifier.ReadManifest(File.ReadAllBytes(path)))
            .ToList();

        var pdfAttachments = Directory.GetFiles(fixturesDir, "*.pdf")
            .Select(path => new MailAttachmentSummary(path, Path.GetFileName(path), new FileInfo(path).Length, "application/pdf"))
            .ToList();

        var reconciliation = verifier.Reconcile(manifestRows, pdfAttachments);

        if (reconciliation.MatchedPairs.Count == 0)
        {
            output.WriteLine("No manifest row matched a local PDF by voucher/filename - nothing was run.");
            return;
        }

        var (baseUrl, model) = ResolveOllamaConnection();
        var totalElapsed = TimeSpan.Zero;

        foreach (var pair in reconciliation.MatchedPairs)
        {
            var pdfPath = Directory.GetFiles(fixturesDir, "*.pdf")
                .First(path => Path.GetFileName(path) == pair.AttachmentName);
            var pdfBytes = File.ReadAllBytes(pdfPath);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var actual = await ExtractAsync(pdfBytes, baseUrl, model);

                var comparison = verifier.CompareFields(pair.Row, actual);

                if (!comparison.IsMatch)
                {
                    foreach (var mismatch in comparison.Mismatches)
                    {
                        output.WriteLine($"{pair.AttachmentName}: {mismatch.FieldName} - manifest '{mismatch.ExcelValue}' vs PDF '{mismatch.PdfValue}'.");
                    }
                }

                Assert.True(comparison.IsMatch, $"{pair.AttachmentName}: extracted fields did not match the manifest row for voucher {pair.Voucher}.");

                Assert.False(string.IsNullOrWhiteSpace(actual.RawText), $"{pair.AttachmentName}: RawText was not populated.");
                Assert.Contains(pair.Voucher, actual.RawText, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                stopwatch.Stop();
                output.WriteLine($"{pair.AttachmentName}: {stopwatch.Elapsed.TotalSeconds:F1}s");
                totalElapsed += stopwatch.Elapsed;
            }
        }

        output.WriteLine($"Ran {reconciliation.MatchedPairs.Count} PDF(s) in {totalElapsed.TotalSeconds:F1}s (avg {totalElapsed.TotalSeconds / reconciliation.MatchedPairs.Count:F1}s/PDF)");
    }

    private static async Task<InvoiceFields> ExtractAsync(byte[] pdfBytes, string baseUrl, string model)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var options = Options.Create(new OllamaOptions { BaseUrl = baseUrl, Model = model });
        var ollamaClient = new OllamaClient(httpClient, options, NullLogger<OllamaClient>.Instance);
        var extractor = new PdfInvoiceFieldExtractor(ollamaClient, NullLogger<PdfInvoiceFieldExtractor>.Instance);

        return await extractor.ExtractAsync(pdfBytes, CancellationToken.None);
    }

    private static string ResolveFixturesDirectory([CallerFilePath] string sourceFilePath = "")
    {
        var envOverride = Environment.GetEnvironmentVariable("AP_REAL_INVOICE_FIXTURES_DIR");
        return string.IsNullOrWhiteSpace(envOverride)
            ? Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Fixtures.local")
            : envOverride;
    }

    private static (string BaseUrl, string Model) ResolveOllamaConnection()
    {
        var baseUrl = Environment.GetEnvironmentVariable("AP_OLLAMA_BASE_URL");
        var model = Environment.GetEnvironmentVariable("AP_OLLAMA_MODEL");
        return (
            string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl,
            string.IsNullOrWhiteSpace(model) ? "qwen3:14b" : model);
    }
}
