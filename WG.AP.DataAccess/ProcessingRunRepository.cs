using Dapper;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;

namespace WG.AP.DataAccess;

/// <summary>
/// Opens and closes the <c>dbo.ProcessingRun</c> row that everything else in a run correlates to.
/// </summary>
public sealed class ProcessingRunRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<ProcessingRunRepository> logger)
{
    public async Task<long> StartAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            return await connection.QuerySingleAsync<long>(new CommandDefinition(
                """
                INSERT INTO [dbo].[ProcessingRun] ([MailboxId]) VALUES (@MailboxId);
                SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
                """,
                new { mailbox.MailboxId },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start a processing run for {MailboxUser}.", mailbox.MailboxUser);
            throw;
        }
    }

    /// <summary>
    /// Closes the run with its counts and outcome.
    /// </summary>
    /// <remarks>
    /// Leaving <c>IsSuccessful</c> null is what makes a crashed run detectable: a row with a
    /// StartedOn in the past and no outcome means the process died before it got here, which
    /// <c>Environment.ExitCode = 1</c> alone never revealed for an unattended scheduled task.
    /// <para>
    /// This is called from a finally block, so it must not throw over a failure to record a failure.
    /// Losing the run's epitaph is bad; turning it into a second, masking exception is worse.
    /// </para>
    /// </remarks>
    public async Task FinishAsync(
        long processingRunId,
        int messageCount,
        int invoiceCount,
        bool isSuccessful,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE [dbo].[ProcessingRun]
                   SET [FinishedOn]   = SYSUTCDATETIME(),
                       [MessageCount] = @MessageCount,
                       [InvoiceCount] = @InvoiceCount,
                       [IsSuccessful] = @IsSuccessful,
                       [ErrorMessage] = @ErrorMessage,
                       [ModifiedBy]   = SUSER_SNAME(),
                       [ModifiedOn]   = SYSUTCDATETIME()
                 WHERE [ProcessingRunId] = @ProcessingRunId;
                """,
                new
                {
                    ProcessingRunId = processingRunId,
                    MessageCount = messageCount,
                    InvoiceCount = invoiceCount,
                    IsSuccessful = isSuccessful,
                    ErrorMessage = MailMessageRepository.Truncate(errorMessage, 1000)
                },
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to close processing run {ProcessingRunId}.", processingRunId);
        }
    }
}
