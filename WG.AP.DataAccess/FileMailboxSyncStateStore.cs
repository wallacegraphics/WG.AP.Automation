using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// Persists one mailbox delta link per mailbox as a small JSON file. Writes go through a
/// temp file plus an atomic <see cref="File.Move(string, string, bool)"/> so a crash mid-write
/// can't leave a corrupt/partial state file behind.
/// </summary>
/// <remarks>
/// Superseded by <see cref="SqlMailboxSyncStateStore"/>; kept as the fallback half of
/// <see cref="DualMailboxSyncStateStore"/> for the first week after cutover, then deleted.
/// <para>
/// The file is now named after the mailbox id rather than the address. Naming it after the address
/// was lossy — the old scheme mapped every non-alphanumeric character to '_', so <c>a.b@x.com</c> and
/// <c>a_b@x.com</c> resolved to one file and silently shared a cursor. Reads still look under the old
/// name when nothing exists under the new one, so a cursor written before cutover is not stranded;
/// writes only ever use the new one.
/// </para>
/// </remarks>
public sealed class FileMailboxSyncStateStore(
    IOptions<MailboxSyncStateOptions> options,
    ILogger<FileMailboxSyncStateStore> logger) : IMailboxSyncStateStore
{
    public async Task<string?> GetDeltaLinkAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var mailboxUser = mailbox.MailboxUser;
        var currentPath = GetFilePath(mailbox);
        var path = currentPath;

        try
        {
            if (!File.Exists(path))
            {
                // Reads fall back to the pre-cutover name, which was derived from the address. Without
                // this, the fallback half of DualMailboxSyncStateStore cannot see the very file it
                // exists to fall back on: the first run after cutover would find no row in SQL and no
                // file under the new name, conclude there is no cursor, and resync the whole inbox.
                // Reads only — saves always write the id-named path, so one save supersedes the legacy
                // file and this branch stops being taken.
                var legacyPath = GetLegacyFilePath(mailbox);

                if (!File.Exists(legacyPath))
                {
                    return null;
                }

                logger.LogWarning(
                    "Read the mailbox sync state for {MailboxUser} from the pre-cutover file {LegacyPath}; "
                    + "the next save writes {Path} instead. Expected at most once, on the first run after cutover.",
                    mailboxUser, legacyPath, currentPath);

                path = legacyPath;
            }

            using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<StateFile>(stream, cancellationToken: cancellationToken);
            return state?.DeltaLink;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read mailbox sync state file {Path} for {MailboxUser}.", path, mailboxUser);
            throw;
        }
    }

    public async Task SaveDeltaLinkAsync(MailboxRef mailbox, string deltaLink, CancellationToken cancellationToken)
    {
        var mailboxUser = mailbox.MailboxUser;
        var path = GetFilePath(mailbox);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var state = new StateFile(mailbox.MailboxId, mailboxUser, deltaLink, DateTimeOffset.UtcNow);

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to write mailbox sync state file {Path} for {MailboxUser}.", path, mailboxUser);
            throw;
        }
        finally
        {
            // File.Move already removed the temp file on the success path; this is a best-effort
            // cleanup for the failure path (serialization or the move itself throwing) so retries
            // after a transient failure don't leave orphaned *.tmp files behind indefinitely.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    // The mailbox id is a GUID, so this needs no sanitising and cannot collide - unlike the
    // address, which it replaced for exactly that reason.
    private string GetFilePath(MailboxRef mailbox) =>
        Path.Combine(options.Value.DataDirectory, $"{mailbox.MailboxId:D}.json");

    // The naming this store used before it keyed on the mailbox id. Reproduced verbatim rather than
    // improved, because its only job is to locate files the old code actually wrote. Read-only, and it
    // goes when this class does.
    private string GetLegacyFilePath(MailboxRef mailbox)
    {
        var safeName = new string(mailbox.MailboxUser
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_')
            .ToArray());

        return Path.Combine(options.Value.DataDirectory, $"{safeName}.json");
    }

    private sealed record StateFile(Guid MailboxId, string MailboxUser, string DeltaLink, DateTimeOffset UpdatedAtUtc);
}
