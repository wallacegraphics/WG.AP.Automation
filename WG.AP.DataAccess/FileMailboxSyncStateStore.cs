using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// Persists one mailbox delta link per mailbox user as a small JSON file. Writes go through a
/// temp file plus an atomic <see cref="File.Move(string, string, bool)"/> so a crash mid-write
/// can't leave a corrupt/partial state file behind.
/// </summary>
public sealed class FileMailboxSyncStateStore(
    IOptions<MailboxSyncStateOptions> options,
    ILogger<FileMailboxSyncStateStore> logger) : IMailboxSyncStateStore
{
    public async Task<string?> GetDeltaLinkAsync(string mailboxUser, CancellationToken cancellationToken)
    {
        var path = GetFilePath(mailboxUser);

        try
        {
            if (!File.Exists(path))
            {
                return null;
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

    public async Task SaveDeltaLinkAsync(string mailboxUser, string deltaLink, CancellationToken cancellationToken)
    {
        var path = GetFilePath(mailboxUser);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var state = new StateFile(mailboxUser, deltaLink, DateTimeOffset.UtcNow);

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
    }

    private string GetFilePath(string mailboxUser)
    {
        var safeName = new string(mailboxUser
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());

        return Path.Combine(options.Value.DataDirectory, $"{safeName}.json");
    }

    private sealed record StateFile(string MailboxUser, string DeltaLink, DateTimeOffset UpdatedAtUtc);
}
