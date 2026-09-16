using System.Text.Json;
using System.Text.Json.Serialization;

namespace WG.AP.Integrations.Pace;

public sealed record PaceInvoiceFields
{
    public required string InvoiceNumber { get; init; }

    public string? SalesOrder { get; init; }

    public required DateOnly InvoiceDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public required decimal Total { get; init; }

    public string? ClientName { get; init; }

    public required string CustomerPO { get; init; }

    public string? CustomerNumber { get; init; }

    public string? OrderAccount { get; init; }

    public string? Terms { get; init; }
}

public static class PaceInvoiceFieldsParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static PaceInvoiceFields Parse(string fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
        {
            throw new InvalidOperationException("Invoice FieldsJson is empty.");
        }

        var fields = JsonSerializer.Deserialize<PaceInvoiceFields>(fieldsJson, JsonOptions)
            ?? throw new InvalidOperationException("Invoice FieldsJson did not deserialize to invoice fields.");

        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(fields.InvoiceNumber))
        {
            missing.Add(nameof(PaceInvoiceFields.InvoiceNumber));
        }

        if (string.IsNullOrWhiteSpace(fields.CustomerPO))
        {
            missing.Add(nameof(PaceInvoiceFields.CustomerPO));
        }

        if (fields.InvoiceDate == default)
        {
            missing.Add(nameof(PaceInvoiceFields.InvoiceDate));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Invoice FieldsJson is missing Pace-required field(s): {string.Join(", ", missing)}.");
        }

        return fields;
    }
}
