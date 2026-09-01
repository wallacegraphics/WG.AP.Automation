namespace WG.AP.Core.Abstractions;

/// <summary>
/// Stores the Graph delta link that marks how far the mailbox has been read.
/// </summary>
/// <remarks>
/// Keyed on <see cref="MailboxRef.MailboxId"/> rather than the address — see <see cref="MailboxRef"/>.
/// <para>
/// The link must only be saved once the caller has finished handling the whole batch. Committing
/// earlier means a crash mid-batch loses mail instead of re-delivering it.
/// </para>
/// </remarks>
public interface IMailboxSyncStateStore
{
    Task<string?> GetDeltaLinkAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    Task SaveDeltaLinkAsync(MailboxRef mailbox, string deltaLink, CancellationToken cancellationToken);
}
