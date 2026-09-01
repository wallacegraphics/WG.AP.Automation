using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    // an unreadable PDF - otherwise the operator spends the morning looking at a perfectly good
    // invoice. Not a valid PDF either, so the message is what is being asserted, not the path.
    var extractor = CreateExtractorThatCannotReachOllama();
    var notAPdf = Encoding.UTF8.GetBytes("this is not a pdf file");
    var request = ExtractionRequest.None;

    var exception = await Assert.ThrowsAnyAsync<Exception>(() => extractor.ExtractAsync(notAPdf, request, CancellationToken.None));

    Assert.Contains("No active extraction prompt", exception.Message, StringComparison.OrdinalIgnoreCase);
}
