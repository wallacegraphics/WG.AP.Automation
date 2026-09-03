using Dapper;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// Records the attachments seen on a message, and where their bytes were stored.
/// </summary>
public sealed class MailAttachmentRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<MailAttachmentRepository> logger)
{
    /// <summary>
    /// Records every attachment on a message, including the ones nothing will read.
    /// </summary>
    /// <remarks>
    /// Excel attachments are recorded even though the manifest logic is gone: an attachment row with
    /// <c>Kind = 'Excel'</c> is how "a manifest arrived and we ignored it" stays visible, which is the
    /// question that gets asked the first time an invoice turns out to be wrong. Their StoredPath and
    /// ContentSha256 stay null because nothing downloads them.
    /// <para>
    /// Idempotent on <c>UQ_MailAttachment_Graph</c>, so a re-delivered message does not duplicate its
    /// attachment rows.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<RecordedAttachment>> RecordAsync(
        long mailMessageId,
        IReadOnlyList<MailAttachmentSummary> attachments,
        CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            return [];
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            var recorded = new List<RecordedAttachment>(attachments.Count);

            foreach (var attachment in attachments)
            {
                var mailAttachmentId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO [dbo].[MailAttachment]
                        ([MailMessageId], [GraphAttachmentId], [FileName], [ContentType], [SizeInBytes], [CreatedBy])
                    SELECT @MailMessageId, @GraphAttachmentId, @FileName, @ContentType, @SizeInBytes, @AppIdentity
                     WHERE NOT EXISTS (SELECT 1 FROM [dbo].[MailAttachment] WITH (UPDLOCK, HOLDLOCK)
                                        WHERE [MailMessageId] = @MailMessageId
                                          AND [GraphAttachmentId] = @GraphAttachmentId);

                    SELECT [MailAttachmentId]
                      FROM [dbo].[MailAttachment]
                     WHERE [MailMessageId] = @MailMessageId
                       AND [GraphAttachmentId] = @GraphAttachmentId;
                    """,
                    new
                    {
                        MailMessageId = mailMessageId,
                        GraphAttachmentId = attachment.Id,
                        FileName = MailMessageRepository.Truncate(attachment.Name, 400),
                        ContentType = MailMessageRepository.Truncate(attachment.ContentType, 200),
                        SizeInBytes = attachment.SizeInBytes,
                        AppIdentity = connectionFactory.AppIdentity
                    },
                    commandTimeout: connectionFactory.CommandTimeoutSeconds,
                    cancellationToken: cancellationToken));

                recorded.Add(new RecordedAttachment(mailAttachmentId, attachment));
            }

            return recorded;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record attachments for mail message {MailMessageId}.", mailMessageId);
            throw;
        }
    }

    /// <summary>Records where an attachment's bytes were written, and their hash.</summary>
    /// <remarks>
    /// Called only after the file has actually been written. <c>CK_MailAttachment_Stored</c> requires
    /// the path and the hash together, and the ordering matters: file first, then the row. An orphan
    /// file is harmless; a row pointing at a file that does not exist is not.
    /// </remarks>
    public async Task SetStoredAsync(
        long mailAttachmentId,
        string storedPath,
        byte[] contentSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE [dbo].[MailAttachment]
                   SET [StoredPath]    = @StoredPath,
                       [ContentSha256] = @ContentSha256,
                       [ModifiedBy]    = @AppIdentity,
                       [ModifiedOn]    = SYSUTCDATETIME()
                 WHERE [MailAttachmentId] = @MailAttachmentId;
                """,
                new
                {
                    MailAttachmentId = mailAttachmentId,
                    StoredPath = storedPath,
                    ContentSha256 = contentSha256,
                    AppIdentity = connectionFactory.AppIdentity
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record the stored file for attachment {MailAttachmentId}.", mailAttachmentId);
            throw;
        }
    }
}
