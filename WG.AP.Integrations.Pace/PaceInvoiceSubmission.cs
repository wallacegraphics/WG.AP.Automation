namespace WG.AP.Integrations.Pace;

public sealed record PaceInvoiceSubmission
{
    public required long InvoiceId { get; init; }
    public string? ClientCode { get; init; }
    public string? ClientName { get; init; }
    public string? PaceVendoreAccountNumber { get; init; }

    public required PaceInvoiceFields Fields { get; init; }
}

public sealed record PaceInvoiceSubmissionResult
{
    public required string StatusCode { get; init; }

    public bool IsTransient { get; init; }

    public string? ResponseJson { get; init; }

    public string? PaceBillBatchId { get; init; }

    public string? PaceBillId { get; init; }

    public string? PaceBillLineId { get; init; }

    public string? ErrorMessage { get; init; }

    public bool RequiresReview { get; init; }
}
