namespace WG.AP.Core.Abstractions;

public interface IMailboxSyncStateStore
{
    Task<string?> GetDeltaLinkAsync(string mailboxUser, CancellationToken cancellationToken);

    Task SaveDeltaLinkAsync(string mailboxUser, string deltaLink, CancellationToken cancellationToken);
}
