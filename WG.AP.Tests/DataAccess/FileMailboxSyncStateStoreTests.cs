using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;

namespace WG.AP.Tests.DataAccess;

public class FileMailboxSyncStateStoreTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));

    private static readonly MailboxRef MailboxA =
        new(new Guid("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), "mailbox-a@wallacegraphics.com");

    private static readonly MailboxRef MailboxB =
        new(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "mailbox-b@wallacegraphics.com");

    private FileMailboxSyncStateStore CreateStore() =>
        new(
            Options.Create(new MailboxSyncStateOptions { DataDirectory = _dataDirectory }),
            NullLogger<FileMailboxSyncStateStore>.Instance);

    [Fact]
    public async Task GetDeltaLinkAsync_ReturnsNull_WhenNoStateHasBeenSaved()
    {
        var store = CreateStore();

        var deltaLink = await store.GetDeltaLinkAsync(MailboxA, CancellationToken.None);

        Assert.Null(deltaLink);
    }

    [Fact]
    public async Task SaveThenGetDeltaLinkAsync_RoundTripsTheValue()
    {
        var store = CreateStore();
        const string deltaLink = "https://graph.microsoft.com/v1.0/users/mailbox@wallacegraphics.com/mailFolders/inbox/messages/delta?%24deltatoken=token-1";

        await store.SaveDeltaLinkAsync(MailboxA, deltaLink, CancellationToken.None);
        var readBack = await store.GetDeltaLinkAsync(MailboxA, CancellationToken.None);

        Assert.Equal(deltaLink, readBack);
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_OverwritesThePreviousValue_ForTheSameMailbox()
    {
        var store = CreateStore();

        await store.SaveDeltaLinkAsync(MailboxA, "token-1", CancellationToken.None);
        await store.SaveDeltaLinkAsync(MailboxA, "token-2", CancellationToken.None);
        var readBack = await store.GetDeltaLinkAsync(MailboxA, CancellationToken.None);

        Assert.Equal("token-2", readBack);
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_KeepsSeparateStateForDifferentMailboxes()
    {
        var store = CreateStore();

        await store.SaveDeltaLinkAsync(MailboxA, "token-a", CancellationToken.None);
        await store.SaveDeltaLinkAsync(MailboxB, "token-b", CancellationToken.None);

        Assert.Equal("token-a", await store.GetDeltaLinkAsync(MailboxA, CancellationToken.None));
        Assert.Equal("token-b", await store.GetDeltaLinkAsync(MailboxB, CancellationToken.None));
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_KeepsMailboxesApart_WhenTheirAddressesDifferOnlyInPunctuation()
    {
        // The old naming scheme derived the filename from the address and replaced every
        // non-alphanumeric character with '_', so these two collided on one file and silently shared a
        // cursor - one mailbox's delta link overwriting the other's. Keying on the mailbox id fixes it.
        var store = CreateStore();
        var dotted = new MailboxRef(Guid.NewGuid(), "a.b@wallacegraphics.com");
        var underscored = new MailboxRef(Guid.NewGuid(), "a_b@wallacegraphics.com");

        await store.SaveDeltaLinkAsync(dotted, "token-dotted", CancellationToken.None);
        await store.SaveDeltaLinkAsync(underscored, "token-underscored", CancellationToken.None);

        Assert.Equal("token-dotted", await store.GetDeltaLinkAsync(dotted, CancellationToken.None));
        Assert.Equal("token-underscored", await store.GetDeltaLinkAsync(underscored, CancellationToken.None));
    }

    [Fact]
    public async Task GetDeltaLinkAsync_ReadsThePreCutoverFile_WhenNothingExistsUnderTheNewName()
    {
        // The fallback half of DualMailboxSyncStateStore exists to carry the cursor through a week-one
        // SQL problem. A file written before this store keyed on the mailbox id sits under a name
        // derived from the address, so without the legacy read the safety net cannot see the very file
        // it is the safety net for - and the first run after cutover resyncs the whole inbox while
        // looking, from the outside, exactly like a mailbox that had never been synced.
        var store = CreateStore();
        var mailbox = new MailboxRef(Guid.NewGuid(), "ap.invoices@wallacegraphics.com");

        WriteLegacyStateFile(mailbox, "legacy-token");

        Assert.Equal("legacy-token", await store.GetDeltaLinkAsync(mailbox, CancellationToken.None));
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_SupersedesThePreCutoverFile_RatherThanWritingToIt()
    {
        var store = CreateStore();
        var mailbox = new MailboxRef(Guid.NewGuid(), "ap.invoices@wallacegraphics.com");
        var legacyPath = Path.Combine(_dataDirectory, "ap_invoices_wallacegraphics_com.json");

        WriteLegacyStateFile(mailbox, "legacy-token");
        await store.SaveDeltaLinkAsync(mailbox, "new-token", CancellationToken.None);

        // The new name is what is read from now on, so the legacy file stops mattering the moment one
        // run commits - which is what keeps this a one-time concession rather than two live cursors.
        Assert.True(File.Exists(Path.Combine(_dataDirectory, $"{mailbox.MailboxId:D}.json")));
        Assert.Equal("new-token", await store.GetDeltaLinkAsync(mailbox, CancellationToken.None));
        Assert.Contains("legacy-token", await File.ReadAllTextAsync(legacyPath));
    }

    // Byte-for-byte what the pre-cutover store wrote: the filename derived from the address, and a
    // payload with no MailboxId property, since the record did not have one yet.
    private void WriteLegacyStateFile(MailboxRef mailbox, string deltaLink)
    {
        var safeName = new string(mailbox.MailboxUser
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_')
            .ToArray());

        Directory.CreateDirectory(_dataDirectory);

        File.WriteAllText(
            Path.Combine(_dataDirectory, $"{safeName}.json"),
            $$"""
            {"MailboxUser":"{{mailbox.MailboxUser}}","DeltaLink":"{{deltaLink}}","UpdatedAtUtc":"2026-08-31T12:00:00+00:00"}
            """);
    }

    [Fact]
    public async Task SaveDeltaLinkAsync_CleansUpTheTempFile_WhenTheMoveFails()
    {
        var store = CreateStore();

        // Force File.Move(tempPath, path, overwrite: true) to fail deterministically by making the
        // destination an existing directory instead of a file.
        var destinationPath = Path.Combine(_dataDirectory, $"{MailboxA.MailboxId:D}.json");
        Directory.CreateDirectory(destinationPath);

        // The destination being an existing directory makes File.Move fail — the exact exception
        // type is platform-dependent (IOException vs UnauthorizedAccessException), so only assert
        // that the write fails and the temp file is nonetheless cleaned up.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SaveDeltaLinkAsync(MailboxA, "token-1", CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_dataDirectory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
