using System.Text.Json;
using WG.AP.DataAccess;
using WG.AP.Invoice.AI;

namespace WG.AP.Tests.Invoice;

/// <summary>
/// Pins the seeded prompt to what the extractor and the parser require.
/// </summary>
/// <remarks>
/// The prompt used to be a C# string literal, so the compiler and these tests saw the same thing. Now
/// it is a row in <c>dbo.ExtractionPrompt</c>, deployed from a .sql file that no compiler checks — so
/// the ways it can break are ways that would only show up at 6am against a real invoice. These tests
/// read the actual seed script (see <see cref="SeededPrompt"/>) and check the properties the runtime
/// depends on.
/// <para>
/// Two of these duplicate database CHECK constraints on purpose. The constraint is the real guarantee
/// — it holds against anyone editing the table directly — but it only fires at publish time, whereas
/// these fail in CI, before a broken prompt reaches a database at all.
/// </para>
/// </remarks>
public class SeededPromptTests
{
    [Fact]
    public void SeedScript_IsPresentInTheRepository()
    {
        // Guards the rest of this class: if the seed moved, every other test here would otherwise fail
        // with a confusing parse error rather than saying what actually happened.
        Assert.True(SeededPrompt.SeedScriptExists, $"Expected the prompt seed script at {FixturePaths.ExtractionPromptSeedScript()}.");
    }

    [Fact]
    public void PromptTemplate_ContainsTheDocumentTextPlaceholder()
    {
        // Mirrors CK_ExtractionPrompt_Placeholder. Without the placeholder the model is asked to
        // extract fields from nothing and answers confidently anyway, which reads as a model problem
        // for a day before anyone suspects the prompt.
        Assert.Contains(PdfInvoiceFieldExtractor.ExtractionPromptPlaceholder, SeededPrompt.Template);
    }

    [Fact]
    public void PromptTemplate_PlaceholderMatchesTheRepositoryAndExtractorConstants()
    {
        // Three copies of this string exist - the SQL CHECK, the repository, and the extractor - and
        // they must be the same string or substitution silently does nothing.
        Assert.Equal(ExtractionPromptRepository.DocumentTextPlaceholder, PdfInvoiceFieldExtractor.ExtractionPromptPlaceholder);
    }

    [Fact]
    public void PromptTemplate_IsNewlineNormalised()
    {
        // A stray CR would reach the model as part of the prompt, making the bytes sent depend on how
        // git checked the seed file out.
        Assert.DoesNotContain('\r', SeededPrompt.Template);
    }

    [Fact]
    public void ResponseSchema_IsValidJson()
    {
        // Mirrors CK_ExtractionPrompt_SchemaJson. A malformed schema makes Ollama return prose, and
        // InvoiceFieldsJsonParser then throws on a document that was perfectly readable.
        var exception = Record.Exception(() => JsonDocument.Parse(SeededPrompt.ResponseSchemaJson));
        Assert.Null(exception);
    }

    [Fact]
    public void ResponseSchema_DeclaresEveryFieldTheParserReads()
    {
        using var document = JsonDocument.Parse(SeededPrompt.ResponseSchemaJson);
        var properties = document.RootElement.GetProperty("properties");

        string[] expected =
        [
            "InvoiceNumber", "SalesOrder", "InvoiceDate", "DueDate", "Total",
            "VendorName", "CustomerPO", "CustomerNumber", "OrderAccount", "Terms"
        ];

        foreach (var name in expected)
        {
            Assert.True(properties.TryGetProperty(name, out _), $"The response schema is missing '{name}', which InvoiceFieldsJsonParser reads.");
        }

        Assert.Equal(expected.Length, properties.EnumerateObject().Count());
    }

    [Fact]
    public void ResponseSchema_KeepsVendorNameAsTheModelFacingKey()
    {
        // Deliberate: the C# property is ClientName, but the schema and prompt still say VendorName
        // because that is the wording the prompt was tuned with. Renaming it would change extraction
        // behaviour, and InvoiceFieldsJsonParser maps across instead. This test exists so a
        // well-meaning rename of the schema key fails here rather than silently losing the field.
        using var document = JsonDocument.Parse(SeededPrompt.ResponseSchemaJson);
        var properties = document.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("VendorName", out _));
        Assert.False(properties.TryGetProperty("ClientName", out _));
    }

    [Fact]
    public void ResponseSchema_RequiresTheFieldsWithoutWhichAnInvoiceIsUseless()
    {
        using var document = JsonDocument.Parse(SeededPrompt.ResponseSchemaJson);

        var required = document.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        Assert.Equal(["InvoiceNumber", "Total"], required);
    }

    [Fact]
    public void BuildPrompt_SubstitutesTheDocumentAndNormalisesItsNewlines()
    {
        // PdfPig joins pages with Environment.NewLine, which is CRLF on Windows and LF elsewhere, so
        // without normalisation the exact prompt sent would differ by machine and a replayed
        // extraction would not reproduce.
        var prompt = PdfInvoiceFieldExtractor.BuildPrompt(SeededPrompt.Template, "line one\r\nline two\rline three");

        Assert.DoesNotContain(PdfInvoiceFieldExtractor.ExtractionPromptPlaceholder, prompt);
        Assert.Contains("line one\nline two\nline three", prompt);
        Assert.DoesNotContain('\r', prompt);
    }
}
