using System.Text.Json;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Email;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;
using WG.AP.Processor.Logging;

namespace WG.AP.Processor;

/// <summary>
/// Reads new mail, extracts invoices from its PDF attachments, records everything, and files each
/// message into a destination folder.
/// </summary>
/// <remarks>
/// Two properties hold this together and neither should be traded away:
/// <list type="bullet">
/// <item>
/// <b>Nothing is processed twice.</b> Every message is recorded and claimed before any work is done
/// (<see cref="MailMessageRepository.DiscoverAndClaimAsync"/>). A message already in a final status is
/// not claimed, and an unclaimed message is skipped entirely — not parsed, not moved. This matters
/// most for the skipped case, where no folder move happens, so the database row is the <em>only</em>
/// record that the decision was already taken.
/// </item>
/// <item>
/// <b>An outage retries; a bad document does not.</b> <see cref="HttpRequestException"/> and
/// <see cref="TaskCanceledException"/> from the extractor propagate out of the message loop, so the
/// delta link is never committed and Graph re-delivers the batch next run. Everything else is a
/// verdict on that message and is recorded as one.
/// </item>
/// <item>
/// <b>Every message stands alone.</b> Graph's conversation/thread id is never read or stored, so a
/// reply is judged purely on its own sender and its own attachments - never on what an earlier or
/// later message in the same Outlook thread contained. A message with no way to reach a verdict
/// (unresolved client, or nothing attached) is left as <c>MailNew</c> in the Inbox indefinitely rather
/// than guessed at from thread context, which would mean trusting content an attacker controls
/// (subject text, PDF content, or simply replying into an existing trusted thread).
/// </item>
/// </list>
/// </remarks>
public sealed class APProcessor(
    IMailSource mailSource,
    MailboxSyncProcessor mailboxSyncProcessor,
    IInvoiceFieldExtractor invoiceFieldExtractor,
    ProcessingRunRepository processingRunRepository,
    MailMessageRepository mailMessageRepository,
    MailAttachmentRepository mailAttachmentRepository,
    InvoiceRepository invoiceRepository,
    ClientRepository clientRepository,
    ExtractionPromptRepository extractionPromptRepository,
    AttachmentFileStore attachmentFileStore,
    ErrorNotifier errorNotifier,
    IOptions<MailboxOptions> mailboxOptions,
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<AlertOptions> alertOptions,
    ILogger<APProcessor> logger)
{
    public async Task ProcessInvoicesAsync(CancellationToken cancellationToken)
    {
        var mailbox = mailboxOptions.Value.ToMailboxRef();
        long? processingRunId = null;
        var deliveredCount = 0;
        var messageCount = 0;
        var invoiceCount = 0;

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(alertOptions.Value.TimeZoneId);

            await mailSource.ValidateAuthAsync(cancellationToken);
            await mailSource.EnsureFoldersExistAsync(cancellationToken);

            processingRunId = await processingRunRepository.StartAsync(mailbox, cancellationToken);
            ProcessingRunContext.CurrentRunId = processingRunId;

            // Configuration is read once per run, not per message: it cannot change mid-run, and
            // onboarding a client is meant to be a few INSERTs that the next run picks up.
            var clientCatalog = await clientRepository.LoadByEmailDomainAsync(cancellationToken);
            var prompts = await extractionPromptRepository.LoadActiveAsync(cancellationToken);
            var mailFolders = await mailMessageRepository.LoadMailFoldersAsync(cancellationToken);

            var batch = await mailboxSyncProcessor.GetNewMessagesAsync(cancellationToken);
            var outcomes = new Dictionary<ApStatus, int>();
            var digestEntries = new List<DigestEntry>();
            var skippedAsAlreadyFinal = 0;

            foreach (var message in batch.Messages)
            {
                var claim = await mailMessageRepository.DiscoverAndClaimAsync(mailbox, processingRunId.Value, message, cancellationToken);
                ProcessingRunContext.CurrentMailMessageId = claim.MailMessageId;

                try
                {
                    if (!claim.Claimed)
                    {
                        if (processingRunId is not null)
                        {
                            var duplicateReplay = await mailMessageRepository.RecordAlreadyFinalReplayNeedsReviewAsync(
                                mailbox,
                                processingRunId.Value,
                                message,
                                cancellationToken);

                            if (duplicateReplay is not null)
                            {
                                deliveredCount++;
                                outcomes[ApStatus.MailNeedsReview] = outcomes.GetValueOrDefault(ApStatus.MailNeedsReview) + 1;

                                var duplicateReplayOutcome = new MessageOutcome(
                                    ApStatus.MailNeedsReview,
                                    duplicateReplay.ErrorMessage,
                                    InvoiceCount: 0,
                                    AttachmentCount: message.Attachments.Count(a => !a.IsInline),
                                    PdfCount: message.Attachments.Count(a => !a.IsInline && IsPdf(a)),
                                    SuccessCount: 0);

                                var destination = await MoveIfRoutedAsync(message.Id, ApStatus.MailNeedsReview, mailFolders, cancellationToken);

                                if (destination is not null)
                                {
                                    digestEntries.Add(new DigestEntry(
                                        destination.Value,
                                        message.ReceivedDateTime,
                                        BuildDigestLine(message, duplicateReplayOutcome, timeZone),
                                        BuildDigestErrorDetail(duplicateReplayOutcome),
                                        Status: ApStatus.MailNeedsReview,
                                        Subject: message.Subject));
                                }

                                continue;
                            }
                        }

                        // Already decided on an earlier run. Re-delivery is normal — it is how the
                        // crash-safe delta ordering works — so this is not a warning.
                        skippedAsAlreadyFinal++;
                        logger.LogInformation(
                            "Message {MessageId} is already in status {StatusId}; leaving it alone.",
                            message.Id, claim.StatusId);
                        continue;
                    }

                    deliveredCount++;
                    messageCount++;

                    logger.LogInformation(
                        "Message {MessageId} from {Sender} received {ReceivedAt}: {AttachmentCount} attachment(s), attempt {AttemptCount}.",
                        message.Id, message.SenderAddress ?? "unknown", message.ReceivedDateTime,
                        message.Attachments.Count(a => !a.IsInline), claim.AttemptCount);

                    var result = await ProcessMessageAsync(mailbox, claim, message, clientCatalog, prompts, cancellationToken);

                    invoiceCount += result.InvoiceCount;
                    outcomes[result.Status] = outcomes.GetValueOrDefault(result.Status) + 1;

                    if (result.Status == ApStatus.MailNew)
                    {
                        // Not a verdict - an unresolved-client message left exactly as claimed (see
                        // ProcessMessageAsync). Nothing to persist or move: the message stays visible in
                        // the Inbox, and being the one non-final mail status, remains claimable again
                        // later if Graph ever resurfaces it once the client is configured.
                    }
                    else
                    {
                        await mailMessageRepository.SetStatusAsync(claim.MailMessageId, result.Status, result.ErrorMessage, cancellationToken);
                        var destination = await MoveIfRoutedAsync(message.Id, result.Status, mailFolders, cancellationToken);

                        if (destination is not null)
                        {
                            digestEntries.Add(new DigestEntry(
                                destination.Value,
                                message.ReceivedDateTime,
                                BuildDigestLine(message, result, timeZone),
                                BuildDigestErrorDetail(result),
                                Status: result.Status,
                                Subject: message.Subject));
                        }
                    }
                }
                finally
                {
                    ProcessingRunContext.CurrentMailMessageId = null;
                }
            }

            await mailboxSyncProcessor.CommitAsync(batch, cancellationToken);

            logger.LogInformation(
                "Mailbox scan complete for {MailboxUser}: {DeliveredCount} delivered, {MessageCount} processed, "
                + "{AlreadyFinal} already final, {InvoiceCount} invoice(s). Outcomes: {Outcomes}.",
                mailbox.MailboxUser, deliveredCount, messageCount, skippedAsAlreadyFinal, invoiceCount,
                BuildOutcomesSummary(outcomes, digestEntries, timeZone));

            var shouldLogErrorRows =
                 outcomes.GetValueOrDefault(ApStatus.MailNeedsReview) > 0
                 || outcomes.GetValueOrDefault(ApStatus.MailError) > 0
                 || outcomes.GetValueOrDefault(ApStatus.MailSkipped) > 0
                 || outcomes.GetValueOrDefault(ApStatus.MailDuplicate) > 0;

            if (processingRunId is not null && shouldLogErrorRows)
            {
                var errorRows = await mailMessageRepository.LoadErrorLogRowsForRunAsync(processingRunId.Value, cancellationToken);

                // Two dbo.MailMessage rows can render to the identical line (e.g. Graph reissuing a
                // message's id after it moves records the same physical email twice) - de-duplicated
                // on the rendered text itself, not the raw row, so rows differing only by the
                // millisecond precision BuildErrorLogLine doesn't print still collapse to one line.
                var loggedLines = new HashSet<string>();

                foreach (var errorRow in errorRows)
                {
                    var line = BuildErrorLogLine(errorRow);

                    if (loggedLines.Add(line))
                    {
                        logger.LogInformation("{ErrorLine}", line);
                    }
                }
            }

            if (digestEntries.Count > 0)
            {
                await errorNotifier.NotifyAsync(
                    $"AP Automation - Processing Summary ({mailbox.MailboxUser})",
                    BuildDigestBody(digestEntries, outcomes),
                    cancellationToken);
            }

            if (processingRunId is not null)
            {
                await processingRunRepository.FinishAsync(processingRunId.Value, messageCount, invoiceCount, isSuccessful: true, errorMessage: null, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mailbox processing failed.");

            // Sent first, before FinishAsync: the team should still hear about the failure even if
            // recording it against dbo.ProcessingRun also fails (e.g. the database is what's down).
            // HTML-encoded because SendMailAsync sends BodyType.Html, and exception.Message is not
            // guaranteed free of '<'/'&'.
            await errorNotifier.NotifyAsync(
                "AP Automation - Mailbox processing failed",
                $"<p>{Html(exception.Message)}</p>\n<p>Processed {messageCount} message(s), {invoiceCount} invoice(s) before failing.</p>",
                cancellationToken);

            if (processingRunId is not null)
            {
                await processingRunRepository.FinishAsync(processingRunId.Value, messageCount, invoiceCount, isSuccessful: false, exception.Message, cancellationToken);
            }

            Environment.ExitCode = 1;
        }
        finally
        {
            ProcessingRunContext.CurrentRunId = null;
        }
    }

    internal sealed record MessageOutcome(
        ApStatus Status,
        string? ErrorMessage,
        int InvoiceCount,
        int AttachmentCount,
        int PdfCount,
        int SuccessCount,
        IReadOnlyList<(string FileName, ApStatus MailStatus, string? Reason)>? PdfOutcomes = null);

    /// <summary>
    /// One entry in the per-run summary email: the routed destination and received time (used to
    /// group and order entries), the one-line summary (see <see cref="BuildDigestLine"/>), and any
    /// per-PDF failure detail lines (see <see cref="BuildDigestErrorDetail"/>) to render under it.
    /// </summary>
    internal sealed record DigestEntry(
        MailDestinationFolder Destination,
        DateTimeOffset? ReceivedAt,
        string SummaryLine,
        IReadOnlyList<string> ErrorDetailLines,
        ApStatus Status = default,
        string? Subject = null);

    /// <summary>
    /// The completion log line's "Outcomes: ..." text - one <c>Status=Count</c> pair per status, plus,
    /// only when duplicate-content collapsing (<see cref="DistinctByRenderedContent"/>) actually
    /// reduced a status's count, a trailing "(routed to Status=N: ...)" naming how many distinct
    /// messages that raw count collapsed to and which ones they were. A status with no collapsing
    /// (the common case) renders exactly as it always has.
    /// </summary>
    internal static string BuildOutcomesSummary(
        IReadOnlyDictionary<ApStatus, int> outcomes,
        IReadOnlyList<DigestEntry> digestEntries,
        TimeZoneInfo timeZone)
    {
        var distinctEntries = DistinctByRenderedContent(digestEntries);

        return string.Join(", ", outcomes.Select(pair =>
        {
            var text = $"{pair.Key}={pair.Value}";
            var distinctForStatus = distinctEntries.Where(entry => entry.Status == pair.Key).ToList();

            if (distinctForStatus.Count > 0 && distinctForStatus.Count < pair.Value)
            {
                var identified = string.Join("; ", distinctForStatus.Select(entry =>
                    $"\"{entry.Subject ?? "(no subject)"}\" received {FormatReceivedAt(entry.ReceivedAt, timeZone)}"));
                text += $" (routed to {pair.Key}={distinctForStatus.Count}: {identified})";
            }

            return text;
        }));
    }

    /// <summary>
    /// Records a message's attachments, extracts an invoice from each PDF, and decides where the
    /// message belongs.
    /// </summary>
    /// <remarks>
    /// The routing rules, in the order they are applied:
    /// <list type="bullet">
    /// <item>an unresolved client is left as <c>MailNew</c> - not our client yet, so nothing is recorded
    /// at all: no attachment rows, no extraction, no Invoice row. The message is left untouched in the
    /// Inbox for the team to handle manually</item>
    /// <item>a known client's message with no attachment at all (e.g. "sending it shortly") is also left
    /// as <c>MailNew</c> in the Inbox - there is nothing to record or review yet</item>
    /// <item>no PDF attachments but at least one non-PDF one — e.g. Excel-only mail — is
    /// <c>MailSkipped</c>, routed to NeedsReview so the ignored manifest stays visible</item>
    /// <item>a PDF that cannot be parsed at all is <c>MailError</c></item>
    /// <item>a missing required field or a duplicate number is <c>MailNeedsReview</c></item>
    /// <item>everything present and readable is <c>MailProcessed</c></item>
    /// </list>
    /// Where one email yields several PDFs with different verdicts, the worst wins for routing -
    /// but the recorded reason (see <see cref="BuildMessageErrorSummary"/>) still names every problem
    /// PDF individually and notes how many others in the same message were processed successfully.
    /// </remarks>
    private async Task<MessageOutcome> ProcessMessageAsync(
        MailboxRef mailbox,
        MailMessageClaim claim,
        MailMessageSummary message,
        IReadOnlyDictionary<string, ClientResolution> clientCatalog,
        IReadOnlyDictionary<int, ExtractionPromptRecord> prompts,
        CancellationToken cancellationToken)
    {
        // Client resolution comes first, before anything is written to the database: an unresolved
        // sender must leave no trace beyond the claim row already made by DiscoverAndClaimAsync - not
        // an attachment row, not a MailSkipped/NeedsReview verdict just because it also happens to have
        // no PDF attached. Left as MailNew rather than classified: the message stays untouched in the
        // Inbox for the team to handle manually once/if this sender is onboarded.
        var client = ClientRepository.Resolve(clientCatalog, message.SenderAddress);

        if (!client.IsKnown)
        {
            logger.LogInformation(
                "Message {MessageId} is from {Sender}, which matches no configured client; leaving it in the Inbox for manual handling.",
                message.Id, message.SenderAddress ?? "unknown");
            return new MessageOutcome(ApStatus.MailNew, null, InvoiceCount: 0, AttachmentCount: 0, PdfCount: 0, SuccessCount: 0);
        }

        if (!message.Attachments.Any(a => !a.IsInline))
        {
            // A known client's reply with no attachment at all has nothing to record and nothing to
            // review yet - left as MailNew, same as an unresolved client, so it stays in the Inbox
            // instead of being pulled into NeedsReview for no reason.
            logger.LogInformation(
                "Message {MessageId} from {Sender} has no attachments; leaving it in the Inbox.",
                message.Id, message.SenderAddress ?? "unknown");
            return new MessageOutcome(ApStatus.MailNew, null, InvoiceCount: 0, AttachmentCount: 0, PdfCount: 0, SuccessCount: 0);
        }

        // Every attachment is recorded, including the Excel ones nothing reads any more: a row with
        // Kind = 'Excel' is how "a manifest arrived and we ignored it" stays answerable later.
        var recorded = await mailAttachmentRepository.RecordAsync(claim.MailMessageId, message.Attachments, cancellationToken);
        var pdfs = recorded.Where(item => IsPdf(item.Attachment)).ToList();
        var attachmentCount = recorded.Count(item => !item.Attachment.IsInline);

        if (pdfs.Count == 0)
        {
            logger.LogInformation(
                "Message {MessageId} has no PDF attachment(s); routing to NeedsReview.", message.Id);
            var skipReason = $"No PDF attachment(s); {attachmentCount} non-PDF attachment(s) received.";
            return new MessageOutcome(ApStatus.MailSkipped, skipReason, InvoiceCount: 0, attachmentCount, PdfCount: 0, SuccessCount: 0);
        }

        // The cap exists so a document that reliably breaks extraction stops consuming every run.
        // NeedsReview rather than Error, deliberately: nobody should silently give up on a payable.
        if (claim.AttemptCount > databaseOptions.Value.MaxAttempts)
        {
            logger.LogWarning(
                "Message {MessageId} has been attempted {AttemptCount} times (cap {MaxAttempts}); routing to review.",
                message.Id, claim.AttemptCount, databaseOptions.Value.MaxAttempts);
            return new MessageOutcome(ApStatus.MailNeedsReview, $"Attempt cap of {databaseOptions.Value.MaxAttempts} reached.", InvoiceCount: 0, attachmentCount, pdfs.Count, SuccessCount: 0);
        }

        var request = BuildExtractionRequest(client, prompts);
        var worst = ApStatus.MailProcessed;
        var invoiceCount = 0;
        var pdfOutcomes = new List<(string FileName, ApStatus MailStatus, string? Reason)>();

        foreach (var pdf in pdfs)
        {
            var (invoiceStatus, mailStatus, reason) = await ProcessPdfAsync(
                mailbox, claim, message, pdf, client, request, cancellationToken);

            invoiceCount++;
            pdfOutcomes.Add((pdf.Attachment.Name, mailStatus, reason));

            if (Severity(mailStatus) > Severity(worst))
            {
                worst = mailStatus;
            }

            logger.LogInformation(
                "Message {MessageId} attachment '{FileName}': {InvoiceStatus}.",
                message.Id, pdf.Attachment.Name, invoiceStatus);
        }

        var pdfSuccessCount = pdfOutcomes.Count(o => o.MailStatus == ApStatus.MailProcessed);
        logger.LogInformation(
            "Message {MessageId} \"{Subject}\" from {Sender} received {ReceivedAt}: {AttachmentCount} attachment(s), {PdfCount} PDF(s), {SuccessCount} processed successfully, {FailedCount} failed.",
            message.Id, message.Subject ?? "(no subject)", message.SenderAddress ?? "unknown", message.ReceivedDateTime,
            attachmentCount, pdfs.Count, pdfSuccessCount, pdfs.Count - pdfSuccessCount);

        // Every problem PDF's own reason, not just the single worst one - so a message that moves to
        // Errors/NeedsReview because one of several PDFs failed still says which PDF, what went wrong,
        // and that the others were fine, instead of silently dropping that context.
        var errorMessage = worst == ApStatus.MailProcessed ? null : BuildMessageErrorSummary(pdfOutcomes);

        return new MessageOutcome(worst, errorMessage, invoiceCount, attachmentCount, pdfs.Count, pdfSuccessCount, pdfOutcomes);
    }

    /// <summary>
    /// Builds the <c>dbo.MailMessage.ErrorMessage</c> text for a message with at least one
    /// non-successful PDF: every distinct problem (see <see cref="RenderProblems"/>), followed by
    /// how many other PDFs in the same message were processed successfully (and which). For the
    /// common case of a single failing PDF with nothing else in the message, this is byte-identical
    /// to that PDF's own reason string.
    /// </summary>
    internal static string BuildMessageErrorSummary(
        IReadOnlyList<(string FileName, ApStatus MailStatus, string? Reason)> pdfOutcomes)
    {
        var summary = string.Join(" ", RenderProblems(pdfOutcomes));

        var succeeded = pdfOutcomes
            .Where(pdf => pdf.MailStatus == ApStatus.MailProcessed)
            .Select(pdf => pdf.FileName)
            .ToList();

        // Problems first, success note last: dbo.MailMessage.ErrorMessage is nvarchar(max), so nothing
        // here is ever clipped, but keeping the actual errors ahead of this trailing note still makes
        // the important part the first thing read.
        return succeeded.Count > 0
            ? $"{summary} {succeeded.Count} other PDF(s) processed successfully: {string.Join(", ", succeeded)}."
            : summary;
    }

    /// <summary>
    /// Renders every non-successful PDF's problem as one string each, collapsing PDFs that share the
    /// identical underlying reason (once the filename is stripped off) into a single "N PDF(s) failed:
    /// {reason}" line instead of repeating the same sentence once per filename. A PDF whose reason is
    /// unique among its message keeps its own <c>Reason</c> string exactly as recorded.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="BuildMessageErrorSummary"/> (feeding <c>dbo.MailMessage.ErrorMessage</c>,
    /// and via it the file log line built by <see cref="BuildErrorLogLine"/>) and
    /// <see cref="BuildDigestErrorDetail"/> (the summary email), so both places agree on what counts
    /// as "the same reason".
    /// </remarks>
    private static IReadOnlyList<string> RenderProblems(
        IReadOnlyList<(string FileName, ApStatus MailStatus, string? Reason)> pdfOutcomes)
    {
        return pdfOutcomes
            .Where(pdf => pdf.MailStatus != ApStatus.MailProcessed)
            .GroupBy(pdf => StripFileNamePrefix(pdf.Reason, pdf.FileName))
            .Select(group => group.Count() == 1
                ? group.First().Reason ?? $"'{group.First().FileName}': {group.First().MailStatus}."
                : $"{group.Count()} PDF(s) failed: {group.Key}")
            .ToList();
    }

    private async Task<(ApStatus InvoiceStatus, ApStatus MailStatus, string? Reason)> ProcessPdfAsync(
        MailboxRef mailbox,
        MailMessageClaim claim,
        MailMessageSummary message,
        RecordedAttachment pdf,
        ClientResolution client,
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        // Not wrapped in a try: a failure here is either Graph being unreachable or the attachment
        // exceeding MaxAttachmentSizeBytes, and neither is a verdict this method can reach about the
        // invoice. Letting it propagate leaves nothing committed and the batch re-delivered next run,
        // which is the correct outcome for both.
        var pdfBytes = await mailSource.GetAttachmentContentAsync(message.Id, pdf.Attachment.Id, cancellationToken);
        ExtractionResult extraction;

        // File first, then the row. CK_MailAttachment_Stored requires the path and hash together, and
        // an orphan file is harmless whereas a row pointing at nothing is not.
        var (storedPath, sha256) = await attachmentFileStore.SaveAsync(
            pdf.MailAttachmentId,
            pdf.Attachment.Name,
            message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
            pdfBytes,
            cancellationToken);

        await mailAttachmentRepository.SetStoredAsync(pdf.MailAttachmentId, storedPath, sha256, cancellationToken);

        // Checked before extraction runs, not after: these are the exact same bytes as an attachment
        // already accounted for, so there is nothing new to read out of them, and skipping the Ollama/
        // regex call avoids re-deriving (and possibly mis-reading differently) data already on record.
        var duplicate = await mailAttachmentRepository.FindDuplicateByHashAsync(pdf.MailAttachmentId, sha256, cancellationToken);

        if (duplicate is not null)
        {
            logger.LogInformation(
                "Message {MessageId}: PDF attachment '{FileName}' is byte-identical to attachment {ExistingAttachmentId} "
                + "on mail message {ExistingMailMessageId}; routing to review without extracting.",
                message.Id, pdf.Attachment.Name, duplicate.MailAttachmentId, duplicate.MailMessageId);

            var duplicateReason = $"'{pdf.Attachment.Name}': Byte-identical PDF (same file content, e.g. a resend in the thread) - "
                + $"identical content already received on \"{duplicate.Subject ?? "(no subject)"}\" (mail message {duplicate.MailMessageId}).";

            // No Invoice row for this one - identical bytes mean there is nothing new to record, and
            // the verdict already lives on the mail message's own status/reason and the digest email.
            return (ApStatus.InvoicePdfDuplicate, ApStatus.MailNeedsReview, duplicateReason);
        }

        try
        {
            extraction = await invoiceFieldExtractor.ExtractAsync(pdfBytes, request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Ollama itself is unreachable or timed out — infrastructure, not a bad invoice. Propagate
            // so nothing gets committed and the whole batch retries next run, rather than misfiling a
            // possibly-good invoice as an error.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Message {MessageId}: PDF attachment '{FileName}' could not be parsed.", message.Id, pdf.Attachment.Name);

            await RecordInvoiceAsync(claim, pdf, client, fields: null, extraction: null, ApStatus.InvoiceError, exception.Message, cancellationToken);
            return (ApStatus.InvoiceError, ApStatus.MailError, $"'{pdf.Attachment.Name}' could not be parsed.");
        }

        var (invoiceStatus, mailStatus, reason) = Classify(client, extraction.Fields, pdf.Attachment.Name);

        var insert = await RecordInvoiceAsync(claim, pdf, client, extraction.Fields, extraction, invoiceStatus, reason, cancellationToken);

        if (insert.IsDuplicate)
        {
            // The unique index decided; this only records the verdict. Note the invoice row itself was
            // rejected, so the duplicate is visible through the message status and the log rather than
            // as a second ledger row - which is the point of the constraint.
            return (ApStatus.InvoiceDuplicate, ApStatus.MailNeedsReview,
                $"'{pdf.Attachment.Name}': Same invoice number for the same client, but different PDF bytes - "
                + $"invoice number '{extraction.Fields.InvoiceNumber}' is already recorded for this client.");
        }

        return (invoiceStatus, mailStatus, reason);
    }

    /// <summary>
    /// Decides an invoice's status from its extracted fields.
    /// </summary>
    /// <remarks>
    /// The required fields checked here are client, invoice date, invoice number, and customer PO.
    /// Total's sign or magnitude is not evaluated here: zero and negative totals are valid,
    /// correctly-extracted data (e.g. a credit memo prints a negative total by design) and must not
    /// be converted to a positive value or treated as an error anywhere in this pipeline.
    /// <para>
    /// <c>CK_Invoice_ExtractedIsComplete</c> only requires <c>Total IS NOT NULL</c>, not <c>&gt; 0</c>,
    /// so the two still cannot drift into disagreement: were this method to mark an incomplete row as
    /// extracted, the insert would fail rather than quietly storing something that claims to be
    /// complete. A NULL total can still only reach the database via the separate "extraction threw
    /// before producing any <see cref="InvoiceFields"/>" path, which records <c>InvoiceError</c>
    /// directly without going through this method at all.
    /// </para>
    /// </remarks>
    internal static (ApStatus InvoiceStatus, ApStatus MailStatus, string? Reason) Classify(
        ClientResolution client,
        InvoiceFields fields,
        string fileName)
    {
        var missing = new List<string>();

        if (!client.IsKnown)
        {
            missing.Add("Client");
        }

        if (string.IsNullOrWhiteSpace(fields.InvoiceNumber))
        {
            missing.Add(nameof(fields.InvoiceNumber));
        }

        if (fields.InvoiceDate is null)
        {
            missing.Add(nameof(fields.InvoiceDate));
        }

        if (string.IsNullOrWhiteSpace(fields.CustomerPO))
        {
            missing.Add(nameof(fields.CustomerPO));
        }

        return missing.Count > 0
            ? (ApStatus.InvoiceNeedsReview, ApStatus.MailNeedsReview, $"'{fileName}': missing {string.Join(", ", missing)}.")
            : (ApStatus.InvoiceExtracted, ApStatus.MailProcessed, null);
    }

    private async Task<InvoiceInsertResult> RecordInvoiceAsync(
        MailMessageClaim claim,
        RecordedAttachment pdf,
        ClientResolution client,
        InvoiceFields? fields,
        ExtractionResult? extraction,
        ApStatus status,
        string? errorMessage,
        CancellationToken cancellationToken) =>
        await invoiceRepository.RecordAsync(
            new InvoiceRecord
            {
                MailMessageId = claim.MailMessageId,
                MailAttachmentId = pdf.MailAttachmentId,
                ClientId = client.ClientId,
                InvoiceFormatId = client.InvoiceFormatId,
                InvoiceNumber = fields?.InvoiceNumber,
                InvoiceDate = fields?.InvoiceDate,
                DueDate = fields?.DueDate,
                Total = fields?.Total,
                SalesOrder = fields?.SalesOrder,
                CustomerPO = fields?.CustomerPO,
                CustomerNumber = fields?.CustomerNumber,
                OrderAccount = fields?.OrderAccount,
                Terms = fields?.Terms,
                ClientNameAsRead = fields?.ClientName,
                RawText = fields?.RawText,
                // RawText is excluded here — it's already stored verbatim in the RawText column,
                // and duplicating it into every FieldsJson row would bloat the structured payload
                // with the same large document text FieldsJson exists to be distinct from.
                FieldsJson = fields is null ? null : JsonSerializer.Serialize(new
                {
                    fields.InvoiceNumber,
                    fields.SalesOrder,
                    fields.InvoiceDate,
                    fields.DueDate,
                    fields.Total,
                    fields.ClientName,
                    fields.CustomerPO,
                    fields.CustomerNumber,
                    fields.OrderAccount,
                    fields.Terms
                }),
                ExtractionMethod = extraction?.Method,
                ExtractionPromptId = extraction?.ExtractionPromptId,
                Status = status,
                ErrorMessage = errorMessage
            },
            cancellationToken);

    private static ExtractionRequest BuildExtractionRequest(
        ClientResolution client,
        IReadOnlyDictionary<int, ExtractionPromptRecord> prompts)
    {
        if (client.InvoiceFormatId is not { } formatId || !prompts.TryGetValue(formatId, out var prompt))
        {
            // No format, or a format with no active prompt: the deterministic tier may still apply,
            // and if it does not, the extractor reports the configuration gap rather than pretending
            // the document was unreadable.
            return new ExtractionRequest(client.ExtractorKey, null, null, null, null);
        }

        return new ExtractionRequest(
            client.ExtractorKey,
            prompt.PromptTemplate,
            prompt.ResponseSchemaJson,
            prompt.ModelName,
            prompt.ExtractionPromptId);
    }

    /// <summary>
    /// Moves the message to the folder its status routes to, if any.
    /// </summary>
    /// <remarks>
    /// The status is committed before the move, not after. Moving mail is retryable and effectively
    /// idempotent; committing a verdict is not. A crash between the two leaves a correctly classified
    /// message sitting in the Inbox — visible and fixable, and never re-processed because the row is
    /// already final. The opposite order would leave a moved message with no recorded verdict, which
    /// is the state that causes double work.
    /// </remarks>
    private async Task<MailDestinationFolder?> MoveIfRoutedAsync(
        string graphMessageId,
        ApStatus status,
        IReadOnlyDictionary<ApStatus, string?> mailFolders,
        CancellationToken cancellationToken)
    {
        if (!mailFolders.TryGetValue(status, out var folderName) || folderName is null)
        {
            return null;
        }

        if (!Enum.TryParse<MailDestinationFolder>(folderName, ignoreCase: true, out var destination))
        {
            logger.LogError(
                "lkup.Status routes {Status} to folder '{FolderName}', which is not a MailDestinationFolder member. "
                + "The message was classified but not moved.",
                status, folderName);
            return null;
        }

        await mailSource.MoveMessageAsync(graphMessageId, destination, cancellationToken);
        logger.LogInformation("Message {MessageId} routed to {Destination}.", graphMessageId, destination);
        return destination;
    }

    /// <summary>
    /// HTML-encodes a value pulled from an untrusted source (Graph mail fields, PDF-extracted text)
    /// before it's embedded in the summary email's HTML body, so a stray <c>&lt;</c>/<c>&amp;</c>/<c>"</c>
    /// renders as literal text instead of being interpreted as markup.
    /// </summary>
    private static string Html(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;

    /// <summary>
    /// Formats a Graph UTC timestamp in <paramref name="timeZone"/>, or "unknown time" when absent -
    /// shared by <see cref="BuildDigestLine"/> and the completion log line's "which one was routed"
    /// detail so both describe the same message the same way.
    /// </summary>
    private static string FormatReceivedAt(DateTimeOffset? utc, TimeZoneInfo timeZone) =>
        utc is { } value
            ? TimeZoneInfo.ConvertTime(value, timeZone).ToString("MM'/'dd'/'yyyy HH':'mm':'ss zzz", CultureInfo.InvariantCulture)
            : "unknown time";

    /// <summary>
    /// One line of the per-run summary email, mirroring <see cref="ProcessMessageAsync"/>'s completion
    /// log line but with the received time converted from Graph's UTC into <paramref name="timeZone"/>,
    /// since the email's whole point is answering "what happened" without opening the log file. The
    /// routed folder is not named here - <see cref="BuildDigestBody"/> already groups entries by folder,
    /// so naming it again per line would just repeat what the group header already says.
    /// </summary>
    internal static string BuildDigestLine(
        MailMessageSummary message,
        MessageOutcome result,
        TimeZoneInfo timeZone)
    {
        var receivedAt = FormatReceivedAt(message.ReceivedDateTime, timeZone);

        return $"\"{Html(message.Subject ?? "(no subject)")}\" from {Html(message.SenderAddress ?? "unknown")} received {receivedAt}: "
            + $"{result.AttachmentCount} attachment(s), {result.PdfCount} PDF(s), {result.SuccessCount} processed successfully, "
            + $"{result.PdfCount - result.SuccessCount} failed.";
    }

    /// <summary>
    /// The indented detail lines shown under a NeedsReview/Errors message's summary line - what makes
    /// those lines actionable from the email alone, naming the specific problem PDF(s) (and, for a
    /// duplicate, the earlier message it matches) rather than leaving the recipient to open the mailbox
    /// just to find out what went wrong.
    /// </summary>
    /// <remarks>
    /// When several PDFs in the same message fail for the identical reason (e.g. a whole resend of an
    /// earlier thread, every attachment byte-identical to one already on file), listing each filename
    /// on its own line just repeats the same sentence dozens of times. Those are collapsed into one
    /// "N PDF(s) failed: {reason}" line; PDFs with genuinely different reasons still get their own line.
    /// </remarks>
    private const string DetailIndent = "&nbsp;&nbsp;&nbsp;&nbsp;";

    internal static IReadOnlyList<string> BuildDigestErrorDetail(MessageOutcome result)
    {
        if (result.PdfOutcomes is null)
        {
            // No per-PDF breakdown available (e.g. the attempt-cap-reached path) - the message-level
            // reason is all there is, so show it as-is.
            return result.ErrorMessage is null ? [] : [$"{DetailIndent}{Html(result.ErrorMessage)}"];
        }

        var lines = RenderProblems(result.PdfOutcomes)
            .Select(line => $"{DetailIndent}{Html(line)}")
            .ToList();

        var succeeded = result.PdfOutcomes.Where(pdf => pdf.MailStatus == ApStatus.MailProcessed).Select(pdf => pdf.FileName).ToList();

        if (succeeded.Count > 0)
        {
            lines.Add($"{DetailIndent}{succeeded.Count} other PDF(s) processed successfully: {Html(string.Join(", ", succeeded))}.");
        }

        return lines;
    }

    /// <summary>
    /// Strips a per-PDF reason's leading <c>'{fileName}': </c> (or <c>'{fileName}' </c>) prefix, leaving
    /// just the cause - so PDFs that failed for the identical reason but have different filenames still
    /// group together in <see cref="RenderProblems"/>.
    /// </summary>
    private static string StripFileNamePrefix(string? reason, string fileName)
    {
        if (reason is null)
        {
            return string.Empty;
        }

        var withColon = $"'{fileName}': ";
        if (reason.StartsWith(withColon, StringComparison.Ordinal))
        {
            return reason[withColon.Length..];
        }

        var withSpace = $"'{fileName}' ";
        if (reason.StartsWith(withSpace, StringComparison.Ordinal))
        {
            return reason[withSpace.Length..];
        }

        return reason;
    }

    /// <summary>
    /// The full HTML body of the per-run summary email: an intro asking the recipient to check the
    /// mailbox folders, a totals line reusing the same <c>outcomes</c> tally already logged in
    /// <see cref="ProcessInvoicesAsync"/>'s completion line, then one section per destination folder
    /// (Processed, NeedsReview, Errors, in that order - skipping any with nothing routed to it), each
    /// ordered by received date ascending.
    /// </summary>
    /// <remarks>
    /// Every visual "block" (intro, totals, each section header, each message) is its own
    /// <c>&lt;p&gt;</c>: mail clients put natural spacing between paragraphs, which is what gives the
    /// blank line after each bolded section header and between NeedsReview/Errors messages, for free.
    /// The Processed section is the one exception - it's deliberately packed into a single paragraph
    /// with <c>&lt;br&gt;</c> between messages and no per-message routing text, ending in one shared
    /// "Routed to Processed (N)." line, since a Processed message needs no more attention than "it happened".
    /// <para>
    /// Entries that would render identical content (same destination, same summary line, same detail
    /// lines) are collapsed to one before building each section's messages - e.g. when the mail
    /// pipeline records two <c>dbo.MailMessage</c> rows for what is actually one physical email (Graph
    /// reissuing a message's id after it moves is a known way this happens), the reader should see it
    /// once, not twice. The two counts this can produce are deliberately both shown, not merged: the
    /// section header states the raw number of routed <c>dbo.MailMessage</c> rows, while the trailing
    /// "Routed to X (N)." line states how many distinct messages that collapsed down to - so "2 rows,
    /// but only 1 email" stays visible instead of silently picking one number.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Collapses entries that would render identical content (same destination, same summary line,
    /// same detail lines) down to one, keeping the first. Shared by <see cref="BuildDigestBody"/> and
    /// the completion log line's outcomes summary in <see cref="ProcessInvoicesAsync"/>, so both agree
    /// on what counts as "the same message" when two <c>dbo.MailMessage</c> rows describe one email.
    /// </summary>
    private static IReadOnlyList<DigestEntry> DistinctByRenderedContent(IEnumerable<DigestEntry> entries) =>
        entries
            .GroupBy(entry => (entry.Destination, entry.SummaryLine, Detail: string.Join("\n", entry.ErrorDetailLines)))
            .Select(group => group.First())
            .ToList();

    internal static string BuildDigestBody(IReadOnlyList<DigestEntry> entries, IReadOnlyDictionary<ApStatus, int> outcomes)
    {
        var totals = string.Join(", ", outcomes
            .Where(pair => pair.Key is ApStatus.MailProcessed or ApStatus.MailNeedsReview or ApStatus.MailError or ApStatus.MailSkipped)
            .Select(pair => $"{pair.Value} {pair.Key}"));

        var distinctEntries = DistinctByRenderedContent(entries);

        var groupBlocks = new[] { MailDestinationFolder.Processed, MailDestinationFolder.NeedsReview, MailDestinationFolder.Errors }
            .Select(destination => (
                destination,
                rawCount: entries.Count(entry => entry.Destination == destination),
                ordered: distinctEntries
                    .Where(entry => entry.Destination == destination)
                    .OrderBy(entry => entry.ReceivedAt ?? DateTimeOffset.MaxValue)
                    .ToList()))
            .Where(group => group.ordered.Count > 0)
            .Select(group => BuildGroupBlock(group.destination, group.rawCount, group.ordered));

        return "<p>This is a summary of the AP Automation mailbox run just completed. Please verify the "
            + "Processed, Errors, and NeedsReview folders as needed.</p>\n"
            + $"<p>Totals: {totals}.</p>\n"
            + string.Join("\n", groupBlocks);
    }

    private static string BuildGroupBlock(MailDestinationFolder destination, int rawCount, IReadOnlyList<DigestEntry> entries)
    {
        var header = $"<p><b>=== {destination} ({rawCount}) ===</b></p>";

        if (destination == MailDestinationFolder.Processed)
        {
            var lines = entries.Select(entry => entry.SummaryLine).Append($"Routed to Processed ({entries.Count}).");
            return $"{header}\n<p>{string.Join("<br>\n", lines)}</p>";
        }

        var messageBlocks = entries.Select(entry =>
        {
            var summary = $"{entry.SummaryLine} Routed to {destination}.";
            var block = entry.ErrorDetailLines.Count == 0
                ? summary
                : $"{summary}<br>\n{string.Join("<br>\n", entry.ErrorDetailLines)}";
            return $"<p>{block}</p>";
        });

        return $"{header}\n{string.Join("\n", messageBlocks)}\n<p>Routed to {destination} ({entries.Count}).</p>";
    }

    /// <summary>
    /// One log line for a persisted <c>dbo.MailMessage.ErrorMessage</c> entry.
    /// </summary>
    internal static string BuildErrorLogLine(MailMessageErrorLogRow row)
    {
        var receivedAt = row.ReceivedOn?.ToString("MM'/'dd'/'yyyy HH':'mm':'ss zzz", CultureInfo.InvariantCulture)
            ?? "unknown time";

        var status = Enum.IsDefined(typeof(ApStatus), row.StatusId)
            ? ((ApStatus)row.StatusId).ToString()
            : row.StatusId.ToString(CultureInfo.InvariantCulture);

        return $"{status}: \"{row.Subject ?? "(no subject)"}\" from {row.SenderAddress ?? "unknown"} "
            + $"received {receivedAt}. Reason: {row.ErrorMessage}";
    }

    // Worst-wins ordering when one email yields several PDFs.
    internal static int Severity(ApStatus status) => status switch
    {
        ApStatus.MailError => 3,
        ApStatus.MailNeedsReview => 2,
        ApStatus.MailProcessed => 1,
        _ => 0
    };

    private static bool IsPdf(MailAttachmentSummary attachment) =>
        attachment.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
