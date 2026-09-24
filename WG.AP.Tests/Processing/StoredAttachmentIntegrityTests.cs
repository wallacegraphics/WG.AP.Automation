using System.Security.Cryptography;
using WG.AP.Processor;

namespace WG.AP.Tests.Processing;

public class StoredAttachmentIntegrityTests
{
    [Fact]
    public void StoredBytesMatchRecordedHash_ReturnsTrue_WhenBytesAreUnchanged()
    {
        var content = new byte[] { 1, 2, 3, 4 };

        Assert.True(APProcessor.StoredBytesMatchRecordedHash(content, SHA256.HashData(content)));
    }

    [Fact]
    public void StoredBytesMatchRecordedHash_ReturnsFalse_WhenBytesWereModifiedOnDisk()
    {
        var original = new byte[] { 1, 2, 3, 4 };
        var corrupted = new byte[] { 1, 2, 3, 5 };

        Assert.False(APProcessor.StoredBytesMatchRecordedHash(corrupted, SHA256.HashData(original)));
    }

    [Fact]
    public void StoredBytesMatchRecordedHash_ReturnsFalse_WhenFileWasTruncated()
    {
        var original = new byte[] { 1, 2, 3, 4 };

        Assert.False(APProcessor.StoredBytesMatchRecordedHash(original[..2], SHA256.HashData(original)));
    }
}
