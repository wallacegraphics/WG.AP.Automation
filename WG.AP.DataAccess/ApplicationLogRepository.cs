using Dapper;

namespace WG.AP.DataAccess;

/// <summary>
/// Appends rows to <c>dbo.ApplicationLog</c>.
/// </summary>
/// <remarks>
/// Deliberately does not take an <c>ILogger</c> and deliberately does not log its own failures — it
/// <em>is</em> a log sink, so reporting a failure through <c>ILogger</c> would recurse. A failing
/// database sink surfaces in the file log instead, which is the sink that still works when the
/// database is the broken thing.
/// <para>
/// Every write gets a fresh connection from the factory, so it is never enlisted in an ambient
/// transaction. A log line written inside a transaction that later rolls back disappears — and it
/// disappears precisely when something went wrong enough to roll back, the moment the line was most
/// worth having.
/// </para>
/// </remarks>
public sealed class ApplicationLogRepository(SqlConnectionFactory connectionFactory)
{
    public async Task WriteAsync(IReadOnlyList<ApplicationLogEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var appIdentity = connectionFactory.AppIdentity;
        var parameters = entries.Select(entry => new
        {
            entry.LoggedOn,
            entry.LogLevel,
            entry.Category,
            entry.Message,
            entry.Exception,
            entry.ProcessingRunId,
            entry.MailMessageId,
            CreatedBy = appIdentity
        });

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO [dbo].[ApplicationLog]
                ([LoggedOn], [LogLevel], [Category], [Message], [Exception], [ProcessingRunId], [MailMessageId], [CreatedBy])
            VALUES
                (@LoggedOn, @LogLevel, @Category, @Message, @Exception, @ProcessingRunId, @MailMessageId, @CreatedBy);
            """,
            parameters,
            commandTimeout: connectionFactory.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }
}

/// <summary>One row bound for <c>dbo.ApplicationLog</c>.</summary>
public sealed record ApplicationLogEntry
{
    public required DateTime LoggedOn { get; init; }
    public required byte LogLevel { get; init; }
    public string? Category { get; init; }
    public required string Message { get; init; }
    public string? Exception { get; init; }
    public long? ProcessingRunId { get; init; }
    public long? MailMessageId { get; init; }
}
