using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;
using WG.AP.Invoice.Pdf;

namespace WG.AP.Invoice.AI;

/// <summary>
/// Extracts invoice fields from a PDF's selectable text via a local Ollama model. PDFs in scope
/// here (SanMar and similar vendor invoices) carry selectable text, not scanned images, so no OCR
/// step is involved.
/// </summary>
public sealed class PdfInvoiceFieldExtractor(OllamaClient ollamaClient, ILogger<PdfInvoiceFieldExtractor> logger) : IInvoiceFieldExtractor
{
    private static readonly object ResponseFormat = new
    {
        type = "object",
        properties = new
        {
            InvoiceNumber = new { type = "string" },
            SalesOrder = new { type = "string" },
            InvoiceDate = new { type = "string" },
            DueDate = new { type = "string" },
            Total = new { type = "number" },
            VendorName = new { type = "string" },
            CustomerPO = new { type = "string" },
            CustomerNumber = new { type = "string" },
            OrderAccount = new { type = "string" },
            Terms = new { type = "string" }
        },
        required = new[] { "InvoiceNumber", "Total" }
    };

    public async Task<InvoiceFields> ExtractAsync(byte[] pdfBytes, CancellationToken cancellationToken)
    {
        try
        {
            var (naturalOrderText, auditText) = ExtractPdfText(pdfBytes);

            if (string.IsNullOrWhiteSpace(auditText))
            {
                throw new InvalidOperationException("PDF contains no extractable text.");
            }

            var deterministic = SanmarPdfHeaderExtractor.TryExtract(naturalOrderText);

            if (deterministic is not null)
            {
                logger.LogInformation("Extracted invoice fields deterministically from a recognized SanMar header layout, without calling Ollama.");
                return deterministic with { RawText = auditText };
            }

            logger.LogInformation("PDF header layout not recognized for deterministic extraction; falling back to Ollama.");

            var rawResponse = await ollamaClient.GenerateAsync(BuildPrompt(auditText), ResponseFormat, cancellationToken);
            var fields = InvoiceFieldsJsonParser.Parse(rawResponse);

            return fields with { RawText = auditText };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to extract invoice fields from a PDF attachment.");
            throw;
        }
    }

    private static (string NaturalOrderText, string AuditText) ExtractPdfText(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);
        var pages = document.GetPages().ToList();

        // Content-stream draw order (no line/row reconstruction) - on SanMar's invoice template,
        // labels and values print as two back-to-back runs in this order, which SanmarPdfHeaderExtractor
        // relies on. This is NOT the same order as the reading-order text built below.
        var naturalOrderText = string.Join(" ", pages.SelectMany(page => page.GetWords()).Select(word => word.Text));

        // Page.Text concatenates letters in content-stream order with no inserted whitespace, so
        // invoices that position text via glyph spacing rather than literal space characters come
        // out as unreadable run-on words (e.g. "InvoiceDate:7/10/2026DueDate:9/8/2026"). Reading
        // order + gap-based word/line reconstruction keeps labels and values separated and on their
        // own lines, which the model needs to reliably locate a given field. Used as the Ollama
        // fallback's prompt input and as the persisted RawText audit field either way.
        var auditText = string.Join(Environment.NewLine, pages.Select(page => ContentOrderTextExtractor.GetText(page, false)));

        return (naturalOrderText, auditText);
    }

    private static string BuildPrompt(string documentText) => $$"""
        Extract fields from this invoice text.

        Return only valid JSON with exactly these keys:
        {
          "InvoiceNumber": "",
          "SalesOrder": "",
          "InvoiceDate": "",
          "DueDate": "",
          "Total": 0,
          "VendorName": "",
          "CustomerPO": "",
          "CustomerNumber": "",
          "OrderAccount": "",
          "Terms": ""
        }

        Rules:
        - InvoiceNumber is the invoice/voucher number the vendor assigned (example: INV-162393962 or CR-005662167).
        - VendorName is the supplier/company shown in the logo/header.
        - Total is the invoice's printed total amount due (may be labeled "Total", "Total Due", or "Subtotal amount").
        - CustomerPO is the customer's purchase order reference (may be labeled "Customer PO", "PO Number", or "Customer Purchase Order").
        - CustomerNumber is the vendor-assigned customer account identifier (may be labeled "Customer Number", "Customer Account", or "Account #").
        - OrderAccount is the account the specific order was placed under (may be labeled "Order Account" or "Account Number"); it is often the same value as CustomerNumber.
        - Terms is the payment terms printed on the invoice (may be labeled "Terms" or "Terms of Payment"), e.g. "Net60".
        - Match fields by meaning, not by exact label text - vendors phrase these labels differently and that phrasing isn't controlled.
        - Dates should be returned exactly as printed on the document.
        - If a field is missing, keep empty string, and 0 for Total.
        - Total must be numeric only.

        Document:
        {{documentText}}
        """;
}
