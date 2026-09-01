namespace WG.AP.DataAccess;

/// <summary>
/// Mirrors the seeded rows of <c>lkup.Status</c>. The values are the contract — they are hand-assigned
/// constants in <c>Scripts/Seed/Status.sql</c>, not IDENTITY values, precisely so this enum can mirror
/// them and so each table can constrain itself to its band with a one-line CHECK.
/// </summary>
/// <remarks>
/// Bands: 10-19 mail, 20-29 invoice. <c>CK_MailMessage_StatusBand</c> and
/// <c>CK_Invoice_StatusBand</c> enforce them, so assigning an invoice status to a message fails at
/// the database rather than producing a nonsense row.
/// <para>
/// Whether a status stops further processing is NOT encoded here: it is the <c>IsFinal</c> column,
/// which the claim UPDATE joins against. That is why adding a new no-reprocess reason is a seed row
/// rather than a code change — and why nothing in this enum should grow an IsFinal-like property.
/// </para>
/// </remarks>
public enum ApStatus
{
    /// <summary>Recorded at discovery, not yet decided. The only non-final mail status.</summary>
    MailNew = 10,

    /// <summary>Every PDF extracted with all five required fields.</summary>
    MailProcessed = 11,

    /// <summary>A required field was missing, the client was unresolved, the invoice number was a
    /// duplicate, or the attempt cap was reached.</summary>
    MailNeedsReview = 12,

    /// <summary>A PDF could not be parsed, or a total was null, zero or negative.</summary>
    MailError = 13,

    /// <summary>No PDF attachments at all — which now includes Excel-only mail. Left in the Inbox.</summary>
    MailSkipped = 14,

    /// <summary>The same email seen again under a new Graph id.</summary>
    MailDuplicate = 15,

    /// <summary>Deleted from the mailbox by a human. Not reachable until tombstones are surfaced.</summary>
    MailDeleted = 16,

    /// <summary>Row created before extraction ran.</summary>
    InvoiceNew = 20,

    /// <summary>All five required fields present. <c>CK_Invoice_ExtractedIsComplete</c> enforces that.</summary>
    InvoiceExtracted = 21,

    InvoiceNeedsReview = 22,

    InvoiceError = 23,

    /// <summary>A duplicate invoice number for the client, as decided by <c>UQ_Invoice_ClientNumber</c>.</summary>
    InvoiceDuplicate = 24
}
