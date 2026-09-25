namespace WG.AP.DataAccess;

public static class PaceSubmissionStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string RetryLater = "RetryLater";
    public const string DryRunPrepared = "DryRunPrepared";
    public const string BillCreated = "BillCreated";
    public const string PoNotReceived = "PoNotReceived";
    public const string NoPo = "NoPo";
    public const string AlreadyEntered = "AlreadyEntered";
    public const string Error = "Error";

    public const int AlreadyEnteredId = 1;
    public const int BillCreatedId = 2;
    public const int DryRunPreparedId = 3;
    public const int ErrorId = 4;
    public const int InProgressId = 5;
    public const int NoPoId = 6;
    public const int PendingId = 7;
    public const int PoNotReceivedId = 8;
    public const int RetryLaterId = 9;

    public static int ToId(string statusCode) => statusCode switch
    {
        Pending => PendingId,
        InProgress => InProgressId,
        RetryLater => RetryLaterId,
        DryRunPrepared => DryRunPreparedId,
        BillCreated => BillCreatedId,
        PoNotReceived => PoNotReceivedId,
        NoPo => NoPoId,
        AlreadyEntered => AlreadyEnteredId,
        Error => ErrorId,
        _ => throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Unknown Pace submission status code.")
    };
}

public sealed record PaceSubmissionRecord
{
    public required long PaceSubmissionId { get; init; }
    public required long InvoiceId { get; init; }
    public required int StatusCodeId { get; init; }
    public required string StatusCode { get; init; }
    public required int AttemptCount { get; init; }
    public DateTime? NextAttemptOn { get; init; }
    public Guid? ClaimToken { get; init; }
    public DateTime? ClaimedOn { get; init; }
    public long? ProcessingRunId { get; init; }
    public string? ResponseJson { get; init; }
    public string? PaceBillBatchId { get; init; }
    public string? PaceBillId { get; init; }
    public string? PaceBillLineId { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record PaceSubmissionClaim
{
    public required long PaceSubmissionId { get; init; }
    public required long InvoiceId { get; init; }
    public required long MailMessageId { get; init; }
    public required string GraphMessageId { get; init; }
    public required int AttemptCount { get; init; }
    public required Guid ClaimToken { get; init; }
    public required string FieldsJson { get; init; }
    public required string? InvoiceNumber { get; init; }
    public required string? CustomerPO { get; init; }
    public required decimal? Total { get; init; }
    public required string? ClientCode { get; init; }
    public required string? ClientName { get; init; }
    public required string? PaceVendorAccountNumber { get; init; }
    public string? PaceBillBatchId { get; init; }
    public string? PaceBillId { get; init; }
    public string? PaceBillLineId { get; init; }

    // For the run summary email: which vendor email this invoice came from, and when it was dated.
    public DateOnly? InvoiceDate { get; init; }
    public string? Subject { get; init; }
    public string? SenderAddress { get; init; }
    public DateTimeOffset? ReceivedOn { get; init; }
}

public static class PaceSubmissionStatusExtensions
{
    /// <summary>
    /// True for a status the Pace step can still change (queued, being worked, or waiting to retry): an email
    /// is routed only once none of its invoices is in one of these.
    /// </summary>
    public static bool IsUnfinishedPaceStatus(this string? statusCode) =>
        statusCode is null or PaceSubmissionStatus.Pending or PaceSubmissionStatus.InProgress or PaceSubmissionStatus.RetryLater;
}

/// <summary>
/// Everything needed to decide where one vendor email goes once the Pace step has finished with it: its
/// status from the mailbox step and the Pace outcome of every invoice that was sent to Pace.
/// </summary>
public sealed record MailRoutingState
{
    public required long MailMessageId { get; init; }
    public required string GraphMessageId { get; init; }
    public required int MailStatusId { get; init; }
    public string? MailErrorMessage { get; init; }
    public string? Subject { get; init; }
    public string? SenderAddress { get; init; }
    public DateTimeOffset? ReceivedOn { get; init; }
    public required IReadOnlyList<MailRoutingSubmission> Submissions { get; init; }
}

/// <summary>
/// One extracted invoice of a <see cref="MailRoutingState"/>. <see cref="PaceSubmissionId"/> is null when the
/// invoice has not been queued for Pace yet, which counts as unfinished.
/// </summary>
public sealed record MailRoutingSubmission
{
    public required long InvoiceId { get; init; }
    public long? PaceSubmissionId { get; init; }
    public string? StatusCode { get; init; }
    public bool RequiresReview { get; init; }
    public string? ErrorMessage { get; init; }
    public int AttemptCount { get; init; }
    public DateTime? NextAttemptOn { get; init; }
    public bool Notified { get; init; }
    public bool MailRouted { get; init; }
}

public sealed record PaceSubmissionCompletion
{
    public required long PaceSubmissionId { get; init; }
    public required Guid ClaimToken { get; init; }
    public required string StatusCode { get; init; }
    public string? ResponseJson { get; init; }
    public string? PaceBillBatchId { get; init; }
    public string? PaceBillId { get; init; }
    public string? PaceBillLineId { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether this outcome still needs the NeedsReview mail route once completed. Persisted so the
    /// mail-routing recovery sweep (<see cref="PaceSubmissionRepository.ClaimUnroutedFinalSubmissionsAsync"/>)
    /// can tell a NeedsReview-worthy completion apart from a plain success on restart.
    /// </summary>
    public required bool RequiresReview { get; init; }

    /// <summary>
    /// The vendor the Pace bill was actually created under (PO vendor, or the no-PO default vendor).
    /// Persisted so a digest replayed by the recovery sweep names this vendor, not the client's configured
    /// account number.
    /// </summary>
    public string? BillVendor { get; init; }
}

/// <summary>
/// A Pace submission whose Pace outcome is already final but whose mail routing (status update + move) was
/// never confirmed - either the process crashed between completing the submission and routing the mail, or a
/// non-transient exception during routing was caught and logged instead of being allowed to re-run the claim.
/// Claimed by <see cref="PaceSubmissionRepository.ClaimUnroutedFinalSubmissionsAsync"/> and replayed by
/// <c>PaceInvoiceProcessor.RecoverUnroutedSubmissionsAsync</c>.
/// </summary>
public sealed record PaceUnroutedSubmission
{
    public required long PaceSubmissionId { get; init; }
    public required long InvoiceId { get; init; }
    public required long MailMessageId { get; init; }
    public required string GraphMessageId { get; init; }
    public required string? InvoiceNumber { get; init; }
    public required string? CustomerPO { get; init; }
    public required string? PaceVendorAccountNumber { get; init; }
    public required string StatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PaceBillBatchId { get; init; }
    public string? PaceBillId { get; init; }
    public required bool RequiresReview { get; init; }
    public string? BillVendor { get; init; }

    /// <summary>
    /// The alert or digest line for this submission was already delivered, so replaying its routing must
    /// redo only the status update and move, never notify again.
    /// </summary>
    public required bool AlreadyNotified { get; init; }

    // For rebuilding the run summary email's line for this invoice exactly as the live run would have.
    public string? ResponseJson { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public decimal? Total { get; init; }
    public string? ClientCode { get; init; }
    public string? ClientName { get; init; }
    public string? Subject { get; init; }
    public string? SenderAddress { get; init; }
    public DateTimeOffset? ReceivedOn { get; init; }
}

public sealed record PaceSubmissionRetry
{
    public required long PaceSubmissionId { get; init; }
    public required Guid ClaimToken { get; init; }
    public required DateTime NextAttemptOn { get; init; }
    public string? ResponseJson { get; init; }
    public string? ErrorMessage { get; init; }
}
