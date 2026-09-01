using Dapper;
using Microsoft.Extensions.Logging;

namespace WG.AP.DataAccess;

/// <summary>
/// Resolves an incoming email's sender to a client, and to that client's PDF format.
/// </summary>
public sealed class ClientRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<ClientRepository> logger)
{
    /// <summary>
    /// Loads the sender-domain to client/format map, once per run.
    /// </summary>
    /// <remarks>
    /// Loaded whole rather than queried per message: it is a handful of rows, it cannot change
    /// mid-run, and onboarding a client is meant to be three INSERTs with no deploy — so the engine
    /// picking the catalog up on its next run is the mechanism, and a per-message query would only add
    /// round trips.
    /// <para>
    /// A client with more than one enabled format is a configuration error here rather than a
    /// detection problem: with no scored format detection yet, the engine has no way to choose between
    /// two. It is logged and the first is used, which is the point at which the deferred detection
    /// tables become necessary.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, ClientResolution>> LoadByEmailDomainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<(string EmailDomain, int ClientId, int? InvoiceFormatId, string? ExtractorKey)>(
                new CommandDefinition(
                    """
                    SELECT c.[EmailDomain], c.[ClientId], f.[InvoiceFormatId], f.[ExtractorKey]
                      FROM [dbo].[Client] AS c
                      LEFT JOIN [dbo].[InvoiceFormat] AS f
                        ON f.[ClientId] = c.[ClientId] AND f.[IsEnabled] = 1
                     WHERE c.[IsEnabled] = 1
                       AND c.[EmailDomain] IS NOT NULL
                     ORDER BY c.[ClientId], f.[InvoiceFormatId];
                    """,
                    commandTimeout: connectionFactory.CommandTimeoutSeconds,
                    cancellationToken: cancellationToken));

            var map = new Dictionary<string, ClientResolution>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (map.TryGetValue(row.EmailDomain, out var existing))
                {
                    logger.LogWarning(
                        "Client {ClientId} has more than one enabled invoice format; using format {InvoiceFormatId} and ignoring {IgnoredFormatId}. "
                        + "Choosing between formats needs scored detection, which does not exist yet.",
                        row.ClientId, existing.InvoiceFormatId, row.InvoiceFormatId);
                    continue;
                }

                map[row.EmailDomain] = new ClientResolution(row.ClientId, row.InvoiceFormatId, row.ExtractorKey);
            }

            return map;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load the client catalog.");
            throw;
        }
    }

    /// <summary>
    /// Matches a sender address against the catalog, returning <see cref="ClientResolution.Unknown"/>
    /// when nothing matches.
    /// </summary>
    /// <remarks>
    /// Matches on the domain rather than the full address, because invoices arrive from whatever
    /// mailbox the client's billing system happens to use. An unresolved sender is not an error: the
    /// invoice is recorded against ClientId 0 and routed to NeedsReview, so it is visible and
    /// actionable rather than dropped.
    /// </remarks>
    public static ClientResolution Resolve(IReadOnlyDictionary<string, ClientResolution> catalog, string? senderAddress)
    {
        if (string.IsNullOrWhiteSpace(senderAddress))
        {
            return ClientResolution.Unknown;
        }

        var atIndex = senderAddress.LastIndexOf('@');

        if (atIndex < 0 || atIndex == senderAddress.Length - 1)
        {
            return ClientResolution.Unknown;
        }

        var domain = senderAddress[(atIndex + 1)..].Trim();

        return catalog.TryGetValue(domain, out var resolution) ? resolution : ClientResolution.Unknown;
    }
}
