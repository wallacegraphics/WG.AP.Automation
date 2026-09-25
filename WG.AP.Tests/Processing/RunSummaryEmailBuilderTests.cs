using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;
using WG.AP.Processor;

namespace WG.AP.Tests.Processing;

/// <summary>
/// Covers the one summary email per vendor email (<see cref="RunSummaryEmailBuilder"/>) and the rule that
/// decides which folder that email is moved to (<see cref="PaceInvoiceProcessor.DecideMailStatus"/>).
/// </summary>
public class RunSummaryEmailBuilderTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static readonly DateTimeOffset CompletedAt = new(2026, 9, 25, 11, 15, 0, TimeSpan.Zero);

    private static EmailRunSummary NewEmail(params PaceSummaryEntry[] entries)
    {
        var email = new EmailRunSummary(7)
        {
            Subject = "SanMar Invoices 09/25",
            SenderAddress = "invoices@sanmar.com",
            ReceivedOn = new DateTimeOffset(2026, 9, 25, 10, 2, 0, TimeSpan.Zero),
            Parse = new EmailParseResult(ApStatus.MailProcessed, 3, 3, ["a.pdf", "b.pdf", "c.pdf"], [])
        };
        email.PaceEntries.AddRange(entries);
        return email;
    }

    private static PaceSummaryEntry Entry(
        PaceSummaryKind kind,
        PaceBillSummary? summary,
        string statusCode = PaceSubmissionStatus.BillCreated,
        string? billId = "4521",
        string? batchId = "812",
        string? reason = null,
        string invoiceNumber = "123456789",
        long invoiceId = 1) =>
        new(kind, invoiceId, invoiceId, invoiceNumber, "12345", new DateOnly(2026, 9, 20), summary?.InvoiceTotal ?? 1234.56m,
            summary?.VendorId ?? "SANMAR", summary?.VendorName ?? "SanMar Corporation", statusCode, batchId, billId, reason, summary);

    private static PaceBillSummary Summary(PaceInvoiceCase invoiceCase, decimal invoiceTotal = 1234.56m, decimal? billTotal = 1234.56m) =>
        new()
        {
            Case = invoiceCase,
            VendorId = "SANMAR",
            VendorName = "SanMar Corporation",
            InvoiceTotal = invoiceTotal,
            BillTotal = billTotal,
            BillBatchDescription = "AUTO 9-25-26",
            ReceiptIdsAdded = [90011, 90012, 90013]
        };

    [Fact]
    public void BuildSubject_NamesTheEmail_ParsingAndPaceCounts_AndTheFolder()
    {
        var email = NewEmail(
            Entry(PaceSummaryKind.Success, Summary(PaceInvoiceCase.NotBilled)),
            Entry(PaceSummaryKind.NeedsReview, Summary(PaceInvoiceCase.PoNotFound)),
            Entry(PaceSummaryKind.Error, Summary(PaceInvoiceCase.NotBilled, billTotal: 980m), statusCode: PaceSubmissionStatus.Error));
        email.RoutedTo = MailDestinationFolder.Errors;

        var subject = RunSummaryEmailBuilder.BuildSubject(email, []);

        Assert.Equal("AP Automation - Summary: \"SanMar Invoices 09/25\" - 3 invoice(s) parsed | Pace: 1 succeeded, 1 needs review, 1 error | Routed to Errors", subject);
    }

    [Fact]
    public void BuildBody_OrdersParsing_ThenSuccess_ThenNeedsReview_ThenErrors_AndEndsWithTheRoutedLine()
    {
        var email = NewEmail(
            Entry(PaceSummaryKind.Error, Summary(PaceInvoiceCase.NotBilled, invoiceTotal: 1000m, billTotal: 980m), statusCode: PaceSubmissionStatus.Error, invoiceNumber: "E1", invoiceId: 1),
            Entry(PaceSummaryKind.NeedsReview, Summary(PaceInvoiceCase.PoNotFound), invoiceNumber: "R1", invoiceId: 2),
            Entry(PaceSummaryKind.Success, Summary(PaceInvoiceCase.NotBilled), invoiceNumber: "S1", invoiceId: 3));
        email.RoutedTo = MailDestinationFolder.Errors;

        var body = RunSummaryEmailBuilder.BuildBody(email, [], CompletedAt, Eastern);

        var parsing = body.IndexOf("=== 1. Invoice parsing ===", StringComparison.Ordinal);
        var success = body.IndexOf("=== 2. Pace – Success (1) ===", StringComparison.Ordinal);
        var review = body.IndexOf("=== 3. Pace – Bill created with default GL account/department – Needs review (1) ===", StringComparison.Ordinal);
        var errors = body.IndexOf("=== 4. Errors (1) ===", StringComparison.Ordinal);

        Assert.True(parsing >= 0 && parsing < success && success < review && review < errors);
        Assert.Contains("This is the summary of the AP Automation run completed 09/25/2026 07:15 AM", body);
        Assert.Contains("Email: \"SanMar Invoices 09/25\" from invoices@sanmar.com received 09/25/2026 06:02 AM", body);
        Assert.Contains("Totals: 3 attachment(s), 3 PDF(s) — 3 parsed, 0 not parsed.", body);
        Assert.Contains("Parsed successfully (3): a.pdf, b.pdf, c.pdf.", body);
        Assert.EndsWith("<p><b>Routed to: Errors</b></p>", body);
    }

    [Fact]
    public void BuildBody_LeavesOutEmptySections()
    {
        var email = NewEmail(Entry(PaceSummaryKind.Success, Summary(PaceInvoiceCase.NotBilled)));
        email.RoutedTo = MailDestinationFolder.Processed;

        var body = RunSummaryEmailBuilder.BuildBody(email, [], CompletedAt, Eastern);

        Assert.DoesNotContain("=== 3.", body);
        Assert.DoesNotContain("=== 4.", body);
        Assert.EndsWith("<p><b>Routed to: Processed</b></p>", body);
    }

    [Fact]
    public void BuildBody_EveryPaceEntryNamesTheInvoicePoVendorDateAndTotal()
    {
        var body = RunSummaryEmailBuilder.BuildBody(NewEmail(Entry(PaceSummaryKind.Success, Summary(PaceInvoiceCase.NotBilled))), [], CompletedAt, Eastern);

        Assert.Contains("<b>Invoice 123456789</b> | PO 12345 | Vendor SANMAR (SanMar Corporation)", body);
        Assert.Contains("Invoice date 09/20/2026 | Invoice total $1,234.56", body);
    }

    [Fact]
    public void DescribePaceOutcome_Case5Success_NamesTheBillBatchAndReceipts()
    {
        var lines = RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Success, Summary(PaceInvoiceCase.NotBilled)));

        Assert.Equal(
            [
                "Bill 4521 created in batch 812 (AUTO 9-25-26) with 3 PO receipt line(s) (receipts 90011, 90012, 90013).",
                "Bill total $1,234.56 matches the invoice total."
            ],
            lines);
    }

    [Fact]
    public void DescribePaceOutcome_Case4Success_NamesTheReceiptsThatWereAlreadyBilled()
    {
        var summary = Summary(PaceInvoiceCase.PartiallyBilled, 450m, 450m) with
        {
            ReceiptIdsAdded = [90021, 90022],
            ReceiptIdsAlreadyBilled = [90020],
            AlreadyBilledOnBillIds = ["4301"]
        };

        var lines = RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Success, summary, billId: "4522"));

        Assert.Equal(
            "PO partially billed. Bill 4522 created in batch 812 (AUTO 9-25-26) with 2 unbilled PO receipt line(s) (receipts 90021, 90022); 1 receipt(s) already billed (90020 on bill(s) 4301) was not added.",
            lines[0]);
    }

    [Fact]
    public void DescribePaceOutcome_Case3_MatchMismatchAndNoBill()
    {
        var match = Summary(PaceInvoiceCase.AllReceiptsBilled, 300m, 300m) with { ApprovedReceiptCount = 2, ReceiptIdsAdded = [], ExistingBillIds = [4400], ExistingBillBatchIds = ["790"] };
        var mismatch = match with { InvoiceTotal = 500m, BillTotal = 480m };
        var noBill = match with { BillTotal = null, ExistingBillIds = [], ExistingBillBatchIds = [], AlreadyBilledOnBillIds = ["4390", "4391"], ApprovedReceiptCount = 3 };

        Assert.Equal(
            "All 2 approved PO receipt(s) were already billed on Pace bill(s) 4400 (batch 790). No new bill was created.",
            RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Success, match, PaceSubmissionStatus.AlreadyEntered))[0]);
        Assert.Equal(
            "All 2 approved PO receipt(s) were already billed on Pace bill(s) 4400 (batch 790) with bill total $480.00; invoice total $500.00 — difference $20.00. No new bill was created.",
            Assert.Single(RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Error, mismatch, PaceSubmissionStatus.Error))));
        Assert.Equal(
            "All 3 approved PO receipt(s) are already billed (on bill(s) 4390, 4391), but no Pace bill exists for vendor SANMAR and invoice 123456789. No new bill was created.",
            Assert.Single(RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Error, noBill, PaceSubmissionStatus.Error))));
    }

    [Fact]
    public void DescribePaceOutcome_Cases1And2_SayWhyTheDefaultCodingWasUsed()
    {
        var case1 = Summary(PaceInvoiceCase.PoNotFound, 210m, 210m) with { GlAccount = 5100, GlDepartment = 20, ReceiptIdsAdded = [] };
        var case2 = case1 with { Case = PaceInvoiceCase.NoApprovedReceipts, ReceiptCount = 2 };

        Assert.Equal(
            [
                "PO 12345 was not found in Pace for this invoice.",
                "Bill 4523 created in batch 812 (AUTO 9-25-26) with 1 line of $210.00 coded to the vendor default GL account 5100 / department 20."
            ],
            RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.NeedsReview, case1, billId: "4523")));
        Assert.Equal(
            [
                "PO 12345 was found in Pace but has no approved receipts (2 receipt(s) found, none with status R).",
                "Bill 4524 created in batch 812 (AUTO 9-25-26) with 1 line of $210.00 coded to the PO vendor default GL account 5100 / department 20."
            ],
            RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.NeedsReview, case2, billId: "4524")));
    }

    [Fact]
    public void DescribePaceOutcome_Case5Mismatch_SaysTheBillWasLeftOpen()
    {
        var summary = Summary(PaceInvoiceCase.NotBilled, 1000m, 980m) with { ReceiptIdsAdded = [90031, 90032] };

        var lines = RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Error, summary, PaceSubmissionStatus.Error, billId: "4525"));

        Assert.Equal(
            [
                "Bill total does not match the invoice total.",
                "Bill 4525 created in batch 812 (AUTO 9-25-26) with 2 PO receipt line(s) (receipts 90031, 90032) totalling $980.00 — difference $20.00 (invoice higher). The bill was left Open in Pace; please correct it before posting."
            ],
            lines);
    }

    [Fact]
    public void DescribePaceOutcome_DryRun_SaysWouldBeCreated()
    {
        var summary = Summary(PaceInvoiceCase.NotBilled) with { DryRun = true };

        var lines = RunSummaryEmailBuilder.DescribePaceOutcome(Entry(PaceSummaryKind.Success, summary, PaceSubmissionStatus.DryRunPrepared, billId: null, batchId: null));

        Assert.StartsWith("Pace writes are disabled; a bill would be created in batch AUTO 9-25-26 with 3 PO receipt line(s)", lines[0]);
    }

    [Fact]
    public void DescribePaceOutcome_WithoutACase_ShowsTheStoredReasonWithoutItsInvoiceAndPoLeadIn()
    {
        var entry = Entry(PaceSummaryKind.Error, null, PaceSubmissionStatus.Error, reason: "Pace invoice 'INV-1' for PO '12345': GL accounting period 9 is closed.");

        Assert.Equal(["GL accounting period 9 is closed."], RunSummaryEmailBuilder.DescribePaceOutcome(entry));
    }

    [Fact]
    public void BuildBody_WhileWaitingForAPaceRetry_SaysTheEmailIsStillInTheInbox()
    {
        var email = NewEmail();
        email.AwaitingPace = true;
        email.WaitingForPace = new PaceRetryInfo(2, 5, new DateTime(2026, 9, 25, 12, 15, 0, DateTimeKind.Utc));

        var body = RunSummaryEmailBuilder.BuildBody(email, [], CompletedAt, Eastern);

        Assert.EndsWith("<p><b>Routed to: Not moved yet — still in Inbox, waiting for Pace (attempt 2 of 5, next try after 08:15 AM).</b></p>", body);
        Assert.EndsWith("Not moved yet", RunSummaryEmailBuilder.BuildSubject(email, []));
    }

    [Fact]
    public void BuildBody_WhenTheMoveFailed_NamesNoFolder()
    {
        var email = NewEmail(Entry(PaceSummaryKind.Error, null, PaceSubmissionStatus.Error, reason: "boom"));

        var body = RunSummaryEmailBuilder.BuildBody(email, [], CompletedAt, Eastern);

        Assert.DoesNotContain("Routed to:", body);
    }

    [Fact]
    public void BuildBody_ListsParsingProblemsAndStepFailuresUnderErrors()
    {
        var email = NewEmail();
        email.Parse = new EmailParseResult(ApStatus.MailNeedsReview, 2, 2, ["a.pdf"], ["'scan_001.pdf': missing InvoiceDate."]);

        var body = RunSummaryEmailBuilder.BuildBody(email, ["Pace processing failed: timeout"], CompletedAt, Eastern);

        Assert.Contains("Not parsed (1):<br>\n&nbsp;&nbsp;&nbsp;&nbsp;&#39;scan_001.pdf&#39;: missing InvoiceDate.", body);
        Assert.Contains("=== 4. Errors (1) ===", body);
        Assert.Contains("<p>Pace processing failed: timeout</p>", body);
        Assert.EndsWith("- processing failed", RunSummaryEmailBuilder.BuildSubject(email, ["Pace processing failed: timeout"]));
    }

    [Fact]
    public void BuildBody_HtmlEncodesValuesFromTheEmailAndPace()
    {
        var email = NewEmail(Entry(PaceSummaryKind.Error, null, PaceSubmissionStatus.Error, reason: "<script>x</script>"));
        email.Subject = "Invoice <1> & more";

        var body = RunSummaryEmailBuilder.BuildBody(email, [], CompletedAt, Eastern);

        Assert.Contains("Invoice &lt;1&gt; &amp; more", body);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", body);
        Assert.DoesNotContain("<script>", body);
    }

    [Fact]
    public void BuildFailureOnly_IsForARunThatFailedBeforeAnyEmailWasSummarized()
    {
        var (subject, body) = RunSummaryEmailBuilder.BuildFailureOnly(["Processing failed: Graph is down."], CompletedAt, Eastern);

        Assert.Equal("AP Automation - Summary: processing failed", subject);
        Assert.Contains("<p>Processing failed: Graph is down.</p>", body);
    }

    [Theory]
    [InlineData(1234.56, "$1,234.56")]
    [InlineData(-20, "-$20.00")]
    [InlineData(0, "$0.00")]
    public void Money_FormatsDollarsWithCents(double value, string expected) =>
        Assert.Equal(expected, RunSummaryEmailBuilder.Money((decimal)value));

    [Theory]
    [InlineData(ApStatus.MailProcessed, PaceSubmissionStatus.BillCreated, false, ApStatus.MailProcessed)]
    [InlineData(ApStatus.MailProcessed, PaceSubmissionStatus.AlreadyEntered, false, ApStatus.MailProcessed)]
    [InlineData(ApStatus.MailProcessed, PaceSubmissionStatus.BillCreated, true, ApStatus.MailNeedsReview)]
    [InlineData(ApStatus.MailProcessed, PaceSubmissionStatus.Error, false, ApStatus.MailError)]
    // Any error goes to Errors, even when the outcome also asked for review.
    [InlineData(ApStatus.MailProcessed, PaceSubmissionStatus.Error, true, ApStatus.MailError)]
    [InlineData(ApStatus.MailProcessed, PaceSubmissionStatus.PoNotReceived, false, ApStatus.MailError)]
    // A PDF of the same email that failed parsing still counts: the worst of both steps wins.
    [InlineData(ApStatus.MailNeedsReview, PaceSubmissionStatus.BillCreated, false, ApStatus.MailNeedsReview)]
    [InlineData(ApStatus.MailError, PaceSubmissionStatus.BillCreated, true, ApStatus.MailError)]
    public void DecideMailStatus_TakesTheWorstOfTheMailboxAndPaceOutcomes(ApStatus mailboxStatus, string paceStatus, bool requiresReview, ApStatus expected)
    {
        var submissions = new[]
        {
            new MailRoutingSubmission { InvoiceId = 1, PaceSubmissionId = 1, StatusCode = PaceSubmissionStatus.BillCreated },
            new MailRoutingSubmission { InvoiceId = 2, PaceSubmissionId = 2, StatusCode = paceStatus, RequiresReview = requiresReview }
        };

        Assert.Equal(expected, PaceInvoiceProcessor.DecideMailStatus(mailboxStatus, submissions));
    }

    [Fact]
    public void BuildMailReason_AppendsEachPaceReasonOnce_SoReroutingDoesNotRepeatIt()
    {
        var submissions = new[]
        {
            new MailRoutingSubmission { InvoiceId = 1, StatusCode = PaceSubmissionStatus.Error, ErrorMessage = "Bill total does not match." },
            new MailRoutingSubmission { InvoiceId = 2, StatusCode = PaceSubmissionStatus.BillCreated, ErrorMessage = "ignored: a success" }
        };

        var first = PaceInvoiceProcessor.BuildMailReason("'scan.pdf': missing InvoiceDate.", submissions);
        var again = PaceInvoiceProcessor.BuildMailReason(first, submissions);

        Assert.Equal("'scan.pdf': missing InvoiceDate. Bill total does not match.", first);
        Assert.Equal(first, again);
        Assert.Null(PaceInvoiceProcessor.BuildMailReason(null, [submissions[1]]));
    }

    [Fact]
    public void RunSummary_Email_ReturnsOneSummaryPerMailMessage_AndFillsInMissingDetails()
    {
        var runSummary = new RunSummary();

        var first = runSummary.Email(7, null, null, null);
        var second = runSummary.Email(7, "Subject", "sender@example.com", CompletedAt);

        Assert.Same(first, second);
        Assert.Equal("Subject", first.Subject);
        Assert.Single(runSummary.Emails);
    }
}
