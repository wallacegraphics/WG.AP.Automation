using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WG.AP.Integrations.Pace;

/// <summary>
/// Which of the PO/receipt rules decided an invoice's Pace outcome. The numbers are the case numbers the
/// AP team uses for these rules, so the summary email and the log can name them the same way.
/// </summary>
public enum PaceInvoiceCase
{
    /// <summary>Decided before the PO/receipt rules applied (e.g. an invoice number that cannot be normalized).</summary>
    Other = 0,

    /// <summary>The PO is not in Pace: a bill coded to the client vendor's default GL account/department.</summary>
    PoNotFound = 1,

    /// <summary>The PO is in Pace but has no approved (status R) receipt: a bill coded to the PO vendor's defaults.</summary>
    NoApprovedReceipts = 2,

    /// <summary>Every approved receipt is already billed: the existing bill is compared with the invoice.</summary>
    AllReceiptsBilled = 3,

    /// <summary>Some approved receipts are billed: a bill for the unbilled ones only.</summary>
    PartiallyBilled = 4,

    /// <summary>No approved receipt is billed yet: a bill for all of them.</summary>
    NotBilled = 5
}

/// <summary>
/// What a Pace outcome did and why, in structured form, so the run summary email can name the vendor, the
/// totals, the bill/batch and the receipts involved instead of repeating a generic reason.
/// </summary>
/// <remarks>
/// Persisted inside <c>intgr.PaceSubmission.ResponseJson</c> (under <see cref="JsonPropertyName"/>) rather than
/// in a column of its own, so a summary line rebuilt by the mail-routing recovery sweep reads the same as the
/// one the live run would have sent. Its presence also marks a row as completed by code that routes the email
/// itself (see <c>PaceSubmissionRepository.ClaimUnroutedFinalSubmissionsAsync</c>).
/// </remarks>
public sealed record PaceBillSummary
{
    public const string JsonPropertyName = "summary";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PaceInvoiceCase Case { get; init; }

    /// <summary>The Pace vendor the bill was (or would be) created under, or was looked up for.</summary>
    public string? VendorId { get; init; }

    public string? VendorName { get; init; }

    public decimal InvoiceTotal { get; init; }

    /// <summary>The total of the bill lines created, or of the existing bill(s) compared (case 3); null when there was no bill to total.</summary>
    public decimal? BillTotal { get; init; }

    /// <summary>The description of the bill batch the bill went into, e.g. "AUTO 9-25-26".</summary>
    public string? BillBatchDescription { get; init; }

    /// <summary>Pace writes were disabled: every bill in this summary is one that would have been created.</summary>
    public bool DryRun { get; init; }

    /// <summary>Every receipt found on the PO, approved or not.</summary>
    public int ReceiptCount { get; init; }

    /// <summary>Approved receipts found on the PO.</summary>
    public int ApprovedReceiptCount { get; init; }

    /// <summary>Receipts a bill line was (or would be) created for.</summary>
    public IReadOnlyList<int> ReceiptIdsAdded { get; init; } = [];

    /// <summary>Approved receipts skipped because they were already fully billed.</summary>
    public IReadOnlyList<int> ReceiptIdsAlreadyBilled { get; init; } = [];

    /// <summary>The bills the already-billed receipts are on, from their existing bill lines.</summary>
    public IReadOnlyList<string> AlreadyBilledOnBillIds { get; init; } = [];

    /// <summary>Existing bills found for this vendor and invoice number.</summary>
    public IReadOnlyList<int> ExistingBillIds { get; init; } = [];

    public IReadOnlyList<string> ExistingBillBatchIds { get; init; } = [];

    /// <summary>The default coding used for a case 1 or case 2 bill.</summary>
    public int? GlAccount { get; init; }

    public int? GlDepartment { get; init; }

    /// <summary>The invoice total less the bill total, when both are known.</summary>
    [JsonIgnore]
    public decimal? Difference => BillTotal is { } billTotal ? InvoiceTotal - billTotal : null;

    /// <summary>
    /// True when the bill total equals the invoice total to the cent. Pace stores BillLine.invoiceAmount as a
    /// double, so an exact decimal comparison would report a match as a mismatch.
    /// </summary>
    [JsonIgnore]
    public bool TotalsMatch => BillTotal is { } billTotal && RoundToCents(billTotal) == RoundToCents(InvoiceTotal);

    internal static decimal RoundToCents(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Returns <paramref name="responseJson"/> with this summary added under <see cref="JsonPropertyName"/>.
    /// A response that is not a JSON object is kept whole under "response", so nothing Pace returned is lost.
    /// </summary>
    public string AttachTo(string? responseJson)
    {
        var root = ParseObject(responseJson);
        root[JsonPropertyName] = JsonSerializer.SerializeToNode(this, JsonOptions);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads a summary written by <see cref="AttachTo"/>; null when there is none or it cannot be read.</summary>
    public static PaceBillSummary? TryReadFrom(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(responseJson) is JsonObject root && root[JsonPropertyName] is JsonObject summary
                ? summary.Deserialize<PaceBillSummary>(JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject ParseObject(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return [];
        }

        try
        {
            var node = JsonNode.Parse(responseJson);
            return node as JsonObject ?? new JsonObject { ["response"] = node };
        }
        catch (JsonException)
        {
            return new JsonObject { ["response"] = responseJson };
        }
    }
}
