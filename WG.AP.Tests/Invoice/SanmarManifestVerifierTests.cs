using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using WG.AP.Core.Abstractions;
using WG.AP.Invoice.Excel;
using WG.AP.Invoice.Models;

namespace WG.AP.Tests.Invoice;

public class SanmarManifestVerifierTests
{
    private static readonly string[] Headers =
    [
        "Due date", "Voucher", "Sales order", "RMA number", "Invoice amount",
        "Customer reference", "Date", "Terms of payment", "Customer account"
    ];

    private static byte[] BuildManifest(params (string Voucher, string SalesOrder, DateTime DueDate, decimal InvoiceAmount, DateTime InvoiceDate)[] rows)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Sheet1");

        for (var i = 0; i < Headers.Length; i++)
        {
            worksheet.Cell(1, i + 1).Value = Headers[i];
        }

        var rowIndex = 2;
        foreach (var row in rows)
        {
            worksheet.Cell(rowIndex, 1).Value = row.DueDate;
            worksheet.Cell(rowIndex, 2).Value = row.Voucher;
            worksheet.Cell(rowIndex, 3).Value = row.SalesOrder;
            worksheet.Cell(rowIndex, 4).Value = "R-04454725";
            worksheet.Cell(rowIndex, 5).Value = row.InvoiceAmount;
            worksheet.Cell(rowIndex, 6).Value = "58395-COBB-3797";
            worksheet.Cell(rowIndex, 7).Value = row.InvoiceDate;
            worksheet.Cell(rowIndex, 8).Value = "Net60";
            worksheet.Cell(rowIndex, 9).Value = "76274-0000";
            rowIndex++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static SanmarManifestVerifier CreateVerifier() => new(NullLogger<SanmarManifestVerifier>.Instance);

    [Fact]
    public void ReadManifest_ParsesRowsWithRealHeaders()
    {
        var bytes = BuildManifest(("INV-162393962", "SO-163263179", new DateTime(2026, 9, 19), 438.90m, new DateTime(2026, 7, 21)));

        var rows = CreateVerifier().ReadManifest(bytes);

        var row = Assert.Single(rows);
        Assert.Equal("INV-162393962", row.Voucher);
        Assert.Equal("SO-163263179", row.SalesOrder);
        Assert.Equal(new DateOnly(2026, 9, 19), row.DueDate);
        Assert.Equal(438.90m, row.InvoiceAmount);
        Assert.Equal(new DateOnly(2026, 7, 21), row.Date);
    }

    [Fact]
    public void ReadManifest_MissingExpectedColumn_Throws()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Sheet1");
        worksheet.Cell(1, 1).Value = "Voucher";
        worksheet.Cell(2, 1).Value = "INV-1";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        Assert.Throws<InvalidOperationException>(() => CreateVerifier().ReadManifest(stream.ToArray()));
    }

    [Fact]
    public void Reconcile_AllVouchersMatchAttachments_NoDiscrepancies()
    {
        var rows = new List<ManifestRow> { new("INV-1", "SO-1", null, null, 100m, null, null, null, null) };
        var attachments = new List<MailAttachmentSummary> { new("a1", "INV-1.pdf", 100, "application/pdf") };

        var result = CreateVerifier().Reconcile(rows, attachments);

        Assert.False(result.HasDiscrepancies);
        var pair = Assert.Single(result.MatchedPairs);
        Assert.Equal("INV-1", pair.Voucher);
    }

    [Fact]
    public void Reconcile_VoucherWithNoAttachedPdf_IsReportedAsMissing()
    {
        var rows = new List<ManifestRow> { new("INV-1", null, null, null, 100m, null, null, null, null) };
        var attachments = new List<MailAttachmentSummary>();

        var result = CreateVerifier().Reconcile(rows, attachments);

        Assert.True(result.HasDiscrepancies);
        Assert.Equal("INV-1", Assert.Single(result.MissingPdfVouchers));
    }

