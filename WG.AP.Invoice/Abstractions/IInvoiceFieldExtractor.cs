using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Abstractions;

/// <summary>
/// Extracts invoice fields from a single PDF attachment. A PDF with no extractable text, or a
/// response that can't be parsed into <see cref="InvoiceFields"/>, is a "suspicious PDF" — the
/// implementation logs and rethrows rather than returning a partial/empty result.
/// </summary>
public interface IInvoiceFieldExtractor
{
    Task<InvoiceFields> ExtractAsync(byte[] pdfBytes, CancellationToken cancellationToken);
}
