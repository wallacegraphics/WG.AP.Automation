using WG.AP.DataAccess;
using WG.AP.Invoice.Models;
using WG.AP.Processor;

namespace WG.AP.Tests.Processing;

/// <summary>
/// Covers <see cref="APProcessor"/>'s classification rules.
/// </summary>
/// <remarks>
/// These replace the manifest-shaped tests that went with the Excel cross-check. The routing tree
/// they exercised — reconcile vouchers against filenames, compare Excel fields to PDF fields — no
/// longer exists; what decides an outcome now is whether the five required fields came out of the PDF.
/// <para>
/// They target <see cref="APProcessor.Classify"/> directly rather than driving
/// <c>ProcessInvoicesAsync</c>, because that method now needs a real database: recording and claiming
/// a message, resolving a client, loading a prompt and writing an invoice are all SQL, and faking six
/// repositories to assert on a status would be testing the fakes. The end-to-end path is verified
/// against a real database instead (see the run-twice check in the verification steps), which is the
/// only way the guarantee that actually matters — that a message is never claimed twice — can be
/// tested at all, since it is enforced by a unique index rather than by this code.
/// </para>
/// </remarks>
public class ProcessorTests
{
    private static readonly ClientResolution KnownClient = new(ClientId: 1, InvoiceFormatId: 1, ExtractorKey: "SANMAR_PDF_HEADER_V1");

    private static InvoiceFields Complete(
        string? invoiceNumber = "INV-162393962",
        DateOnly? invoiceDate = null,
        decimal total = 1234.56m,
        string? customerPO = "PO-4455") =>
        new(
            invoiceNumber!,
            SalesOrder: "SO-1",
            invoiceDate ?? new DateOnly(2026, 9, 1),
            DueDate: new DateOnly(2026, 11, 1),
            total,
            ClientName: "SanMar",
            customerPO,
            CustomerNumber: "C-1",
            OrderAccount: "A-1",
            Terms: "Net60",
            RawText: "raw");

    [Fact]
    public void Classify_WithEveryRequiredField_IsExtractedAndProcessed()
    {
        var (invoiceStatus, mailStatus, reason) = APProcessor.Classify(KnownClient, Complete(), "INV-1.pdf");

        Assert.Equal(ApStatus.InvoiceExtracted, invoiceStatus);
        Assert.Equal(ApStatus.MailProcessed, mailStatus);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-500)]
    public void Classify_WithAZeroOrNegativeTotal_IsExtractedAndProcessed(decimal total)
    {
        // Not an error, deliberately: a zero or negative total (e.g. a credit memo) is valid,
        // correctly-extracted data - it must never be treated as a review/error signal or converted
        // to a positive value anywhere in this pipeline.
        var (invoiceStatus, mailStatus, reason) = APProcessor.Classify(KnownClient, Complete(total: total), "INV-1.pdf");

        Assert.Equal(ApStatus.InvoiceExtracted, invoiceStatus);
        Assert.Equal(ApStatus.MailProcessed, mailStatus);
        Assert.Null(reason);
    }

    [Fact]
    public void Classify_WithAMissingInvoiceNumber_NeedsReview()
    {
        var (invoiceStatus, mailStatus, reason) = APProcessor.Classify(KnownClient, Complete(invoiceNumber: ""), "INV-1.pdf");

        Assert.Equal(ApStatus.InvoiceNeedsReview, invoiceStatus);
        Assert.Equal(ApStatus.MailNeedsReview, mailStatus);
        Assert.Contains(nameof(InvoiceFields.InvoiceNumber), reason!);
    }

    [Fact]
    public void Classify_WithAMissingInvoiceDate_NeedsReview()
    {
        var fields = Complete() with { InvoiceDate = null };
        var (invoiceStatus, _, reason) = APProcessor.Classify(KnownClient, fields, "INV-1.pdf");

        Assert.Equal(ApStatus.InvoiceNeedsReview, invoiceStatus);
        Assert.Contains(nameof(InvoiceFields.InvoiceDate), reason!);
    }

    [Fact]
    public void Classify_WithAMissingCustomerPO_NeedsReview()
    {
        var (invoiceStatus, _, reason) = APProcessor.Classify(KnownClient, Complete(customerPO: null), "INV-1.pdf");

        Assert.Equal(ApStatus.InvoiceNeedsReview, invoiceStatus);
        Assert.Contains(nameof(InvoiceFields.CustomerPO), reason!);
    }

    [Fact]
    public void Classify_WithAnUnresolvedClient_NeedsReview()
    {
        // Unknown-client invoices are also excluded from UQ_Invoice_ClientNumber, so two of them can
        // share a number without one being rejected as a false duplicate. Review is where a human sees
        // both.
        var (invoiceStatus, mailStatus, reason) = APProcessor.Classify(ClientResolution.Unknown, Complete(), "INV-1.pdf");

        Assert.Equal(ApStatus.InvoiceNeedsReview, invoiceStatus);
        Assert.Equal(ApStatus.MailNeedsReview, mailStatus);
        Assert.Contains("Client", reason!);
    }

    [Fact]
    public void Classify_WithSeveralMissingFields_NamesThemAll()
    {
        var fields = Complete(invoiceNumber: "", customerPO: " ") with { InvoiceDate = null };
        var (_, _, reason) = APProcessor.Classify(ClientResolution.Unknown, fields, "INV-1.pdf");

        Assert.Contains("Client", reason!);
        Assert.Contains(nameof(InvoiceFields.InvoiceNumber), reason!);
        Assert.Contains(nameof(InvoiceFields.InvoiceDate), reason!);
        Assert.Contains(nameof(InvoiceFields.CustomerPO), reason!);
    }

    [Theory]
    [InlineData(ApStatus.MailError, ApStatus.MailNeedsReview)]
    [InlineData(ApStatus.MailError, ApStatus.MailProcessed)]
    [InlineData(ApStatus.MailNeedsReview, ApStatus.MailProcessed)]
    public void Severity_RanksTheWorseOutcomeHigher(ApStatus worse, ApStatus better)
    {
        // One email with several PDFs takes the worst verdict, so this ordering is what stops a single
        // clean invoice filing an email that also contained an unparseable one.
        Assert.True(APProcessor.Severity(worse) > APProcessor.Severity(better));
    }
}
