using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;

namespace WG.AP.Email;

/// <summary>
/// Fetches only mail that's new since the last successfully committed sync, using
/// <see cref="IMailSource.GetInboxDeltaAsync"/> under an <see cref="IMailboxSyncStateStore"/>
/// checkpoint. <see cref="CommitAsync"/> must be called only once the caller has fully handled the
/// batch — committing earlier risks losing messages if processing fails partway through.
/// </summary>
public sealed class MailboxSyncProcessor(
    IMailSource mailSource,
    IMailboxSyncStateStore syncStateStore,
    IOptions<MailboxOptions> mailboxOptions,
    ILogger<MailboxSyncProcessor> logger)
{
    public async Task<MailboxDeltaResult> GetNewMessagesAsync(CancellationToken cancellationToken)
    {
        string? mailboxUser = null;

        try
        {
            // Read inside the try, not before it: this can itself throw (e.g. required Mailbox
            // config missing) and must be logged with context like everything else here.
            mailboxUser = mailboxOptions.Value.MailboxUser;

            var deltaLink = await syncStateStore.GetDeltaLinkAsync(mailboxUser, cancellationToken);

            logger.LogInformation(
                "Fetching mailbox delta for {MailboxUser} ({SyncKind} sync).",
                mailboxUser,
                deltaLink is null ? "initial" : "incremental");

            return await mailSource.GetInboxDeltaAsync(deltaLink, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to fetch new mailbox messages for {MailboxUser}.", mailboxUser ?? "(unknown — mailbox options unavailable)");
            throw;
        }
    }

    public async Task CommitAsync(MailboxDeltaResult batch, CancellationToken cancellationToken)
    {
        string? mailboxUser = null;

        try
        {
            mailboxUser = mailboxOptions.Value.MailboxUser;
            await syncStateStore.SaveDeltaLinkAsync(mailboxUser, batch.DeltaLink, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to commit mailbox sync state for {MailboxUser}.", mailboxUser ?? "(unknown — mailbox options unavailable)");
            throw;
        }
    }
}
