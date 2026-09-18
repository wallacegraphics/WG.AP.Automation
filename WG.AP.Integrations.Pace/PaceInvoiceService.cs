using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Integrations.Pace;

public sealed class PaceInvoiceService(
    IPaceClient paceClient,
    PaceBillBatchResolver billBatchResolver,
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
            if (string.IsNullOrWhiteSpace(submission.PaceVendoreAccountNumber))
            {
                var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': client has no Pace vendor account number configured.";
                logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}.", errorMessage, submission.InvoiceId);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ErrorMessage = errorMessage
                };
            }

            var normalizedInvoiceNumber = NormalizePaceInvoiceNumber(submission.Fields.InvoiceNumber);
            var existingBills = await LoadBillsByPaceVendoreAccountNumberAndInvoiceAsync(submission.PaceVendoreAccountNumber, normalizedInvoiceNumber, cancellationToken);

            if (existingBills.Count > 0)
            {
                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.AlreadyEntered,
                    ResponseJson = SerializeResponse(new
                    {
                        submission.Fields.InvoiceNumber,
                        submission.Fields.CustomerPO,
                        submission.PaceVendoreAccountNumber,
                        Bills = existingBills
                    }),
                    PaceBillBatchId = string.Join(",", existingBills.Select(bill => bill.BillBatch).Where(billBatch => !string.IsNullOrWhiteSpace(billBatch)).Distinct(StringComparer.OrdinalIgnoreCase)),
                    PaceBillId = string.Join(",", existingBills.Select(bill => bill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Distinct(StringComparer.OrdinalIgnoreCase))
                };
            }

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
                var errorMessage = BuildPoNotReceivedError(submission, purchaseOrderLines.PurchaseOrderLines);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendoreAccountNumber={PaceVendoreAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendoreAccountNumber);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ResponseJson = SerializeResponse(new { submission.Fields.InvoiceNumber, submission.Fields.CustomerPO, InvoiceTotal = submission.Fields.Total, PurchaseOrderLines = purchaseOrderLines.PurchaseOrderLines }),
                    ErrorMessage = errorMessage
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
                var errorMessage = BuildNoReceiptsError(submission, billableLines);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendoreAccountNumber={PaceVendoreAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendoreAccountNumber);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ResponseJson = SerializeResponse(new { submission.Fields.InvoiceNumber, submission.Fields.CustomerPO, PurchaseOrderLines = billableLines }),
                    ErrorMessage = errorMessage
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
                        receipt.Receipt.PurchaseOrderLine,
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
                var errorMessage = BuildNoUnpaidReceiptsError(submission, receipts, billLines);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendoreAccountNumber={PaceVendoreAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendoreAccountNumber);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ResponseJson = SerializeResponse(new
                    {
                        submission.Fields.InvoiceNumber,
                        submission.Fields.CustomerPO,
                        InvoiceTotal = submission.Fields.Total,
                        Receipts = receipts,
                        BillLines = billLines
                    }),
                    ErrorMessage = errorMessage
                };
            }

            var receiptTotal = billableReceipts.Sum(receipt => receipt.InvoiceAmount);

            if (receiptTotal != submission.Fields.Total)
            {
                var errorMessage = BuildReceiptTotalMismatchError(submission, receiptTotal, billableReceipts);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendoreAccountNumber={PaceVendoreAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendoreAccountNumber);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ResponseJson = SerializeResponse(new
                    {
                        submission.Fields.InvoiceNumber,
                        submission.Fields.CustomerPO,
                        InvoiceTotal = submission.Fields.Total,
                        PaceReceiptTotal = receiptTotal,
                        BillableReceipts = billableReceipts
                    }),
                    ErrorMessage = errorMessage
                };
            }

            var prepared = new
            {
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                submission.PaceVendoreAccountNumber,
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

            var batchDate = DateOnly.FromDateTime(DateTime.Today);
            var batchDescription = BuildPaceBillBatchDescription(batchDate, submission);
            var batchResolution = await billBatchResolver.ResolveOrCreateAsync(batchDate, batchDescription, cancellationToken);

            switch (batchResolution)
            {
                case BillBatchResolution.PeriodClosed periodClosed:
                {
                    var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}': GL accounting period {periodClosed.GlAccountingPeriodId} is not open for posting (status '{periodClosed.GlPeriodStatus}'); invoice routed to the error report.";
                    logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}.", errorMessage, submission.InvoiceId);

                    return new PaceInvoiceSubmissionResult
                    {
                        StatusCode = PaceInvoiceOutcomeStatus.Error,
                        ErrorMessage = errorMessage
                    };
                }

                case BillBatchResolution.Unresolvable unresolvable:
                {
                    var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}': {unresolvable.Reason}";
                    logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}.", errorMessage, submission.InvoiceId);

                    return new PaceInvoiceSubmissionResult
                    {
                        StatusCode = PaceInvoiceOutcomeStatus.Error,
                        ErrorMessage = errorMessage
                    };
                }

                case BillBatchResolution.Resolved resolved:
                    return new PaceInvoiceSubmissionResult
                    {
                        StatusCode = PaceInvoiceOutcomeStatus.Error,
                        PaceBillBatchId = resolved.BillBatchId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ErrorMessage = "Pace writes are enabled; bill batch resolved but bill/bill-line creation is not implemented until PR 2."
                    };

                default:
                    throw new InvalidOperationException($"Unhandled {nameof(BillBatchResolution)} type '{batchResolution.GetType()}'.");
            }
        }
        catch (PaceValueObjectNotFoundException exception)
        {
            var errorMessage = BuildPaceValueObjectNotFoundError(submission, exception);

            logger.LogError(
                "{ErrorMessage} InvoiceId={InvoiceId}; InvoiceNumber={InvoiceNumber}; CustomerPO={CustomerPO}; PaceObjectName={PaceObjectName}; XPathFilter={XPathFilter}; Offset={Offset}.",
                errorMessage,
                submission.InvoiceId,
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                exception.ObjectName,
                exception.XpathFilter,
                exception.Offset);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ResponseJson = SerializePaceException(exception.ApiException),
                ErrorMessage = errorMessage
            };
        }
        catch (ApiException exception)
        {
            if (IsAuthenticationFailureStatus(exception.StatusCode))
            {
                var errorMessage = $"Pace authentication or authorization failed with HTTP {exception.StatusCode}; retry after credentials or permissions are fixed.";
                logger.LogError(
                    exception,
                    "{ErrorMessage} InvoiceId={InvoiceId}; InvoiceNumber={InvoiceNumber}; CustomerPO={CustomerPO}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.Fields.InvoiceNumber,
                    submission.Fields.CustomerPO);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.RetryLater,
                    IsTransient = true,
                    ResponseJson = SerializePaceException(exception),
                    ErrorMessage = errorMessage
                };
            }

            if (exception.StatusCode == 404)
            {
                var errorMessage = BuildPaceHttpNotFoundError(submission, exception);

                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; InvoiceNumber={InvoiceNumber}; CustomerPO={CustomerPO}; StatusCode={StatusCode}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.Fields.InvoiceNumber,
                    submission.Fields.CustomerPO,
                    exception.StatusCode);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ResponseJson = SerializePaceException(exception),
                    ErrorMessage = errorMessage
                };
            }

            logger.LogError(exception, "Pace request failed with status {StatusCode} for invoice {InvoiceId}, invoice number {InvoiceNumber}, PO {CustomerPO}.", exception.StatusCode, submission.InvoiceId, submission.Fields.InvoiceNumber, submission.Fields.CustomerPO);

            var isTransient = IsTransientStatus(exception.StatusCode);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = isTransient ? PaceInvoiceOutcomeStatus.RetryLater : PaceInvoiceOutcomeStatus.Error,
                IsTransient = isTransient,
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
        return new PurchaseOrderLineLookup(await LoadPurchaseOrderLinesAsync(submission.Fields.CustomerPO, cancellationToken), Result: null);
    }

    private async Task<List<PurchaseOrderLineValue>> LoadPurchaseOrderLinesAsync(string poNumber, CancellationToken cancellationToken)
    {
        var pacePoNumber = NormalizePacePurchaseOrderNumber(poNumber);

        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
        {
            ObjectName = "PurchaseOrderLine",
            XpathFilter = $"@purchaseOrder/@poNumber = {XPathStringLiteral(pacePoNumber)}",
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

    private async Task<List<BillValue>> LoadBillsByPaceVendoreAccountNumberAndInvoiceAsync(string paceVendoreAccountNumber, string invoiceNumber, CancellationToken cancellationToken)
    {
        List<IReadOnlyDictionary<string, object?>> rows;

        try
        {
            rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
            {
                ObjectName = "Bill",
                XpathFilter = $"@vendor = {XPathStringLiteral(paceVendoreAccountNumber)} and @invoiceNumber = {XPathStringLiteral(invoiceNumber)}",
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
        }
        catch (PaceValueObjectNotFoundException exception) when (string.Equals(exception.ObjectName, "Bill", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Pace bill duplicate probe found no bill for Pace vendor account number {PaceVendoreAccountNumber} and invoice number {InvoiceNumber}. XPathFilter={XPathFilter}; Offset={Offset}.",
                paceVendoreAccountNumber,
                invoiceNumber,
                exception.XpathFilter,
                exception.Offset);

            return [];
        }

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

            ValueObjectsGroup page;

            try
            {
                page = await paceClient.LoadValueObjectsAsync(descriptor, cancellationToken: cancellationToken);
            }
            catch (ApiException exception) when (exception.StatusCode == 404)
            {
                throw new PaceValueObjectNotFoundException(
                    descriptor.ObjectName ?? "ValueObject",
                    descriptor.XpathFilter,
                    offset,
                    exception);
            }

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

    private static string NormalizePaceInvoiceNumber(string invoiceNumber)
    {
        var normalized = new string(invoiceNumber
            .Where(character => character != '-' && !char.IsWhiteSpace(character) && character != '\u00A0')
            .ToArray());

        return normalized.StartsWith("INV", StringComparison.OrdinalIgnoreCase)
            ? normalized[3..]
            : normalized;
    }

    private static string NormalizePacePurchaseOrderNumber(string poNumber)
    {
        var trimmed = poNumber.Trim();
        var dashIndex = trimmed.IndexOf('-', StringComparison.Ordinal);

        if (dashIndex == 5 && trimmed[..dashIndex].All(char.IsDigit))
        {
            return trimmed[..dashIndex];
        }

        if (dashIndex == 4
            && trimmed[..dashIndex].All(char.IsDigit)
            && trimmed.Length >= dashIndex + 5
            && trimmed[(dashIndex + 1)..(dashIndex + 5)].All(char.IsDigit))
        {
            return $"CS{trimmed[(dashIndex + 1)..(dashIndex + 5)]}";
        }

        return trimmed;
    }

    private static string BuildPaceBillBatchDescription(DateOnly batchDate, PaceInvoiceSubmission submission) =>
        $"{batchDate:MM_dd_yyyy}_{NormalizePaceCustomerAccountNumber(submission)}_{PaceBillBatchResolver.EnteredBy}";

    private static string NormalizePaceCustomerAccountNumber(PaceInvoiceSubmission submission)
    {
        var accountNumber = FirstNonEmpty(
            submission.Fields.CustomerNumber,
            submission.Fields.OrderAccount,
            submission.PaceVendoreAccountNumber);

        if (accountNumber is null)
        {
            return "unknown";
        }

        var trimmed = accountNumber.Trim();
        var dashIndex = trimmed.IndexOf('-', StringComparison.Ordinal);

        return dashIndex > 0
            ? trimmed[..dashIndex]
            : trimmed;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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

    private static string GetPaceResponseReason(ApiException exception)
    {
        var response = exception.Response;

        if (string.IsNullOrWhiteSpace(response))
        {
            return exception.Message;
        }

        var jsonReason = TryGetJsonResponseReason(response);

        if (!string.IsNullOrWhiteSpace(jsonReason))
        {
            return jsonReason;
        }

        var htmlReason = TryGetHtmlElementText(response, "p")
            ?? TryGetHtmlElementText(response, "h1")
            ?? TryGetHtmlElementText(response, "title");

        if (!string.IsNullOrWhiteSpace(htmlReason))
        {
            return htmlReason;
        }

        return response.Trim();
    }

    private static string? TryGetJsonResponseReason(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            foreach (var propertyName in new[] { "message", "error", "errorMessage", "detail", "title" })
            {
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? TryGetHtmlElementText(string response, string elementName)
    {
        var startTag = $"<{elementName}>";
        var endTag = $"</{elementName}>";
        var startIndex = response.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);

        if (startIndex < 0)
        {
            return null;
        }

        startIndex += startTag.Length;
        var endIndex = response.IndexOf(endTag, startIndex, StringComparison.OrdinalIgnoreCase);

        if (endIndex <= startIndex)
        {
            return null;
        }

        var elementText = response[startIndex..endIndex].Trim();
        return string.IsNullOrWhiteSpace(elementText) ? null : WebUtility.HtmlDecode(elementText);
    }

    private static bool IsTransientStatus(int statusCode) =>
        statusCode is 408 or 429 or >= 500;

    private static bool IsAuthenticationFailureStatus(int statusCode) =>
        statusCode is 401 or 403;

    private static string BuildPaceValueObjectNotFoundError(PaceInvoiceSubmission submission, PaceValueObjectNotFoundException exception) =>
        exception.ObjectName switch
        {
            "PurchaseOrderLine" => $"Pace returned HTTP 404 while loading purchase order lines for invoice '{submission.Fields.InvoiceNumber}' and PO '{submission.Fields.CustomerPO}'. Pace response: {GetPaceResponseReason(exception.ApiException)} Verify the Pace REST base URL, service path, and PurchaseOrderLine value-object endpoint.",
            "PurchaseOrderReceipt" => $"Pace returned HTTP 404 while loading purchase order receipts for invoice '{submission.Fields.InvoiceNumber}' and PO '{submission.Fields.CustomerPO}'. Pace response: {GetPaceResponseReason(exception.ApiException)} Verify the Pace REST base URL, service path, and PurchaseOrderReceipt value-object endpoint.",
            "BillLine" => $"Pace returned HTTP 404 while loading bill lines for invoice '{submission.Fields.InvoiceNumber}' and PO '{submission.Fields.CustomerPO}'. Pace response: {GetPaceResponseReason(exception.ApiException)} Verify the Pace REST base URL, service path, and BillLine value-object endpoint.",
            "Bill" => $"Pace returned HTTP 404 while checking invoice '{submission.Fields.InvoiceNumber}' for duplicate Pace bills. Pace response: {GetPaceResponseReason(exception.ApiException)} Verify the Pace REST base URL, service path, and Bill value-object endpoint.",
            _ => $"Pace returned HTTP 404 while loading {exception.ObjectName} for invoice '{submission.Fields.InvoiceNumber}' and PO '{submission.Fields.CustomerPO}'. Pace response: {GetPaceResponseReason(exception.ApiException)} Verify the Pace REST base URL, service path, and requested Pace value-object endpoint."
        };

    private static string BuildPaceHttpNotFoundError(PaceInvoiceSubmission submission) =>
        $"Pace returned HTTP 404 while processing invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}'.";

    private static string BuildPaceHttpNotFoundError(PaceInvoiceSubmission submission, ApiException exception) =>
        $"{BuildPaceHttpNotFoundError(submission)} Pace response: {GetPaceResponseReason(exception)} Verify the Pace REST base URL, service path, and requested Pace resource.";

    private static string BuildPoNotReceivedError(PaceInvoiceSubmission submission, IReadOnlyCollection<PurchaseOrderLineValue> purchaseOrderLines) =>
        $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': no received PO lines were found. Invoice total: {submission.Fields.Total:0.####}. Purchase order line ids: {string.Join(", ", purchaseOrderLines.Select(line => line.Id))}.";

    private static string BuildNoReceiptsError(PaceInvoiceSubmission submission, IReadOnlyCollection<PurchaseOrderLineValue> billableLines) =>
        $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': no unpaid PO receipts were found. Purchase order line ids: {string.Join(", ", billableLines.Select(line => line.Id))}.";

    private static string BuildNoUnpaidReceiptsError(PaceInvoiceSubmission submission, IReadOnlyCollection<PurchaseOrderReceiptValue> receipts, IReadOnlyCollection<BillLineValue> billLines) =>
        $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': PO receipts exist but no unpaid receipt quantity remains, and no Pace bill was found for vendor/invoice. Invoice total: {submission.Fields.Total:0.####}. Receipt ids: {string.Join(", ", receipts.Select(receipt => receipt.Id))}. Bill line ids: {JoinOrNone(billLines.Select(line => line.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))}. Existing bill ids: {JoinOrNone(billLines.Select(line => line.Bill).Where(bill => !string.IsNullOrWhiteSpace(bill)).Distinct(StringComparer.OrdinalIgnoreCase))}.";

    private static string BuildReceiptTotalMismatchError(PaceInvoiceSubmission submission, decimal receiptTotal, IReadOnlyCollection<BillableReceipt> billableReceipts) =>
        $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': invoice total {submission.Fields.Total:0.####} does not match unpaid PO receipt total {receiptTotal:0.####}. Purchase order line ids: {string.Join(", ", billableReceipts.Select(receipt => receipt.PurchaseOrderLine).Distinct())}. Receipt ids: {string.Join(", ", billableReceipts.Select(receipt => receipt.PurchaseOrderReceipt))}.";

    private static string JoinOrNone(IEnumerable<string?> values)
    {
        var joined = string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(joined) ? "none" : joined;
    }

    private sealed class PaceValueObjectNotFoundException(string objectName, string? xpathFilter, int offset, ApiException apiException)
        : Exception($"Pace {objectName} lookup returned HTTP 404 at offset {offset}.", apiException)
    {
        public string ObjectName { get; } = objectName;

        public string? XpathFilter { get; } = xpathFilter;

        public int Offset { get; } = offset;

        public ApiException ApiException { get; } = apiException;
    }

    private sealed record PurchaseOrderLineValue(int Id, decimal QtyReceived, bool InvoiceComplete, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);

    private sealed record PurchaseOrderLineLookup(List<PurchaseOrderLineValue>? PurchaseOrderLines, PaceInvoiceSubmissionResult? Result);

    private sealed record PurchaseOrderReceiptValue(int Id, int PurchaseOrderLine, decimal Quantity, decimal UnitCost, decimal? ExtendedPrice, decimal BilledQuantity, decimal? BilledAmount, string? StockingUom);

    private sealed record BillLineValue(int Id, int PurchaseOrderReceipt, string? Bill);

    private sealed record BillValue(int Id, string? Vendor, string? InvoiceNumber, string? PoNumber, string? BillBatch, string? PostingStatus);

    private sealed record BillableReceipt(int PurchaseOrderReceipt, int PurchaseOrderLine, decimal InvoiceAmount, decimal PoQuantity, decimal PoUnitPrice, string? PoUom, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);
}
