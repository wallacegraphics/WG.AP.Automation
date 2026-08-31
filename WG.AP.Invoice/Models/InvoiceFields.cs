namespace WG.AP.Invoice.Models;

/// <summary>
/// Fields extracted from a single PDF invoice attachment via <see cref="Abstractions.IInvoiceFieldExtractor"/>.
/// </summary>
public sealed record InvoiceFields(
    string InvoiceNumber,
    string? SalesOrder,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    decimal Total,
    string? VendorName,
    string? CustomerPO,
    string? CustomerNumber = null,
    string? OrderAccount = null,
    string? Terms = null,
    string RawText = "");
