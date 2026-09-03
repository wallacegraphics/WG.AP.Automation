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

    // Matches @PromptTemplate, @PromptTemplateV2, @PromptTemplateV3, etc. The seed script's own
    // documented convention (see its header comment) is to APPEND a new "@PromptTemplateVN ... = N'...'"
    // block per version and deactivate the previous one in the same run - never to edit a version's
    // literal in place. So the LAST such declaration in the file is always the current one, by
    // construction of that convention, regardless of how many versions have accumulated.
    private static readonly Regex PromptTemplateDeclaration =
        new(@"@PromptTemplate\w*\s+NVARCHAR\(MAX\)\s*=\s*", RegexOptions.Compiled);

    private static (string Template, string ResponseSchemaJson) Load()
    {
        var script = File.ReadAllText(FixturePaths.ExtractionPromptSeedScript());

        var templateMatches = PromptTemplateDeclaration.Matches(script);

        if (templateMatches.Count == 0)
        {
            throw new InvalidOperationException(
                "Could not find any '@PromptTemplate... NVARCHAR(MAX) = ' declaration in the seed script. "
                + "If the seed was restructured, update SeededPrompt.");
        }

        var lastTemplateMatch = templateMatches[^1];

        return (
            ExtractSqlStringLiteral(script, lastTemplateMatch.Index + lastTemplateMatch.Length),
            ExtractSqlStringLiteral(script, "@ResponseSchemaJson NVARCHAR(MAX) = "));
    }

    private static string ExtractSqlStringLiteral(string script, string declarationPrefix)
    {
        var declarationIndex = script.IndexOf(declarationPrefix, StringComparison.Ordinal);

        if (declarationIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find '{declarationPrefix}' in the seed script. If the seed was restructured, update SeededPrompt.");
        }

        return ExtractSqlStringLiteral(script, declarationIndex + declarationPrefix.Length);
    }

    /// <summary>
    /// Pulls one <c>N'...'</c> literal out of the seed script (starting the search at
    /// <paramref name="searchFrom"/>) and undoes T-SQL's quote doubling.
    /// </summary>
    private static string ExtractSqlStringLiteral(string script, int searchFrom)
    {
        var openQuote = script.IndexOf('\'', searchFrom);

        if (openQuote < 0)
        {
            throw new InvalidOperationException($"No opening quote found after position {searchFrom}.");
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
            throw new InvalidOperationException($"Unterminated string literal starting at position {openQuote}.");
        }

        var raw = script[(openQuote + 1)..index].Replace("''", "'");

        // The seed normalises to CRLF on insert so the stored value reads correctly in SSMS; the
        // repository normalises back to LF before use. Do the same here so a comparison is meaningful
        // regardless of how git checked the .sql file out.
        return Regex.Replace(raw, "\r\n|\r", "\n");
    }
}
