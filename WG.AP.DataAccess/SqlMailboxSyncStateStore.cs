using Dapper;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// The database-backed delta link store, replacing <see cref="FileMailboxSyncStateStore"/>.
/// </summary>
/// <remarks>
/// The primary key on <c>dbo.MailboxSyncState.MailboxId</c> gives the uniqueness that the file store
/// only got from its filename — and it fixes a real defect there: that store's sanitiser maps every
/// non-alphanumeric character to '_', so two addresses differing only in punctuation
/// (<c>a.b@x.com</c> and <c>a_b@x.com</c>) collide on one file and silently share a cursor.
/// </remarks>
public sealed class SqlMailboxSyncStateStore(
    SqlConnectionFactory connectionFactory,
    ILogger<SqlMailboxSyncStateStore> logger) : IMailboxSyncStateStore
{
    public async Task<string?> GetDeltaLinkAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                """
                SELECT [DeltaLink]
                  FROM [dbo].[MailboxSyncState]
                 WHERE [MailboxId] = @MailboxId;
                """,
                new { mailbox.MailboxId },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read the mailbox sync state for {MailboxUser} ({MailboxId}).", mailbox.MailboxUser, mailbox.MailboxId);
            throw;
        }
    }

    public async Task SaveDeltaLinkAsync(MailboxRef mailbox, string deltaLink, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                MERGE [dbo].[MailboxSyncState] WITH (HOLDLOCK) AS target
                USING (SELECT @MailboxId AS [MailboxId]) AS source
                    ON target.[MailboxId] = source.[MailboxId]
                WHEN MATCHED THEN
                    UPDATE SET target.[MailboxUser] = @MailboxUser,
                               target.[DeltaLink]   = @DeltaLink,
                               target.[ModifiedBy]  = SUSER_SNAME(),
                               target.[ModifiedOn]  = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([MailboxId], [MailboxUser], [DeltaLink])
                    VALUES (@MailboxId, @MailboxUser, @DeltaLink);
                """,
                new { mailbox.MailboxId, mailbox.MailboxUser, DeltaLink = deltaLink },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the mailbox sync state for {MailboxUser} ({MailboxId}).", mailbox.MailboxUser, mailbox.MailboxId);
            throw;
        }
    }
}
