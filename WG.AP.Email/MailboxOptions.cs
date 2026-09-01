using WG.AP.Core.Abstractions;

namespace WG.AP.Email;

public sealed class MailboxOptions
{
    public const string SectionName = "Mailbox";

    public required string TenantId { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public required string MailboxUser { get; init; }

    /// <summary>
    /// The mailbox's Entra object id. Find it in the Entra portal under the mailbox user's
    /// properties ("Object ID").
    /// </summary>
    /// <remarks>
    /// Mailbox-scoped state is keyed on this rather than on the address, so renaming the AP mailbox
    /// does not start a fresh delta sync and re-decide every message still in the Inbox.
    /// <para>
    /// Configured rather than resolved from Graph on purpose. Reading <c>/users/{id}</c> app-only
    /// needs <c>User.Read.All</c>, and this app is deliberately granted only <c>Mail.ReadWrite</c>
    /// and <c>Mail.Send</c> — buying a stable key with a broader directory permission would be a poor
    /// trade. A startup guard rejects an empty value, since a wrong id silently forks the dedup
    /// namespace rather than failing.
    /// </para>
    /// </remarks>
    public required Guid MailboxId { get; init; }

    /// <summary>The mailbox as <see cref="IMailboxSyncStateStore"/> and the repositories key it.</summary>
    public MailboxRef ToMailboxRef() => new(MailboxId, MailboxUser);

    /// <summary>
    /// Must be explicitly set to true. SDP-178 requires this adapter to run only against a test
    /// mailbox — it moves mail into subfolders and must never be pointed at the live AP inbox.
    /// </summary>
    public required bool IsTestMailbox { get; init; }

    /// <summary>
    /// Hard cap on a single attachment's size. Defaults to Exchange Online's standard org-wide
    /// message size limit (35MB) — an attachment above this is a hard error rather than an attempt
    /// to load an arbitrarily large blob into memory.
    /// </summary>
    public long MaxAttachmentSizeBytes { get; init; } = 35L * 1024 * 1024;
}
