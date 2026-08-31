using System.Globalization;
using System.Text.Json;
using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.AI;

/// <summary>Parses an Ollama JSON response (per the schema in <see cref="PdfInvoiceFieldExtractor"/>) into <see cref="InvoiceFields"/>.</summary>
public static class InvoiceFieldsJsonParser
{
    public static InvoiceFields Parse(string rawResponse)
    {
        using var document = JsonDocument.Parse(rawResponse);
        var root = document.RootElement;

        var invoiceNumber = GetOptionalString(root, "InvoiceNumber")
            ?? throw new InvalidOperationException("Ollama response did not include an InvoiceNumber.");

        return new InvoiceFields(
            invoiceNumber,
            GetOptionalString(root, "SalesOrder"),
            ParseDate(GetOptionalString(root, "InvoiceDate")),
            ParseDate(GetOptionalString(root, "DueDate")),
            GetAmount(root),
            GetOptionalString(root, "VendorName"),
            GetOptionalString(root, "CustomerPO"),
            GetOptionalString(root, "CustomerNumber"),
            GetOptionalString(root, "OrderAccount"),
            GetOptionalString(root, "Terms"));
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var stringValue = value.GetString();
        return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue.Trim();
    }

    private static decimal GetAmount(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "Total", out var value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };
    }

    // The model's structured-output schema declares PascalCase property names, but the model
    // doesn't always echo that casing back exactly (observed both "InvoiceNumber" and
    // "invoiceNumber" from the same model/prompt) - JsonElement.TryGetProperty is case-sensitive,
    // so a plain lookup would intermittently and silently miss a field that's actually present.
    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static DateOnly? ParseDate(string? rawValue) =>
        rawValue is not null && DateOnly.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
