using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Abstractions;

/// <summary>
/// Extracts invoice fields from a single PDF attachment. A PDF with no extractable text, or a
/// response that can't be parsed into <see cref="InvoiceFields"/>, is a "suspicious PDF" — the
/// implementation logs and rethrows rather than returning a partial/empty result.
/// </summary>
/// <remarks>
/// Implementations must let <see cref="HttpRequestException"/> and <see cref="TaskCanceledException"/>
/// propagate. Those mean the model is unreachable rather than the document being bad, and the caller
/// depends on them escaping so that nothing final is committed and the batch is retried on the next
/// run instead of a possibly-good invoice being filed as an error.
/// </remarks>
public interface IInvoiceFieldExtractor
{
    Task<ExtractionResult> ExtractAsync(byte[] pdfBytes, ExtractionRequest request, CancellationToken cancellationToken);
}
