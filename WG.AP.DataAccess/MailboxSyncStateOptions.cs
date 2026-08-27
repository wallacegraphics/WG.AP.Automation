namespace WG.AP.DataAccess;

public sealed class MailboxSyncStateOptions
{
    public const string SectionName = "MailboxSyncState";

    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "mailbox-sync-state");
}
