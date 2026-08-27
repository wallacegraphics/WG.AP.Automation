using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.Email;

namespace WG.AP.Tests.Email;

public class MailboxSyncProcessorTests
{
    private const string MailboxUser = "test-mailbox@wallacegraphics.com";

    private sealed class FakeMailSource : IMailSource
    {
        public string? ReceivedDeltaLink { get; private set; }
        public MailboxDeltaResult DeltaResultToReturn { get; set; } = new([], "next-delta-link");

        public Task<MailboxDeltaResult> GetInboxDeltaAsync(string? deltaLink, CancellationToken cancellationToken)
        {
            ReceivedDeltaLink = deltaLink;
            return Task.FromResult(DeltaResultToReturn);
        }

        public Task ValidateAuthAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task EnsureFoldersExistAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<MailMessageSummary> EnumerateInboxAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MailMessageSummary?> GetMessageAsync(string messageId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<byte[]> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> MoveMessageAsync(string messageId, MailDestinationFolder destination, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class FakeSyncStateStore : IMailboxSyncStateStore
    {
        private readonly Dictionary<string, string> _deltaLinksByMailbox = [];
        public int SaveCallCount { get; private set; }

        public void Seed(string mailboxUser, string deltaLink) => _deltaLinksByMailbox[mailboxUser] = deltaLink;

        public Task<string?> GetDeltaLinkAsync(string mailboxUser, CancellationToken cancellationToken) =>
            Task.FromResult(_deltaLinksByMailbox.GetValueOrDefault(mailboxUser));

        public Task SaveDeltaLinkAsync(string mailboxUser, string deltaLink, CancellationToken cancellationToken)
        {
            SaveCallCount++;
            _deltaLinksByMailbox[mailboxUser] = deltaLink;
            return Task.CompletedTask;
        }
    }

    private static MailboxSyncProcessor CreateProcessor(FakeMailSource mailSource, FakeSyncStateStore syncStateStore) =>
        new(
            mailSource,
            syncStateStore,
            Options.Create(new MailboxOptions
            {
                TenantId = "tenant",
                ClientId = "client",
                ClientSecret = "secret",
                MailboxUser = MailboxUser,
                IsTestMailbox = true
            }),
            NullLogger<MailboxSyncProcessor>.Instance);

    [Fact]
    public async Task GetNewMessagesAsync_OnFirstRun_PassesNullDeltaLinkToTheMailSource()
    {
        var mailSource = new FakeMailSource();
        var syncStateStore = new FakeSyncStateStore();
        var processor = CreateProcessor(mailSource, syncStateStore);

        await processor.GetNewMessagesAsync(CancellationToken.None);

        Assert.Null(mailSource.ReceivedDeltaLink);
    }

    [Fact]
    public async Task GetNewMessagesAsync_PassesThePreviouslySavedDeltaLink_ToTheMailSource()
    {
        var mailSource = new FakeMailSource();
        var syncStateStore = new FakeSyncStateStore();
        syncStateStore.Seed(MailboxUser, "saved-delta-link");
        var processor = CreateProcessor(mailSource, syncStateStore);

        await processor.GetNewMessagesAsync(CancellationToken.None);

        Assert.Equal("saved-delta-link", mailSource.ReceivedDeltaLink);
    }

    [Fact]
    public async Task GetNewMessagesAsync_DoesNotPersistAnything_UntilCommitAsyncIsCalled()
    {
        var mailSource = new FakeMailSource();
        var syncStateStore = new FakeSyncStateStore();
        var processor = CreateProcessor(mailSource, syncStateStore);

        await processor.GetNewMessagesAsync(CancellationToken.None);

        Assert.Equal(0, syncStateStore.SaveCallCount);
    }

    [Fact]
    public async Task CommitAsync_SavesTheBatchsDeltaLink_ForTheConfiguredMailbox()
    {
        var mailSource = new FakeMailSource
        {
            DeltaResultToReturn = new MailboxDeltaResult([], "new-delta-link")
        };
        var syncStateStore = new FakeSyncStateStore();
        var processor = CreateProcessor(mailSource, syncStateStore);

        var batch = await processor.GetNewMessagesAsync(CancellationToken.None);
        await processor.CommitAsync(batch, CancellationToken.None);

        Assert.Equal(1, syncStateStore.SaveCallCount);
        Assert.Equal("new-delta-link", await syncStateStore.GetDeltaLinkAsync(MailboxUser, CancellationToken.None));
    }
}
