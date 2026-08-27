using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.DataAccess;

namespace WG.AP.Tests.DataAccess;

public class FileMailboxSyncStateStoreTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));

    private FileMailboxSyncStateStore CreateStore() =>
        new(
            Options.Create(new MailboxSyncStateOptions { DataDirectory = _dataDirectory }),
            NullLogger<FileMailboxSyncStateStore>.Instance);

    [Fact]
    public async Task GetDeltaLinkAsync_ReturnsNull_WhenNoStateHasBeenSaved()
    {
        var store = CreateStore();

        var deltaLink = await store.GetDeltaLinkAsync("mailbox@wallacegraphics.com", CancellationToken.None);

        Assert.Null(deltaLink);
    }

    [Fact]
    public async Task SaveThenGetDeltaLinkAsync_RoundTripsTheValue()
    {
        var store = CreateStore();
        const string mailboxUser = "mailbox@wallacegraphics.com";
        const string deltaLink = "https://graph.microsoft.com/v1.0/users/mailbox@wallacegraphics.com/mailFolders/inbox/messages/delta?%24deltatoken=token-1";

        await store.SaveDeltaLinkAsync(mailboxUser, deltaLink, CancellationToken.None);
        var readBack = await store.GetDeltaLinkAsync(mailboxUser, CancellationToken.None);

        Assert.Equal(deltaLink, readBack);
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_OverwritesThePreviousValue_ForTheSameMailbox()
    {
        var store = CreateStore();
        const string mailboxUser = "mailbox@wallacegraphics.com";

        await store.SaveDeltaLinkAsync(mailboxUser, "token-1", CancellationToken.None);
        await store.SaveDeltaLinkAsync(mailboxUser, "token-2", CancellationToken.None);
        var readBack = await store.GetDeltaLinkAsync(mailboxUser, CancellationToken.None);

        Assert.Equal("token-2", readBack);
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_KeepsSeparateStateForDifferentMailboxes()
    {
        var store = CreateStore();

        await store.SaveDeltaLinkAsync("mailbox-a@wallacegraphics.com", "token-a", CancellationToken.None);
        await store.SaveDeltaLinkAsync("mailbox-b@wallacegraphics.com", "token-b", CancellationToken.None);

        Assert.Equal("token-a", await store.GetDeltaLinkAsync("mailbox-a@wallacegraphics.com", CancellationToken.None));
        Assert.Equal("token-b", await store.GetDeltaLinkAsync("mailbox-b@wallacegraphics.com", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
