namespace WG.AP.Core.Abstractions;

/// <summary>
/// Identifies the mailbox being processed.
/// </summary>
/// <param name="MailboxId">
/// The mailbox's Entra object id. This — not the address — is what mailbox-scoped state is keyed
/// on, because it survives a rename: keyed on the address, renaming the AP mailbox would start a
/// fresh delta sync and re-decide every message still sitting in the Inbox.
/// </param>
/// <param name="MailboxUser">The UPN. Carried alongside so stored state stays readable by a human.</param>
public sealed record MailboxRef(Guid MailboxId, string MailboxUser);
