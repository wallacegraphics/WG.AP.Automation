using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace WG.AP.DataAccess;

/// <summary>
/// Writes the invoice ledger.
/// </summary>
public sealed class InvoiceRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<InvoiceRepository> logger)
{
    /// <summary>
    /// Records an extracted invoice, or reports that its number is a duplicate for that client.
    /// </summary>
    /// <remarks>
    /// Duplicates are detected by attempting the insert and catching the unique-index violation, not
    /// by querying first. A check-then-insert has a race that <c>UQ_Invoice_ClientNumber</c> does not,
    /// and the index is the thing that must be authoritative — it is what prevents paying an invoice
    /// twice. The constraint decides; this method reports.
    /// <para>
    /// Note the index deliberately excludes ClientId 0, so two unresolved-client invoices sharing a
    /// number are both recorded rather than one being rejected as a false duplicate. Those go to
    /// NeedsReview instead, where a human can see both.
    /// </para>
    /// </remarks>
    public async Task<InvoiceInsertResult> RecordAsync(InvoiceRecord invoice, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var invoiceId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                """
                INSERT INTO [dbo].[Invoice]
                    ([MailMessageId], [MailAttachmentId], [ClientId], [InvoiceFormatId],
                     [InvoiceNumber], [InvoiceDate], [DueDate], [Total], [SalesOrder], [CustomerPO],
                     [CustomerNumber], [OrderAccount], [Terms], [ClientNameAsRead], [RawText],
                     [ExtractionMethod], [ExtractionPromptId], [StatusId], [ErrorMessage])
                VALUES
                    (@MailMessageId, @MailAttachmentId, @ClientId, @InvoiceFormatId,
                     @InvoiceNumber, @InvoiceDate, @DueDate, @Total, @SalesOrder, @CustomerPO,
                     @CustomerNumber, @OrderAccount, @Terms, @ClientNameAsRead, @RawText,
                     @ExtractionMethod, @ExtractionPromptId, @StatusId, @ErrorMessage);

                SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
                """,
                new
                {
                    invoice.MailMessageId,
                    invoice.MailAttachmentId,
                    invoice.ClientId,
                    invoice.InvoiceFormatId,
                    InvoiceNumber = MailMessageRepository.Truncate(invoice.InvoiceNumber, 100),
                    invoice.InvoiceDate,
                    invoice.DueDate,
                    invoice.Total,
                    SalesOrder = MailMessageRepository.Truncate(invoice.SalesOrder, 100),
                    CustomerPO = MailMessageRepository.Truncate(invoice.CustomerPO, 200),
                    CustomerNumber = MailMessageRepository.Truncate(invoice.CustomerNumber, 100),
                    OrderAccount = MailMessageRepository.Truncate(invoice.OrderAccount, 100),
                    Terms = MailMessageRepository.Truncate(invoice.Terms, 100),
                    ClientNameAsRead = MailMessageRepository.Truncate(invoice.ClientNameAsRead, 200),
                    invoice.RawText,
                    invoice.ExtractionMethod,
                    invoice.ExtractionPromptId,
                    StatusId = (int)invoice.Status,
                    ErrorMessage = MailMessageRepository.Truncate(invoice.ErrorMessage, 1000)
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return new InvoiceInsertResult(invoiceId, IsDuplicate: false);
        }
        catch (SqlException exception) when (MailMessageRepository.IsUniqueViolation(exception))
        {
            // Two different unique indexes can reject this insert, and they mean opposite things — so
            // which one fired has to be established rather than assumed:
            //
            //   UQ_Invoice_Attachment   — this exact PDF already has an invoice row. That is not a
            //     duplicate invoice, it is idempotency working. A crash between recording the invoice
            //     and marking the mail final leaves the message non-final, so the next run re-delivers
            //     it, re-claims it and extracts the same attachment again. The row already there is
            //     the answer, and treating it as a duplicate number would file a perfectly good
            //     invoice into NeedsReview for a human to puzzle over.
            //
            //   UQ_Invoice_ClientNumber — a *different* attachment carrying a number this client has
            //     already billed. That is the duplicate the index exists to catch.
            //
            // Reading the attachment's row back separates them. Doing it here rather than before the
            // insert keeps the index authoritative: the query only runs once the constraint has already
            // spoken, so it adds no check-then-insert race.
            var existingInvoiceId = await FindByAttachmentAsync(invoice.MailAttachmentId, cancellationToken);

            if (existingInvoiceId is not null)
            {
                // The recorded row wins over this re-extraction, which matters if the two disagree: a
                // second Ollama pass over the same PDF can read a field differently, and the invoice
                // that was already recorded is the one the ledger and any downstream Pace payload refer
                // to. A re-read that differs is worth knowing about, hence the log line.
                logger.LogInformation(
                    "Attachment {MailAttachmentId} already has invoice {InvoiceId} (invoice number {InvoiceNumber}); "
                    + "keeping the recorded row rather than writing a second one. Expected after a crash mid-processing.",
                    invoice.MailAttachmentId, existingInvoiceId, invoice.InvoiceNumber);

                return new InvoiceInsertResult(existingInvoiceId, IsDuplicate: false);
            }

            // Expected, not exceptional: this is how a duplicate invoice number is discovered.
            logger.LogWarning(
                "Invoice number {InvoiceNumber} for client {ClientId} is already recorded (attachment {MailAttachmentId}).",
                invoice.InvoiceNumber, invoice.ClientId, invoice.MailAttachmentId);

            return new InvoiceInsertResult(InvoiceId: null, IsDuplicate: true);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to record the invoice from attachment {MailAttachmentId} on mail message {MailMessageId}.",
                invoice.MailAttachmentId, invoice.MailMessageId);
            throw;
        }
    }

    /// <summary>
    /// The invoice already recorded for an attachment, or null. <c>UQ_Invoice_Attachment</c> makes this
    /// at most one row.
    /// </summary>
    private async Task<long?> FindByAttachmentAsync(long mailAttachmentId, CancellationToken cancellationToken)
    {
        // A fresh connection: the one the insert used was disposed on the way out of the try block.
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            SELECT [InvoiceId]
              FROM [dbo].[Invoice]
             WHERE [MailAttachmentId] = @MailAttachmentId;
            """,
            new { MailAttachmentId = mailAttachmentId },
            commandTimeout: connectionFactory.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }
}
