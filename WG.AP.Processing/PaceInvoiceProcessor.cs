using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;
using WG.AP.Processor.Logging;

namespace WG.AP.Processor;

/// <summary>
/// Sends each extracted invoice to Pace, records the outcome, and - once every invoice of a vendor email has a
/// Pace outcome - moves that email out of the Inbox, once, to the folder its worst result calls for: any error
/// to Errors, otherwise anything needing review to NeedsReview, otherwise Processed. The mailbox step leaves
/// a parsed email in the Inbox precisely so this is the only move it gets.
/// </summary>
/// <remarks>
/// Sends no email itself: every outcome becomes a line in <see cref="RunSummary"/>, sent once at the end of the
/// run by <see cref="RunSummaryNotifier"/>, after which <see cref="FinalizeSummaryAsync"/> records what was
/// delivered and routed.
/// </remarks>
public sealed class PaceInvoiceProcessor(
    IMailSource mailSource,
    MailMessageRepository mailMessageRepository,
    PaceSubmissionRepository paceSubmissionRepository,
    IPaceInvoiceService paceInvoiceService,
    RunSummary runSummary,
    IOptions<PaceOptions> paceOptions,
    ILogger<PaceInvoiceProcessor> logger)
{
    public Task ProcessPendingAsync(CancellationToken cancellationToken) =>
        ProcessPendingAsync(ProcessingRunContext.CurrentRunId, cancellationToken);

    public async Task ProcessPendingAsync(long? processingRunId, CancellationToken cancellationToken)
    {
        var enqueued = await paceSubmissionRepository.EnqueueExtractedInvoicesAsync(cancellationToken);
        logger.LogInformation("Pace submission enqueue complete: {EnqueuedCount} invoice(s) queued.", enqueued);

        var processed = 0;

        await RecoverUnroutedSubmissionsAsync(cancellationToken);

        while (true)
        {
            var claim = await paceSubmissionRepository.ClaimNextAsync(processingRunId, cancellationToken);

            if (claim is null)
            {
                break;
            }

            processed++;
            await ProcessClaimAsync(claim, cancellationToken);
        }

        logger.LogInformation("Pace submission processing complete: {ProcessedCount} submission(s) processed.", processed);
    }

    /// <summary>
    /// Runs after the run's summary emails were sent. A row is done only when its summary line was delivered
    /// and its email was moved: rows reported in a delivered summary are marked notified, and routed once their
    /// email was moved. Anything else keeps MailRoutedOn NULL, so the next run's sweep reports or moves it again.
    /// </summary>
    public async Task FinalizeSummaryAsync(IReadOnlySet<long> deliveredMailMessageIds)
    {
        try
        {
            var notified = new List<long>();
            var routed = new List<long>();

            foreach (var email in runSummary.Emails)
            {
                var delivered = deliveredMailMessageIds.Contains(email.MailMessageId);
                var reportedNow = email.PaceEntries.Select(entry => entry.PaceSubmissionId).ToHashSet();

                if (delivered)
                {
                    notified.AddRange(reportedNow);
                }
                else if (reportedNow.Count > 0)
                {
                    logger.LogWarning(
                        "Pace summary for {EntryCount} invoice(s) could not be sent; they will be included in the next run's summary: {InvoiceIds}.",
                        reportedNow.Count,
                        string.Join(", ", email.PaceEntries.Select(entry => entry.InvoiceId)));
                }

                if (email.RoutedTo is not null)
                {
                    routed.AddRange(email.RoutedSubmissions
                        .Where(submission => submission.AlreadyNotified || (delivered && reportedNow.Contains(submission.PaceSubmissionId)))
                        .Select(submission => submission.PaceSubmissionId));
                }
            }

            // CancellationToken.None: a cancelled run must still record notifications for work it already committed.
            await paceSubmissionRepository.MarkNotifiedAsync(notified, CancellationToken.None);
            await paceSubmissionRepository.MarkMailRoutedAsync(routed, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Never mask the run's own outcome with bookkeeping; unmarked rows are simply swept again next run.
            logger.LogError(exception, "Failed to record which Pace summary lines were delivered and which emails were routed.");
        }

        // Only after the summary: a row is not done until its summary line is sent, so its lease must hold
        // until then or an overlapping run could put it in a second summary.
        await ReleaseUnfinishedRoutingAsync(runSummary.RoutingAttempts);
    }

    private async Task ReleaseUnfinishedRoutingAsync(HashSet<long> routingAttempts)
    {
        if (routingAttempts.Count == 0)
        {
            return;
        }

        try
        {
            // Rows that finished routing are skipped by the repository (MailRoutedOn is set), so passing every
            // attempt is safe. CancellationToken.None, like the summary: a cancelled run should still hand these back.
            await paceSubmissionRepository.ReleaseRoutingClaimsAsync(routingAttempts, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Never mask the exception that ended the claim loop. The leases then simply lapse on their own.
            logger.LogError(
                exception,
                "Failed to release the routing lease on Pace submission(s) {PaceSubmissionIds}; they will be retried once the lease expires.",
                string.Join(", ", routingAttempts));
        }
    }

    private async Task ProcessClaimAsync(PaceSubmissionClaim claim, CancellationToken cancellationToken)
    {
        var email = runSummary.Email(claim.MailMessageId, claim.Subject, claim.SenderAddress, claim.ReceivedOn);
        email.AwaitingPace = true;

        try
        {
            if (!string.IsNullOrWhiteSpace(claim.PaceBillId) || !string.IsNullOrWhiteSpace(claim.PaceBillLineId))
            {
                await CompleteAndRouteAsync(claim, new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceSubmissionStatus.AlreadyEntered,
                    PaceBillBatchId = claim.PaceBillBatchId,
                    PaceBillId = claim.PaceBillId,
                    PaceBillLineId = claim.PaceBillLineId,
                    ErrorMessage = "Pace IDs were already recorded locally before this retry; skipping duplicate Pace write.",
                    RequiresReview = false
                }, cancellationToken);

                return;
            }

            PaceInvoiceFields fields;

            try
            {
                fields = PaceInvoiceFieldsParser.Parse(claim.FieldsJson);
            }
            catch (Exception exception) when (IsProcessingFailure(exception))
            {
                logger.LogError(exception, "Pace submission {PaceSubmissionId} (invoice {InvoiceId}): stored invoice fields could not be parsed.", claim.PaceSubmissionId, claim.InvoiceId);

                await CompleteAndRouteAsync(claim, new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceSubmissionStatus.Error,
                    ErrorMessage = $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': stored invoice fields could not be processed. {exception.Message}",
                    RequiresReview = false
                }, cancellationToken, parseFailure: true);

                return;
            }

            var result = await paceInvoiceService.SubmitAsync(new PaceInvoiceSubmission
            {
                InvoiceId = claim.InvoiceId,
                ClientCode = claim.ClientCode,
                ClientName = claim.ClientName,
                PaceVendorAccountNumber = claim.PaceVendorAccountNumber,
                Fields = fields
            }, cancellationToken);

            if (result.IsTransient || result.StatusCode == PaceSubmissionStatus.RetryLater)
            {
                var maxAttempts = paceOptions.Value.MaxAttempts;

                if (claim.AttemptCount < maxAttempts)
                {
                    var nextAttemptOn = CalculateNextAttemptOn(claim.AttemptCount);
                    var savedRetry = await paceSubmissionRepository.RetryLaterAsync(new PaceSubmissionRetry
                    {
                        PaceSubmissionId = claim.PaceSubmissionId,
                        ClaimToken = claim.ClaimToken,
                        NextAttemptOn = nextAttemptOn,
                        ResponseJson = result.ResponseJson,
                        ErrorMessage = result.ErrorMessage
                    }, cancellationToken);

                    if (!savedRetry)
                    {
                        logger.LogWarning("Pace submission {PaceSubmissionId} retry update skipped because its claim token no longer matched.", claim.PaceSubmissionId);
                    }

                    email.WaitingForPace = new PaceRetryInfo(claim.AttemptCount, maxAttempts, nextAttemptOn);
                    return;
                }

                // Pace has been unreachable for every allowed attempt. Retrying for ever would leave the email in
                // the Inbox with nobody told, so the invoice becomes an Error and its email goes to Errors.
                var gaveUpMessage = $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': Pace could not be reached after {claim.AttemptCount} attempts: {result.ErrorMessage ?? "no error detail was returned"}";
                logger.LogError(
                    "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}': Pace could not be reached after {AttemptCount} attempt(s) (Pace:MaxAttempts {MaxAttempts}); giving up and routing the email to Errors. Last error: {LastError}. InvoiceId={InvoiceId}; PaceSubmissionId={PaceSubmissionId}.",
                    claim.InvoiceNumber,
                    claim.CustomerPO,
                    claim.AttemptCount,
                    maxAttempts,
                    result.ErrorMessage,
                    claim.InvoiceId,
                    claim.PaceSubmissionId);

                result = new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceSubmissionStatus.Error,
                    ResponseJson = result.ResponseJson,
                    ErrorMessage = gaveUpMessage
                };
            }

            await CompleteAndRouteAsync(claim, result, cancellationToken);
        }
        catch (Exception exception) when (IsProcessingFailure(exception))
        {
            // Not a bad stored invoice: something failed while working with Pace (e.g. createBill returned no id,
            // or Pace paged inconsistently), so it is reported as a Pace failure, with the real reason.
            logger.LogError(exception, "Pace submission {PaceSubmissionId} (invoice {InvoiceId}) failed while processing in Pace; completed as Error.", claim.PaceSubmissionId, claim.InvoiceId);

            await CompleteAndRouteAsync(claim, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceSubmissionStatus.Error,
                ErrorMessage = $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': Pace processing failed: {exception.Message}",
                RequiresReview = false
            }, cancellationToken, parseFailure: true);
        }
    }

    private static bool IsProcessingFailure(Exception exception) =>
        exception is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException;

    /// <summary>
    /// Records the Pace outcome, adds its line to the run summary, and moves the vendor email if this was the
    /// last of its invoices Pace had to decide.
    /// </summary>
    private async Task CompleteAndRouteAsync(
        PaceSubmissionClaim claim,
        PaceInvoiceSubmissionResult result,
        CancellationToken cancellationToken,
        bool parseFailure = false)
    {
        // Every completion carries a summary: it is what the email line is built from, and what tells the
        // recovery sweep this row's email is routed by the Pace step (see ClaimUnroutedFinalSubmissionsAsync).
        var storedSummary = result.Summary ?? new PaceBillSummary
        {
            Case = PaceInvoiceCase.Other,
            VendorId = result.BillVendor ?? claim.PaceVendorAccountNumber,
            InvoiceTotal = claim.Total ?? 0m
        };

        var savedCompletion = await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = claim.PaceSubmissionId,
            ClaimToken = claim.ClaimToken,
            StatusCode = result.StatusCode,
            ResponseJson = storedSummary.AttachTo(result.ResponseJson),
            PaceBillBatchId = result.PaceBillBatchId,
            PaceBillId = result.PaceBillId,
            PaceBillLineId = result.PaceBillLineId,
            ErrorMessage = result.ErrorMessage,
            RequiresReview = result.RequiresReview,
            BillVendor = result.BillVendor
        }, cancellationToken);

        if (!savedCompletion)
        {
            logger.LogWarning(
                parseFailure
                    ? "Pace submission {PaceSubmissionId} parse-error update skipped because its claim token no longer matched."
                    : "Pace submission {PaceSubmissionId} completion skipped because its claim token no longer matched.",
                claim.PaceSubmissionId);
            return;
        }

        var email = runSummary.Email(claim.MailMessageId, claim.Subject, claim.SenderAddress, claim.ReceivedOn);
        email.PaceEntries.Add(new PaceSummaryEntry(
            PaceSummaryEntry.KindOf(result.StatusCode, result.RequiresReview),
            claim.PaceSubmissionId,
            claim.InvoiceId,
            claim.InvoiceNumber,
            claim.CustomerPO,
            claim.InvoiceDate,
            claim.Total,
            result.Summary?.VendorId ?? result.BillVendor ?? claim.PaceVendorAccountNumber,
            result.Summary?.VendorName ?? claim.ClientName,
            result.StatusCode,
            result.PaceBillBatchId,
            result.PaceBillId,
            result.ErrorMessage,
            result.Summary));

        await RouteEmailIfFinishedAsync(claim.MailMessageId, [claim.PaceSubmissionId], cancellationToken);
    }

    /// <summary>
    /// Moves the vendor email once none of its invoices is still waiting on Pace. Never throws: the Pace
    /// outcomes are already final by now, so a routing failure is logged and left for the next run's sweep
    /// (MailRoutedOn stays NULL) instead of aborting the rest of the claim loop.
    /// </summary>
    /// <remarks>
    /// The status is committed before the move, as in the mailbox step: a crash between the two leaves a
    /// correctly classified message in the Inbox, which the sweep moves on the next run.
    /// </remarks>
    private async Task RouteEmailIfFinishedAsync(long mailMessageId, IReadOnlyCollection<long> paceSubmissionIds, CancellationToken cancellationToken)
    {
        runSummary.RoutingAttempts.UnionWith(paceSubmissionIds);

        try
        {
            var state = await paceSubmissionRepository.LoadMailRoutingStateAsync(mailMessageId, cancellationToken);

            if (state is null)
            {
                return;
            }

            runSummary.RoutingAttempts.UnionWith(state.Submissions.Where(submission => submission.PaceSubmissionId is not null).Select(submission => submission.PaceSubmissionId!.Value));

            var email = runSummary.Email(state.MailMessageId, state.Subject, state.SenderAddress, state.ReceivedOn);

            if (email.RoutedTo is not null)
            {
                return;
            }

            var unfinished = state.Submissions.Where(submission => submission.StatusCode.IsUnfinishedPaceStatus()).ToList();

            if (unfinished.Count > 0)
            {
                email.AwaitingPace = true;

                var retry = unfinished
                    .Where(submission => submission.StatusCode == PaceSubmissionStatus.RetryLater)
                    .OrderByDescending(submission => submission.AttemptCount)
                    .FirstOrDefault();

                if (retry is not null)
                {
                    email.WaitingForPace ??= new PaceRetryInfo(retry.AttemptCount, paceOptions.Value.MaxAttempts, retry.NextAttemptOn);
                }

                logger.LogInformation(
                    "Message {GraphMessageId} (mail message {MailMessageId}) stays in the Inbox: {UnfinishedCount} of its {InvoiceCount} invoice(s) are not finished in Pace yet.",
                    state.GraphMessageId,
                    mailMessageId,
                    unfinished.Count,
                    state.Submissions.Count);
                return;
            }

            // Pace is done with every invoice of this email, so it is no longer waiting on Pace - even if the move
            // below fails, in which case the summary simply names no folder.
            email.AwaitingPace = false;
            email.WaitingForPace = null;

            var status = DecideMailStatus((ApStatus)state.MailStatusId, state.Submissions);
            var destination = status switch
            {
                ApStatus.MailError => MailDestinationFolder.Errors,
                ApStatus.MailNeedsReview => MailDestinationFolder.NeedsReview,
                _ => MailDestinationFolder.Processed
            };

            await mailMessageRepository.SetStatusAsync(mailMessageId, status, BuildMailReason(state.MailErrorMessage, state.Submissions), cancellationToken);
            await mailSource.MoveMessageAsync(state.GraphMessageId, destination, cancellationToken);

            email.RoutedTo = destination;
            email.RoutedSubmissions.AddRange(state.Submissions
                .Where(submission => submission.PaceSubmissionId is not null)
                .Select(submission => (submission.PaceSubmissionId!.Value, submission.Notified)));

            foreach (var submission in state.Submissions)
            {
                var kind = PaceSummaryEntry.KindOf(submission.StatusCode, submission.RequiresReview);

                if (kind == PaceSummaryKind.Error && destination == MailDestinationFolder.Errors)
                {
                    logger.LogInformation(
                        "Pace validation error routed message {GraphMessageId} for invoice {InvoiceId} to Errors.",
                        state.GraphMessageId,
                        submission.InvoiceId);
                }
                else if (kind == PaceSummaryKind.NeedsReview && destination == MailDestinationFolder.NeedsReview)
                {
                    logger.LogInformation(
                        "Pace invoice {InvoiceId} (needs review) routed message {GraphMessageId} to NeedsReview.",
                        submission.InvoiceId,
                        state.GraphMessageId);
                }
            }

            logger.LogInformation(
                "Message {GraphMessageId} (mail message {MailMessageId}) routed to {Destination} after the Pace step: {InvoiceCount} invoice(s) sent to Pace, mailbox status {MailboxStatus}, final status {FinalStatus}.",
                state.GraphMessageId,
                mailMessageId,
                destination,
                state.Submissions.Count,
                (ApStatus)state.MailStatusId,
                status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Pace submission(s) {PaceSubmissionIds} of mail message {MailMessageId} completed but mail routing failed; the next run will redo the routing.",
                string.Join(", ", paceSubmissionIds),
                mailMessageId);
        }
    }

    /// <summary>
    /// The worst of the mailbox step's verdict and every Pace outcome: any error is Errors, otherwise anything
    /// that needs review is NeedsReview, otherwise Processed.
    /// </summary>
    internal static ApStatus DecideMailStatus(ApStatus mailboxStatus, IEnumerable<MailRoutingSubmission> submissions)
    {
        var worst = mailboxStatus switch
        {
            ApStatus.MailError => ApStatus.MailError,
            ApStatus.MailNeedsReview or ApStatus.MailSkipped or ApStatus.MailDuplicate => ApStatus.MailNeedsReview,
            _ => ApStatus.MailProcessed
        };

        foreach (var submission in submissions)
        {
            var paceStatus = PaceSummaryEntry.KindOf(submission.StatusCode, submission.RequiresReview) switch
            {
                PaceSummaryKind.Error => ApStatus.MailError,
                PaceSummaryKind.NeedsReview => ApStatus.MailNeedsReview,
                _ => ApStatus.MailProcessed
            };

            if (APProcessor.Severity(paceStatus) > APProcessor.Severity(worst))
            {
                worst = paceStatus;
            }
        }

        return worst;
    }

    /// <summary>
    /// The email's stored reason: the mailbox step's own reason, followed by every Pace error or review reason
    /// not already in it - so re-routing the same email (the recovery sweep) never repeats one.
    /// </summary>
    internal static string? BuildMailReason(string? mailboxReason, IEnumerable<MailRoutingSubmission> submissions)
    {
        var reasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(mailboxReason))
        {
            reasons.Add(mailboxReason);
        }

        foreach (var submission in submissions)
        {
            if (PaceSummaryEntry.KindOf(submission.StatusCode, submission.RequiresReview) != PaceSummaryKind.Success
                && !string.IsNullOrWhiteSpace(submission.ErrorMessage)
                && !reasons.Any(reason => reason.Contains(submission.ErrorMessage, StringComparison.Ordinal)))
            {
                reasons.Add(submission.ErrorMessage);
            }
        }

        return reasons.Count == 0 ? mailboxReason : string.Join(" ", reasons);
    }

    /// <summary>
    /// Replays mail routing for submissions whose Pace outcome is already final but whose routing never got
    /// confirmed - e.g. the process crashed, a prior run's routing failed, the email was still waiting on a
    /// sibling invoice, or the summary email could not be sent. Runs at the top of every ProcessPendingAsync
    /// call so a stuck email self-heals on the next scheduled run rather than staying in the Inbox. A row whose
    /// summary line already went out is replayed without being reported again.
    /// </summary>
    private async Task RecoverUnroutedSubmissionsAsync(CancellationToken cancellationToken)
    {
        var unrouted = await paceSubmissionRepository.ClaimUnroutedFinalSubmissionsAsync(cancellationToken);

        foreach (var emailGroup in unrouted.GroupBy(submission => submission.MailMessageId))
        {
            var first = emailGroup.First();
            var email = runSummary.Email(first.MailMessageId, first.Subject, first.SenderAddress, first.ReceivedOn);

            foreach (var submission in emailGroup.Where(submission => !submission.AlreadyNotified))
            {
                var summary = PaceBillSummary.TryReadFrom(submission.ResponseJson);
                var reason = string.IsNullOrWhiteSpace(submission.ErrorMessage)
                    ? submission.RequiresReview
                        ? $"Pace invoice '{submission.InvoiceNumber}' for PO '{submission.CustomerPO}' requires review."
                        : null
                    : submission.ErrorMessage;

                email.PaceEntries.Add(new PaceSummaryEntry(
                    PaceSummaryEntry.KindOf(submission.StatusCode, submission.RequiresReview),
                    submission.PaceSubmissionId,
                    submission.InvoiceId,
                    submission.InvoiceNumber,
                    submission.CustomerPO,
                    submission.InvoiceDate,
                    submission.Total,
                    summary?.VendorId ?? submission.BillVendor ?? submission.PaceVendorAccountNumber,
                    summary?.VendorName ?? submission.ClientName,
                    submission.StatusCode,
                    submission.PaceBillBatchId,
                    submission.PaceBillId,
                    reason,
                    summary is { Case: not PaceInvoiceCase.Other } ? summary : null));
            }

            await RouteEmailIfFinishedAsync(first.MailMessageId, emailGroup.Select(submission => submission.PaceSubmissionId).ToList(), cancellationToken);
        }

        if (unrouted.Count > 0)
        {
            logger.LogInformation(
                "Pace mail-routing recovery: retried {Count} previously-completed submission(s) whose mail routing had not been confirmed.",
                unrouted.Count);
        }
    }

    private static DateTime CalculateNextAttemptOn(int attemptCount)
    {
        var delayMinutes = Math.Min(Math.Pow(2, Math.Max(1, attemptCount)), 60);
        return DateTime.UtcNow.AddMinutes(delayMinutes);
    }
}
