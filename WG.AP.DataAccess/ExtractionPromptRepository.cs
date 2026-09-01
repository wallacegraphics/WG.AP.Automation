using Dapper;
using Microsoft.Extensions.Logging;

namespace WG.AP.DataAccess;

/// <summary>
/// Loads the active Ollama prompt for each invoice format.
/// </summary>
public sealed class ExtractionPromptRepository(
    SqlConnectionFactory connectionFactory,
    ILogger<ExtractionPromptRepository> logger)
{
    /// <summary>The placeholder the document text is substituted into.</summary>
    /// <remarks>
    /// <c>CK_ExtractionPrompt_Placeholder</c> refuses to store a prompt that does not contain it — a
    /// prompt deployed with no document interpolated into it returns confident nonsense and looks like
    /// a model problem for a day.
    /// </remarks>
    public const string DocumentTextPlaceholder = "{{DocumentText}}";

    /// <summary>
    /// Loads the one active prompt per format, keyed by format id. Loaded once per run.
    /// </summary>
    /// <remarks>
    /// <c>UQ_ExtractionPrompt_OneActive</c> guarantees at most one active row per format, so this
    /// cannot fan out and the caller never has to choose.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, ExtractionPromptRecord>> LoadActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<ExtractionPromptRow>(new CommandDefinition(
                """
                SELECT p.[InvoiceFormatId], p.[ExtractionPromptId], p.[Version],
                       p.[PromptTemplate], p.[ResponseSchemaJson], p.[ModelName]
                  FROM [dbo].[ExtractionPrompt] AS p
                  JOIN [dbo].[InvoiceFormat]    AS f ON f.[InvoiceFormatId] = p.[InvoiceFormatId]
                 WHERE p.[IsActive] = 1
                   AND f.[IsEnabled] = 1;
                """,
                commandTimeout: connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return rows.ToDictionary(
                row => row.InvoiceFormatId,
                row => new ExtractionPromptRecord(
                    row.ExtractionPromptId,
                    row.Version,
                    // Stored with CRLF so it reads correctly in SSMS and in the seed script. Normalised
                    // to LF here so the model sees byte-identical input regardless of how the row was
                    // authored or how git checked the seed file out - which is what makes replaying an
                    // old extraction meaningful.
                    NormalizeNewlines(row.PromptTemplate),
                    row.ResponseSchemaJson,
                    row.ModelName));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load active extraction prompts.");
            throw;
        }
    }

    internal static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");

    private sealed record ExtractionPromptRow(
        int InvoiceFormatId,
        int ExtractionPromptId,
        int Version,
        string PromptTemplate,
        string ResponseSchemaJson,
        string? ModelName);
}
