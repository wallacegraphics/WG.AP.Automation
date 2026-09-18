using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.DataAccess;

namespace WG.AP.Tests.DataAccess;

/// <summary>
/// Covers the two properties a retried <c>ProcessPdfAsync</c> depends on: that <see cref="AttachmentFileStore.LoadAsync"/>
/// round-trips exactly what <see cref="AttachmentFileStore.SaveAsync"/> wrote, and that a missing file
/// surfaces as the specific exception type the retry branch catches. Also documents the .NET path
/// behavior behind the guard clause added to <see cref="AttachmentFileStore.SaveAsync"/> after a real
/// production failure (2026-09-17, attachment 16479) where a null directory name reached
/// <c>Directory.CreateDirectory</c> as a bare, contextless <see cref="ArgumentNullException"/> - see
/// the last test for why that guard cannot be exercised through <c>SaveAsync</c>'s own parameters.
/// </summary>
public class AttachmentFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));

    private AttachmentFileStore CreateStore() =>
        new(
            Options.Create(new FileStorageOptions { RootDirectory = _root }),
            NullLogger<AttachmentFileStore>.Instance);

    [Fact]
    public async Task LoadAsync_ReturnsExactlyWhatSaveAsyncWrote()
    {
        Directory.CreateDirectory(_root);
        var store = CreateStore();
        var content = new byte[] { 1, 2, 3, 4, 5 };

        var (relativePath, sha256) = await store.SaveAsync(
            mailAttachmentId: 1,
            fileName: "invoice.pdf",
            receivedOn: new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
            content: content,
            cancellationToken: CancellationToken.None);

        var loaded = await store.LoadAsync(relativePath, CancellationToken.None);

        Assert.Equal(content, loaded);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(loaded), sha256);
    }

    [Fact]
    public async Task LoadAsync_ThrowsFileNotFoundException_WhenThePathWasNeverWritten()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "2026", "09"));
        var store = CreateStore();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.LoadAsync(Path.Combine("2026", "09", "999-nope.pdf"), CancellationToken.None));
    }

    [Fact]
    public void PathGetDirectoryName_ReturnsNullOrEmpty_ForABareRootOrFileName()
    {
        // Documents exactly the .NET behavior the guard clause in SaveAsync exists to catch. Note this
        // combination is not reproducible through SaveAsync's own parameters: relativePath always has a
        // yyyy\MM\ prefix (so it can never reduce to a bare root or bare filename), and
        // SanitizeFileName strips ':' and '\' so the filename segment can never make Path.Combine treat
        // it as rooted and discard the configured root. The 2026-09-17 production failure (attachment
        // 16479) that motivated this guard could not be reproduced for that reason - the row that would
        // explain exactly what input triggered it was deleted before this was investigated. This test
        // instead pins the underlying Path.GetDirectoryName behavior directly, so the guard clause in
        // SaveAsync remains explainable even without a working repro.
        Assert.Null(Path.GetDirectoryName(@"\\server\share"));
        Assert.Equal(string.Empty, Path.GetDirectoryName("bare-filename.pdf"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }

        GC.SuppressFinalize(this);
    }
}
