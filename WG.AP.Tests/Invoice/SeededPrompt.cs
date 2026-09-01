using System.Text.RegularExpressions;

namespace WG.AP.Tests.Invoice;

/// <summary>
/// The prompt and response schema exactly as <c>Scripts/Seed/ExtractionPrompt.sql</c> deploys them.
/// </summary>
/// <remarks>
/// Parsed out of the seed script rather than duplicated here, and that is the whole point: a copy in
/// the test project would drift from the deployed prompt the first time someone edited one and not the
/// other, and the test would then be passing against a prompt nobody uses. Reading the real file means
/// the tests exercise what production actually gets.
/// </remarks>
internal static class SeededPrompt
{
    private static readonly Lazy<(string Template, string ResponseSchemaJson)> Parsed = new(Load);

    /// <summary>Newline-normalised to LF, matching what <c>ExtractionPromptRepository</c> hands the extractor.</summary>
    internal static string Template => Parsed.Value.Template;

    internal static string ResponseSchemaJson => Parsed.Value.ResponseSchemaJson;

    internal static bool SeedScriptExists => File.Exists(FixturePaths.ExtractionPromptSeedScript());

    private static (string Template, string ResponseSchemaJson) Load()
    {
        var script = File.ReadAllText(FixturePaths.ExtractionPromptSeedScript());

        return (
            ExtractSqlStringLiteral(script, "@PromptTemplate NVARCHAR(MAX) = "),
            ExtractSqlStringLiteral(script, "@ResponseSchemaJson NVARCHAR(MAX) = "));
    }

    /// <summary>
    /// Pulls one <c>N'...'</c> literal out of the seed script and undoes T-SQL's quote doubling.
    /// </summary>
    private static string ExtractSqlStringLiteral(string script, string declarationPrefix)
    {
        var declarationIndex = script.IndexOf(declarationPrefix, StringComparison.Ordinal);

        if (declarationIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find '{declarationPrefix}' in the seed script. If the seed was restructured, update SeededPrompt.");
        }

        var openQuote = script.IndexOf('\'', declarationIndex + declarationPrefix.Length);

        if (openQuote < 0)
        {
            throw new InvalidOperationException($"No opening quote after '{declarationPrefix}'.");
        }

        // Walk to the closing quote, treating '' as an escaped single quote rather than a terminator.
        var index = openQuote + 1;

        while (index < script.Length)
        {
            if (script[index] != '\'')
            {
                index++;
                continue;
            }

            if (index + 1 < script.Length && script[index + 1] == '\'')
            {
                index += 2;
                continue;
            }

            break;
        }

        if (index >= script.Length)
        {
            throw new InvalidOperationException($"Unterminated string literal after '{declarationPrefix}'.");
        }

        var raw = script[(openQuote + 1)..index].Replace("''", "'");

        // The seed normalises to CRLF on insert so the stored value reads correctly in SSMS; the
        // repository normalises back to LF before use. Do the same here so a comparison is meaningful
        // regardless of how git checked the .sql file out.
        return Regex.Replace(raw, "\r\n|\r", "\n");
    }
}
