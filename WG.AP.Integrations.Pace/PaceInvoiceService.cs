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

    // Bill.Terms is required by Pace - createBill returns HTTP 500 ("terms, Value required") if it's
    // left null, which is what every automated bill creation was doing until this was added. Maps the
    // "NetNN" token SanmarPdfHeaderExtractor pulls straight off the PDF (TermsRegex: "Terms:\s*(\S+)")
    // to the matching Pace Terms value-object id (confirmed 2026-09-21 against Pace staging -
    // loadValueObjects on "Terms" lists every plain "Net N Days" entry at these ids; the discount-based
    // ones like "2% 10 Net 30" are not handled here since nothing on a SanMar invoice maps to them).
    private static readonly Dictionary<int, int> PaceTermsIdByNetDays = new()
    {
        [10] = 5005,
        [15] = 5013,
        [30] = 1,
        [45] = 5008,
        [60] = 5010,
        [75] = 5027,
        [90] = 5018
    };

    // Falls back to this when the extracted Terms text doesn't parse as "NetNN" or names a day count
    // Pace has no plain entry for - every vendor this pipeline bills is SanMar today, whose own
    // invoices always carry Net 60 terms, so this is a reasonable default rather than a guess, but it
    // is a default and not a real per-vendor lookup.
    private const int DefaultTermsId = 5010;

    private static readonly System.Text.RegularExpressions.Regex NetDaysRegex =
        new(@"^Net\s*(?<days>\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            // The duplicate probe runs per lane against the vendor actually sent to createBill (the PO's own
            // vendor, or the ClientCode vendor for no-PO bills), and the bill batch is resolved only after that
            // probe passes so duplicates never create an empty daily batch.
            var normalizedInvoiceNumber = NormalizePaceInvoiceNumber(submission.ClientCode, submission.Fields.InvoiceNumber);

            if (normalizedInvoiceNumber is null)
            {
                var errorMessage = string.Equals(submission.ClientCode, SanMarClientCode, StringComparison.OrdinalIgnoreCase)
                    ? $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': invoice number does not start with INV - review manually."
                    : $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': invoice number normalization rules are not known yet for client '{submission.ClientCode ?? "unknown"}'.";

                logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}; ClientCode={ClientCode}.", errorMessage, submission.InvoiceId, submission.ClientCode);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ErrorMessage = errorMessage
                };
            }

            var purchaseOrderLines = await LoadPurchaseOrderLinesOrNoPoAsync(submission, cancellationToken);

            if (purchaseOrderLines.Result is not null)
            {
                return purchaseOrderLines.Result;
            }

            if (purchaseOrderLines.PurchaseOrderLines is null || purchaseOrderLines.PurchaseOrderLines.Count == 0)
            {
                return await CreateNoPoBillAsync(submission, normalizedInvoiceNumber, cancellationToken);
            }

            var receivedLines = purchaseOrderLines.PurchaseOrderLines.Where(line => line.QtyReceived > 0).ToList();

            if (receivedLines.Count == 0)
            {
                var errorMessage = BuildPoNotReceivedError(submission, purchaseOrderLines.PurchaseOrderLines);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendorAccountNumber);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.PoNotReceived,
                    ResponseJson = SerializeResponse(new { submission.Fields.InvoiceNumber, submission.Fields.CustomerPO, InvoiceTotal = submission.Fields.Total, PurchaseOrderLines = purchaseOrderLines.PurchaseOrderLines }),
                    ErrorMessage = errorMessage
                };
            }

            var billableLines = receivedLines.Where(line => !line.InvoiceComplete).ToList();

            if (billableLines.Count == 0)
            {
                logger.LogInformation(
                    "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}': all {ReceivedLineCount} received PO line(s) already invoice-complete; nothing to bill. InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                    submission.Fields.InvoiceNumber,
                    submission.Fields.CustomerPO,
                    receivedLines.Count,
                    submission.InvoiceId,
                    submission.PaceVendorAccountNumber);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.AlreadyEntered,
                    ResponseJson = SerializeResponse(new { submission.Fields.CustomerPO, PurchaseOrderLines = receivedLines })
                };
            }

            var poVendor = billableLines[0].Vendor;
            var poVendorError = ValidatePurchaseOrderVendor(submission, poVendor);

            if (poVendorError is not null)
            {
                return poVendorError;
            }

            // The duplicate probe stays ahead of every receipt gate below. Those gates treat "no unpaid receipt
            // quantity remains" as an error, which is only true when no bill of ours exists - once a BillLine is
            // posted the receipt's billedQuantity rises, so a re-submitted invoice would otherwise be reported as
            // an error instead of AlreadyEntered (docs/references/project-plan.md: an existing Pace bill for
            // (vendor, invoiceNumber) means ALREADY ENTERED).
            var poVendorExistingBills = await LoadBillsForBillVendorAsync(poVendor!, normalizedInvoiceNumber, cancellationToken);
            var reconciliationTarget = await SelectIncompleteBillToResumeAsync(submission, poVendorExistingBills, poVendor, cancellationToken);

            if (poVendorExistingBills.Count > 0 && reconciliationTarget is null)
            {
                return BuildAlreadyEnteredResult(submission, poVendor!, poVendorExistingBills);
            }

            if (reconciliationTarget is not null && !IsUnpostedBill(reconciliationTarget))
            {
                return BuildPostedBillCannotResumeError(submission, poVendor, reconciliationTarget);
            }

            var receipts = await LoadReceiptsAsync(billableLines.Select(line => line.Id), cancellationToken);

            if (receipts.Count == 0)
            {
                var errorMessage = BuildNoReceiptsError(submission, billableLines);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendorAccountNumber);

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
                var errorMessage = BuildNoUnpaidReceiptsError(submission, receipts, billLines, reconciliationTarget);
                logger.LogError(
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendorAccountNumber);

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
                    "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                    errorMessage,
                    submission.InvoiceId,
                    submission.PaceVendorAccountNumber);

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

            logger.LogInformation(
                "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}': reconciled invoice total {InvoiceTotal} against {ReceiptCount} unpaid PO receipt(s). PO line ids: {PoLineIds}. InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                submission.Fields.Total,
                billableReceipts.Count,
                string.Join(", ", billableReceipts.Select(receipt => receipt.PurchaseOrderLine).Distinct()),
                submission.InvoiceId,
                submission.PaceVendorAccountNumber);

            var prepared = new
            {
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                submission.PaceVendorAccountNumber,
                BillableReceipts = billableReceipts
            };

            if (!options.Value.WriteEnabled)
            {
                logger.LogInformation(
                    "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}': dry-run prepared (Pace writes disabled); would bill vendor {PaceVendorAccountNumber} for {ReceiptCount} receipt(s) totalling {Total}. InvoiceId={InvoiceId}.",
                    submission.Fields.InvoiceNumber,
                    submission.Fields.CustomerPO,
                    submission.PaceVendorAccountNumber,
                    billableReceipts.Count,
                    submission.Fields.Total,
                    submission.InvoiceId);

                return new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.DryRunPrepared,
                    ResponseJson = SerializeResponse(prepared)
                };
            }

            int billId;
            string billBatchIdForResult;

            if (reconciliationTarget is not null)
            {
                billId = reconciliationTarget.Id;
                billBatchIdForResult = reconciliationTarget.BillBatch ?? string.Empty;
            }
            else
            {
                var (batchError, resolved, billBatchDate) = await ResolveBillBatchAsync(submission, cancellationToken);

                if (batchError is not null)
                {
                    return batchError;
                }

                var (createdBill, duplicateResult) = await TryCreateBillAsync(submission, normalizedInvoiceNumber, poVendor, resolved.BillBatchId, resolved.GlAccountingPeriodId, billBatchDate, cancellationToken);

                if (duplicateResult is not null)
                {
                    return duplicateResult;
                }

                if (createdBill!.Id is null)
                {
                    throw new InvalidOperationException("Pace createBill did not return an id.");
                }

                billId = createdBill.Id.Value;
                billBatchIdForResult = resolved.BillBatchId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var createdLineIds = new List<int>();

            // The resume target is only ever a bill with no lines at all, so every billable receipt still needs one.
            foreach (var receipt in billableReceipts)
            {
                var createdLine = await paceClient.CreateBillLineAsync(new BillLine
                {
                    Bill = billId,
                    PurchaseOrderReceipt = receipt.PurchaseOrderReceipt,
                    InvoiceAmount = (double)receipt.InvoiceAmount,
                    GlAccount = receipt.GlAccount,
                    GlDepartment = receipt.GlDepartment,
                    Job = receipt.Job,
                    JobPart = receipt.JobPart,
                    ActivityCode = receipt.ActivityCode
                }, cancellationToken: cancellationToken);

                if (createdLine.Id is null)
                {
                    throw new InvalidOperationException($"Pace createBillLine did not return an id for bill {billId}.");
                }

                createdLineIds.Add(createdLine.Id.Value);
            }

            logger.LogInformation(
                "Pace bill {BillId} (batch {BillBatchId}) has {LineCount} new bill line(s) (ids: {BillLineIds}) for invoice '{InvoiceNumber}'. InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
                billId,
                billBatchIdForResult,
                createdLineIds.Count,
                string.Join(", ", createdLineIds),
                submission.Fields.InvoiceNumber,
                submission.InvoiceId,
                submission.PaceVendorAccountNumber);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
                PaceBillBatchId = billBatchIdForResult,
                PaceBillId = billId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PaceBillLineId = string.Join(",", createdLineIds),
                BillVendor = poVendor,
                ResponseJson = SerializeResponse(new
                {
                    submission.Fields.InvoiceNumber,
                    submission.Fields.CustomerPO,
                    BillId = billId,
                    BillBatchId = billBatchIdForResult,
                    BillLineIds = createdLineIds
                })
            };
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

            if (exception.StatusCode == 200)
            {
                // An ApiException can only carry HTTP 200 when the request itself succeeded and the
                // response body failed to deserialize client-side (see ReadObjectResponseAsync in the
                // generated client) - there is no legitimate "business" reason for Pace to fail a
                // successful response. That is far more likely a passing malformed/empty-body glitch
                // than a genuine data problem, so retry instead of routing the invoice to Errors.
                var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': Pace returned HTTP 200 but the response body could not be parsed; retrying. {exception.Message}";

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

    private async Task<PaceInvoiceSubmissionResult> CreateNoPoBillAsync(
        PaceInvoiceSubmission submission,
        string normalizedInvoiceNumber,
        CancellationToken cancellationToken)
    {
        var noPoVendorCode = submission.ClientCode;

        if (string.IsNullOrWhiteSpace(noPoVendorCode))
        {
            var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': no matching Pace PO found, and the client code is not available to load the Pace vendor default GL account/department.";
            logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}; ClientName={ClientName}; PaceVendorAccountNumber={PaceVendorAccountNumber}.", errorMessage, submission.InvoiceId, submission.ClientName, submission.PaceVendorAccountNumber);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ErrorMessage = errorMessage,
                RequiresReview = true
            };
        }

        var vendorDefaultCoding = await LoadVendorDefaultCodingByCodeAsync(noPoVendorCode, cancellationToken);

        if (vendorDefaultCoding is null)
        {
            var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': no matching Pace PO found, and Pace vendor '{noPoVendorCode}' was not found; cannot load the Pace vendor default GL account/department.";
            logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}; ClientCode={ClientCode}; ClientName={ClientName}; PaceVendorAccountNumber={PaceVendorAccountNumber}.", errorMessage, submission.InvoiceId, noPoVendorCode, submission.ClientName, submission.PaceVendorAccountNumber);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ErrorMessage = errorMessage,
                RequiresReview = true
            };
        }

        if (vendorDefaultCoding.GlAccount is null or <= 0 || vendorDefaultCoding.GlDepartment is null or <= 0)
        {
            var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': Pace vendor default GL account/department for '{vendorDefaultCoding.Name}' is missing or invalid for no-PO bill creation.";
            logger.LogError(
                "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorName={PaceVendorName}; VendorDefaultGlAccount={VendorDefaultGlAccount}; VendorDefaultGlDepartment={VendorDefaultGlDepartment}.",
                errorMessage,
                submission.InvoiceId,
                vendorDefaultCoding.Name,
                vendorDefaultCoding.GlAccount,
                vendorDefaultCoding.GlDepartment);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ErrorMessage = errorMessage,
                RequiresReview = true
            };
        }

        var defaultNoPoVendor = vendorDefaultCoding.Id ?? vendorDefaultCoding.Name;

        if (string.IsNullOrWhiteSpace(defaultNoPoVendor))
        {
            var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': Pace vendor '{vendorDefaultCoding.Name}' did not include a usable id for no-PO bill creation.";
            logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorName={PaceVendorName}.", errorMessage, submission.InvoiceId, vendorDefaultCoding.Name);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ErrorMessage = errorMessage,
                RequiresReview = true
            };
        }

        var noPoVendorExistingBills = await LoadBillsForBillVendorAsync(defaultNoPoVendor, normalizedInvoiceNumber, cancellationToken);
        var noPoReconciliationTarget = await SelectIncompleteBillToResumeAsync(submission, noPoVendorExistingBills, defaultNoPoVendor, cancellationToken);

        if (noPoVendorExistingBills.Count > 0 && noPoReconciliationTarget is null)
        {
            return BuildAlreadyEnteredResult(submission, defaultNoPoVendor, noPoVendorExistingBills);
        }

        if (noPoReconciliationTarget is not null && !IsUnpostedBill(noPoReconciliationTarget))
        {
            return BuildPostedBillCannotResumeError(submission, defaultNoPoVendor, noPoReconciliationTarget) with { RequiresReview = true };
        }

        if (!options.Value.WriteEnabled)
        {
            logger.LogInformation(
                "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}': no matching Pace PO found; dry-run prepared (Pace writes disabled) for the no-PO lane using Pace vendor {PaceVendorId}/{PaceVendorName} default GL account {VendorDefaultGlAccount}/department {VendorDefaultGlDepartment}. InvoiceId={InvoiceId}.",
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                vendorDefaultCoding.Id,
                vendorDefaultCoding.Name,
                vendorDefaultCoding.GlAccount,
                vendorDefaultCoding.GlDepartment,
                submission.InvoiceId);

            return new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.DryRunPrepared,
                ResponseJson = SerializeResponse(new
                {
                    submission.Fields.InvoiceNumber,
                    submission.Fields.CustomerPO,
                    submission.ClientCode,
                    submission.ClientName,
                    submission.PaceVendorAccountNumber,
                    PaceVendorName = vendorDefaultCoding.Name,
                    PaceVendorId = vendorDefaultCoding.Id,
                    VendorDefaultGlAccount = vendorDefaultCoding.GlAccount,
                    VendorDefaultGlDepartment = vendorDefaultCoding.GlDepartment
                }),
                ErrorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': no matching Pace PO found; Pace writes are disabled, a bill would be created using the Pace vendor default GL account/department.",
                BillVendor = vendorDefaultCoding.Id ?? vendorDefaultCoding.Name,
                RequiresReview = true
            };
        }

        int noPoBillId;
        string noPoBillBatchIdForResult;

        if (noPoReconciliationTarget is not null)
        {
            noPoBillId = noPoReconciliationTarget.Id;
            noPoBillBatchIdForResult = noPoReconciliationTarget.BillBatch ?? string.Empty;
        }
        else
        {
            var (batchError, resolved, batchDate) = await ResolveBillBatchAsync(submission, cancellationToken);

            if (batchError is not null)
            {
                // Returned unchanged, as in the PO lane: batch failures (e.g. a closed GL period) route to Errors.
                return batchError;
            }

            var (createdBill, duplicateResult) = await TryCreateBillAsync(submission, normalizedInvoiceNumber, defaultNoPoVendor, resolved.BillBatchId, resolved.GlAccountingPeriodId, batchDate, cancellationToken);

            if (duplicateResult is not null)
            {
                return duplicateResult;
            }

            if (createdBill!.Id is null)
            {
                throw new InvalidOperationException("Pace createBill did not return an id.");
            }

            noPoBillId = createdBill.Id.Value;
            noPoBillBatchIdForResult = resolved.BillBatchId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var createdLine = await paceClient.CreateBillLineAsync(new BillLine
        {
            Bill = noPoBillId,
            InvoiceAmount = (double)submission.Fields.Total,
            GlAccount = vendorDefaultCoding.GlAccount,
            GlDepartment = vendorDefaultCoding.GlDepartment
        }, cancellationToken: cancellationToken);

        if (createdLine.Id is null)
        {
            throw new InvalidOperationException($"Pace createBillLine did not return an id for bill {noPoBillId}.");
        }

        logger.LogInformation(
            "Pace bill {BillId} (batch {BillBatchId}) has bill line {BillLineId} for invoice '{InvoiceNumber}' with no matching Pace PO, coded to Pace vendor {PaceVendorId}/{PaceVendorName} default GL account {VendorDefaultGlAccount}/department {VendorDefaultGlDepartment}. InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
            noPoBillId,
            noPoBillBatchIdForResult,
            createdLine.Id,
            submission.Fields.InvoiceNumber,
            vendorDefaultCoding.Id,
            vendorDefaultCoding.Name,
            vendorDefaultCoding.GlAccount,
            vendorDefaultCoding.GlDepartment,
            submission.InvoiceId,
            submission.PaceVendorAccountNumber);

        return new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
            PaceBillBatchId = noPoBillBatchIdForResult,
            PaceBillId = noPoBillId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PaceBillLineId = createdLine.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BillVendor = defaultNoPoVendor,
            ResponseJson = SerializeResponse(new
            {
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                submission.ClientCode,
                submission.ClientName,
                BillId = noPoBillId,
                BillBatchId = noPoBillBatchIdForResult,
                BillLineId = createdLine.Id,
                PaceVendorName = vendorDefaultCoding.Name,
                PaceVendorId = vendorDefaultCoding.Id,
                VendorDefaultGlAccount = vendorDefaultCoding.GlAccount,
                VendorDefaultGlDepartment = vendorDefaultCoding.GlDepartment
            }),
            ErrorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': no matching Pace PO found; bill {noPoBillId} created in batch {noPoBillBatchIdForResult} using the Pace vendor default GL account/department.",
            RequiresReview = true
        };
    }

    /// <summary>
    /// Probes for an existing bill under the vendor that will actually be sent to createBill.
    /// </summary>
    private Task<List<BillValue>> LoadBillsForBillVendorAsync(string billVendor, string normalizedInvoiceNumber, CancellationToken cancellationToken) =>
        LoadBillsByPaceVendorAccountNumberAndInvoiceAsync(billVendor, normalizedInvoiceNumber, cancellationToken);

    /// <summary>
    /// Decides whether an existing bill for this vendor/invoice is a completed entry (the ordinary duplicate
    /// case) or the debris of an attempt whose createBill succeeded and whose createBillLine never landed.
    /// Returns null when there is nothing to resume - no bill at all, or a bill that already carries lines.
    /// A bill carrying *some* lines is deliberately left alone: Pace raises a receipt's billedQuantity as each
    /// line is posted, so the receipts behind those lines no longer look billable and the remainder cannot be
    /// reconstructed here without double-billing. That case belongs to a human, and reports as AlreadyEntered.
    /// </summary>
    private async Task<BillValue?> SelectIncompleteBillToResumeAsync(
        PaceInvoiceSubmission submission,
        List<BillValue> existingBills,
        string? billVendor,
        CancellationToken cancellationToken)
    {
        if (existingBills.Count == 0)
        {
            return null;
        }

        var existingLines = await LoadBillLinesForBillsAsync(existingBills.Select(bill => bill.Id), cancellationToken);

        if (existingLines.Count > 0)
        {
            return null;
        }

        var target = existingBills[0];

        logger.LogWarning(
            "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}': Pace bill {BillId} for vendor {Vendor} exists with no bill lines ({BillCount} bill(s) matched); resuming line creation against it instead of reporting AlreadyEntered. InvoiceId={InvoiceId}.",
            submission.Fields.InvoiceNumber,
            submission.Fields.CustomerPO,
            target.Id,
            billVendor,
            existingBills.Count,
            submission.InvoiceId);

        return target;
    }

    /// <summary>
    /// Bills this app creates are written with PostingStatus "Open" (see TryCreateBillAsync). Anything else -
    /// already posted, or a status this code does not recognise - must not have lines appended to it, because
    /// resuming skips the GL-period and batch checks that createBill would otherwise have enforced.
    /// </summary>
    private static bool IsUnpostedBill(BillValue bill) =>
        string.Equals(bill.PostingStatus, "Open", StringComparison.OrdinalIgnoreCase);

    private PaceInvoiceSubmissionResult BuildPostedBillCannotResumeError(PaceInvoiceSubmission submission, string? billVendor, BillValue bill)
    {
        var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': Pace bill {bill.Id} for vendor {billVendor} has no bill lines, but its posting status is '{bill.PostingStatus ?? "unknown"}' rather than 'Open'; bill lines cannot be added to it safely and it needs manual review.";

        logger.LogError(
            "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
            errorMessage,
            submission.InvoiceId,
            submission.PaceVendorAccountNumber);

        return new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.Error,
            ResponseJson = SerializeResponse(new { submission.Fields.InvoiceNumber, submission.Fields.CustomerPO, Vendor = billVendor, Bill = bill }),
            ErrorMessage = errorMessage
        };
    }

    private async Task<(PaceInvoiceSubmissionResult? Error, BillBatchResolution.Resolved Batch, DateOnly BatchDate)> ResolveBillBatchAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken)
    {
        var dailyBatchDate = DateOnly.FromDateTime(DateTime.Today);
        var batchDescription = BuildBillBatchDescription(dailyBatchDate, "AUTO");
        var batchResolution = await billBatchResolver.ResolveOrCreateAsync(submission.Fields.InvoiceDate, dailyBatchDate, batchDescription, cancellationToken);
        var batchErrorResult = HandleUnresolvedBatch(submission, batchResolution);

        return batchErrorResult is not null
            ? (batchErrorResult, null!, dailyBatchDate)
            : (null, (BillBatchResolution.Resolved)batchResolution, dailyBatchDate);
    }

    private PaceInvoiceSubmissionResult BuildAlreadyEnteredResult(PaceInvoiceSubmission submission, string? vendor, List<BillValue> existingBills)
    {
        logger.LogInformation(
            "Pace invoice '{InvoiceNumber}' for PO '{PoNumber}' already entered: found {BillCount} existing Pace bill(s) for vendor {Vendor} (ids: {BillIds}; bill batch(es): {BillBatches}). InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
            submission.Fields.InvoiceNumber,
            submission.Fields.CustomerPO,
            existingBills.Count,
            vendor,
            string.Join(", ", existingBills.Select(bill => bill.Id)),
            JoinOrNone(existingBills.Select(bill => bill.BillBatch)),
            submission.InvoiceId,
            submission.PaceVendorAccountNumber);

        return new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.AlreadyEntered,
            ResponseJson = SerializeResponse(new
            {
                submission.Fields.InvoiceNumber,
                submission.Fields.CustomerPO,
                submission.PaceVendorAccountNumber,
                Vendor = vendor,
                Bills = existingBills
            }),
            PaceBillBatchId = string.Join(",", existingBills.Select(bill => bill.BillBatch).Where(billBatch => !string.IsNullOrWhiteSpace(billBatch)).Distinct(StringComparer.OrdinalIgnoreCase)),
            PaceBillId = string.Join(",", existingBills.Select(bill => bill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    private PaceInvoiceSubmissionResult? ValidatePurchaseOrderVendor(PaceInvoiceSubmission submission, string? poVendor)
    {
        if (!string.IsNullOrWhiteSpace(poVendor))
        {
            return null;
        }

        var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': Pace purchase order has no vendor; cannot create bill.";

        logger.LogError(
            "{ErrorMessage} InvoiceId={InvoiceId}; PaceVendorAccountNumber={PaceVendorAccountNumber}.",
            errorMessage,
            submission.InvoiceId,
            submission.PaceVendorAccountNumber);

        return new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.Error,
            ResponseJson = SerializeResponse(new { submission.Fields.InvoiceNumber, submission.Fields.CustomerPO, PurchaseOrderVendor = poVendor, submission.PaceVendorAccountNumber }),
            ErrorMessage = errorMessage
        };
    }

    private PaceInvoiceSubmissionResult? HandleUnresolvedBatch(PaceInvoiceSubmission submission, BillBatchResolution batchResolution)
    {
        switch (batchResolution)
        {
            case BillBatchResolution.PeriodClosed periodClosed:
            {
                var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}': GL accounting period {periodClosed.GlAccountingPeriodId} for invoice date {submission.Fields.InvoiceDate:yyyy-MM-dd} is locked/closed for posting (status '{periodClosed.GlPeriodStatus}'); invoice routed to Errors.";
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

            case BillBatchResolution.Resolved:
                return null;

            default:
                throw new InvalidOperationException($"Unhandled {nameof(BillBatchResolution)} type '{batchResolution.GetType()}'.");
        }
    }

    private async Task<(Bill? Bill, PaceInvoiceSubmissionResult? DuplicateResult)> TryCreateBillAsync(
        PaceInvoiceSubmission submission,
        string normalizedInvoiceNumber,
        string? vendor,
        int billBatchId,
        int paymentPeriodId,
        DateOnly batchDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdBill = await paceClient.CreateBillAsync(new Bill
            {
                BillBatch = billBatchId,
                PaymentPeriod = paymentPeriodId,
                Vendor = vendor,
                InvoiceNumber = normalizedInvoiceNumber,
                InvoiceDate = submission.Fields.InvoiceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                BillType = "1",
                BillStatus = 1,
                PostingStatus = "Open",
                InvoiceAmount = (double)submission.Fields.Total,
                PoNumber = submission.Fields.CustomerPO,
                Reference = submission.Fields.CustomerPO,
                // Same date as the bill batch itself - the date this transaction is being entered into
                // Pace, not the vendor's own invoice date (that's InvoiceDate above).
                VoucherDate = batchDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                DateDue = submission.Fields.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Terms = ResolvePaceTermsId(submission.Fields.Terms, submission.InvoiceId)
            }, cancellationToken: cancellationToken);

            return (createdBill, null);
        }
        catch (ApiException exception) when (IsDuplicateInvoiceError(exception))
        {
            var errorMessage = $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': Pace rejected createBill as a duplicate invoice for vendor {vendor}.";
            logger.LogError("{ErrorMessage} InvoiceId={InvoiceId}.", errorMessage, submission.InvoiceId);

            return (null, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ResponseJson = SerializePaceException(exception),
                ErrorMessage = errorMessage
            });
        }
    }

    private static bool IsDuplicateInvoiceError(ApiException exception) =>
        exception.StatusCode == 500
        && exception.Response is not null
        && exception.Response.Contains("duplicate invoice", StringComparison.OrdinalIgnoreCase);

    private int ResolvePaceTermsId(string? extractedTerms, long invoiceId)
    {
        var match = extractedTerms is null ? null : NetDaysRegex.Match(extractedTerms);

        if (match is { Success: true }
            && int.TryParse(match.Groups["days"].Value, out var days)
            && PaceTermsIdByNetDays.TryGetValue(days, out var termsId))
        {
            return termsId;
        }

        logger.LogWarning(
            "Invoice {InvoiceId}: extracted Terms '{ExtractedTerms}' did not map to a known Pace Terms id; " +
            "falling back to the default ({DefaultTermsId}).",
            invoiceId, extractedTerms, DefaultTermsId);

        return DefaultTermsId;
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
            XpathFilter = $"@purchaseOrder/@poNumber = {PaceXPath.StringLiteral(pacePoNumber)}",
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

        var poVendor = rows.Count == 0 ? null : await LoadPurchaseOrderVendorAsync(pacePoNumber, cancellationToken);

        return rows
            .Select(fields => new PurchaseOrderLineValue(
                GetRequiredInt(fields, "id"),
                GetDecimal(fields, "qtyReceived") ?? 0,
                GetBool(fields, "invoiceComplete") ?? false,
                GetNullableInt(fields, "glAccount"),
                GetNullableInt(fields, "glDepartment"),
                GetString(fields, "job"),
                GetString(fields, "jobPart"),
                GetString(fields, "activityCode"),
                poVendor))
            .ToList();
    }

    private async Task<string?> LoadPurchaseOrderVendorAsync(string pacePoNumber, CancellationToken cancellationToken)
    {
        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
        {
            ObjectName = "PurchaseOrder",
            XpathFilter = $"@poNumber = {PaceXPath.StringLiteral(pacePoNumber)}",
            Fields =
            [
                Field("id", "@id"),
                Field("vendor", "@vendor")
            ]
        }, cancellationToken);

        if (rows.Count > 1)
        {
            logger.LogWarning(
                "Pace PurchaseOrder query returned {PurchaseOrderCount} row(s) for PO {PoNumber}; using the first vendor.",
                rows.Count,
                pacePoNumber);
        }

        return rows.Count == 0 ? null : GetString(rows[0], "vendor");
    }

    private async Task<VendorDefaultCoding?> LoadVendorDefaultCodingByCodeAsync(string vendorCode, CancellationToken cancellationToken)
    {
        var trimmedVendorCode = vendorCode.Trim();

        try
        {
            var vendor = await paceClient.ReadVendorAsync(trimmedVendorCode, cancellationToken: cancellationToken);

            return new VendorDefaultCoding(
                string.IsNullOrWhiteSpace(vendor.Id) ? trimmedVendorCode : vendor.Id,
                string.IsNullOrWhiteSpace(vendor.Name) ? trimmedVendorCode : vendor.Name,
                vendor.GlAccount,
                vendor.GlDepartment);
        }
        // Only Pace's own "vendor not found" 404 is a data problem; an endpoint-level 404 (missing or
        // misconfigured readVendor route) must reach the integration error path instead of NeedsReview.
        catch (ApiException exception) when (exception.StatusCode == 404 && !IsEndpointNotFoundResponse(exception))
        {
            logger.LogError(
                "Pace Vendor {VendorCode} was not found while loading the Pace vendor default GL account/department.",
                trimmedVendorCode);

            return null;
        }
    }

    /// <summary>
    /// True when a 404 came from the web server itself (an HTML "Not Found" page for a missing or
    /// misconfigured endpoint) rather than from Pace's value-object service reporting no matching rows.
    /// </summary>
    /// <remarks>
    /// Only Pace's own no-match 404 may be read as "no duplicate bill": treating an endpoint failure the
    /// same way would let createBill run blind and create a duplicate while hiding the integration fault.
    /// </remarks>
    private static bool IsEndpointNotFoundResponse(ApiException exception)
    {
        var body = exception.Response;

        return !string.IsNullOrWhiteSpace(body)
            && (body.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || body.Contains("requested URL was not found", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<BillValue>> LoadBillsByPaceVendorAccountNumberAndInvoiceAsync(string paceVendorAccountNumber, string invoiceNumber, CancellationToken cancellationToken)
    {
        List<IReadOnlyDictionary<string, object?>> rows;

        try
        {
            rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
            {
                ObjectName = "Bill",
                XpathFilter = $"@vendor = {PaceXPath.StringLiteral(paceVendorAccountNumber)} and @invoiceNumber = {PaceXPath.StringLiteral(invoiceNumber)}",
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
        catch (PaceValueObjectNotFoundException exception) when (string.Equals(exception.ObjectName, "Bill", StringComparison.OrdinalIgnoreCase)
            && !IsEndpointNotFoundResponse(exception.ApiException))
        {
            logger.LogInformation(
                "Pace bill duplicate probe found no bill for Pace vendor account number {PaceVendorAccountNumber} and invoice number {InvoiceNumber}. XPathFilter={XPathFilter}; Offset={Offset}.",
                paceVendorAccountNumber,
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

        return ParseBillLineRows(rows);
    }

    /// <summary>
    /// Loads the bill lines already posted against specific bills, so a duplicate probe can tell whether an
    /// existing Bill is fully entered or was left incomplete by a prior createBillLine failure.
    /// </summary>
    private async Task<List<BillLineValue>> LoadBillLinesForBillsAsync(IEnumerable<int> billIds, CancellationToken cancellationToken)
    {
        var rows = await LoadAllValueObjectRowsAsync(new ValueObjectDescriptor
        {
            ObjectName = "BillLine",
            XpathFilter = OrFilter("@bill", billIds),
            Fields =
            [
                Field("id", "@id"),
                Field("purchaseOrderReceipt", "@purchaseOrderReceipt"),
                Field("bill", "@bill")
            ]
        }, cancellationToken);

        return ParseBillLineRows(rows);
    }

    private static List<BillLineValue> ParseBillLineRows(List<IReadOnlyDictionary<string, object?>> rows) =>
        rows
            .Select(fields => new BillLineValue(
                GetRequiredInt(fields, "id"),
                GetNullableInt(fields, "purchaseOrderReceipt"),
                GetString(fields, "bill")))
            .ToList();

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

                break;
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
                break;
            }

            if (pageRows.Count < PacePageSize)
            {
                if (page.TotalRecords is not null)
                {
                    throw new InvalidOperationException($"Pace returned only {pageRows.Count} {descriptor.ObjectName} rows at offset {offset}, but reported {page.TotalRecords.Value} total record(s).");
                }

                break;
            }

            offset += PacePageSize;
        }

        logger.LogInformation(
            "Pace {ObjectName} query returned {RowCount} row(s). XPathFilter={XPathFilter}.",
            descriptor.ObjectName,
            rows.Count,
            descriptor.XpathFilter);

        return rows;
    }

    private static FieldDescriptor Field(string name, string xpath) => new() { Name = name, Xpath = xpath };

    private static string OrFilter(string field, IEnumerable<int> values) =>
        string.Join(" or ", values.Select(value => $"{field} = {value}"));

    private const string SanMarClientCode = "SANMAR";

    /// <summary>
    /// Returns the Pace invoice number, or null when it cannot be normalized. Only SanMar's format is
    /// known today: its invoice numbers always carry an "INV" prefix, which is stripped only when followed
    /// by a separator or digit so values like "INVEST-123" are never corrupted.
    /// </summary>
    internal static string? NormalizePaceInvoiceNumber(string? clientCode, string invoiceNumber)
    {
        if (!string.Equals(clientCode, SanMarClientCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = invoiceNumber.Trim();

        if (trimmed.Length <= 3
            || !trimmed.StartsWith("INV", StringComparison.OrdinalIgnoreCase)
            || !(trimmed[3] == '-' || char.IsWhiteSpace(trimmed[3]) || trimmed[3] == '\u00A0' || char.IsDigit(trimmed[3])))
        {
            return null;
        }

        var normalized = new string(trimmed[3..]
            .Where(character => character != '-' && !char.IsWhiteSpace(character) && character != '\u00A0')
            .ToArray());

        return normalized.Length > 0 && normalized.All(char.IsDigit) ? normalized : null;
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

    private static string BuildBillBatchDescription(DateOnly batchDate, string prefix) =>
        $"{prefix} {batchDate.Month}-{batchDate.Day}-{batchDate:yy}";

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

    private static string BuildNoUnpaidReceiptsError(PaceInvoiceSubmission submission, IReadOnlyCollection<PurchaseOrderReceiptValue> receipts, IReadOnlyCollection<BillLineValue> billLines, BillValue? emptyBillFound)
    {
        var billClause = emptyBillFound is null
            ? "and no Pace bill was found for vendor/invoice"
            : $"and the only Pace bill found for vendor/invoice ({emptyBillFound.Id}) has no bill lines to complete";

        return $"Pace invoice '{submission.Fields.InvoiceNumber}' for PO '{submission.Fields.CustomerPO}': PO receipts exist but no unpaid receipt quantity remains, {billClause}. Invoice total: {submission.Fields.Total:0.####}. Receipt ids: {string.Join(", ", receipts.Select(receipt => receipt.Id))}. Bill line ids: {JoinOrNone(billLines.Select(line => line.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))}. Existing bill ids: {JoinOrNone(billLines.Select(line => line.Bill).Where(bill => !string.IsNullOrWhiteSpace(bill)).Distinct(StringComparer.OrdinalIgnoreCase))}.";
    }

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

    private sealed record PurchaseOrderLineValue(int Id, decimal QtyReceived, bool InvoiceComplete, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode, string? Vendor);

    private sealed record PurchaseOrderLineLookup(List<PurchaseOrderLineValue>? PurchaseOrderLines, PaceInvoiceSubmissionResult? Result);

    private sealed record PurchaseOrderReceiptValue(int Id, int PurchaseOrderLine, decimal Quantity, decimal UnitCost, decimal? ExtendedPrice, decimal BilledQuantity, decimal? BilledAmount, string? StockingUom);

    private sealed record BillLineValue(int Id, int? PurchaseOrderReceipt, string? Bill);

    private sealed record BillValue(int Id, string? Vendor, string? InvoiceNumber, string? PoNumber, string? BillBatch, string? PostingStatus);

    private sealed record VendorDefaultCoding(string? Id, string Name, int? GlAccount, int? GlDepartment);

    private sealed record BillableReceipt(int PurchaseOrderReceipt, int PurchaseOrderLine, decimal InvoiceAmount, decimal PoQuantity, decimal PoUnitPrice, string? PoUom, int? GlAccount, int? GlDepartment, string? Job, string? JobPart, string? ActivityCode);
}
