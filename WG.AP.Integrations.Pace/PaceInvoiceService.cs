using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Integrations.Pace;

public sealed class PaceInvoiceService(
    IPaceClient paceClient,
    IOptions<PaceOptions> options,
    ILogger<PaceInvoiceService> logger) : IPaceInvoiceService
{
    private const int PacePageSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            var purchaseOrderLines = await LoadPurchaseOrderLinesOrNoPoAsync(submission, cancellationToken);

            if (purchaseOrderLines.Result is not null)
            {
                return purchaseOrderLines.Result;
            }

            if (purchaseOrderLines.PurchaseOrderLines is null)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.NoPo,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO }),
                    ErrorMessage = $"Pace purchase order '{submission.Fields.CustomerPO}' was not found."
                };
            }

            if (purchaseOrderLines.PurchaseOrderLines.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.NoPo,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO }),
                    ErrorMessage = $"Pace purchase order '{submission.Fields.CustomerPO}' was not found."
                };
            }

            var receivedLines = purchaseOrderLines.PurchaseOrderLines.Where(line => line.QtyReceived > 0).ToList();

            if (receivedLines.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.PoNotReceived,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO, PurchaseOrderLines = purchaseOrderLines })
                };
            }

            var billableLines = receivedLines.Where(line => !line.InvoiceComplete).ToList();

            if (billableLines.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.AlreadyEntered,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO, PurchaseOrderLines = receivedLines })
                };
            }

            var receipts = await LoadReceiptsAsync(billableLines.Select(line => line.Id), cancellationToken);

            if (receipts.Count == 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.PoNotReceived,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO, ReceivedLines = receivedLines })
                };
            }

            var billLines = await LoadBillLinesAsync(receipts.Select(receipt => receipt.Id), cancellationToken);
            var lineById = billableLines.ToDictionary(line => line.Id);
            var billableReceipts = receipts
                .Select(receipt => new
                {
                    Receipt = receipt,
                    RemainingQuantity = receipt.Quantity - receipt.BilledQuantity
                })
                .Where(receipt => receipt.RemainingQuantity > 0)
                .Select(receipt =>
                {
                    var line = lineById[receipt.Receipt.PurchaseOrderLine];
                    var invoiceAmount = receipt.Receipt.ExtendedPrice is not null && receipt.Receipt.BilledAmount is not null
                        ? receipt.Receipt.ExtendedPrice.Value - receipt.Receipt.BilledAmount.Value
                        : receipt.RemainingQuantity * receipt.Receipt.UnitCost;

                    return new BillableReceipt(
                        receipt.Receipt.Id,
                        invoiceAmount,
                        receipt.RemainingQuantity,
                        receipt.Receipt.UnitCost,
                        receipt.Receipt.StockingUom,
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

            if (string.IsNullOrWhiteSpace(submission.PaceVendorId))
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ErrorMessage = $"Cannot prepare Pace bill for invoice '{submission.Fields.InvoiceNumber}' because the client has no Pace vendor id."
                };
            }

            var existingBills = await LoadBillsByVendorAndInvoiceAsync(submission.PaceVendorId, submission.Fields.InvoiceNumber, cancellationToken);

            if (existingBills.Count > 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.AlreadyEntered,
                    ResponseJson = SerializeResponse(new
                    {
                        submission.Fields.InvoiceNumber,
                        submission.Fields.CustomerPO,
                        submission.PaceVendorId,
                        Bills = existingBills
                    }),
                    PaceBillBatchId = string.Join(",", existingBills.Select(bill => bill.BillBatch).Where(billBatch => !string.IsNullOrWhiteSpace(billBatch)).Distinct(StringComparer.OrdinalIgnoreCase)),
                    PaceBillId = string.Join(",", existingBills.Select(bill => bill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Distinct(StringComparer.OrdinalIgnoreCase))
                };
            }

            var prepared = new
            {
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                submission.PaceVendorId,
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
        catch (ApiException exception)
        {
            logger.LogError(exception, "Pace request failed with status {StatusCode} for invoice {InvoiceId}, invoice number {InvoiceNumber}, PO {CustomerPO}.", exception.StatusCode, submission.InvoiceId, submission.Fields.InvoiceNumber, submission.Fields.CustomerPO);

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
            logger.LogError(exception, "Pace HTTP request failed for invoice {InvoiceId}, invoice number {InvoiceNumber}, PO {CustomerPO}.", submission.InvoiceId, submission.Fields.InvoiceNumber, submission.Fields.CustomerPO);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.RetryLater,
                IsTransient = true,
                ErrorMessage = exception.Message
            };
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Pace request timed out for invoice {InvoiceId}, invoice number {InvoiceNumber}, PO {CustomerPO}.", submission.InvoiceId, submission.Fields.InvoiceNumber, submission.Fields.CustomerPO);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.RetryLater,
                IsTransient = true,
                ErrorMessage = exception.Message
            };
        }
    }

    private async Task<PurchaseOrderLineLookup> LoadPurchaseOrderLinesOrNoPoAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            return new PurchaseOrderLineLookup(await LoadPurchaseOrderLinesAsync(submission.Fields.CustomerPO, cancellationToken), Result: null);
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            logger.LogError(exception, "Pace purchase order lookup failed with 404 for invoice {InvoiceId}, invoice number {InvoiceNumber}, PO {CustomerPO}.", submission.InvoiceId, submission.Fields.InvoiceNumber, submission.Fields.CustomerPO);
            return new PurchaseOrderLineLookup(PurchaseOrderLines: null, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.NoPo,
                ResponseJson = SerializePaceException(exception),
                ErrorMessage = $"Pace purchase order '{submission.Fields.CustomerPO}' was not found."
            });
        }
    }

    private async Task<List<PurchaseOrderLineValue>> LoadPurchaseOrderLinesAsync(string poNumber, CancellationToken cancellationToken)
    {
        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
        {
            ObjectName = "PurchaseOrderLine",
            XpathFilter = $"@purchaseOrder/@poNumber = {XPathStringLiteral(poNumber)}",
            Fields =
            [
                Field("id", "@id"),
                Field("qtyReceived", "@qtyReceived"),
                Field("invoiceComplete", "@invoiceComplete"),
                Field("glAccount", "@glAccount"),
                Field("glDepartment", "@glDepartment"),
                Field("job", "@job"),
                Field("jobPart", "@jobPart"),
                Field("activityCode", "@activityCode")
            ]
        }, cancellationToken);

        return rows
            .Select(fields => new PurchaseOrderLineValue(
                GetRequiredInt(fields, "id"),
                GetDecimal(fields, "qtyReceived") ?? 0,
                GetBool(fields, "invoiceComplete") ?? false,
                GetNullableInt(fields, "glAccount"),
                GetNullableInt(fields, "glDepartment"),
                GetString(fields, "job"),
                GetString(fields, "jobPart"),
                GetString(fields, "activityCode")))
            .ToList();
    }

    private async Task<List<BillValue>> LoadBillsByVendorAndInvoiceAsync(string paceVendorId, string invoiceNumber, CancellationToken cancellationToken)
    {
        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
        {
            ObjectName = "Bill",
            XpathFilter = $"@vendor = {XPathStringLiteral(paceVendorId)} and @invoiceNumber = {XPathStringLiteral(invoiceNumber)}",
            Fields =
            [
                Field("id", "@id"),
                Field("vendor", "@vendor"),
                Field("invoiceNumber", "@invoiceNumber"),
                Field("poNumber", "@poNumber"),
                Field("billBatch", "@billBatch"),
                Field("postingStatus", "@postingStatus")
            ]
        }, cancellationToken);

        return rows
            .Select(fields => new BillValue(
                GetRequiredInt(fields, "id"),
                GetString(fields, "vendor"),
                GetString(fields, "invoiceNumber"),
                GetString(fields, "poNumber"),
                GetString(fields, "billBatch"),
                GetString(fields, "postingStatus")))
            .ToList();
    }

    private async Task<List<PurchaseOrderReceiptValue>> LoadReceiptsAsync(IEnumerable<int> purchaseOrderLineIds, CancellationToken cancellationToken)
    {
        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
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
                Field("billedQuantity", "@billedQuantity"),
                Field("billedAmount", "@billedAmount"),
                Field("stockingUOM", "@stockingUOM")
            ]
        }, cancellationToken);

        return rows
            .Select(fields => new PurchaseOrderReceiptValue(
                GetRequiredInt(fields, "id"),
                GetRequiredInt(fields, "purchaseOrderLine"),
                GetDecimal(fields, "quantity") ?? 0,
                GetDecimal(fields, "unitCost") ?? 0,
                GetDecimal(fields, "extendedPrice"),
                GetDecimal(fields, "billedQuantity") ?? 0,
                GetDecimal(fields, "billedAmount"),
                GetString(fields, "stockingUOM")))
            .ToList();
    }

    private async Task<List<BillLineValue>> LoadBillLinesAsync(IEnumerable<int> receiptIds, CancellationToken cancellationToken)
    {
        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
        {
            ObjectName = "BillLine",
            XpathFilter = OrFilter("@purchaseOrderReceipt", receiptIds),
            Fields =
            [
                Field("id", "@id"),
                Field("purchaseOrderReceipt", "@purchaseOrderReceipt"),
                Field("bill", "@bill")
            ]
        }, cancellationToken);

        return rows
            .Select(fields => new BillLineValue(
                GetRequiredInt(fields, "id"),
                GetRequiredInt(fields, "purchaseOrderReceipt"),
                GetString(fields, "bill")))
            .ToList();
    }

    private async Task<List<IReadOnlyDictionary<string, object?>>> LoadAllValueObjectRowsAsync(ValueObjectDescriptor descriptor, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;

        while (true)
        {
            descriptor.Limit = PacePageSize;
            descriptor.Offset = offset;
            descriptor.XpathSorts = [new XPathDataSort { Xpath = "@id", Descending = false }];

            var page = await paceClient.LoadValueObjectsAsync(descriptor, cancellationToken: cancellationToken);
            var pageRows = ReadRows(page).ToList();

            if (pageRows.Count == 0)
            {
                if (page.TotalRecords is not null && rows.Count < page.TotalRecords.Value)
                {
                    throw new InvalidOperationException($"Pace returned no {descriptor.ObjectName} rows at offset {offset}, but reported {page.TotalRecords.Value} total record(s).");
                }

                return rows;
            }

            foreach (var row in pageRows)
            {
                var id = GetString(row, "id");

                if (!string.IsNullOrWhiteSpace(id) && !seenIds.Add(id))
                {
                    throw new InvalidOperationException($"Pace returned duplicate {descriptor.ObjectName} id '{id}' while paging value objects.");
                }

                rows.Add(row);
            }

            if (page.TotalRecords is not null && rows.Count >= page.TotalRecords.Value)
            {
                return rows;
            }

            if (pageRows.Count < PacePageSize)
            {
                if (page.TotalRecords is not null)
                {
                    throw new InvalidOperationException($"Pace returned only {pageRows.Count} {descriptor.ObjectName} rows at offset {offset}, but reported {page.TotalRecords.Value} total record(s).");
                }

                return rows;
            }

            offset += PacePageSize;
        }
    }

    private static FieldDescriptor Field(string name, string xpath) => new() { Name = name, Xpath = xpath };

    private static string OrFilter(string field, IEnumerable<int> values) =>
        string.Join(" or ", values.Select(value => $"{field} = {value}"));

    private static string XPathStringLiteral(string value)
    {
        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{value}'";
        }

        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value}\"";
        }

        return $"concat({string.Join(", \"'\", ", value.Split('\'').Select(part => $"'{part}'"))})";
    }

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

    private static bool? GetBool(IReadOnlyDictionary<string, object?> fields, string name)
    {
        var value = GetString(fields, name);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => bool.Parse(value)
        };
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

    private sealed record PurchaseOrderLineValue(int Id, decimal QtyReceived, bool InvoiceComplete, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);

    private sealed record PurchaseOrderLineLookup(List<PurchaseOrderLineValue>? PurchaseOrderLines, PaceInvoiceSubmissionResult? Result);

    private sealed record PurchaseOrderReceiptValue(int Id, int PurchaseOrderLine, decimal Quantity, decimal UnitCost, decimal? ExtendedPrice, decimal BilledQuantity, decimal? BilledAmount, string? StockingUom);

    private sealed record BillLineValue(int Id, int PurchaseOrderReceipt, string? Bill);

    private sealed record BillValue(int Id, string? Vendor, string? InvoiceNumber, string? PoNumber, string? BillBatch, string? PostingStatus);

    private sealed record BillableReceipt(int PurchaseOrderReceipt, decimal InvoiceAmount, decimal PoQuantity, decimal PoUnitPrice, string? PoUom, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);
}
