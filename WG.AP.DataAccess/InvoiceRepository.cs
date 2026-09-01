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
}
