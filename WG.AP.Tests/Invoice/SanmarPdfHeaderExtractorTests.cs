using WG.AP.Invoice.Pdf;

namespace WG.AP.Tests.Invoice;

public class SanmarPdfHeaderExtractorTests
{
    // Verbatim (modulo line wrapping) output of `document.GetPages().SelectMany(p => p.GetWords())`
    // joined with spaces, captured from the real WG.AP.Tests/Invoice/Fixtures.local/INV-162117994.pdf
    // fixture - real invoices interleave unrelated header content between some label/value pairs.
    private const string RealInvoice162117994 =
        "INVOICE Invoice Number: INV-162117994 Sales Order: SO-162958735 Invoice Date: 7/10/2026 " +
        "Customer Sales & Service Credit Department Due Date: 9/8/2026 Toll Free: (800) 346-3369 " +
        "Toll Free: (800) 426-6399 Customer Number: 76274-0000 Email: creditinquiries@sanmar.com " +
        "www.sanmar.com Terms: Net60 Customer PO: 58195-AMC-5263 Order Account: 76274-0000 Office ID: " +
        "To make a payment, visit your My SanMar account dashboard on sanmar.com and click 'View and Pay Invoices'. " +
        "MAILING: WALLACE GRAPHICS INC Wallace Graphics SHIP TO: 11455 Lakefield Dr Attn to: 58195-AMC-5263 " +
        "Total Sales subtotal 1.00 79.27 cases amount Sales tax Shipping, handling & other fees " +
        "Cincinnati, OH Dallas, TX Richmond, VA Total 79.27 Starting in June, you must have a valid exemption.";

    // Verbatim capture from INV-162393962.pdf - same template, but Customer PO's value is multi-word
    // free text rather than a compact code.
    private const string RealInvoice162393962 =
        "INVOICE Invoice Number: INV-162393962 Sales Order: SO-163263179 Invoice Date: 7/21/2026 " +
        "Customer Sales & Service Credit Department Due Date: 9/19/2026 Toll Free: (800) 346-3369 " +
        "Toll Free: (800) 426-6399 Customer Number: 76274-0000 Email: creditinquiries@sanmar.com " +
        "www.sanmar.com Terms: Net60 Customer PO: 2702-2225 Cobb Nutri Team Order Account: 76274-0000 " +
        "Office ID: To make a payment, visit your My SanMar account dashboard on sanmar.com. " +
        "Total Sales subtotal 2.00 438.90 cases amount Sales tax Shipping, handling & other fees " +
        "Cincinnati, OH Dallas, TX Richmond, VA Total 438.90 Starting in June, you must have a valid exemption.";

