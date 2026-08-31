using WG.AP.Core.Abstractions;
using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Abstractions;

/// <summary>
/// Deterministic, non-LLM verification of a SanMar-style Excel manifest against the PDF invoices
/// attached to the same message. Kept separate from <see cref="IInvoiceFieldExtractor"/>: reading
/// the manifest and reconciling filenames needs no extraction output, and only the field-level
/// comparison consumes it.
/// </summary>
public interface IAttachmentManifestVerifier
{
    /// <summary>Parses the manifest workbook. Throws if the file can't be read or is missing expected columns.</summary>
    IReadOnlyList<ManifestRow> ReadManifest(byte[] excelBytes);

    /// <summary>Phase 1: reconciles manifest Vouchers against attached PDF filenames — no field extraction involved.</summary>
    ManifestReconciliation Reconcile(IReadOnlyList<ManifestRow> manifestRows, IReadOnlyList<MailAttachmentSummary> pdfAttachments);

    /// <summary>Phase 2: compares a matched pair's manifest fields against its extracted PDF fields.</summary>
    InvoiceFieldComparisonResult CompareFields(ManifestRow row, InvoiceFields extractedFields);
}
