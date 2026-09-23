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
    public required string? PaceVendoreAccountNumber { get; init; }
    public string? PaceBillBatchId { get; init; }
    public string? PaceBillId { get; init; }
    public string? PaceBillLineId { get; init; }
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
}

public sealed record PaceSubmissionRetry
{
    public required long PaceSubmissionId { get; init; }
    public required Guid ClaimToken { get; init; }
    public required DateTime NextAttemptOn { get; init; }
    public string? ResponseJson { get; init; }
    public string? ErrorMessage { get; init; }
}
