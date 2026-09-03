using System.Text.Json;
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
    IOptions<MailboxOptions> mailboxOptions,
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<APProcessor> logger)
{
    public async Task ProcessInvoicesAsync(CancellationToken cancellationToken)
    {
        var mailbox = mailboxOptions.Value.ToMailboxRef();
        long? processingRunId = null;
        var messageCount = 0;
        var invoiceCount = 0;

        try
        {
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
            var skippedAsAlreadyFinal = 0;

            foreach (var message in batch.Messages)
            {
                var claim = await mailMessageRepository.DiscoverAndClaimAsync(mailbox, processingRunId.Value, message, cancellationToken);
                ProcessingRunContext.CurrentMailMessageId = claim.MailMessageId;

                try
                {
                    if (!claim.Claimed)
                    {
                        // Already decided on an earlier run. Re-delivery is normal — it is how the
                        // crash-safe delta ordering works — so this is not a warning.
                        skippedAsAlreadyFinal++;
                        logger.LogInformation(
                            "Message {MessageId} is already in status {StatusId}; leaving it alone.",
                            message.Id, claim.StatusId);
                        continue;
                    }

                    messageCount++;

                    logger.LogInformation(
                        "Message {MessageId} from {Sender} received {ReceivedAt}: {AttachmentCount} attachment(s), attempt {AttemptCount}.",
                        message.Id, message.SenderAddress ?? "unknown", message.ReceivedDateTime,
                        message.Attachments.Count, claim.AttemptCount);

                    var result = await ProcessMessageAsync(mailbox, claim, message, clientCatalog, prompts, cancellationToken);

                    invoiceCount += result.InvoiceCount;
                    outcomes[result.Status] = outcomes.GetValueOrDefault(result.Status) + 1;

                    await mailMessageRepository.SetStatusAsync(claim.MailMessageId, result.Status, result.ErrorMessage, cancellationToken);
                    await MoveIfRoutedAsync(message.Id, result.Status, mailFolders, cancellationToken);
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
                mailbox.MailboxUser, batch.Messages.Count, messageCount, skippedAsAlreadyFinal, invoiceCount,
                string.Join(", ", outcomes.Select(pair => $"{pair.Key}={pair.Value}")));

            if (processingRunId is not null)
            {
                await processingRunRepository.FinishAsync(processingRunId.Value, messageCount, invoiceCount, isSuccessful: true, errorMessage: null, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mailbox processing failed.");

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

    private sealed record MessageOutcome(ApStatus Status, string? ErrorMessage, int InvoiceCount);

    /// <summary>
    /// Records a message's attachments, extracts an invoice from each PDF, and decides where the
    /// message belongs.
    /// </summary>
    /// <remarks>
    /// The routing rules, in the order they are applied:
    /// <list type="bullet">
    /// <item>no PDF attachments — including Excel-only mail — is <c>MailSkipped</c>, routed to NeedsReview</item>
    /// <item>a PDF that cannot be parsed at all is <c>MailError</c></item>
    /// <item>a missing required field, an unresolved client, or a duplicate number is <c>MailNeedsReview</c></item>
    /// <item>everything present and readable is <c>MailProcessed</c></item>
    /// </list>
    /// Where one email yields several PDFs with different verdicts, the worst wins.
    /// </remarks>
    private async Task<MessageOutcome> ProcessMessageAsync(
        MailboxRef mailbox,
        MailMessageClaim claim,
        MailMessageSummary message,
        IReadOnlyDictionary<string, ClientResolution> clientCatalog,
        IReadOnlyDictionary<int, ExtractionPromptRecord> prompts,
        CancellationToken cancellationToken)
    {
        // Every attachment is recorded, including the Excel ones nothing reads any more: a row with
        // Kind = 'Excel' is how "a manifest arrived and we ignored it" stays answerable later.
        var recorded = await mailAttachmentRepository.RecordAsync(claim.MailMessageId, message.Attachments, cancellationToken);
        var pdfs = recorded.Where(item => IsPdf(item.Attachment)).ToList();

        if (pdfs.Count == 0)
        {
            logger.LogInformation(
                "Message {MessageId} has no PDF attachment(s); routing to NeedsReview.", message.Id);
            return new MessageOutcome(ApStatus.MailSkipped, null, InvoiceCount: 0);
        }

        // The cap exists so a document that reliably breaks extraction stops consuming every run.
        // NeedsReview rather than Error, deliberately: nobody should silently give up on a payable.
        if (claim.AttemptCount > databaseOptions.Value.MaxAttempts)
        {
            logger.LogWarning(
                "Message {MessageId} has been attempted {AttemptCount} times (cap {MaxAttempts}); routing to review.",
                message.Id, claim.AttemptCount, databaseOptions.Value.MaxAttempts);
            return new MessageOutcome(ApStatus.MailNeedsReview, $"Attempt cap of {databaseOptions.Value.MaxAttempts} reached.", InvoiceCount: 0);
        }

        var client = ClientRepository.Resolve(clientCatalog, message.SenderAddress);

        if (!client.IsKnown)
        {
            logger.LogWarning(
                "Message {MessageId} is from {Sender}, which matches no enabled client; its invoices will need review.",
                message.Id, message.SenderAddress ?? "unknown");
        }

        var request = BuildExtractionRequest(client, prompts);
        var worst = ApStatus.MailProcessed;
        string? errorMessage = null;
        var invoiceCount = 0;

        foreach (var pdf in pdfs)
        {
            var (invoiceStatus, mailStatus, reason) = await ProcessPdfAsync(
                mailbox, claim, message, pdf, client, request, cancellationToken);

            invoiceCount++;

            if (Severity(mailStatus) > Severity(worst))
            {
                worst = mailStatus;
                errorMessage = reason;
            }

            logger.LogInformation(
                "Message {MessageId} attachment '{FileName}': {InvoiceStatus}.",
                message.Id, pdf.Attachment.Name, invoiceStatus);
        }

        return new MessageOutcome(worst, errorMessage, invoiceCount);
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
                $"Invoice number '{extraction.Fields.InvoiceNumber}' is already recorded for this client.");
        }

        return (invoiceStatus, mailStatus, reason);
    }

    /// <summary>
    /// Decides an invoice's status from its extracted fields.
    /// </summary>
    /// <remarks>
    /// The five required fields are client, invoice date, invoice number, customer PO and total. A
    /// total's sign or magnitude is not evaluated here: zero and negative totals are valid,
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
    private async Task MoveIfRoutedAsync(
        string graphMessageId,
        ApStatus status,
        IReadOnlyDictionary<ApStatus, string?> mailFolders,
        CancellationToken cancellationToken)
    {
        if (!mailFolders.TryGetValue(status, out var folderName) || folderName is null)
        {
            return;
        }

        if (!Enum.TryParse<MailDestinationFolder>(folderName, ignoreCase: true, out var destination))
        {
            logger.LogError(
                "lkup.Status routes {Status} to folder '{FolderName}', which is not a MailDestinationFolder member. "
                + "The message was classified but not moved.",
                status, folderName);
            return;
        }

        await mailSource.MoveMessageAsync(graphMessageId, destination, cancellationToken);
        logger.LogInformation("Message {MessageId} routed to {Destination}.", graphMessageId, destination);
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
