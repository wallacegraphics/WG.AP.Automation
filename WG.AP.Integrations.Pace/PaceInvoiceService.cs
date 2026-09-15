using System.Text.Json;
using Microsoft.Extensions.Options;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Integrations.Pace;

public sealed class PaceInvoiceService(IPaceClient paceClient, IOptions<PaceOptions> options) : IPaceInvoiceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            var purchaseOrderLines = await LoadPurchaseOrderLinesAsync(submission.Fields.CustomerPO, cancellationToken);

            if (purchaseOrderLines.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.NoPo,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO }),
                    ErrorMessage = $"Pace purchase order '{submission.Fields.CustomerPO}' was not found."
                };
            }

            var receivedLines = purchaseOrderLines.Where(line => line.QtyReceived > 0).ToList();

            if (receivedLines.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.PoNotReceived,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO, PurchaseOrderLines = purchaseOrderLines })
                };
            }

            var receipts = await LoadReceiptsAsync(receivedLines.Select(line => line.Id), cancellationToken);

            if (receipts.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.PoNotReceived,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO, ReceivedLines = receivedLines })
                };
            }

            var billLines = await LoadBillLinesAsync(receipts.Select(receipt => receipt.Id), cancellationToken);
            var consumedReceiptIds = billLines.Select(line => line.PurchaseOrderReceipt).ToHashSet();
            var lineById = receivedLines.ToDictionary(line => line.Id);
            var billableReceipts = receipts
                .Where(receipt => !consumedReceiptIds.Contains(receipt.Id))
                .Select(receipt =>
                {
                    var line = lineById[receipt.PurchaseOrderLine];
                    return new BillableReceipt(
                        receipt.Id,
                        receipt.ExtendedPrice ?? receipt.Quantity * receipt.UnitCost,
                        receipt.Quantity,
                        receipt.UnitCost,
                        receipt.StockingUom,
                        line.GlAccount,
                        line.GlDepartment,
                        line.Job,
                        line.JobPart,
                        line.ActivityCode);
                })
                .ToList();

            if (billableReceipts.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.AlreadyEntered,
                    ResponseJson = SerializeResponse(new
                    {
                        submission.Fields.CustomerPO,
                        BillLines = billLines
                    }),
                    PaceBillId = string.Join(",", billLines.Select(line => line.Bill).Where(bill => !string.IsNullOrWhiteSpace(bill)).Distinct(StringComparer.OrdinalIgnoreCase))
                };
            }

            var prepared = new
            {
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                BillableReceipts = billableReceipts
            };

            if (!options.Value.WriteEnabled)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.DryRunPrepared,
                    ResponseJson = SerializeResponse(prepared)
                };
            }

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ErrorMessage = "Pace writes are enabled in configuration, but bill creation mapping is not implemented until staging behavior is confirmed."
            };
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.NoPo,
                ResponseJson = SerializePaceException(exception),
                ErrorMessage = $"Pace purchase order '{submission.Fields.CustomerPO}' was not found."
            };
        }
        catch (ApiException exception)
        {
            return new PaceInvoiceSubmissionResult
            {
                StatusCode = IsTransientStatus(exception.StatusCode) ? PaceInvoiceOutcomeStatus.RetryLater : PaceInvoiceOutcomeStatus.Error,
                IsTransient = IsTransientStatus(exception.StatusCode),
                ResponseJson = SerializePaceException(exception),
                ErrorMessage = exception.Message
            };
        }
        catch (HttpRequestException exception)
        {
            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.RetryLater,
                IsTransient = true,
                ErrorMessage = exception.Message
            };
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.RetryLater,
                IsTransient = true,
                ErrorMessage = exception.Message
            };
        }
    }

    private async Task<List<PurchaseOrderLineValue>> LoadPurchaseOrderLinesAsync(string poNumber, CancellationToken cancellationToken)
    {
        var result = await paceClient.LoadValueObjectsAsync(new ValueObjectDescriptor
        {
            ObjectName = "PurchaseOrderLine",
            XpathFilter = $"@purchaseOrder/@poNumber = '{EscapeXPathLiteral(poNumber)}'",
            Fields =
            [
                Field("id", "@id"),
                Field("qtyReceived", "@qtyReceived"),
                Field("glAccount", "@glAccount"),
                Field("glDepartment", "@glDepartment"),
                Field("job", "@job"),
                Field("jobPart", "@jobPart"),
                Field("activityCode", "@activityCode")
            ],
            Limit = 500
        }, cancellationToken: cancellationToken);

        return ReadRows(result)
            .Select(fields => new PurchaseOrderLineValue(
                GetRequiredInt(fields, "id"),
                GetDecimal(fields, "qtyReceived") ?? 0,
                GetNullableInt(fields, "glAccount"),
                GetNullableInt(fields, "glDepartment"),
                GetString(fields, "job"),
                GetString(fields, "jobPart"),
                GetString(fields, "activityCode")))
            .ToList();
    }

    private async Task<List<PurchaseOrderReceiptValue>> LoadReceiptsAsync(IEnumerable<int> purchaseOrderLineIds, CancellationToken cancellationToken)
    {
        var result = await paceClient.LoadValueObjectsAsync(new ValueObjectDescriptor
        {
            ObjectName = "PurchaseOrderReceipt",
            XpathFilter = OrFilter("@purchaseOrderLine", purchaseOrderLineIds),
            Fields =
            [
                Field("id", "@id"),
                Field("purchaseOrderLine", "@purchaseOrderLine"),
                Field("quantity", "@quantity"),
                Field("unitCost", "@unitCost"),
                Field("extendedPrice", "@extendedPrice"),
                Field("stockingUOM", "@stockingUOM")
            ],
            Limit = 500
        }, cancellationToken: cancellationToken);

        return ReadRows(result)
            .Select(fields => new PurchaseOrderReceiptValue(
                GetRequiredInt(fields, "id"),
                GetRequiredInt(fields, "purchaseOrderLine"),
                GetDecimal(fields, "quantity") ?? 0,
                GetDecimal(fields, "unitCost") ?? 0,
                GetDecimal(fields, "extendedPrice"),
                GetString(fields, "stockingUOM")))
            .ToList();
    }

    private async Task<List<BillLineValue>> LoadBillLinesAsync(IEnumerable<int> receiptIds, CancellationToken cancellationToken)
    {
        var result = await paceClient.LoadValueObjectsAsync(new ValueObjectDescriptor
        {
            ObjectName = "BillLine",
            XpathFilter = OrFilter("@purchaseOrderReceipt", receiptIds),
            Fields =
            [
                Field("id", "@id"),
                Field("purchaseOrderReceipt", "@purchaseOrderReceipt"),
                Field("bill", "@bill")
            ],
            Limit = 500
        }, cancellationToken: cancellationToken);

        return ReadRows(result)
            .Select(fields => new BillLineValue(
                GetRequiredInt(fields, "id"),
                GetRequiredInt(fields, "purchaseOrderReceipt"),
                GetString(fields, "bill")))
            .ToList();
    }

    private static FieldDescriptor Field(string name, string xpath) => new() { Name = name, Xpath = xpath };

    private static string OrFilter(string field, IEnumerable<int> values) =>
        string.Join(" or ", values.Select(value => $"{field} = {value}"));

    private static string EscapeXPathLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(ValueObjectsGroup group) =>
        group.ValueObjects?.Select(valueObject =>
            (IReadOnlyDictionary<string, object?>)(valueObject.Fields ?? [])
                .Where(field => !string.IsNullOrWhiteSpace(field.Name))
                .ToDictionary(field => field.Name!, field => field.Value, StringComparer.OrdinalIgnoreCase)) ?? [];

    private static string? GetString(IReadOnlyDictionary<string, object?> fields, string name) =>
        fields.TryGetValue(name, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static int GetRequiredInt(IReadOnlyDictionary<string, object?> fields, string name) =>
        GetNullableInt(fields, name) ?? throw new InvalidOperationException($"Pace response did not include required '{name}' value.");

    private static int? GetNullableInt(IReadOnlyDictionary<string, object?> fields, string name)
    {
        var value = GetString(fields, name);
        return string.IsNullOrWhiteSpace(value) ? null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static decimal? GetDecimal(IReadOnlyDictionary<string, object?> fields, string name)
    {
        var value = GetString(fields, name);
        return string.IsNullOrWhiteSpace(value) ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SerializeResponse(object response) =>
        JsonSerializer.Serialize(response, JsonOptions);

    private static string SerializePaceException(ApiException exception) =>
        SerializeResponse(new
        {
            exception.StatusCode,
            RawResponse = exception.Response
        });

    private static bool IsTransientStatus(int statusCode) =>
        statusCode is 408 or 429 or >= 500;

    private sealed record PurchaseOrderLineValue(int Id, decimal QtyReceived, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);

    private sealed record PurchaseOrderReceiptValue(int Id, int PurchaseOrderLine, decimal Quantity, decimal UnitCost, decimal? ExtendedPrice, string? StockingUom);

    private sealed record BillLineValue(int Id, int PurchaseOrderReceipt, string? Bill);

    private sealed record BillableReceipt(int PurchaseOrderReceipt, decimal InvoiceAmount, decimal PoQuantity, decimal PoUnitPrice, string? PoUom, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);
}
