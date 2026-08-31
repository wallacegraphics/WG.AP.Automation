namespace WG.AP.Invoice.Models;

/// <summary>
/// Result of reconciling a manifest's Voucher list against the PDF attachments actually present on
/// a message — filename-level only, no field extraction involved yet.
/// </summary>
public sealed record ManifestReconciliation(
    IReadOnlyList<string> MissingPdfVouchers,
    IReadOnlyList<string> UnexpectedAttachments,
    IReadOnlyList<string> DuplicateVouchers,
    IReadOnlyList<ManifestPair> MatchedPairs)
{
    public bool HasDiscrepancies => MissingPdfVouchers.Count > 0 || UnexpectedAttachments.Count > 0 || DuplicateVouchers.Count > 0;
}