    [Fact]
    public void TryExtract_RealInvoiceWithSingleTokenCustomerPO_ReturnsExpectedFields()
    {
        var fields = SanmarPdfHeaderExtractor.TryExtract(RealInvoice162117994);

        Assert.NotNull(fields);
        Assert.Equal("INV-162117994", fields!.InvoiceNumber);
        Assert.Equal("SO-162958735", fields.SalesOrder);
        Assert.Equal(new DateOnly(2026, 7, 10), fields.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 9, 8), fields.DueDate);
        Assert.Equal("76274-0000", fields.CustomerNumber);
        Assert.Equal("Net60", fields.Terms);
        Assert.Equal("58195-AMC-5263", fields.CustomerPO);
        Assert.Equal("76274-0000", fields.OrderAccount);
        Assert.Equal("SanMar", fields.ClientName);
        Assert.Equal(79.27m, fields.Total);
        Assert.Equal(string.Empty, fields.RawText);
    }

    [Fact]
    public void TryExtract_RealInvoiceWithMultiWordCustomerPO_ReturnsExpectedFields()
    {
        var fields = SanmarPdfHeaderExtractor.TryExtract(RealInvoice162393962);

        Assert.NotNull(fields);
        Assert.Equal("INV-162393962", fields!.InvoiceNumber);
        Assert.Equal("2702-2225 Cobb Nutri Team", fields.CustomerPO);
        Assert.Equal("76274-0000", fields.OrderAccount);
        Assert.Equal(438.90m, fields.Total);
    }

    [Fact]
    public void TryExtract_BlankCustomerPO_ReturnsNullCustomerPO()
    {
        var text = "Invoice Number: INV-1 Sales Order: SO-1 Invoice Date: 1/1/2026 Due Date: 2/1/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: Order Account: ORDERACCT-1 Office ID: " +
            "Total 79.27";

        var fields = SanmarPdfHeaderExtractor.TryExtract(text);

        Assert.NotNull(fields);
        Assert.Null(fields!.CustomerPO);
        Assert.Equal("ORDERACCT-1", fields.OrderAccount);
    }

    [Fact]
    public void TryExtract_UnrecognizedLabelText_ReturnsNull()
    {
        const string text = "Vendor XYZ Invoice # 123 Date: 1/1/2026 Amount Due: 79.27 Please pay promptly.";

        Assert.Null(SanmarPdfHeaderExtractor.TryExtract(text));
    }

    [Fact]
    public void TryExtract_MissingSalesOrderLabel_ReturnsNull()
    {
        var text = "Invoice Number: INV-1 Invoice Date: 1/1/2026 Due Date: 2/1/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: PO-1 Order Account: ORDERACCT-1 " +
            "Total 79.27";

        Assert.Null(SanmarPdfHeaderExtractor.TryExtract(text));
    }

    [Fact]
    public void TryExtract_InvoiceDateInvalid_ReturnsNull()
    {
        var text = "Invoice Number: INV-1 Sales Order: SO-1 Invoice Date: 13/45/2026 Due Date: 2/1/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: PO-1 Order Account: ORDERACCT-1 " +
            "Total 79.27";

        Assert.Null(SanmarPdfHeaderExtractor.TryExtract(text));
    }

    [Fact]
    public void TryExtract_DueDateInvalid_ReturnsNull()
    {
        var text = "Invoice Number: INV-1 Sales Order: SO-1 Invoice Date: 1/1/2026 Due Date: 13/45/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: PO-1 Order Account: ORDERACCT-1 " +
            "Total 79.27";

        Assert.Null(SanmarPdfHeaderExtractor.TryExtract(text));
    }

    [Fact]
    public void TryExtract_NoTotalFound_ReturnsNull()
    {
        var text = "Invoice Number: INV-1 Sales Order: SO-1 Invoice Date: 1/1/2026 Due Date: 2/1/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: PO-1 Order Account: ORDERACCT-1";

        Assert.Null(SanmarPdfHeaderExtractor.TryExtract(text));
    }

    [Fact]
    public void TryExtract_TotalFollowedByCases_SkipsToRealTotalElsewhere_ReturnsCorrectTotal()
    {
        var text = "Invoice Number: INV-1 Sales Order: SO-1 Invoice Date: 1/1/2026 Due Date: 2/1/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: PO-1 Order Account: ORDERACCT-1 " +
            "Total 5.00 cases Sales subtotal amount 79.27 Total 79.27";

        var fields = SanmarPdfHeaderExtractor.TryExtract(text);

        Assert.NotNull(fields);
        Assert.Equal(79.27m, fields!.Total);
    }

    [Fact]
    public void TryExtract_SubtotalLinePresent_DoesNotMatchAsTotal()
    {
        var text = "Invoice Number: INV-1 Sales Order: SO-1 Invoice Date: 1/1/2026 Due Date: 2/1/2026 " +
            "Customer Number: ACCT-1 Terms: Net30 Customer PO: PO-1 Order Account: ORDERACCT-1 " +
            "Subtotal 500.00 Total 79.27";

        var fields = SanmarPdfHeaderExtractor.TryExtract(text);

        Assert.NotNull(fields);
        Assert.Equal(79.27m, fields!.Total);
    }

    [Fact]
    public void TryExtract_EmptyText_ReturnsNull()
    {
        Assert.Null(SanmarPdfHeaderExtractor.TryExtract(""));
        Assert.Null(SanmarPdfHeaderExtractor.TryExtract("   "));
    }
}
