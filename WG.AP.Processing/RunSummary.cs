using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;

namespace WG.AP.Processor;

/// <summary>
/// Everything one run did, collected per vendor email so each gets a single summary email covering both
/// steps: its parsing result from the mailbox step, the Pace outcome of every invoice on it, and the folder
/// it finally went to. Filled by <see cref="APProcessor"/> and <see cref="PaceInvoiceProcessor"/>, sent once
/// at the end of the run by <see cref="RunSummaryNotifier"/>.
/// </summary>
/// <remarks>
/// A singleton, because the process runs one pass and exits. Not thread-safe: both steps run one after the
/// other, never concurrently.
/// </remarks>
public sealed class RunSummary
{
    private readonly Dictionary<long, EmailRunSummary> emails = [];
    private readonly List<string> failures = [];

    /// <summary>Every email that took part in this run, in the order it was first seen.</summary>
    public IReadOnlyList<EmailRunSummary> Emails => emails.Values.ToList();

    /// <summary>Why a step stopped part way, e.g. "Mailbox processing failed: ...".</summary>
    public IReadOnlyList<string> Failures => failures;

    /// <summary>Pace submissions this run tried to route: any still unrouted at the end have their lease released.</summary>
    public HashSet<long> RoutingAttempts { get; } = [];

    /// <summary>The summary for this vendor email, created on first use; later calls fill in details it lacked.</summary>
    public EmailRunSummary Email(long mailMessageId, string? subject, string? senderAddress, DateTimeOffset? receivedOn)
    {
        if (!emails.TryGetValue(mailMessageId, out var email))
        {
            email = new EmailRunSummary(mailMessageId);
            emails.Add(mailMessageId, email);
        }

        email.Subject ??= subject;
        email.SenderAddress ??= senderAddress;
        email.ReceivedOn ??= receivedOn;

        return email;
    }

    public void AddFailure(string failure) => failures.Add(failure);
}

/// <summary>What happened to one vendor email during this run.</summary>
public sealed class EmailRunSummary(long mailMessageId)
{
    public long MailMessageId { get; } = mailMessageId;

    public string? Subject { get; set; }

    public string? SenderAddress { get; set; }

    public DateTimeOffset? ReceivedOn { get; set; }

    /// <summary>The mailbox step's result, when the email was parsed in this run.</summary>
    public EmailParseResult? Parse { get; set; }

    /// <summary>One entry per invoice whose Pace outcome this run reports.</summary>
    public List<PaceSummaryEntry> PaceEntries { get; } = [];

    /// <summary>The folder the email was moved to in this run; null while it has not been moved.</summary>
    public MailDestinationFolder? RoutedTo { get; set; }

    /// <summary>At least one invoice from this email was sent to Pace, so the Pace step decides its folder.</summary>
    public bool AwaitingPace { get; set; }

    /// <summary>Set when the email stays in the Inbox because Pace could not be reached and will be retried.</summary>
    public PaceRetryInfo? WaitingForPace { get; set; }

    /// <summary>
    /// The Pace submissions of this email at the time it was moved, and whether each had already been reported
    /// before this run. They are marked routed only once their summary line is delivered.
    /// </summary>
    public List<(long PaceSubmissionId, bool AlreadyNotified)> RoutedSubmissions { get; } = [];
}

/// <summary>The mailbox step's verdict on one email, reduced to what its summary email shows.</summary>
public sealed record EmailParseResult(
    ApStatus Status,
    int AttachmentCount,
    int PdfCount,
    IReadOnlyList<string> ParsedFiles,
    IReadOnlyList<string> Problems);

/// <summary>Why an email is still in the Inbox: the attempt Pace failed on, and when the next one is due.</summary>
public sealed record PaceRetryInfo(int Attempt, int MaxAttempts, DateTime? NextAttemptOnUtc);

public enum PaceSummaryKind
{
    Success,
    NeedsReview,
    Error
}

/// <summary>One invoice's line in the summary email.</summary>
public sealed record PaceSummaryEntry(
    PaceSummaryKind Kind,
    long PaceSubmissionId,
    long InvoiceId,
    string? InvoiceNumber,
    string? CustomerPO,
    DateOnly? InvoiceDate,
    decimal? InvoiceTotal,
    string? VendorId,
    string? VendorName,
    string StatusCode,
    string? PaceBillBatchId,
    string? PaceBillId,
    string? Reason,
    PaceBillSummary? Summary)
{
    /// <summary>
    /// Any error sends the email to Errors, whatever else the outcome says; otherwise an outcome that needs
    /// review sends it to NeedsReview; anything else is a success.
    /// </summary>
    public static PaceSummaryKind KindOf(string? statusCode, bool requiresReview) =>
        statusCode is PaceSubmissionStatus.Error or PaceSubmissionStatus.PoNotReceived
            ? PaceSummaryKind.Error
            : requiresReview ? PaceSummaryKind.NeedsReview : PaceSummaryKind.Success;
}
