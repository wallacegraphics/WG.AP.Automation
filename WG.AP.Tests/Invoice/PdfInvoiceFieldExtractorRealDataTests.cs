using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Invoice.AI;
using WG.AP.Invoice.Models;
using Xunit.Abstractions;

namespace WG.AP.Tests.Invoice;

// Opt-in local integration test: it needs real invoice PDFs plus their SanMar Excel manifest (never
// committed) and a locally running Ollama server. With no fixtures present it reports as Skipped, so
// it is inert on CI. With fixtures present but Ollama unreachable it fails rather than skipping -
// deliberately, because at that point the only thing missing is a service the developer meant to have
// running. See WG.AP.Tests/Invoice/Fixtures.local/ (gitignored) for how to add fixtures.
//
// Skipped rather than an early return, for the same reason SqlRepositoryTests uses Skip.If: an early
// return reports as Passed, so "no fixtures, nothing ran" looked exactly like "verified against the
// client's own numbers". That mattered here more than anywhere - a path bug in FixturePaths had this
// test finding no fixtures and passing, which is the one failure mode a real-data test must not have.
//
// Verification is against the real manifest - not hand-written expected-value files - so it
// automatically covers whichever PDF/manifest-row pairs happen to be present, and it checks the
// extractor against the client's own numbers rather than against our idea of them. That is why
// TestManifestReader exists at all: production no longer reads Excel, but removing this test's oracle
// at the same time as the production cross-check would have left extraction with no real-data check.
public class PdfInvoiceFieldExtractorRealDataTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task ExtractAsync_OnRealInvoicePdfs_MatchesTheExcelManifest()
    {
        var fixturesDir = FixturePaths.ResolveRealInvoiceDirectory();

        Skip.IfNot(
            Directory.Exists(fixturesDir),
            $"Fixtures directory not found at {fixturesDir}. Drop real invoice PDFs and a manifest there to run this test.");

        var manifestRows = Directory.GetFiles(fixturesDir, "*.xlsx")
            .SelectMany(path => TestManifestReader.ReadManifest(File.ReadAllBytes(path)))
            .GroupBy(row => row.Voucher, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // The join key is SanMar's convention: Excel "Voucher" == PDF filename == the invoice number
        // printed on the PDF. Matching directly here rather than through a reconciliation step, since
        // discrepancies are not what this test is about.
        var pairs = Directory.GetFiles(fixturesDir, "*.pdf")
            .Select(path => (Path: path, Voucher: Path.GetFileNameWithoutExtension(path)))
            .Where(pdf => manifestRows.ContainsKey(pdf.Voucher))
            .ToList();

        Skip.If(
            pairs.Count == 0,
            "No manifest row matched a local PDF by voucher/filename - nothing to run. The join key is the "
            + "PDF filename matching the Excel 'Voucher' column.");

        var (baseUrl, model) = ResolveOllamaConnection();
        var totalElapsed = TimeSpan.Zero;

        foreach (var (path, voucher) in pairs)
        {
            var row = manifestRows[voucher];
            var fileName = Path.GetFileName(path);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var actual = await ExtractAsync(File.ReadAllBytes(path), baseUrl, model);
                var mismatches = TestManifestReader.Compare(row, actual);

                foreach (var mismatch in mismatches)
                {
                    output.WriteLine($"{fileName}: {mismatch.FieldName} - manifest '{mismatch.ManifestValue}' vs PDF '{mismatch.PdfValue}'.");
                }

                Assert.True(mismatches.Count == 0, $"{fileName}: extracted fields did not match the manifest row for voucher {voucher}.");

                Assert.False(string.IsNullOrWhiteSpace(actual.RawText), $"{fileName}: RawText was not populated.");
                Assert.Contains(voucher, actual.RawText, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                stopwatch.Stop();
                output.WriteLine($"{fileName}: {stopwatch.Elapsed.TotalSeconds:F1}s");
                totalElapsed += stopwatch.Elapsed;
            }
        }

        output.WriteLine($"Ran {pairs.Count} PDF(s) in {totalElapsed.TotalSeconds:F1}s (avg {totalElapsed.TotalSeconds / pairs.Count:F1}s/PDF)");
    }

    private static async Task<InvoiceFields> ExtractAsync(byte[] pdfBytes, string baseUrl, string model)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var options = Options.Create(new OllamaOptions { BaseUrl = baseUrl, Model = model });
        var ollamaClient = new OllamaClient(httpClient, options, NullLogger<OllamaClient>.Instance);
        var extractor = new PdfInvoiceFieldExtractor(ollamaClient, NullLogger<PdfInvoiceFieldExtractor>.Instance);

        // The prompt now comes from dbo.ExtractionPrompt at runtime, so the test supplies the seeded
        // one directly. That keeps this test independent of a database while still exercising the same
        // prompt and schema the seed deploys.
        var request = new ExtractionRequest(
            ExtractionRequest.SanmarPdfHeaderExtractorKey,
            SeededPrompt.Template,
            SeededPrompt.ResponseSchemaJson,
            ModelName: null,
            ExtractionPromptId: 1);

        var result = await extractor.ExtractAsync(pdfBytes, request, CancellationToken.None);
        return result.Fields;
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
