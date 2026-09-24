using Dapper;
using Microsoft.Extensions.Logging;

namespace WG.AP.DataAccess;

public sealed class PaceSubmissionRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<PaceSubmissionRepository> logger)
{
    private const int DefaultClaimLeaseMinutes = 60;

    /// <summary>Width of intgr.PaceSubmission.BillVendor; keep in step with the table definition.</summary>
    public const int BillVendorMaxLength = 100;

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
                   client.[Code] AS [ClientCode],
                   client.[Name] AS [ClientName],
                   client.[PaceVendorAccountNumber],
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
                   [ResponseJson] = @ResponseJson,
                   [PaceBillBatchId] = @PaceBillBatchId,
                   [PaceBillId] = @PaceBillId,
                   [PaceBillLineId] = @PaceBillLineId,
                   [ErrorMessage] = @ErrorMessage,
                   [RequiresReview] = @RequiresReview,
                   [BillVendor] = @BillVendor,
                   -- The completing run owns this row's mail routing from here, so an overlapping run's
                   -- recovery sweep does not route it a second time while this one is still doing it.
                   [RoutingClaimedOn] = SYSUTCDATETIME(),
                   -- A completion is a new Pace outcome, so earlier routing and notification no longer describe
                   -- it. A requeued row (RequeuePaceDryRunPrepared.sql, or by hand) would otherwise keep a stale
                   -- MailRoutedOn, and the recovery sweep would never retry a failed route of the new outcome.
                   [MailRoutedOn] = NULL,
                   [NotifiedOn] = NULL,
                   [ModifiedOn] = SYSUTCDATETIME()
            WHERE [PaceSubmissionId] = @PaceSubmissionId
              AND [ClaimToken] = @ClaimToken;
            """,
                new
                {
                    completion.PaceSubmissionId,
                    completion.ClaimToken,
                    StatusId = PaceSubmissionStatus.ToId(completion.StatusCode),
                    completion.ResponseJson,
                    completion.PaceBillBatchId,
                    completion.PaceBillId,
                    completion.PaceBillLineId,
                    completion.ErrorMessage,
                    completion.RequiresReview,
                    // Display-only (the recovered digest line), so an overlong Pace vendor name is cut to the
                    // column width rather than allowed to fail this save - a failed completion after the bill
                    // already exists in Pace would leave the claim InProgress and get it resubmitted.
                    BillVendor = completion.BillVendor is { Length: > BillVendorMaxLength } billVendor
                        ? billVendor[..BillVendorMaxLength]
                        : completion.BillVendor
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

    /// <summary>
    /// Confirms this submission's mail routing is fully done - status update, move, and notification
    /// delivered - independent of its Pace StatusCodeId. Until this runs, a crash or exception anywhere in
    /// routing leaves MailRoutedOn NULL and the submission eligible for
    /// <see cref="ClaimUnroutedFinalSubmissionsAsync"/> to retry on the next run.
    /// <para>
    /// Deliberately does not bump ModifiedOn: the recovery window is measured from the Pace outcome, and
    /// routing bookkeeping moving it would let a row outlive the window it is meant to age out of.
    /// </para>
    /// </summary>
    public Task MarkMailRoutedAsync(long paceSubmissionId, CancellationToken cancellationToken) =>
        MarkMailRoutedAsync([paceSubmissionId], cancellationToken);

    public async Task MarkMailRoutedAsync(IReadOnlyCollection<long> paceSubmissionIds, CancellationToken cancellationToken)
    {
        if (paceSubmissionIds.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [MailRoutedOn] = SYSUTCDATETIME()
            WHERE [PaceSubmissionId] IN @PaceSubmissionIds;
            """,
                new { PaceSubmissionIds = paceSubmissionIds },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to mark Pace submission(s) {PaceSubmissionIds} as mail-routed.", string.Join(", ", paceSubmissionIds));
            throw;
        }
    }

    /// <summary>
    /// Records that the alert or digest line for these submissions was actually delivered, so a later retry
    /// of their routing (e.g. a failed move) does not notify AP a second time. Like
    /// <see cref="MarkMailRoutedAsync(long, CancellationToken)"/>, does not bump ModifiedOn.
    /// </summary>
    public async Task MarkNotifiedAsync(IReadOnlyCollection<long> paceSubmissionIds, CancellationToken cancellationToken)
    {
        if (paceSubmissionIds.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [NotifiedOn] = SYSUTCDATETIME()
            WHERE [PaceSubmissionId] IN @PaceSubmissionIds
              AND [NotifiedOn] IS NULL;
            """,
                new { PaceSubmissionIds = paceSubmissionIds },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to mark Pace submission(s) {PaceSubmissionIds} as notified.", string.Join(", ", paceSubmissionIds));
            throw;
        }
    }

    /// <summary>
    /// Hands back the routing lease on submissions a run tried to route but did not finish, so the very next
    /// run retries them however soon it starts, instead of waiting for the lease to lapse. Called once, at the
    /// end of the run, so the lease still protects these rows for as long as the run that owns them is going.
    /// <para>
    /// Sets RoutingClaimedOn to an already-expired time rather than NULL: NULL marks a row completed before
    /// routing tracking existed, which the sweep must never touch. Does not bump ModifiedOn.
    /// </para>
    /// </summary>
    public async Task ReleaseRoutingClaimsAsync(IReadOnlyCollection<long> paceSubmissionIds, CancellationToken cancellationToken)
    {
        if (paceSubmissionIds.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [RoutingClaimedOn] = DATEADD(MINUTE, -@RoutingLeaseMinutes, SYSUTCDATETIME())
            WHERE [PaceSubmissionId] IN @PaceSubmissionIds
              AND [MailRoutedOn] IS NULL;
            """,
                new
                {
                    PaceSubmissionIds = paceSubmissionIds,
                    RoutingLeaseMinutes = connectionFactory.PaceRoutingLeaseMinutes
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to release the routing lease on Pace submission(s) {PaceSubmissionIds}.", string.Join(", ", paceSubmissionIds));
            throw;
        }
    }

    /// <summary>
    /// Claims Pace-final submissions (RequiresReview, or a StatusCode that always routes to Errors) whose mail
    /// routing was never confirmed via <see cref="MarkMailRoutedAsync(long, CancellationToken)"/> - the durable
    /// trail left behind when a crash or exception happens anywhere between CompleteAsync and the end of routing.
    /// <para>
    /// Claims rather than reads: each returned row's RoutingClaimedOn lease is renewed in the same statement,
    /// and rows whose lease is still live are skipped. So two overlapping runs get disjoint rows, and a sweep
    /// never picks up a row another run completed moments ago and is still routing - either would otherwise
    /// send the same alert twice. The claim does not bump ModifiedOn, or a permanently failing row re-claimed
    /// every run would never age out of the window below.
    /// </para>
    /// <para>
    /// Only rows this code completed are ever eligible: CompleteAsync is the one way a submission becomes
    /// final, and it always stamps RoutingClaimedOn. Every row that existed before these columns has
    /// MailRoutedOn NULL too, so without that guard the first run after deploy would replay all recent
    /// history - re-moving mail, overwriting mail statuses a human may since have corrected, and re-sending
    /// every alert. RoutingClaimedOn NULL is what tells history apart, with no backfill or deploy step.
    /// </para>
    /// <para>
    /// Also bounded to submissions completed within <see cref="DatabaseOptions.PaceRecoveryWindowDays"/> days,
    /// which stops a permanently unroutable message (e.g. its Graph message was deleted) from being retried on
    /// every run forever; anything that ages out of it has stopped being a transient failure and needs a
    /// person, not another automated retry.
    /// </para>
    /// </summary>
    public async Task<List<PaceUnroutedSubmission>> ClaimUnroutedFinalSubmissionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<PaceUnroutedSubmission>(new CommandDefinition(
            """
            DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
            DECLARE @WindowStart DATETIME2(3) = DATEADD(DAY, -@RecoveryWindowDays, @Now);
            DECLARE @LeaseExpiredOn DATETIME2(3) = DATEADD(MINUTE, -@RoutingLeaseMinutes, @Now);

            WITH Unrouted AS
            (
                -- Final rows always have ModifiedOn (ClaimNextAsync and CompleteAsync both set it), so no
                -- COALESCE with CreatedOn is needed, and without one this seeks IX_PaceSubmission_RoutingRecovery.
                SELECT candidate.[PaceSubmissionId]
                FROM [intgr].[PaceSubmission] AS candidate WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
                WHERE candidate.[MailRoutedOn] IS NULL
                  AND candidate.[ModifiedOn] >= @WindowStart
                  -- NULL = completed before this code existed: history, never swept (see the table comment).
                  -- IS NOT NULL is implied by the comparison, but spelled out so the filtered index matches.
                  AND candidate.[RoutingClaimedOn] IS NOT NULL
                  AND candidate.[RoutingClaimedOn] <= @LeaseExpiredOn
                  AND (candidate.[RequiresReview] = 1
                       OR candidate.[StatusCodeId] IN (@ErrorStatusId, @PoNotReceivedStatusId))
            )
            UPDATE submission
               SET [RoutingClaimedOn] = @Now
            OUTPUT
                   inserted.[PaceSubmissionId],
                   inserted.[InvoiceId],
                   mailMessage.[MailMessageId],
                   mailMessage.[GraphMessageId],
                   invoice.[InvoiceNumber],
                   invoice.[CustomerPO],
                   client.[PaceVendorAccountNumber],
                   status.[StatusCode],
                   inserted.[ErrorMessage],
                   inserted.[PaceBillBatchId],
                   inserted.[PaceBillId],
                   inserted.[RequiresReview],
                   inserted.[BillVendor],
                   CAST(CASE WHEN inserted.[NotifiedOn] IS NULL THEN 0 ELSE 1 END AS BIT) AS [AlreadyNotified]
            FROM [intgr].[PaceSubmission] AS submission
            INNER JOIN Unrouted AS unrouted
                ON unrouted.[PaceSubmissionId] = submission.[PaceSubmissionId]
            INNER JOIN [intgr].[PaceSubmissionStatus] AS status
                ON status.[StatusCodeId] = submission.[StatusCodeId]
            INNER JOIN [dbo].[Invoice] AS invoice
                ON invoice.[InvoiceId] = submission.[InvoiceId]
            INNER JOIN [dbo].[MailMessage] AS mailMessage
                ON mailMessage.[MailMessageId] = invoice.[MailMessageId]
            INNER JOIN [dbo].[Client] AS client
                ON client.[ClientId] = invoice.[ClientId];
            """,
                new
                {
                    ErrorStatusId = PaceSubmissionStatus.ErrorId,
                    PoNotReceivedStatusId = PaceSubmissionStatus.PoNotReceivedId,
                    RecoveryWindowDays = connectionFactory.PaceRecoveryWindowDays,
                    // DatabaseOptions.PaceRoutingLeaseMinutes: how long the run that completed or swept a row
                    // owns its routing before another run may retry it.
                    RoutingLeaseMinutes = connectionFactory.PaceRoutingLeaseMinutes
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return rows.AsList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to claim unrouted final Pace submissions for mail-routing recovery.");
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
