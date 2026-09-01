using System.Globalization;
using ClosedXML.Excel;
using WG.AP.Invoice.Models;

namespace WG.AP.Tests.Invoice;

/// <summary>
/// Reads a SanMar Excel manifest, for tests only.
/// </summary>
/// <remarks>
/// The production manifest logic is gone — nothing reads Excel any more. This exists because
/// <see cref="PdfInvoiceFieldExtractorRealDataTests"/> validates extraction against the client's own
/// numbers rather than against hand-written expected values, and the manifest is where those numbers
/// come from. Deleting the reader outright would have quietly removed the only real-data check in the
/// suite at the same moment the cross-check was removed from production, which is the wrong direction.
/// <para>
/// It is deliberately smaller than the class it replaces: only reading and comparing, no
/// reconciliation. Reconciliation existed to decide routing, which is now the extractor's business and
/// none of this test's.
/// </para>
/// </remarks>
internal static class TestManifestReader
{
    private const decimal AmountTolerance = 0.01m;

    private static readonly string[] RequiredColumns =
    [
        "Voucher", "Sales order", "Due date", "Invoice amount", "RMA number",
        "Customer reference", "Date", "Terms of payment", "Customer account"
    ];

    /// <summary>One data row of the manifest: what the client says it billed.</summary>
    /// <remarks>Column names mirror the real workbook header row.</remarks>
    internal sealed record Row(
        string Voucher,
        string? SalesOrder,
        DateOnly? Date,
        DateOnly? DueDate,
        decimal InvoiceAmount,
        string? RmaNumber,
        string? CustomerReference,
        string? TermsOfPayment,
        string? CustomerAccount);

    internal sealed record Mismatch(string FieldName, string ManifestValue, string PdfValue);

    internal static IReadOnlyList<Row> ReadManifest(byte[] excelBytes)
    {
        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First();
        var range = worksheet.RangeUsed() ?? throw new InvalidOperationException("Manifest worksheet is empty.");

        var columnIndexByName = range.FirstRow().Cells()
            .ToDictionary(cell => cell.GetString().Trim(), cell => cell.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

        foreach (var required in RequiredColumns)
        {
            if (!columnIndexByName.ContainsKey(required))
            {
                throw new InvalidOperationException($"Manifest is missing expected column '{required}'.");
            }
        }

        var rows = new List<Row>();

        foreach (var row in range.RowsUsed().Skip(1))
        {
            var voucher = row.Cell(columnIndexByName["Voucher"]).GetString().Trim();

            if (string.IsNullOrWhiteSpace(voucher))
            {
                continue;
            }

            rows.Add(new Row(
                voucher,
                NullIfEmpty(row.Cell(columnIndexByName["Sales order"]).GetString()),
                ParseExcelDate(row.Cell(columnIndexByName["Date"])),
                ParseExcelDate(row.Cell(columnIndexByName["Due date"])),
                row.Cell(columnIndexByName["Invoice amount"]).GetValue<decimal>(),
                NullIfEmpty(row.Cell(columnIndexByName["RMA number"]).GetString()),
                NullIfEmpty(row.Cell(columnIndexByName["Customer reference"]).GetString()),
                NullIfEmpty(row.Cell(columnIndexByName["Terms of payment"]).GetString()),
                NullIfEmpty(row.Cell(columnIndexByName["Customer account"]).GetString())));
        }

        return rows;
    }

    /// <summary>
    /// Compares one manifest row against the fields extracted from its PDF.
    /// </summary>
    /// <remarks>
    /// The field mapping was confirmed against real data: the manifest's "Customer reference" is the
    /// PDF's Customer PO, and its "Customer account" is the PDF's Order Account. The PDF also carries a
    /// separate Customer Number, which the manifest has no column for and which is therefore not
    /// cross-checked. Date and RMA number are deliberately not compared either.
    /// </remarks>
    internal static IReadOnlyList<Mismatch> Compare(Row row, InvoiceFields extracted)
    {
        var mismatches = new List<Mismatch>();

        if (!string.Equals(row.Voucher, extracted.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new Mismatch("InvoiceNumber", row.Voucher, extracted.InvoiceNumber));
        }

        AddIfDifferent(mismatches, "SalesOrder", row.SalesOrder, extracted.SalesOrder);
        AddIfDifferent(mismatches, "CustomerPO", row.CustomerReference, extracted.CustomerPO);
        AddIfDifferent(mismatches, "OrderAccount", row.CustomerAccount, extracted.OrderAccount);
        AddIfDifferent(mismatches, "Terms", row.TermsOfPayment, extracted.Terms);

        // Only compared when both sides have a value: a manifest with no due date is not evidence that
        // the PDF read one wrongly.
        if (row.DueDate is not null && extracted.DueDate is not null && row.DueDate != extracted.DueDate)
        {
            mismatches.Add(new Mismatch(
                "DueDate",
                row.DueDate.Value.ToString("d", CultureInfo.InvariantCulture),
                extracted.DueDate.Value.ToString("d", CultureInfo.InvariantCulture)));
        }

        if (Math.Abs(row.InvoiceAmount - extracted.Total) > AmountTolerance)
        {
            mismatches.Add(new Mismatch(
                "Total",
                row.InvoiceAmount.ToString("F2", CultureInfo.InvariantCulture),
                extracted.Total.ToString("F2", CultureInfo.InvariantCulture)));
        }

        return mismatches;
    }

    private static void AddIfDifferent(List<Mismatch> mismatches, string fieldName, string? manifestValue, string? pdfValue)
    {
        if (!string.Equals(manifestValue?.Trim(), pdfValue?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new Mismatch(fieldName, manifestValue ?? string.Empty, pdfValue ?? string.Empty));
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? ParseExcelDate(IXLCell cell) =>
        cell.TryGetValue(out DateTime value) ? DateOnly.FromDateTime(value) : null;
}
