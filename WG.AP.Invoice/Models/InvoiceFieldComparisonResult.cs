namespace WG.AP.Invoice.Models;

/// <summary>Field-level comparison result for one matched (manifest row, extracted PDF) pair.</summary>
public sealed record InvoiceFieldComparisonResult(string Voucher, IReadOnlyList<FieldMismatch> Mismatches)
{
    public bool IsMatch => Mismatches.Count == 0;
}
