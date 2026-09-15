using Dapper;

namespace WG.AP.DataAccess;

public sealed class PaceSubmissionRepository(SqlConnectionFactory connectionFactory)
{
    public async Task<int> EnqueueExtractedInvoicesAsync(CancellationToken cancellationToken)
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
                  FROM [intgr].[PaceSubmission] AS existing
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

    public async Task<PaceSubmissionClaim?> ClaimNextAsync(long? processingRunId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PaceSubmissionClaim>(new CommandDefinition(
            """
            DECLARE @ClaimToken UNIQUEIDENTIFIER = NEWID();
            DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

            WITH NextSubmission AS
            (
                SELECT TOP (1) submission.[PaceSubmissionId]
                FROM [intgr].[PaceSubmission] AS submission WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE submission.[StatusCodeId] IN (@PendingStatusId, @RetryLaterStatusId)
                  AND (submission.[NextAttemptOn] IS NULL OR submission.[NextAttemptOn] <= @Now)
                ORDER BY submission.[CreatedOn], submission.[PaceSubmissionId]
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
                   inserted.[AttemptCount],
                   inserted.[ClaimToken],
                   invoice.[FieldsJson],
                   invoice.[InvoiceNumber],
                   invoice.[CustomerPO],
                   invoice.[Total],
                   inserted.[PaceBillBatchId],
                   inserted.[PaceBillId],
                   inserted.[PaceBillLineId]
            FROM [intgr].[PaceSubmission] AS submission
            INNER JOIN NextSubmission AS nextSubmission
                ON nextSubmission.[PaceSubmissionId] = submission.[PaceSubmissionId]
            INNER JOIN [dbo].[Invoice] AS invoice
                ON invoice.[InvoiceId] = submission.[InvoiceId];
            """,
            new
            {
                PendingStatusId = PaceSubmissionStatus.PendingId,
                RetryLaterStatusId = PaceSubmissionStatus.RetryLaterId,
                InProgressStatusId = PaceSubmissionStatus.InProgressId,
                ProcessingRunId = processingRunId
            },
            commandTimeout: connectionFactory.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> CompleteAsync(PaceSubmissionCompletion completion, CancellationToken cancellationToken)
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

    public async Task<bool> RetryLaterAsync(PaceSubmissionRetry retry, CancellationToken cancellationToken)
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
}
