using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WG.AP.Invoice.AI;
using WG.AP.Invoice.Models;

namespace WG.AP.Tests.Invoice;

public class PdfInvoiceFieldExtractorTests
{
    // Points at an address nothing listens on, so any call to Ollama fails loudly with a connection
    // refusal rather than silently succeeding against something real.
    private static PdfInvoiceFieldExtractor CreateExtractorThatCannotReachOllama()
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        var options = Options.Create(new OllamaOptions { BaseUrl = "http://127.0.0.1:1", Model = "qwen3:14b" });
        var ollamaClient = new OllamaClient(httpClient, options, NullLogger<OllamaClient>.Instance);

        return new PdfInvoiceFieldExtractor(ollamaClient, NullLogger<PdfInvoiceFieldExtractor>.Instance);
    }

    private static ExtractionRequest SanmarRequest() =>
        new(
            ExtractionRequest.SanmarPdfHeaderExtractorKey,
            SeededPrompt.Template,
            SeededPrompt.ResponseSchemaJson,
            ModelName: null,
            ExtractionPromptId: 1);

    /// <summary>
    /// A minimal but genuinely valid PDF carrying selectable text, built at test time.
    /// </summary>
    /// <remarks>
    /// Generated rather than committed because the real invoices are client documents that deliberately
    /// stay out of the repository (see <c>FixturePaths.ResolveRealInvoiceDirectory</c>), and the tests
    /// below have to get <em>past</em> <c>PdfDocument.Open</c> to reach the branch they are about. The
    /// text resembles no client's layout, which is the point — the deterministic extractor must not
    /// match it.
    /// </remarks>
    private static byte[] BuildPdfWithText(string text = "Some document text matching no known invoice layout.")
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        page.AddText(text, 12, new PdfPoint(50, 700), font);

        return builder.Build();
    }

    [Fact]
    public async Task ExtractAsync_WhenBytesAreNotAValidPdf_ThrowsRatherThanCallingOllama()
    {
        var extractor = CreateExtractorThatCannotReachOllama();
        var notAPdf = Encoding.UTF8.GetBytes("this is not a pdf file");

        await Assert.ThrowsAnyAsync<Exception>(() => extractor.ExtractAsync(notAPdf, SanmarRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_WithNoConfiguredPrompt_SaysSoRatherThanBlamingTheDocument()
    {
        // A format with no active prompt is a configuration gap, and it has to be distinguishable from
        // an unreadable PDF — otherwise the operator spends the morning looking at a perfectly good
        // invoice. So the document here is deliberately valid and readable: the only thing wrong with
        // this extraction is the missing prompt, and the message has to say so and name where to fix it.
        var extractor = CreateExtractorThatCannotReachOllama();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync(BuildPdfWithText(), ExtractionRequest.None, CancellationToken.None));

        Assert.Contains("No active extraction prompt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dbo.ExtractionPrompt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOllamaDocumentText_IncludesBothAuditTextAndNaturalOrderText()
    {
        // ContentOrderTextExtractor's Y-position reconstruction (auditText) can scramble a label/value
        // block that content-stream order (naturalOrderText) gets right - see the method's own remarks.
        // Regression coverage: this must not silently regress back to auditText alone, which is what
        // caused a real CustomerPO to be reported missing despite being present on the document.
        const string auditText = "Customer PO:\nOrder Account: 76274-0000";
        const string naturalOrderText = "2513-2494 Customer PO: Order Account: 76274-0000";

        var combined = PdfInvoiceFieldExtractor.BuildOllamaDocumentText(auditText, naturalOrderText);

        Assert.Contains(auditText, combined, StringComparison.Ordinal);
        Assert.Contains(naturalOrderText, combined, StringComparison.Ordinal);
        Assert.True(combined.IndexOf(auditText, StringComparison.Ordinal)
                    < combined.IndexOf(naturalOrderText, StringComparison.Ordinal),
            "auditText should remain the primary (first) view, with naturalOrderText appended as a supplementary copy.");
    }

    [Fact]
    public async Task ExtractAsync_WithAPromptConfigured_ReachesOllamaRatherThanReportingAConfigurationGap()
    {
        // The counterpart that makes the test above mean something: with a prompt present, the same
        // unrecognised document gets as far as the model, so the configuration-gap message is a real
        // branch rather than the only thing this extractor ever says about an unfamiliar layout.
        var extractor = CreateExtractorThatCannotReachOllama();

        var request = new ExtractionRequest(
            ExtractorKey: null,
            SeededPrompt.Template,
            SeededPrompt.ResponseSchemaJson,
            ModelName: null,
            ExtractionPromptId: 1);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => extractor.ExtractAsync(BuildPdfWithText(), request, CancellationToken.None));

        Assert.DoesNotContain("No active extraction prompt", exception.Message, StringComparison.Ordinal);
    }
}
