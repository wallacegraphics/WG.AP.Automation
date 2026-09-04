using System.Text.Json;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;
using WG.AP.Invoice.Pdf;

namespace WG.AP.Invoice.AI;

/// <summary>
/// Extracts invoice fields from a PDF's selectable text, deterministically where the layout is
/// recognised and via a local Ollama model otherwise. PDFs in scope here carry selectable text, not
/// scanned images, so no OCR step is involved.
/// </summary>
public sealed class PdfInvoiceFieldExtractor(OllamaClient ollamaClient, ILogger<PdfInvoiceFieldExtractor> logger) : IInvoiceFieldExtractor
{
    public async Task<ExtractionResult> ExtractAsync(byte[] pdfBytes, ExtractionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (naturalOrderText, auditText) = ExtractPdfText(pdfBytes);

            if (string.IsNullOrWhiteSpace(auditText))
            {
                throw new InvalidOperationException("PDF contains no extractable text.");
            }

            // Gated on the resolved format rather than tried unconditionally. SanmarPdfHeaderExtractor
            // encodes one client's layout, so running it against another client's invoice risks a
            // silent wrong read - see ExtractionRequest.ExtractorKey.
            if (request.ExtractorKey == ExtractionRequest.SanmarPdfHeaderExtractorKey)
            {
                var deterministic = SanmarPdfHeaderExtractor.TryExtract(naturalOrderText, out var failureReason);

                if (deterministic is not null)
                {
                    logger.LogInformation("Extracted invoice fields deterministically from a recognized SanMar header layout, without calling Ollama.");
                    return new ExtractionResult(deterministic with { RawText = auditText }, ExtractionResult.RegexMethod, ExtractionPromptId: null);
                }

                logger.LogInformation("PDF header layout not recognized for deterministic extraction; falling back to Ollama. Reason: {FailureReason}", failureReason);
            }

            if (request.PromptTemplate is null || request.ResponseSchemaJson is null)
            {
                // Not a bad PDF, a gap in configuration - so it says which gap, rather than surfacing
                // as an opaque parse failure that looks like the document's fault.
                throw new InvalidOperationException(
                    "No active extraction prompt is configured for this document's invoice format, so Ollama cannot be used. "
                    + "Seed dbo.ExtractionPrompt for the format, or check that the client resolved to one.");
            }

            var prompt = BuildPrompt(request.PromptTemplate, BuildOllamaDocumentText(auditText, naturalOrderText));

            // The schema is stored as text and forwarded as parsed JSON, so what reaches Ollama is what
            // was reviewed in the seed script rather than a re-serialisation of it.
            using var responseSchema = JsonDocument.Parse(request.ResponseSchemaJson);

            var rawResponse = await ollamaClient.GenerateAsync(
                prompt,
                responseSchema.RootElement,
                request.ModelName,
                cancellationToken);

            var fields = InvoiceFieldsJsonParser.Parse(rawResponse);

            return new ExtractionResult(
                fields with { RawText = auditText },
                ExtractionResult.OllamaMethod,
                request.ExtractionPromptId);
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

    /// <summary>
    /// Builds the text sent to Ollama as <c>{{DocumentText}}</c>, distinct from <c>auditText</c> alone.
    /// </summary>
    /// <remarks>
    /// <c>auditText</c> (<see cref="ContentOrderTextExtractor"/>'s Y-position line reconstruction) can
    /// scramble a multi-field label/value block on some layouts - confirmed against a real SanMar
    /// invoice where a group of three values printed before their three labels, so a label was
    /// immediately followed by the start of the NEXT field rather than its own value, and Ollama
    /// reported the field missing even though it was printed on the document. <c>naturalOrderText</c>
    /// (content-stream draw order) happens to preserve correct label/value adjacency for that same
    /// group - which is exactly why <see cref="SanmarPdfHeaderExtractor"/> is built on it - though
    /// that is a documented property of SanMar's specific template, not a general guarantee for every
    /// vendor's PDF. Appending it as a second copy, rather than replacing <c>auditText</c> with it,
    /// keeps the readable reading-order text as the primary view (v2 of the seeded prompt tells the
    /// model to check both when a field seems missing) without betting the whole extraction on
    /// content-stream order being correct for an unknown layout.
    /// <para>
    /// <c>dbo.Invoice.RawText</c> (<c>auditText</c> alone) is intentionally unaffected - it is the
    /// audit trail a human reads back, not the model's literal input.
    /// </para>
    /// </remarks>
    internal static string BuildOllamaDocumentText(string auditText, string naturalOrderText) =>
        $"{auditText}{Environment.NewLine}{Environment.NewLine}"
        + $"=== Same document, raw order (a value separated from its label above may appear here instead) ==={Environment.NewLine}"
        + naturalOrderText;

    /// <summary>
    /// Substitutes the document text into the stored prompt template.
    /// </summary>
    /// <remarks>
    /// Both sides are normalised to LF. The template arrives that way from
    /// <c>ExtractionPromptRepository</c> (it is stored CRLF so it stays readable in SSMS), and the
    /// document text is normalised here because PdfPig joins pages with
    /// <see cref="Environment.NewLine"/>, which is CRLF on Windows and LF elsewhere. Without this the
    /// exact bytes sent to the model would depend on the machine, and replaying a stored extraction
    /// would not reproduce it.
    /// </remarks>
    internal static string BuildPrompt(string promptTemplate, string documentText) =>
        promptTemplate.Replace(
            ExtractionPromptPlaceholder,
            documentText.Replace("\r\n", "\n").Replace("\r", "\n"));

    /// <summary>Kept in step with <c>CK_ExtractionPrompt_Placeholder</c>, which enforces its presence.</summary>
    internal const string ExtractionPromptPlaceholder = "{{DocumentText}}";
}
