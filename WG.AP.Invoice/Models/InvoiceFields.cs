namespace WG.AP.Invoice.Models;

/// <summary>
/// Fields extracted from a single PDF invoice attachment via <see cref="Abstractions.IInvoiceFieldExtractor"/>.
/// </summary>
/// <param name="ClientName">
/// The issuing company as printed on the document, before it is matched to a <c>dbo.Client</c> row.
/// Note the Ollama response schema still calls this key <c>VendorName</c>: that wording is
/// model-facing English the prompt was tuned with, and rewording it would change extraction
/// behaviour, so <see cref="AI.InvoiceFieldsJsonParser"/> maps it across instead.
/// </param>
public sealed record InvoiceFields(
    string InvoiceNumber,
    string? SalesOrder,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    decimal Total,
    string? ClientName,
    string? CustomerPO,
    string? CustomerNumber = null,
    string? OrderAccount = null,
    string? Terms = null,
    string RawText = "");
