using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// Reads and writes <c>dbo.MailMessage</c> — the table that makes "an email is never processed
/// twice" true.
/// </summary>
public sealed class MailMessageRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<MailMessageRepository> logger)
{
    /// <summary>
    /// Records a message if it has not been seen before, then claims it for processing.
    /// </summary>
    /// <returns>
    /// A claim whose <see cref="MailMessageClaim.Claimed"/> is false when the message is already in a
    /// final status. The caller must then leave it entirely alone — no parsing, no folder move.
    /// </returns>
    /// <remarks>
    /// Two statements, and between them they are the entire no-reprocess mechanism:
    /// <list type="number">
    /// <item>
    /// The INSERT is guarded by <c>WHERE NOT EXISTS</c> and made safe by
    /// <c>UQ_MailMessage_MessageKeyHash</c>. Graph re-delivers a batch whenever a run crashes before
    /// the delta link is committed, so this runs against already-recorded messages constantly and
    /// must be a no-op when it does. It never touches StatusId, which is why a hundred re-deliveries
    /// of a processed message change nothing but ModifiedOn.
    /// </item>
    /// <item>
    /// The UPDATE joins <c>lkup.Status</c> and gates on <c>IsFinal = 0</c>. The gate is a WHERE
    /// clause rather than an if-statement in C#, so it cannot be forgotten at a call site, and a new
    /// no-reprocess reason is a seed row rather than a code change.
    /// </item>
    /// </list>
    /// A transient failure needs no status of its own: the extractor rethrows, nothing final commits,
    /// the delta link is not advanced, and the row stays claimable. Retry works through the absence of
    /// a final state, exactly as it worked through the absence of a committed delta link before this
    /// table existed.
    /// </remarks>
    public async Task<MailMessageClaim> DiscoverAndClaimAsync(
        MailboxRef mailbox,
        long processingRunId,
        MailMessageSummary message,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            return await connection.QuerySingleAsync<MailMessageClaim>(new CommandDefinition(
                """
                DECLARE @Hash BINARY(32) = CONVERT(BINARY(32), HASHBYTES('SHA2_256',
                    CONCAT(CONVERT(CHAR(36), @MailboxId), N'|', @GraphMessageId)));

                INSERT INTO [dbo].[MailMessage]
                    ([ProcessingRunId], [MailboxId], [GraphMessageId], [SenderAddress], [Subject], [ReceivedOn], [StatusId])
                SELECT @ProcessingRunId, @MailboxId, @GraphMessageId, @SenderAddress, @Subject, @ReceivedOn, @NewStatusId
                 WHERE NOT EXISTS (SELECT 1 FROM [dbo].[MailMessage] WITH (UPDLOCK, HOLDLOCK)
                                    WHERE [MessageKeyHash] = @Hash);

                UPDATE m
                   SET [AttemptCount]    = m.[AttemptCount] + 1,
                       [LastAttemptOn]   = SYSUTCDATETIME(),
                       [ProcessingRunId] = @ProcessingRunId,
                       [ModifiedBy]      = SUSER_SNAME(),
                       [ModifiedOn]      = SYSUTCDATETIME()
                  FROM [dbo].[MailMessage] AS m
                  JOIN [lkup].[Status]     AS s ON s.[StatusId] = m.[StatusId]
                 WHERE m.[MessageKeyHash] = @Hash
                   AND s.[IsFinal] = 0;

                DECLARE @Claimed BIT = CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END;

                SELECT m.[MailMessageId], @Claimed AS [Claimed], m.[StatusId], m.[AttemptCount]
                  FROM [dbo].[MailMessage] AS m
                 WHERE m.[MessageKeyHash] = @Hash;
                """,
                new
                {
                    mailbox.MailboxId,
                    ProcessingRunId = processingRunId,
                    GraphMessageId = message.Id,
                    SenderAddress = message.SenderAddress,
                    Subject = Truncate(message.Subject, 500),
                    ReceivedOn = message.ReceivedDateTime,
                    NewStatusId = (int)ApStatus.MailNew
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record or claim message {MessageId} for {MailboxUser}.", message.Id, mailbox.MailboxUser);
            throw;
        }
    }

    /// <summary>Sets the final status of a message, and the folder it was routed to.</summary>
    public async Task SetStatusAsync(
        long mailMessageId,
        ApStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE [dbo].[MailMessage]
                   SET [StatusId]     = @StatusId,
                       [ErrorMessage] = @ErrorMessage,
                       [ModifiedBy]   = SUSER_SNAME(),
                       [ModifiedOn]   = SYSUTCDATETIME()
                 WHERE [MailMessageId] = @MailMessageId;
                """,
                new
                {
                    MailMessageId = mailMessageId,
                    StatusId = (int)status,
                    ErrorMessage = Truncate(errorMessage, 1000)
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to set status {Status} on mail message {MailMessageId}.", status, mailMessageId);
            throw;
        }
    }

    /// <summary>
    /// Loads which mail folder each status routes to. Null means "leave the message in the Inbox".
    /// </summary>
    /// <remarks>
    /// Read from <c>lkup.Status.MailFolder</c> rather than switched on in C#, so "MailSkipped stays in
    /// the Inbox" is data — which is also what lets a fourth folder be introduced later by updating
    /// one lookup row. Loaded once per run rather than per message: it cannot change mid-run, and a
    /// round trip per message would buy nothing.
    /// </remarks>
    public async Task<IReadOnlyDictionary<ApStatus, string?>> LoadMailFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<(int StatusId, string? MailFolder)>(new CommandDefinition(
                "SELECT [StatusId], [MailFolder] FROM [lkup].[Status];",
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return rows.ToDictionary(row => (ApStatus)row.StatusId, row => row.MailFolder);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load the status-to-folder map from lkup.Status.");
            throw;
        }
    }

    // Subject and error text are bounded in the schema but not at the source: Graph subjects can run
    // long, and an exception message is arbitrary. Truncating here keeps a long value from failing
    // the write - losing the tail of a subject is a far better outcome than losing the row.
    internal static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>True when the exception is a unique-index or unique-constraint violation.</summary>
    internal static bool IsUniqueViolation(SqlException exception) =>
        exception.Number is 2601 or 2627;
}
