using WG.AP.Core.Abstractions;
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

    [Fact]
    public void BuildMessageErrorSummary_AllPdfsFail_ListsEachReason()
    {
        var outcomes = new (string FileName, ApStatus MailStatus, string? Reason)[]
        {
            ("INV-1.pdf", ApStatus.MailError, "'INV-1.pdf' could not be parsed."),
            ("INV-2.pdf", ApStatus.MailNeedsReview, "'INV-2.pdf': missing InvoiceDate."),
        };

        var summary = APProcessor.BuildMessageErrorSummary(outcomes);

        Assert.Contains("'INV-1.pdf' could not be parsed.", summary);
        Assert.Contains("'INV-2.pdf': missing InvoiceDate.", summary);
        Assert.DoesNotContain("processed successfully", summary);
    }

    [Fact]
    public void BuildMessageErrorSummary_MixedOutcome_ListsFailuresAndNamesSuccesses()
    {
        var outcomes = new (string FileName, ApStatus MailStatus, string? Reason)[]
        {
            ("Bad.pdf", ApStatus.MailError, "'Bad.pdf' could not be parsed."),
            ("Good1.pdf", ApStatus.MailProcessed, null),
            ("Good2.pdf", ApStatus.MailProcessed, null),
        };

        var summary = APProcessor.BuildMessageErrorSummary(outcomes);

        Assert.Contains("'Bad.pdf' could not be parsed.", summary);
        Assert.Contains("2 other PDF(s) processed successfully:", summary);
        Assert.Contains("Good1.pdf", summary);
        Assert.Contains("Good2.pdf", summary);
    }

    [Fact]
    public void BuildMessageErrorSummary_SinglePdfFails_MatchesReasonExactly()
    {
        var outcomes = new (string FileName, ApStatus MailStatus, string? Reason)[]
        {
            ("INV-1.pdf", ApStatus.MailError, "'INV-1.pdf' could not be parsed."),
        };

        var summary = APProcessor.BuildMessageErrorSummary(outcomes);

        Assert.Equal("'INV-1.pdf' could not be parsed.", summary);
    }

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(
    OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static MailMessageSummary SanmarMessage(DateTimeOffset? receivedDateTime) =>
        new("msg-1", receivedDateTime, "ashleywhite@sanmar.com", "SanMar 76274 Week 8.22-8.28", Attachments: []);

    [Fact]
    public void BuildDigestLine_ConvertsUtcReceivedTimeToTheConfiguredZone()
    {
        // 16:05:58 UTC on Aug 28 falls in Eastern daylight time (UTC-4), so it must read as 12:05:58 -04:00,
        // not the raw UTC value Graph returns.
        var message = SanmarMessage(new DateTimeOffset(2026, 8, 28, 16, 5, 58, TimeSpan.Zero));
        var result = new APProcessor.MessageOutcome(ApStatus.MailProcessed, null, InvoiceCount: 79, AttachmentCount: 80, PdfCount: 79, SuccessCount: 79);

        var line = APProcessor.BuildDigestLine(message, result, MailDestinationFolder.Processed, Eastern);

        Assert.Contains("08/28/2026 12:05:58 -04:00", line);
        Assert.Contains("\"SanMar 76274 Week 8.22-8.28\" from ashleywhite@sanmar.com", line);
        Assert.Contains("80 attachment(s), 79 PDF(s), 79 processed successfully, 0 failed.", line);
        Assert.Contains("Routed to Processed.", line);
    }

    [Fact]
    public void BuildDigestLine_WithNoReceivedTime_FallsBackInsteadOfThrowing()
    {
        var message = SanmarMessage(receivedDateTime: null);
        var result = new APProcessor.MessageOutcome(ApStatus.MailError, "boom", InvoiceCount: 1, AttachmentCount: 1, PdfCount: 1, SuccessCount: 0);

        var line = APProcessor.BuildDigestLine(message, result, MailDestinationFolder.Errors, Eastern);

        Assert.Contains("unknown time", line);
        Assert.Contains("Routed to Errors.", line);
    }

    [Fact]
    public void BuildErrorLogLine_IncludesStatusMessageIdentityAndReason()
    {
        var row = new MailMessageErrorLogRow(
            StatusId: (int)ApStatus.MailNeedsReview,
            SenderAddress: "ashleywhite@sanmar.com",
            Subject: "SanMar 76274 Week 7.18-7.24",
            ReceivedOn: new DateTimeOffset(2026, 7, 24, 17, 16, 10, TimeSpan.Zero),
            ErrorMessage: "'76274.pdf': missing InvoiceDate.");

        var line = APProcessor.BuildErrorLogLine(row);

        Assert.Contains("MailNeedsReview:", line);
        Assert.Contains("\"SanMar 76274 Week 7.18-7.24\" from ashleywhite@sanmar.com", line);
        Assert.Contains("received 07/24/2026 17:16:10 +00:00", line);
        Assert.Contains("Reason: '76274.pdf': missing InvoiceDate.", line);
    }

    [Fact]
    public void BuildErrorLogLine_WithNoReceivedTime_FallsBackInsteadOfThrowing()
    {
        var row = new MailMessageErrorLogRow(
            StatusId: (int)ApStatus.MailError,
            SenderAddress: null,
            Subject: null,
            ReceivedOn: null,
            ErrorMessage: "boom");

        var line = APProcessor.BuildErrorLogLine(row);

        Assert.Contains("MailError:", line);
        Assert.Contains("\"(no subject)\" from unknown", line);
        Assert.Contains("received unknown time", line);
        Assert.Contains("Reason: boom", line);
    }

    [Fact]
    public void BuildDigestBody_IncludesIntroLinesAndTotals()
    {
        var digestLines = new[] { "line one.", "line two." };
        var outcomes = new Dictionary<ApStatus, int>
        {
            [ApStatus.MailProcessed] = 2,
            [ApStatus.MailNeedsReview] = 1,
            [ApStatus.MailError] = 0,
            [ApStatus.MailSkipped] = 3,
        };

        var body = APProcessor.BuildDigestBody(digestLines, outcomes);

        Assert.Contains("verify the", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line one.", body);
        Assert.Contains("line two.", body);
        Assert.Contains("2 MailProcessed", body);
        Assert.Contains("1 MailNeedsReview", body);
        Assert.Contains("0 MailError", body);
        Assert.Contains("3 MailSkipped", body);
    }
}
