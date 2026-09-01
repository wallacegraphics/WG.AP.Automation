using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WG.AP.DataAccess;

/// <summary>Where invoice attachment bytes are kept.</summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>
    /// Root directory (a UNC share in Dev and Prod) that stored attachments live under.
    /// </summary>
    /// <remarks>
    /// Only the root lives in configuration; the database stores paths relative to it. That way moving
    /// the share is a config change rather than an UPDATE across every historical row — and the
    /// 7-year retention on these files means there will be a lot of historical rows.
    /// </remarks>
    public required string RootDirectory { get; init; }
}

/// <summary>
/// Writes attachment bytes to the file share and returns the relative path and hash to record.
/// </summary>
public sealed class AttachmentFileStore(
    IOptions<FileStorageOptions> options,
    ILogger<AttachmentFileStore> logger)
{
    /// <summary>
    /// Writes one attachment and returns its path relative to the configured root, plus its SHA-256.
    /// </summary>
    /// <remarks>
    /// Laid out as <c>yyyy\MM\{mailAttachmentId}-{filename}</c>. The id prefix is what makes the name
    /// unique: clients do send two attachments with the same filename on one email, so the filename
    /// alone would have one silently overwrite the other. The date folders keep any single directory
    /// small enough to open, and make retention a matter of deleting a month.
    /// <para>
    /// The caller writes the file before recording the row, never the other way round.
    /// <c>CK_MailAttachment_Stored</c> requires the path and hash together, and an orphan file is
    /// harmless whereas a row pointing at a file that does not exist is not.
    /// </para>
    /// </remarks>
    public async Task<(string RelativePath, byte[] Sha256)> SaveAsync(
        long mailAttachmentId,
        string fileName,
        DateTimeOffset receivedOn,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine(
            receivedOn.ToString("yyyy"),
            receivedOn.ToString("MM"),
            $"{mailAttachmentId}-{SanitizeFileName(fileName)}");

        var fullPath = Path.Combine(options.Value.RootDirectory, relativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, content, cancellationToken);

            return (relativePath, SHA256.HashData(content));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to store attachment {MailAttachmentId} at {FullPath}.", mailAttachmentId, fullPath);
            throw;
        }
    }

    // Attachment names come from an external mailbox, so they are untrusted input on a path. Anything
    // that is not plainly safe becomes '_' - which also rules out the '..' and separator characters
    // that would otherwise let a crafted name escape the configured root.
    private static string SanitizeFileName(string fileName)
    {
        var safe = new string(fileName
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray())
            .TrimStart('.');

        return safe.Length == 0 ? "attachment" : Truncate(safe, 120);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
