using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Invoice.AI;

namespace WG.AP.Tests.Invoice;

public class PdfInvoiceFieldExtractorTests
{
    [Fact]
    public async Task ExtractAsync_WhenBytesAreNotAValidPdf_ThrowsRatherThanCallingOllama()
    {
        // Ollama is never expected to be called here — point at an address nothing listens on, so a
        // stray call would fail loudly (connection refused) rather than silently succeeding.
        var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        var options = Options.Create(new OllamaOptions { BaseUrl = "http://127.0.0.1:1", Model = "qwen3:14b" });
        var ollamaClient = new OllamaClient(httpClient, options, NullLogger<OllamaClient>.Instance);
        var extractor = new PdfInvoiceFieldExtractor(ollamaClient, NullLogger<PdfInvoiceFieldExtractor>.Instance);

        var notAPdf = Encoding.UTF8.GetBytes("this is not a pdf file");

        await Assert.ThrowsAnyAsync<Exception>(() => extractor.ExtractAsync(notAPdf, CancellationToken.None));
    }
}
