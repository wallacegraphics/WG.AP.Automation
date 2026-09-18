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
/// Startup validation for <see cref="FileStorageOptions"/>: the root must exist and be writable.
/// </summary>
/// <remarks>
/// A non-empty string is not the guarantee that matters. The real failure modes are a UNC share that
/// is unreachable or has not been provisioned yet, and a path the service account can read but not
/// write — neither of which a string check sees. Without this the first symptom is a mid-run
/// exception from <see cref="AttachmentFileStore.SaveAsync"/> after mail has already been fetched.
/// <para>
/// The root is deliberately <b>not</b> created on demand, unlike the <c>yyyy\MM</c> folders beneath
/// it. Invoice attachments carry a 7-year retention requirement, so a mistyped path must fail loudly
/// rather than quietly produce a new empty folder that nobody is backing up — and on a UNC share,
/// provisioning is IT's to do, not this process's.
/// </para>
/// <para>
/// Writability is checked by actually writing, because permissions, share-level rights and
/// read-only mounts cannot be inferred from the path. Note this costs a round trip to the share at
/// startup, and an unreachable UNC path will block for the OS timeout before failing.
/// </para>
/// </remarks>
public sealed class FileStorageOptionsValidator : IValidateOptions<FileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options)
    {
        var root = options.RootDirectory;

        if (string.IsNullOrWhiteSpace(root))
        {
            return ValidateOptionsResult.Fail(
                $"{FileStorageOptions.SectionName}:RootDirectory is required — invoice attachments are kept for " +
                "7 years and the database only stores paths relative to it.");
        }

        try
        {
            if (!Directory.Exists(root))
            {
                return ValidateOptionsResult.Fail(
                    $"{FileStorageOptions.SectionName}:RootDirectory '{root}' does not exist or is unreachable. " +
                    "It is not created on demand: attachments are kept for 7 years, so a mistyped or offline path " +
                    "must fail here rather than silently write invoice data somewhere unmonitored. Create the " +
                    "directory, or correct the setting.");
            }
        }
        catch (Exception exception)
        {
            return ValidateOptionsResult.Fail(
                $"{FileStorageOptions.SectionName}:RootDirectory '{root}' could not be read: {exception.Message}");
        }

        // Create-and-write, not just create: a zero-byte file can succeed where an actual write fails.
        var probePath = Path.Combine(root, $".wgap-writecheck-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(probePath, [0x57, 0x47]);
        }
        catch (Exception exception)
        {
            return ValidateOptionsResult.Fail(
                $"{FileStorageOptions.SectionName}:RootDirectory '{root}' exists but is not writable by this " +
                $"account: {exception.Message}");
        }
        finally
        {
            // Best effort. A probe file left behind is harmless clutter, and failing startup over it would
            // turn a missing delete permission into an outage - retention deletes are a separate concern.
            try
            {
                File.Delete(probePath);
            }
            catch
            {
                // Intentionally ignored.
            }
        }

        return ValidateOptionsResult.Success;
    }
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
    /// <para>
    /// "Retention is a matter of deleting a month" stops being true the moment two different
    /// attachment rows share one physical file, which the caller does deliberately for a
    /// content-duplicate (see <c>MailAttachmentRepository.FindDuplicateByHashAsync</c>): the file lives
    /// under the FIRST attachment's <c>yyyy\MM</c>, but a LATER-dated duplicate row's only copy can be
    /// that same file. A future retention sweep must not delete a month's folder just because every
    /// file in it is old - it must first confirm no <c>MailAttachment.StoredPath</c> of any age still
    /// points into it. No such sweep exists in this repo yet; this is a warning for whoever builds one.
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
            var directoryPath = Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(directoryPath))
            {
                // Path.Combine on two non-null strings should never produce a path with no directory
                // component, so this is not expected - but it happened once (2026-09-17, attachment
                // 16479) and the prior code's Path.GetDirectoryName(fullPath)! turned it into a bare
                // ArgumentNullException from Directory.CreateDirectory with no context at all. Failing
                // here instead captures every input that produced the bad path, so a recurrence is
                // diagnosable on the spot rather than needing another after-the-fact log investigation.
                throw new InvalidOperationException(
                    $"Attachment {mailAttachmentId}: computed path '{fullPath}' (root '{options.Value.RootDirectory}', " +
                    $"relative '{relativePath}', original filename '{fileName}') has no directory component; refusing to write.");
            }

            Directory.CreateDirectory(directoryPath);
            await File.WriteAllBytesAsync(fullPath, content, cancellationToken);

            return (relativePath, SHA256.HashData(content));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to store attachment {MailAttachmentId} at {FullPath}.", mailAttachmentId, fullPath);
            throw;
        }
    }

    /// <summary>Reads back the bytes at a path previously returned by <see cref="SaveAsync"/>.</summary>
    /// <remarks>
    /// Used by a retried message claim to avoid re-fetching from Graph and re-writing a file that a
    /// prior attempt on the same message already stored. Deliberately not wrapped in a try/catch that
    /// swallows anything: the caller distinguishes "the file is genuinely gone" (fall back to Graph)
    /// from every other I/O failure (propagate, same as an unreachable share does in
    /// <see cref="SaveAsync"/>), and only the caller has the context to make that call.
    /// </remarks>
    public Task<byte[]> LoadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(options.Value.RootDirectory, relativePath);

        return File.ReadAllBytesAsync(fullPath, cancellationToken);
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
