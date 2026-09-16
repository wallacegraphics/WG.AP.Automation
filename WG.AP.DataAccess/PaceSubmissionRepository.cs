using Dapper;
using Microsoft.Extensions.Logging;

namespace WG.AP.DataAccess;

public sealed class PaceSubmissionRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<PaceSubmissionRepository> logger)
{
    private const int DefaultClaimLeaseMinutes = 60;

    public async Task<int> EnqueueExtractedInvoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            return await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO [intgr].[PaceSubmission]
                    ([InvoiceId], [StatusCodeId])
                SELECT
                    invoice.[InvoiceId],
                    @PendingStatusId
                FROM [dbo].[Invoice] AS invoice
                WHERE invoice.[StatusId] = @InvoiceExtractedStatus
                  AND invoice.[FieldsJson] IS NOT NULL
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM [intgr].[PaceSubmission] AS existing WITH (UPDLOCK, HOLDLOCK)
                      WHERE existing.[InvoiceId] = invoice.[InvoiceId]
                  );
            """,
                new
                {
                    PendingStatusId = PaceSubmissionStatus.PendingId,
                    InvoiceExtractedStatus = (int)ApStatus.InvoiceExtracted
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to enqueue extracted invoices for Pace submission.");
            throw;
        }
    }

    public async Task<PaceSubmissionClaim?> ClaimNextAsync(long? processingRunId, CancellationToken cancellationToken, int claimLeaseMinutes = DefaultClaimLeaseMinutes)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            return await connection.QuerySingleOrDefaultAsync<PaceSubmissionClaim>(new CommandDefinition(
            """
            DECLARE @ClaimToken UNIQUEIDENTIFIER = NEWID();
            DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
            DECLARE @LeaseExpiredOn DATETIME2(3) = DATEADD(MINUTE, -@ClaimLeaseMinutes, @Now);

            WITH NextSubmission AS
            (
                SELECT TOP (1) submission.[PaceSubmissionId]
                FROM [intgr].[PaceSubmission] AS submission WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
                WHERE submission.[StatusCodeId] = @PendingStatusId
                   OR (submission.[StatusCodeId] = @RetryLaterStatusId
                       AND (submission.[NextAttemptOn] IS NULL OR submission.[NextAttemptOn] <= @Now))
                   OR (submission.[StatusCodeId] = @InProgressStatusId
                       AND submission.[ClaimedOn] <= @LeaseExpiredOn)
                ORDER BY CASE
                        WHEN submission.[StatusCodeId] = @PendingStatusId THEN 0
                        WHEN submission.[StatusCodeId] = @RetryLaterStatusId THEN 1
                        ELSE 2
                    END,
                    submission.[CreatedOn],
                    submission.[PaceSubmissionId]
            )
            UPDATE submission
               SET [StatusCodeId] = @InProgressStatusId,
                   [AttemptCount] = submission.[AttemptCount] + 1,
                   [NextAttemptOn] = NULL,
                   [ClaimToken] = @ClaimToken,
                   [ClaimedOn] = @Now,
                   [ProcessingRunId] = @ProcessingRunId,
                   [ModifiedOn] = @Now
            OUTPUT
                   inserted.[PaceSubmissionId],
                   inserted.[InvoiceId],
                   mailMessage.[MailMessageId],
                   mailMessage.[GraphMessageId],
                   inserted.[AttemptCount],
                   inserted.[ClaimToken],
                   invoice.[FieldsJson],
                   invoice.[InvoiceNumber],
                   invoice.[CustomerPO],
                   invoice.[Total],
                   client.[PaceVendorId],
                   inserted.[PaceBillBatchId],
                   inserted.[PaceBillId],
                   inserted.[PaceBillLineId]
            FROM [intgr].[PaceSubmission] AS submission
            INNER JOIN NextSubmission AS nextSubmission
                ON nextSubmission.[PaceSubmissionId] = submission.[PaceSubmissionId]
            INNER JOIN [dbo].[Invoice] AS invoice
                ON invoice.[InvoiceId] = submission.[InvoiceId]
            INNER JOIN [dbo].[MailMessage] AS mailMessage
                ON mailMessage.[MailMessageId] = invoice.[MailMessageId]
            INNER JOIN [dbo].[Client] AS client
                ON client.[ClientId] = invoice.[ClientId];
            """,
                new
                {
                    PendingStatusId = PaceSubmissionStatus.PendingId,
                    RetryLaterStatusId = PaceSubmissionStatus.RetryLaterId,
                    InProgressStatusId = PaceSubmissionStatus.InProgressId,
                    ProcessingRunId = processingRunId,
                    ClaimLeaseMinutes = Math.Max(1, claimLeaseMinutes)
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to claim the next Pace submission for processing run {ProcessingRunId} with claim lease {ClaimLeaseMinutes} minute(s).", processingRunId, Math.Max(1, claimLeaseMinutes));
            throw;
        }
    }

    public async Task<bool> CompleteAsync(PaceSubmissionCompletion completion, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [StatusCodeId] = @StatusId,
                   [ClaimToken] = NULL,
                   [ClaimedOn] = NULL,
                   [RequestJson] = @RequestJson,
                   [ResponseJson] = @ResponseJson,
                   [PaceBillBatchId] = @PaceBillBatchId,
                   [PaceBillId] = @PaceBillId,
                   [PaceBillLineId] = @PaceBillLineId,
                   [ErrorMessage] = @ErrorMessage,
                   [ModifiedOn] = SYSUTCDATETIME()
            WHERE [PaceSubmissionId] = @PaceSubmissionId
              AND [ClaimToken] = @ClaimToken;
            """,
                new
                {
                    completion.PaceSubmissionId,
                    completion.ClaimToken,
                    StatusId = PaceSubmissionStatus.ToId(completion.StatusCode),
                    completion.RequestJson,
                    completion.ResponseJson,
                    completion.PaceBillBatchId,
                    completion.PaceBillId,
                    completion.PaceBillLineId,
                    completion.ErrorMessage
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return affected == 1;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to complete Pace submission {PaceSubmissionId} with status {StatusCode}.", completion.PaceSubmissionId, completion.StatusCode);
            throw;
        }
    }

    public async Task<bool> RetryLaterAsync(PaceSubmissionRetry retry, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [StatusCodeId] = @RetryLaterStatusId,
                   [NextAttemptOn] = @NextAttemptOn,
                   [ClaimToken] = NULL,
                   [ClaimedOn] = NULL,
                   [RequestJson] = @RequestJson,
                   [ResponseJson] = @ResponseJson,
                   [ErrorMessage] = @ErrorMessage,
                   [ModifiedOn] = SYSUTCDATETIME()
            WHERE [PaceSubmissionId] = @PaceSubmissionId
              AND [ClaimToken] = @ClaimToken;
            """,
                new
                {
                    retry.PaceSubmissionId,
                    retry.ClaimToken,
                    RetryLaterStatusId = PaceSubmissionStatus.RetryLaterId,
                    retry.NextAttemptOn,
                    retry.RequestJson,
                    retry.ResponseJson,
                    retry.ErrorMessage
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return affected == 1;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to schedule Pace submission {PaceSubmissionId} for retry on {NextAttemptOn}.", retry.PaceSubmissionId, retry.NextAttemptOn);
            throw;
        }
    }
}
