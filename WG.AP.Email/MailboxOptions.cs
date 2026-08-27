namespace WG.AP.Email;

public sealed class MailboxOptions
{
    public const string SectionName = "Mailbox";

    public required string TenantId { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public required string MailboxUser { get; init; }

    /// <summary>
    /// Must be explicitly set to true. SDP-178 requires this adapter to run only against a test
    /// mailbox — it moves mail into subfolders and must never be pointed at the live AP inbox.
    /// </summary>
    public required bool IsTestMailbox { get; init; }
}
