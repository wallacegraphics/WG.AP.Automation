using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>What the claim step decided about a message.</summary>
/// <param name="MailMessageId">The surrogate key, whether or not the message was claimed.</param>
/// <param name="Claimed">
/// False when the message is already in a final status — the caller must then not parse it, not move
/// it, and not write anything further about it. This is the whole no-reprocess gate.
/// </param>
/// <param name="StatusId">The status the row is in, so a skip can be logged with a reason.</param>
/// <param name="AttemptCount">Attempts including this one, for comparison against the cap.</param>
public sealed record MailMessageClaim(long MailMessageId, bool Claimed, int StatusId, int AttemptCount);

/// <summary>A stored attachment, as recorded.</summary>
public sealed record RecordedAttachment(long MailAttachmentId, MailAttachmentSummary Attachment);

/// <summary>The client an incoming email resolved to.</summary>
/// <param name="ClientId">0 when the sender domain matched no enabled client.</param>
/// <param name="InvoiceFormatId">Null when the client has no enabled format.</param>
/// <param name="ExtractorKey">
/// Which extractor may run. It gates the deterministic tier: SanMar's regexes are only tried when
/// this says so, because with more than one client they would otherwise be pointed at another
/// client's invoice.
/// </param>
public sealed record ClientResolution(int ClientId, int? InvoiceFormatId, string? ExtractorKey)
{
    public const int UnknownClientId = 0;

    public static ClientResolution Unknown { get; } = new(UnknownClientId, null, null);

    public bool IsKnown => ClientId > UnknownClientId;
}

/// <summary>
/// The active prompt for one invoice format: prompt text, response schema and model, as one
/// versioned unit.
/// </summary>
/// <remarks>
/// They travel together because the response schema's <c>required</c> list is part of the prompt's
/// contract with <c>InvoiceFieldsJsonParser</c>, and because the prompt was tuned against a specific
/// model. Splitting them would let one be deployed without the other.
/// </remarks>
public sealed record ExtractionPromptRecord(
    int ExtractionPromptId,
    int Version,
    string PromptTemplate,
    string ResponseSchemaJson,
    string? ModelName);

/// <summary>Everything recorded about one extracted invoice.</summary>
public sealed record InvoiceRecord
{
    public required long MailMessageId { get; init; }
    public required long MailAttachmentId { get; init; }
    public required int ClientId { get; init; }
    public int? InvoiceFormatId { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public decimal? Total { get; init; }
    public string? SalesOrder { get; init; }
    public string? CustomerPO { get; init; }
    public string? CustomerNumber { get; init; }
    public string? OrderAccount { get; init; }
    public string? Terms { get; init; }
    public string? ClientNameAsRead { get; init; }
    public string? RawText { get; init; }
    public string? FieldsJson { get; init; }
    public string? ExtractionMethod { get; init; }
    public int? ExtractionPromptId { get; init; }
    public required ApStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>The outcome of trying to record an invoice.</summary>
/// <param name="InvoiceId">
/// Null when the insert was rejected as a duplicate number. On a replay of the same attachment this is
/// the id of the row already recorded, not a new one.
/// </param>
/// <param name="IsDuplicate">
/// True when <c>UQ_Invoice_ClientNumber</c> rejected the row. The constraint decides and the code
/// records — a duplicate is never determined by querying first, because a check-then-insert has a
/// race that a unique index does not.
/// <para>
/// False when <c>UQ_Invoice_Attachment</c> rejected it instead: re-extracting one attachment is
/// idempotency after a crash, not a duplicate invoice, and the two must not share an outcome.
/// </para>
/// </param>
public sealed record InvoiceInsertResult(long? InvoiceId, bool IsDuplicate);
