namespace WG.AP.Core.Abstractions;

public interface IMailSource
{
    Task ValidateAuthAsync(CancellationToken cancellationToken);

    Task EnsureFoldersExistAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<MailMessageSummary> EnumerateInboxAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns only inbox messages added/changed since <paramref name="deltaLink"/> (or the whole
    /// current inbox, on a first sync where <paramref name="deltaLink"/> is null), along with the
    /// link to resume from next time. Messages that left the inbox are omitted, not returned as
    /// tombstones — callers only see mail they still need to act on.
    /// </summary>
    Task<MailboxDeltaResult> GetInboxDeltaAsync(string? deltaLink, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a single message by id, independent of which folder it currently lives in — the
    /// mechanism SDP-178 relies on to prove an id survives a <see cref="MoveMessageAsync"/> call.
    /// </summary>
    Task<MailMessageSummary?> GetMessageAsync(string messageId, CancellationToken cancellationToken);

    Task<byte[]> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken cancellationToken);

    Task<string> MoveMessageAsync(string messageId, MailDestinationFolder destination, CancellationToken cancellationToken);
}
