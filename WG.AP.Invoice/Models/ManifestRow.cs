namespace WG.AP.Invoice.Models;

/// <summary>
/// One row of a SanMar-style Excel manifest, as read by <see cref="Abstractions.IAttachmentManifestVerifier"/>.
/// Column names mirror the workbook's own headers (e.g. "Voucher", "Sales order") rather than
/// generic invoice terminology, since those headers are what a maintainer will see when comparing
/// this record against the source file.
/// </summary>
public sealed record ManifestRow(
    string Voucher,
    string? SalesOrder,
    DateOnly? Date,
    DateOnly? DueDate,
    decimal InvoiceAmount,
    string? RmaNumber,
    string? CustomerReference,
    string? TermsOfPayment,
    string? CustomerAccount);
