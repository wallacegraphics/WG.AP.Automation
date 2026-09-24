using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;
using WG.AP.Processor.Logging;

namespace WG.AP.Processor;

public sealed class PaceInvoiceProcessor(
    IMailSource mailSource,
    MailMessageRepository mailMessageRepository,
    PaceSubmissionRepository paceSubmissionRepository,
    IPaceInvoiceService paceInvoiceService,
    ErrorNotifier errorNotifier,
    ILogger<PaceInvoiceProcessor> logger)
{
    /// <summary>
    /// Subject prefix of the one summary email each run sends for its Pace step, covering every Pace error and
    /// every needs-review invoice together - there are no per-invoice Pace alert emails.
    /// </summary>
    internal const string PaceSummarySubjectPrefix = "AP Automation - Pace summary";

    // A stored Pace error starts by naming the invoice and PO; the summary line already names both.
    private static readonly Regex ReasonInvoiceAndPoPrefixRegex = new(@"^Pace invoice '.*?' for PO '.*?':\s*", RegexOptions.Compiled);

    public Task ProcessPendingAsync(CancellationToken cancellationToken) =>
        ProcessPendingAsync(ProcessingRunContext.CurrentRunId, cancellationToken);

    public async Task ProcessPendingAsync(long? processingRunId, CancellationToken cancellationToken)
    {
        var enqueued = await paceSubmissionRepository.EnqueueExtractedInvoicesAsync(cancellationToken);
        logger.LogInformation("Pace submission enqueue complete: {EnqueuedCount} invoice(s) queued.", enqueued);

        var processed = 0;
        var summaryEntries = new List<PaceSummaryEntry>();

        // Every submission this run tries to route, live or recovered. Whatever is still unrouted at the end
        // has its lease released, so the next run retries it straight away rather than after the lease lapses.
        var routingAttempts = new HashSet<long>();

        try
        {
            await RecoverUnroutedSubmissionsAsync(summaryEntries, routingAttempts, cancellationToken);

            while (true)
            {
                var claim = await paceSubmissionRepository.ClaimNextAsync(processingRunId, cancellationToken);

                if (claim is null)
                {
                    break;
                }

                processed++;
                await ProcessClaimAsync(claim, summaryEntries, routingAttempts, cancellationToken);
            }

            logger.LogInformation("Pace submission processing complete: {ProcessedCount} submission(s) processed.", processed);
        }
        finally
        {
            // Entries here were already finalized, so they will never be claimed again - send the summary even
            // when a later claim threw. If this send fails too, their NotifiedOn stays NULL and the recovery
            // sweep puts them in the next run's summary.
            await SendPaceSummaryAsync(summaryEntries);

            // Only after the summary: a row is not done until its summary line is sent, so its lease must hold
            // until then or an overlapping run could put it in a second summary.
            await ReleaseUnfinishedRoutingAsync(routingAttempts);
        }
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

    private async Task SendPaceSummaryAsync(List<PaceSummaryEntry> summaryEntries)
    {
        if (summaryEntries.Count == 0)
        {
            return;
        }

        var invoiceIds = string.Join(", ", summaryEntries.Select(entry => entry.InvoiceId));

        try
        {
            // CancellationToken.None: a cancelled run must still deliver notifications for work it already committed.
            var sent = await errorNotifier.NotifyAsync(
                BuildPaceSummarySubject(summaryEntries),
                BuildPaceSummaryBody(summaryEntries),
                CancellationToken.None);

            if (!sent)
            {
                logger.LogWarning(
                    "Pace summary for {EntryCount} invoice(s) could not be sent; they will be included in the next run's summary: {InvoiceIds}.",
                    summaryEntries.Count,
                    invoiceIds);
                return;
            }

            // Only now is each entry's notification delivered. An entry whose move also succeeded is fully
            // routed; one whose move failed is left for the sweep to redo the move alone, without reporting again.
            await paceSubmissionRepository.MarkNotifiedAsync(
                summaryEntries.Select(entry => entry.PaceSubmissionId).ToList(),
                CancellationToken.None);
            await paceSubmissionRepository.MarkMailRoutedAsync(
                summaryEntries.Where(entry => entry.Moved).Select(entry => entry.PaceSubmissionId).ToList(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Never mask the exception that ended the claim loop with a notification or bookkeeping failure.
            logger.LogError(
                exception,
                "Failed to send or record the Pace summary for {EntryCount} invoice(s): {InvoiceIds}.",
                summaryEntries.Count,
                invoiceIds);
        }
    }

    private async Task ProcessClaimAsync(
        PaceSubmissionClaim claim,
        List<PaceSummaryEntry> summaryEntries,
        HashSet<long> routingAttempts,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(claim.PaceBillId) || !string.IsNullOrWhiteSpace(claim.PaceBillLineId))
            {
                await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
                {
                    PaceSubmissionId = claim.PaceSubmissionId,
                    ClaimToken = claim.ClaimToken,
                    StatusCode = PaceSubmissionStatus.AlreadyEntered,
                    PaceBillBatchId = claim.PaceBillBatchId,
                    PaceBillId = claim.PaceBillId,
                    PaceBillLineId = claim.PaceBillLineId,
                    ErrorMessage = "Pace IDs were already recorded locally before this retry; skipping duplicate Pace write.",
                    RequiresReview = false
                }, cancellationToken);

                return;
            }

            var fields = PaceInvoiceFieldsParser.Parse(claim.FieldsJson);
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
                var savedRetry = await paceSubmissionRepository.RetryLaterAsync(new PaceSubmissionRetry
                {
                    PaceSubmissionId = claim.PaceSubmissionId,
                    ClaimToken = claim.ClaimToken,
                    NextAttemptOn = CalculateNextAttemptOn(claim.AttemptCount),
                    ResponseJson = result.ResponseJson,
                    ErrorMessage = result.ErrorMessage
                }, cancellationToken);

                if (!savedRetry)
                {
                    logger.LogWarning("Pace submission {PaceSubmissionId} retry update skipped because its claim token no longer matched.", claim.PaceSubmissionId);
                }

                return;
            }

            var savedCompletion = await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
            {
                PaceSubmissionId = claim.PaceSubmissionId,
                ClaimToken = claim.ClaimToken,
                StatusCode = result.StatusCode,
                ResponseJson = result.ResponseJson,
                PaceBillBatchId = result.PaceBillBatchId,
                PaceBillId = result.PaceBillId,
                PaceBillLineId = result.PaceBillLineId,
                ErrorMessage = result.ErrorMessage,
                RequiresReview = result.RequiresReview,
                BillVendor = result.BillVendor
            }, cancellationToken);

            if (!savedCompletion)
            {
                logger.LogWarning("Pace submission {PaceSubmissionId} completion skipped because its claim token no longer matched.", claim.PaceSubmissionId);
            }

            if (savedCompletion && result.RequiresReview)
            {
                await RouteAndMarkAsync(
                    routingAttempts,
                    claim.PaceSubmissionId,
                    claim.InvoiceId,
                    () => RouteNeedsReviewAsync(claim, result, summaryEntries, cancellationToken),
                    cancellationToken);
            }
            else if (savedCompletion && result.StatusCode is PaceSubmissionStatus.Error or PaceSubmissionStatus.PoNotReceived)
            {
                await RouteAndMarkAsync(
                    routingAttempts,
                    claim.PaceSubmissionId,
                    claim.InvoiceId,
                    () => RoutePaceErrorAsync(claim, result.StatusCode, result.ErrorMessage, summaryEntries, cancellationToken),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            var savedCompletion = await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
            {
                PaceSubmissionId = claim.PaceSubmissionId,
                ClaimToken = claim.ClaimToken,
                StatusCode = PaceSubmissionStatus.Error,
                ErrorMessage = exception.Message,
                RequiresReview = false
            }, cancellationToken);

            if (!savedCompletion)
            {
                logger.LogWarning("Pace submission {PaceSubmissionId} parse-error update skipped because its claim token no longer matched.", claim.PaceSubmissionId);
            }

            if (savedCompletion)
            {
                await RouteAndMarkAsync(
                    routingAttempts,
                    claim.PaceSubmissionId,
                    claim.InvoiceId,
                    () => RoutePaceErrorAsync(
                        claim,
                        PaceSubmissionStatus.Error,
                        $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': stored invoice fields could not be processed. {exception.Message}",
                        summaryEntries,
                        cancellationToken),
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Runs a mail-routing action after a Pace submission is already final/non-reclaimable, without letting a
    /// routing failure abort the rest of the claim loop. Any exception here is logged and swallowed - the
    /// submission's MailRoutedOn stays NULL, so RecoverUnroutedSubmissionsAsync redoes just this routing step
    /// on the next run instead of the invoice being silently stuck. The route action itself records the row's
    /// summary entry or marks it routed, because when that happens differs per case.
    /// </summary>
    private async Task RouteAndMarkAsync(
        HashSet<long> routingAttempts,
        long paceSubmissionId,
        long invoiceId,
        Func<Task> routeAction,
        CancellationToken cancellationToken)
    {
        routingAttempts.Add(paceSubmissionId);

        try
        {
            await routeAction();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Pace submission {PaceSubmissionId} (invoice {InvoiceId}) completed but mail routing failed; the next run will redo the routing.",
                paceSubmissionId,
                invoiceId);
        }
    }

    /// <summary>
    /// Replays mail routing for submissions whose Pace outcome is already final but whose routing never got
    /// confirmed - e.g. the process crashed, a prior run's routing exception was caught by RouteAndMarkAsync,
    /// or the summary email could not be sent. Runs at the top of every ProcessPendingAsync call so a stuck
    /// invoice self-heals on the next scheduled run rather than staying in the Inbox indefinitely. A row whose
    /// summary line already went out is replayed without being reported again.
    /// </summary>
    private async Task RecoverUnroutedSubmissionsAsync(
        List<PaceSummaryEntry> summaryEntries,
        HashSet<long> routingAttempts,
        CancellationToken cancellationToken)
    {
        var unrouted = await paceSubmissionRepository.ClaimUnroutedFinalSubmissionsAsync(cancellationToken);

        foreach (var submission in unrouted)
        {
            var reason = string.IsNullOrWhiteSpace(submission.ErrorMessage)
                ? submission.RequiresReview
                    ? $"Pace invoice '{submission.InvoiceNumber}' for PO '{submission.CustomerPO}' requires review."
                    : $"Pace invoice '{submission.InvoiceNumber}' for PO '{submission.CustomerPO}': Pace validation failed."
                : submission.ErrorMessage;

            await RouteAndMarkAsync(
                routingAttempts,
                submission.PaceSubmissionId,
                submission.InvoiceId,
                () => submission.RequiresReview
                    ? RouteNeedsReviewAsync(
                        submission.PaceSubmissionId,
                        submission.InvoiceId,
                        submission.InvoiceNumber,
                        submission.CustomerPO,
                        submission.BillVendor ?? submission.PaceVendorAccountNumber,
                        submission.StatusCode,
                        submission.PaceBillBatchId,
                        submission.PaceBillId,
                        reason,
                        submission.MailMessageId,
                        submission.GraphMessageId,
                        submission.AlreadyNotified,
                        summaryEntries,
                        cancellationToken)
                    : RoutePaceErrorAsync(
                        submission.PaceSubmissionId,
                        submission.InvoiceId,
                        submission.InvoiceNumber,
                        submission.CustomerPO,
                        submission.StatusCode,
                        submission.MailMessageId,
                        submission.GraphMessageId,
                        reason,
                        submission.AlreadyNotified,
                        summaryEntries,
                        cancellationToken),
                cancellationToken);
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

    /// <param name="statusCode">
    /// The submission's actual outcome (Error or PoNotReceived), recorded on the summary entry as-is - the
    /// recovery path uses the stored status, so passing anything else would label the same invoice
    /// differently depending on which path routed it.
    /// </param>
    private Task RoutePaceErrorAsync(
        PaceSubmissionClaim claim,
        string statusCode,
        string? errorMessage,
        List<PaceSummaryEntry> summaryEntries,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': Pace validation failed."
            : errorMessage;

        return RoutePaceErrorAsync(
            claim.PaceSubmissionId,
            claim.InvoiceId,
            claim.InvoiceNumber,
            claim.CustomerPO,
            statusCode,
            claim.MailMessageId,
            claim.GraphMessageId,
            message,
            alreadyNotified: false,
            summaryEntries,
            cancellationToken);
    }

    /// <summary>
    /// Takes primitive fields rather than a <see cref="PaceSubmissionClaim"/> so both live processing and
    /// <see cref="RecoverUnroutedSubmissionsAsync"/> (which only has a persisted row, not a live claim) can
    /// share this routing logic.
    /// <para>
    /// Sends no email of its own: the error is reported once, in the run's Pace summary
    /// (<see cref="SendPaceSummaryAsync"/>), which is also where the row is marked notified/routed. The entry
    /// is added before the move, so a failed move is still reported. A row whose summary line was already
    /// delivered gets no entry and is marked routed as soon as its move succeeds.
    /// </para>
    /// </summary>
    private async Task RoutePaceErrorAsync(
        long paceSubmissionId,
        long invoiceId,
        string? invoiceNumber,
        string? customerPO,
        string statusCode,
        long mailMessageId,
        string graphMessageId,
        string message,
        bool alreadyNotified,
        List<PaceSummaryEntry> summaryEntries,
        CancellationToken cancellationToken)
    {
        await mailMessageRepository.SetStatusAsync(mailMessageId, ApStatus.MailError, message, cancellationToken);

        PaceSummaryEntry? entry = null;

        if (!alreadyNotified)
        {
            entry = new PaceSummaryEntry(
                MailDestinationFolder.Errors,
                paceSubmissionId,
                invoiceId,
                invoiceNumber,
                customerPO,
                PaceVendorAccountNumber: null,
                statusCode,
                PaceBillBatchId: null,
                PaceBillId: null,
                message);
            summaryEntries.Add(entry);
        }

        await mailSource.MoveMessageAsync(graphMessageId, MailDestinationFolder.Errors, cancellationToken);

        logger.LogInformation(
            "Pace validation error routed message {GraphMessageId} for invoice {InvoiceId} to Errors.",
            graphMessageId,
            invoiceId);

        if (entry is not null)
        {
            entry.Moved = true;
        }
        else
        {
            await paceSubmissionRepository.MarkMailRoutedAsync(paceSubmissionId, cancellationToken);
        }
    }

    private Task RouteNeedsReviewAsync(
        PaceSubmissionClaim claim,
        PaceInvoiceSubmissionResult result,
        List<PaceSummaryEntry> summaryEntries,
        CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}' requires review."
            : result.ErrorMessage;

        return RouteNeedsReviewAsync(
            claim.PaceSubmissionId,
            claim.InvoiceId,
            claim.InvoiceNumber,
            claim.CustomerPO,
            result.BillVendor ?? claim.PaceVendorAccountNumber,
            result.StatusCode,
            result.PaceBillBatchId,
            result.PaceBillId,
            reason,
            claim.MailMessageId,
            claim.GraphMessageId,
            alreadyNotified: false,
            summaryEntries,
            cancellationToken);
    }

    /// <summary>
    /// Takes primitive fields rather than a <see cref="PaceSubmissionClaim"/> so both live processing and
    /// <see cref="RecoverUnroutedSubmissionsAsync"/> (which only has a persisted row, not a live claim) can
    /// share this routing logic.
    /// <para>
    /// The summary goes out once, at the end of the run, so a summary-bound row is marked notified/routed there
    /// (<see cref="SendPaceSummaryAsync"/>), not here. A row whose summary line was already delivered gets no
    /// entry and is marked routed as soon as its move succeeds.
    /// </para>
    /// </summary>
    private async Task RouteNeedsReviewAsync(
        long paceSubmissionId,
        long invoiceId,
        string? invoiceNumber,
        string? customerPO,
        string? billVendor,
        string statusCode,
        string? paceBillBatchId,
        string? paceBillId,
        string reason,
        long mailMessageId,
        string graphMessageId,
        bool alreadyNotified,
        List<PaceSummaryEntry> summaryEntries,
        CancellationToken cancellationToken)
    {
        await mailMessageRepository.SetStatusAsync(mailMessageId, ApStatus.MailNeedsReview, reason, cancellationToken);

        // Added after the status is committed (so the summary never reports an unsaved route) and before the
        // move (so a move failure does not drop this finalized submission from the summary).
        PaceSummaryEntry? entry = null;

        if (!alreadyNotified)
        {
            entry = new PaceSummaryEntry(
                MailDestinationFolder.NeedsReview,
                paceSubmissionId,
                invoiceId,
                invoiceNumber,
                customerPO,
                billVendor,
                statusCode,
                paceBillBatchId,
                paceBillId,
                reason);
            summaryEntries.Add(entry);
        }

        await mailSource.MoveMessageAsync(graphMessageId, MailDestinationFolder.NeedsReview, cancellationToken);

        logger.LogInformation(
            "Pace invoice {InvoiceId} (no matching PO) routed message {GraphMessageId} to NeedsReview.",
            invoiceId,
            graphMessageId);

        if (entry is not null)
        {
            entry.Moved = true;
        }
        else
        {
            await paceSubmissionRepository.MarkMailRoutedAsync(paceSubmissionId, cancellationToken);
        }
    }

    private static string BuildPaceSummarySubject(IReadOnlyList<PaceSummaryEntry> entries)
    {
        var errorCount = entries.Count(entry => entry.Destination == MailDestinationFolder.Errors);
        var needsReviewCount = entries.Count - errorCount;

        return $"{PaceSummarySubjectPrefix} ({errorCount} {(errorCount == 1 ? "error" : "errors")}, {needsReviewCount} need review)";
    }

    private static string BuildPaceSummaryBody(IReadOnlyList<PaceSummaryEntry> entries)
    {
        var errors = entries.Where(entry => entry.Destination == MailDestinationFolder.Errors).ToList();
        var needsReview = entries.Where(entry => entry.Destination == MailDestinationFolder.NeedsReview).ToList();
        var blocks = new List<string>
        {
            "<p>This is a summary of the Pace step of the AP Automation run just completed. Please review the "
            + "Errors and NeedsReview folders as needed.</p>"
        };

        if (errors.Count > 0)
        {
            blocks.Add($"<p><b>=== Errors ({errors.Count}) ===</b></p>");
            blocks.AddRange(errors.Select(entry =>
                $"<p><b>Invoice {Html(entry.InvoiceNumber ?? "unknown")}</b> (PO {Html(entry.CustomerPO ?? "unknown")}): "
                + $"{BuildStatusLabel(entry)}{Html(BuildReasonForSummary(entry.Reason))}<br>\n{BuildMoveLine(entry)}</p>"));
        }

        if (needsReview.Count > 0)
        {
            blocks.Add($"<p><b>=== NeedsReview ({needsReview.Count}) ===</b></p>");
            blocks.Add("<p>These invoices had no matching purchase order in Pace. Please verify the coding and PO number; "
                + "entries marked 'No bill was created' need manual entry after the listed problem is fixed.</p>");
            blocks.AddRange(needsReview.Select(entry =>
            {
                var billDescription = entry.StatusCode switch
                {
                    PaceSubmissionStatus.BillCreated =>
                        $"Bill {Html(entry.PaceBillId)} created in batch {Html(entry.PaceBillBatchId)} using the Pace vendor default GL account/department.",
                    PaceSubmissionStatus.DryRunPrepared =>
                        "Pace writes are disabled; a bill would be created using the Pace vendor default GL account/department.",
                    _ =>
                        $"No bill was created (status {Html(entry.StatusCode)}): {Html(BuildReasonForSummary(entry.Reason))}"
                };

                return $"<p><b>Invoice {Html(entry.InvoiceNumber ?? "unknown")}</b> (PO {Html(entry.CustomerPO ?? "unknown")}, vendor {Html(entry.PaceVendorAccountNumber ?? "unknown")}): "
                    + $"no matching Pace PO was found. {billDescription}<br>\n{BuildMoveLine(entry)}</p>";
            }));
        }

        return string.Join("\n", blocks);
    }

    /// <summary>
    /// PoNotReceived shares the Errors folder with real errors but is not one - the PO exists, its goods just
    /// haven't been received in Pace yet - so its line says so up front.
    /// </summary>
    private static string BuildStatusLabel(PaceSummaryEntry entry) =>
        entry.StatusCode == PaceSubmissionStatus.PoNotReceived ? "PO not received: " : string.Empty;

    /// <summary>
    /// Where the email actually is. The summary is sent at the end of the run, so this reflects the move's real
    /// outcome - a failed move is not reported as if the email were already in its folder.
    /// </summary>
    private static string BuildMoveLine(PaceSummaryEntry entry) =>
        entry.Moved
            ? $"Moved to: {entry.Destination}"
            : $"Could not be moved to {entry.Destination}, still in Inbox";

    /// <summary>
    /// The stored error without its leading "Pace invoice '...' for PO '...': ", since the summary line already
    /// names the invoice and PO, and with its first letter capitalised.
    /// </summary>
    internal static string BuildReasonForSummary(string reason)
    {
        var trimmed = ReasonInvoiceAndPoPrefixRegex.Replace(reason, string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return reason;
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;

    private sealed record PaceSummaryEntry(
        MailDestinationFolder Destination,
        long PaceSubmissionId,
        long InvoiceId,
        string? InvoiceNumber,
        string? CustomerPO,
        string? PaceVendorAccountNumber,
        string StatusCode,
        string? PaceBillBatchId,
        string? PaceBillId,
        string Reason)
    {
        /// <summary>
        /// Set once this entry's message has been moved. Only moved entries are marked fully routed after the
        /// summary is sent; the rest keep MailRoutedOn NULL so the sweep redoes the move.
        /// </summary>
        public bool Moved { get; set; }
    }
}
