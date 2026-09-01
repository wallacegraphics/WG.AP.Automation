namespace WG.AP.Invoice.Models;

/// <summary>
/// The per-document configuration an extraction runs under, resolved from the client's invoice
/// format and its active prompt.
/// </summary>
/// <param name="ExtractorKey">
/// Which deterministic extractor may run, or null when none applies and Ollama should handle the
/// document directly.
/// <para>
/// This gate is a correctness fix, not a tidiness one. With a single client it did not matter that
/// the SanMar regexes ran against every PDF, because every PDF was SanMar's. With more than one
/// client they would be pointed at another client's invoice, and although
/// <c>SanmarPdfHeaderExtractor.TryExtract</c> is all-or-nothing, "every SanMar pattern happens to
/// match someone else's layout" is a silent wrong-data path rather than a crash.
/// </para>
/// </param>
/// <param name="PromptTemplate">
/// The Ollama prompt, already newline-normalised, containing the document-text placeholder. Null when
/// the format has no active prompt, in which case Ollama cannot be used as a fallback.
/// </param>
/// <param name="ResponseSchemaJson">
/// Ollama's structured-output schema, carried alongside the prompt because its <c>required</c> list is
/// part of the prompt's contract with <see cref="AI.InvoiceFieldsJsonParser"/>.
/// </param>
/// <param name="ModelName">A model pinned to this prompt, or null to use the configured default.</param>
/// <param name="ExtractionPromptId">
/// Recorded against the invoice so an extraction stays explainable months later, when the active
/// prompt has moved on.
/// </param>
public sealed record ExtractionRequest(
    string? ExtractorKey,
    string? PromptTemplate,
    string? ResponseSchemaJson,
    string? ModelName,
    int? ExtractionPromptId)
{
    /// <summary>The key that selects the SanMar header regexes.</summary>
    public const string SanmarPdfHeaderExtractorKey = "SANMAR_PDF_HEADER_V1";

    /// <summary>No client resolved, so no deterministic extractor and no prompt.</summary>
    public static ExtractionRequest None { get; } = new(null, null, null, null, null);
}

/// <summary>What an extraction produced, and how.</summary>
/// <param name="Method">
/// <c>Regex</c> or <c>Ollama</c>. Recorded per invoice, so "how often does the LLM get involved?" is a
/// query — and so a client's layout change shows up as deterministic extraction quietly giving way to
/// the model, which is the earliest available signal that a template moved.
/// </param>
/// <param name="ExtractionPromptId">The prompt version used, when the model was involved.</param>
public sealed record ExtractionResult(InvoiceFields Fields, string Method, int? ExtractionPromptId)
{
    public const string RegexMethod = "Regex";
    public const string OllamaMethod = "Ollama";
}
