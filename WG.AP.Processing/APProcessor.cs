using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.Email;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;

namespace WG.AP.Processor;

public sealed class APProcessor(
    IMailSource mailSource,
    MailboxSyncProcessor mailboxSyncProcessor,
    IInvoiceFieldExtractor invoiceFieldExtractor,
    IAttachmentManifestVerifier manifestVerifier,
    IOptions<MailboxOptions> mailboxOptions,
    ILogger<APProcessor> logger)
{
    public async Task ProcessInvoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await mailSource.ValidateAuthAsync(cancellationToken);
            await mailSource.EnsureFoldersExistAsync(cancellationToken);

            var batch = await mailboxSyncProcessor.GetNewMessagesAsync(cancellationToken);
            var attachmentCount = 0;
            var skipped = 0;
            var routedCounts = new Dictionary<MailDestinationFolder, int>
            {
                [MailDestinationFolder.Processed] = 0,
                [MailDestinationFolder.Errors] = 0,
                [MailDestinationFolder.NeedsReview] = 0
            };

            foreach (var message in batch.Messages)
            {
                attachmentCount += message.Attachments.Count;

                logger.LogInformation(
                    "Message {MessageId} from {Sender} received {ReceivedAt}: {AttachmentCount} attachment(s).",
                    message.Id,
                    message.SenderAddress ?? "unknown",
                    message.ReceivedDateTime,
                    message.Attachments.Count);

                var destination = await DetermineDestinationAsync(message, cancellationToken);

                if (destination is null)
                {
                    // Not an invoice-shaped email (no PDF/Excel attachments) — out of scope, leave it in Inbox.
                    skipped++;
                    continue;
                }

                await mailSource.MoveMessageAsync(message.Id, destination.Value, cancellationToken);
                routedCounts[destination.Value]++;

                logger.LogInformation("Message {MessageId} routed to {Destination}.", message.Id, destination.Value);
            }

            await mailboxSyncProcessor.CommitAsync(batch, cancellationToken);

            logger.LogInformation(
                "Mailbox scan complete for {MailboxUser}: {MessageCount} new message(s), {AttachmentCount} attachment(s), " +
                "{Processed} processed, {Errors} errored, {NeedsReview} needing review, {Skipped} skipped (no invoice attachments).",
                mailboxOptions.Value.MailboxUser,
                batch.Messages.Count,
                attachmentCount,
                routedCounts[MailDestinationFolder.Processed],
                routedCounts[MailDestinationFolder.Errors],
                routedCounts[MailDestinationFolder.NeedsReview],
                skipped);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mailbox processing failed.");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Decides where a message belongs, per the routing tree: a parse failure or unreadable
    /// manifest is an <see cref="MailDestinationFolder.Errors"/>; a manifest/field mismatch is
    /// <see cref="MailDestinationFolder.NeedsReview"/>; a clean reconciliation is
    /// <see cref="MailDestinationFolder.Processed"/>. Returns null for a message with no PDF/Excel
    /// attachments at all — not invoice-shaped, so left untouched in Inbox.
    /// </summary>
    private async Task<MailDestinationFolder?> DetermineDestinationAsync(MailMessageSummary message, CancellationToken cancellationToken)
    {
        var pdfAttachments = message.Attachments.Where(IsPdf).ToList();
        var excelAttachments = message.Attachments.Where(IsExcel).ToList();

        if (pdfAttachments.Count == 0 && excelAttachments.Count == 0)
        {
            return null;
        }

        if (excelAttachments.Count > 1)
        {
            logger.LogError("Message {MessageId} has {Count} Excel attachments; expected exactly one manifest.", message.Id, excelAttachments.Count);
            return MailDestinationFolder.Errors;
        }

        if (excelAttachments.Count == 0)
        {
            logger.LogWarning("Message {MessageId} has PDF attachment(s) but no Excel manifest attachment; out of scope for automated verification.", message.Id);
            return MailDestinationFolder.NeedsReview;
        }

        IReadOnlyList<ManifestRow> manifestRows;

        try
        {
            var excelBytes = await mailSource.GetAttachmentContentAsync(message.Id, excelAttachments[0].Id, cancellationToken);
            manifestRows = manifestVerifier.ReadManifest(excelBytes);

            if (manifestRows.Count == 0)
            {
                throw new InvalidOperationException("Manifest contained no data rows.");
            }
        }
        catch (Exception exception) when (exception is not (HttpRequestException or TaskCanceledException))
        {
            logger.LogError(exception, "Message {MessageId}: manifest '{FileName}' could not be read.", message.Id, excelAttachments[0].Name);
            return MailDestinationFolder.Errors;
        }

        var reconciliation = manifestVerifier.Reconcile(manifestRows, pdfAttachments);
        if (reconciliation.HasDiscrepancies)
        {
            logger.LogWarning(
                "Message {MessageId}: manifest discrepancy — missing PDF(s) for voucher(s) [{Missing}], unexpected attachment(s) [{Unexpected}], duplicate attachment(s) [{DuplicateAttachments}], duplicate voucher(s) [{Duplicates}].",
                message.Id,
                string.Join(", ", reconciliation.MissingPdfVouchers),
                string.Join(", ", reconciliation.UnexpectedAttachments),
                string.Join(", ", reconciliation.DuplicateAttachments),
                string.Join(", ", reconciliation.DuplicateVouchers));
            return MailDestinationFolder.NeedsReview;
        }

        var extractedByVoucher = new Dictionary<string, InvoiceFields>(StringComparer.OrdinalIgnoreCase);
        var hasParseFailure = false;

        foreach (var pair in reconciliation.MatchedPairs)
        {
                var attachment = pdfAttachments.First(a => a.Name == pair.AttachmentName);

                try
                {
                    var pdfBytes = await mailSource.GetAttachmentContentAsync(message.Id, attachment.Id, cancellationToken);
                    extractedByVoucher[pair.Voucher] = await invoiceFieldExtractor.ExtractAsync(pdfBytes, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    // Ollama itself is unreachable/timed out — an infrastructure problem, not a bad
                    // invoice. Propagate so nothing gets committed and the whole batch retries next run,
                    // rather than misfiling a possibly-good invoice into Errors.
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Message {MessageId}: PDF attachment '{FileName}' (voucher {Voucher}) could not be parsed.", message.Id, attachment.Name, pair.Voucher);
                    hasParseFailure = true;
                }
        }

        if (hasParseFailure)
        {
                return MailDestinationFolder.Errors;
        }

        var fieldMismatches = reconciliation.MatchedPairs
                .Select(pair => manifestVerifier.CompareFields(pair.Row, extractedByVoucher[pair.Voucher]))
                .Where(result => !result.IsMatch)
                .ToList();

        if (fieldMismatches.Count > 0)
        {
            foreach (var mismatch in fieldMismatches)
            {
                logger.LogWarning(
                    "Message {MessageId}: voucher {Voucher} field mismatch(es): {Mismatches}.",
                    message.Id,
                    mismatch.Voucher,
                    string.Join("; ", mismatch.Mismatches.Select(m => $"{m.FieldName} excel='{m.ExcelValue}' pdf='{m.PdfValue}'")));
            }

            return MailDestinationFolder.NeedsReview;
        }

        return MailDestinationFolder.Processed;
    }

    private static bool IsPdf(MailAttachmentSummary attachment) =>
        attachment.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcel(MailAttachmentSummary attachment) =>
        attachment.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
}