    [Fact]
    public void Reconcile_AttachmentWithNoMatchingVoucher_IsReportedAsUnexpected()
    {
        var rows = new List<ManifestRow>();
        var attachments = new List<MailAttachmentSummary> { new("a1", "INV-999.pdf", 100, "application/pdf") };

        var result = CreateVerifier().Reconcile(rows, attachments);

        Assert.True(result.HasDiscrepancies);
        Assert.Equal("INV-999", Assert.Single(result.UnexpectedAttachments));
    }

    [Fact]
    public void Reconcile_DuplicatePdfAttachments_AreReported()
    {
        var rows = new List<ManifestRow> { new("INV-1", null, null, null, 100m, null, null, null, null) };
        var attachments = new List<MailAttachmentSummary>
        {
            new("a1", "INV-1.pdf", 100, "application/pdf"),
            new("a2", "INV-1.pdf", 120, "application/pdf")
        };

        var result = CreateVerifier().Reconcile(rows, attachments);

        Assert.True(result.HasDiscrepancies);
        Assert.Equal("INV-1", Assert.Single(result.DuplicateAttachments));
        Assert.Single(result.MatchedPairs);
    }

    [Fact]
    public void Reconcile_DuplicateVoucherInManifest_IsReported()
    {
        var rows = new List<ManifestRow>
        {
            new("INV-1", null, null, null, 100m, null, null, null, null),
            new("INV-1", null, null, null, 200m, null, null, null, null)
        };
        var attachments = new List<MailAttachmentSummary> { new("a1", "INV-1.pdf", 100, "application/pdf") };

        var result = CreateVerifier().Reconcile(rows, attachments);

        Assert.True(result.HasDiscrepancies);
        Assert.Equal("INV-1", Assert.Single(result.DuplicateVouchers));
    }

    [Fact]
    public void CompareFields_MatchingFields_NoMismatches()
    {
        var row = new ManifestRow("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 438.90m, null, "PO-1", "Net60", "ACCT-1");
        var extracted = new InvoiceFields("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 438.90m, "SanMar", "PO-1", null, "ACCT-1", "Net60");

        var result = CreateVerifier().CompareFields(row, extracted);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void CompareFields_AmountWithinRoundingTolerance_IsNotAMismatch()
    {
        var row = new ManifestRow("INV-1", null, null, null, 438.90m, null, null, null, null);
        var extracted = new InvoiceFields("INV-1", null, null, null, 438.899m, null, null);

        var result = CreateVerifier().CompareFields(row, extracted);

        Assert.True(result.IsMatch);
    }

    [Theory]
    [InlineData("SalesOrder")]
    [InlineData("DueDate")]
    [InlineData("Total")]
    [InlineData("InvoiceNumber")]
    [InlineData("CustomerPO")]
    [InlineData("OrderAccount")]
    [InlineData("Terms")]
    public void CompareFields_DisagreeingField_IsReportedAsAMismatch(string fieldName)
    {
        var row = new ManifestRow("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 100m, null, null, null, null);
        var extracted = fieldName switch
        {
            "SalesOrder" => new InvoiceFields("INV-1", "SO-DIFFERENT", null, new DateOnly(2026, 9, 19), 100m, null, null),
            "DueDate" => new InvoiceFields("INV-1", "SO-1", null, new DateOnly(2026, 9, 20), 100m, null, null),
            "Total" => new InvoiceFields("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 999m, null, null),
            "InvoiceNumber" => new InvoiceFields("INV-DIFFERENT", "SO-1", null, new DateOnly(2026, 9, 19), 100m, null, null),
            "CustomerPO" => new InvoiceFields("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 100m, null, "PO-1"),
            "OrderAccount" => new InvoiceFields("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 100m, null, null, null, "ACCT-1"),
            "Terms" => new InvoiceFields("INV-1", "SO-1", null, new DateOnly(2026, 9, 19), 100m, null, null, null, null, "Net60"),
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName))
        };

        var result = CreateVerifier().CompareFields(row, extracted);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Mismatches, m => m.FieldName == fieldName);
    }
}
