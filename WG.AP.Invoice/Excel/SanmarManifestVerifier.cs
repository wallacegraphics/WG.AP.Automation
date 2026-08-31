using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Excel;

/// <summary>
/// Reads and reconciles a SanMar-style Excel manifest. This vendor's manifest/PDF convention (a
/// join key of Excel "Voucher" == PDF filename == PDF's printed Invoice Number) is not universal —
/// see the column names read below, which mirror the real SanMar workbook header row.
/// </summary>
public sealed class SanmarManifestVerifier(ILogger<SanmarManifestVerifier> logger) : IAttachmentManifestVerifier
{
    private const decimal AmountTolerance = 0.01m;

    private static readonly string[] RequiredColumns =
    [
        "Voucher", "Sales order", "Due date", "Invoice amount", "RMA number",
        "Customer reference", "Date", "Terms of payment", "Customer account"
    ];

    public IReadOnlyList<ManifestRow> ReadManifest(byte[] excelBytes)
    {
        try
        {
            using var stream = new MemoryStream(excelBytes);
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheets.First();
            var range = worksheet.RangeUsed() ?? throw new InvalidOperationException("Manifest worksheet is empty.");

            var headerRow = range.FirstRow();
            var columnIndexByName = headerRow.Cells()
                .ToDictionary(cell => cell.GetString().Trim(), cell => cell.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

            foreach (var required in RequiredColumns)
            {
                if (!columnIndexByName.ContainsKey(required))
                {
                    throw new InvalidOperationException($"Manifest is missing expected column '{required}'.");
                }
            }

            var voucherCol = columnIndexByName["Voucher"];
            var salesOrderCol = columnIndexByName["Sales order"];
            var dueDateCol = columnIndexByName["Due date"];
            var invoiceAmountCol = columnIndexByName["Invoice amount"];
            var rmaCol = columnIndexByName["RMA number"];
            var customerReferenceCol = columnIndexByName["Customer reference"];
            var dateCol = columnIndexByName["Date"];
            var termsCol = columnIndexByName["Terms of payment"];
            var customerAccountCol = columnIndexByName["Customer account"];

            var rows = new List<ManifestRow>();

            foreach (var row in range.RowsUsed().Skip(1))
            {
                var voucher = row.Cell(voucherCol).GetString().Trim();

                if (string.IsNullOrWhiteSpace(voucher))
                {
                    continue;
                }

                rows.Add(new ManifestRow(
                    voucher,
                    NullIfEmpty(row.Cell(salesOrderCol).GetString()),
                    ParseExcelDate(row.Cell(dateCol)),
                    ParseExcelDate(row.Cell(dueDateCol)),
                    row.Cell(invoiceAmountCol).GetValue<decimal>(),
                    NullIfEmpty(row.Cell(rmaCol).GetString()),
                    NullIfEmpty(row.Cell(customerReferenceCol).GetString()),
                    NullIfEmpty(row.Cell(termsCol).GetString()),
                    NullIfEmpty(row.Cell(customerAccountCol).GetString())));
            }

            return rows;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read the Excel manifest attachment.");
            throw;
        }
    }

    public ManifestReconciliation Reconcile(IReadOnlyList<ManifestRow> manifestRows, IReadOnlyList<MailAttachmentSummary> pdfAttachments)
    {
        var duplicateVouchers = manifestRows
            .GroupBy(row => row.Voucher, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        var distinctRowsByVoucher = manifestRows
            .GroupBy(row => row.Voucher, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var attachmentGroups = pdfAttachments
            .GroupBy(attachment => Path.GetFileNameWithoutExtension(attachment.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attachmentsByStem = attachmentGroups
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var duplicateAttachments = attachmentGroups
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        var matched = new List<ManifestPair>();
        var missing = new List<string>();

        foreach (var (voucher, row) in distinctRowsByVoucher)
        {
            if (attachmentsByStem.TryGetValue(voucher, out var attachment))
            {
                matched.Add(new ManifestPair(voucher, attachment.Name, row));
            }
            else
            {
                missing.Add(voucher);
            }
        }

        var unexpected = attachmentsByStem.Keys
            .Where(stem => !distinctRowsByVoucher.ContainsKey(stem))
            .ToList();

        return new ManifestReconciliation(missing, unexpected, duplicateAttachments, duplicateVouchers, matched);
    }

    public InvoiceFieldComparisonResult CompareFields(ManifestRow row, InvoiceFields extractedFields)
    {
        var mismatches = new List<FieldMismatch>();

        if (!string.Equals(row.Voucher, extractedFields.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new FieldMismatch("InvoiceNumber", row.Voucher, extractedFields.InvoiceNumber));
        }

        if (!EqualsNormalized(row.SalesOrder, extractedFields.SalesOrder))
        {
            mismatches.Add(new FieldMismatch("SalesOrder", row.SalesOrder ?? string.Empty, extractedFields.SalesOrder ?? string.Empty));
        }

        // The manifest's "Customer reference" column is SanMar's PO reference, i.e. the PDF's
        // "Customer PO"; its "Customer account" column matches the PDF's "Order Account" (confirmed
        // against real data). The PDF also carries a separate "Customer Number", but the manifest
        // has no distinct column for it, so it isn't cross-checked here.
        if (!EqualsNormalized(row.CustomerReference, extractedFields.CustomerPO))
        {
            mismatches.Add(new FieldMismatch("CustomerPO", row.CustomerReference ?? string.Empty, extractedFields.CustomerPO ?? string.Empty));
        }

        if (!EqualsNormalized(row.CustomerAccount, extractedFields.OrderAccount))
        {
            mismatches.Add(new FieldMismatch("OrderAccount", row.CustomerAccount ?? string.Empty, extractedFields.OrderAccount ?? string.Empty));
        }

        if (!EqualsNormalized(row.TermsOfPayment, extractedFields.Terms))
        {
            mismatches.Add(new FieldMismatch("Terms", row.TermsOfPayment ?? string.Empty, extractedFields.Terms ?? string.Empty));
        }

        if (row.DueDate is not null && extractedFields.DueDate is not null && row.DueDate != extractedFields.DueDate)
        {
            mismatches.Add(new FieldMismatch(
                "DueDate",
                row.DueDate.Value.ToString("d", CultureInfo.InvariantCulture),
                extractedFields.DueDate.Value.ToString("d", CultureInfo.InvariantCulture)));
        }

        if (Math.Abs(row.InvoiceAmount - extractedFields.Total) > AmountTolerance)
        {
            mismatches.Add(new FieldMismatch(
                "Total",
                row.InvoiceAmount.ToString("F2", CultureInfo.InvariantCulture),
                extractedFields.Total.ToString("F2", CultureInfo.InvariantCulture)));
        }

        return new InvoiceFieldComparisonResult(row.Voucher, mismatches);
    }

    private static bool EqualsNormalized(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? ParseExcelDate(IXLCell cell) =>
        cell.TryGetValue(out DateTime value) ? DateOnly.FromDateTime(value) : null;
}
