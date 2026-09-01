using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// Writes the delta link to both the database and the file store, and reads from the database with
/// the file as a fallback. Temporary, for the first week after cutover.
/// </summary>
/// <remarks>
/// The delta link is the mailbox cursor. If it goes missing, Graph re-delivers the entire inbox —
/// which is harmless now that <c>dbo.MailMessage</c> makes re-delivery a no-op, but it is slow and
/// noisy, and it would happen on <em>every</em> run for as long as the SQL write is broken. Week one
/// is exactly when the SQL path is unproven: a wrong connection string, a missing INSERT grant, a
/// MERGE that silently matches nothing.
/// <para>
/// So the database is authoritative, the file is a safety net, and a database read that comes back
/// empty falls back to the file rather than triggering a full resync. Remove this class and the file
/// store once the two have agreed for a week.
/// </para>
/// <para>
/// A failure to write the <em>file</em> is logged and swallowed: once the database write has
/// succeeded the run's state is safely recorded, and failing the run over the redundant copy would
/// make the safety net a new source of outage.
/// </para>
/// </remarks>
public sealed class DualMailboxSyncStateStore(
    SqlMailboxSyncStateStore primary,
    FileMailboxSyncStateStore fallback,
    ILogger<DualMailboxSyncStateStore> logger) : IMailboxSyncStateStore
{
    public async Task<string?> GetDeltaLinkAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var deltaLink = await primary.GetDeltaLinkAsync(mailbox, cancellationToken);

        if (deltaLink is not null)
        {
            return deltaLink;
        }

        var fromFile = await fallback.GetDeltaLinkAsync(mailbox, cancellationToken);

        if (fromFile is not null)
        {
            logger.LogWarning(
                "Mailbox sync state for {MailboxUser} was missing from the database but present in the file store; using the file. "
                + "This is expected exactly once, on the first run after cutover.",
                mailbox.MailboxUser);
        }

        return fromFile;
    }

    public async Task SaveDeltaLinkAsync(MailboxRef mailbox, string deltaLink, CancellationToken cancellationToken)
    {
        // Database first: it is the one that has to succeed.
        await primary.SaveDeltaLinkAsync(mailbox, deltaLink, cancellationToken);

        try
        {
            await fallback.SaveDeltaLinkAsync(mailbox, deltaLink, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Saved the mailbox sync state to the database but not to the fallback file for {MailboxUser}.", mailbox.MailboxUser);
        }
    }
}
