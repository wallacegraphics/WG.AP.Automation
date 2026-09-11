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

    [Fact]
    public void BuildMessageErrorSummary_AllPdfsFail_WithIdenticalReasons_CollapsesToOneEntry()
    {
        // Mirrors a resent thread where every PDF is byte-identical to one already on file: 3 different
        // filenames, but the identical cause, so the persisted text (and, via it, the file log line)
        // should read as one sentence, not the same sentence repeated 3 times.
        var outcomes = new (string FileName, ApStatus MailStatus, string? Reason)[]
        {
            ("INV-1.pdf", ApStatus.MailNeedsReview, "'INV-1.pdf': Byte-identical PDF - identical content already received on \"Week 8.22-8.28\" (mail message 99)."),
            ("INV-2.pdf", ApStatus.MailNeedsReview, "'INV-2.pdf': Byte-identical PDF - identical content already received on \"Week 8.22-8.28\" (mail message 99)."),
            ("INV-3.pdf", ApStatus.MailNeedsReview, "'INV-3.pdf': Byte-identical PDF - identical content already received on \"Week 8.22-8.28\" (mail message 99)."),
        };

        var summary = APProcessor.BuildMessageErrorSummary(outcomes);

        Assert.Equal("3 PDF(s) failed: Byte-identical PDF - identical content already received on \"Week 8.22-8.28\" (mail message 99).", summary);
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

        var line = APProcessor.BuildDigestLine(message, result, Eastern);

        Assert.Contains("08/28/2026 12:05:58 -04:00", line);
        Assert.Contains("\"SanMar 76274 Week 8.22-8.28\" from ashleywhite@sanmar.com", line);
        Assert.Contains("80 attachment(s), 79 PDF(s), 79 processed successfully, 0 failed.", line);
    }

    [Fact]
    public void BuildDigestLine_WithNoReceivedTime_FallsBackInsteadOfThrowing()
    {
        var message = SanmarMessage(receivedDateTime: null);
        var result = new APProcessor.MessageOutcome(ApStatus.MailError, "boom", InvoiceCount: 1, AttachmentCount: 1, PdfCount: 1, SuccessCount: 0);

        var line = APProcessor.BuildDigestLine(message, result, Eastern);

        Assert.Contains("unknown time", line);
    }

    [Fact]
    public void BuildDigestLine_HtmlEncodesSubjectAndSender()
    {
        // The body is now BodyType.Html - a subject containing '<'/'&' must render as literal text,
        // not be interpreted as markup or break the surrounding tags.
        var message = new MailMessageSummary(
            "msg-1", new DateTimeOffset(2026, 8, 28, 16, 5, 58, TimeSpan.Zero),
            "a&b@sanmar.com", "<Weekly> Invoices & Credits", Attachments: []);
        var result = new APProcessor.MessageOutcome(ApStatus.MailProcessed, null, InvoiceCount: 1, AttachmentCount: 1, PdfCount: 1, SuccessCount: 1);

        var line = APProcessor.BuildDigestLine(message, result, Eastern);

        Assert.Contains("&lt;Weekly&gt; Invoices &amp; Credits", line);
        Assert.Contains("a&amp;b@sanmar.com", line);
        Assert.DoesNotContain("<Weekly>", line);
    }

    [Fact]
    public void BuildDigestErrorDetail_AllIdenticalReasons_CollapsesToOneLine()
    {
        // Mirrors a resent thread where every PDF is byte-identical to one already on file: 3 different
        // filenames, but the same duplicate-target message, so they should read as one line, not three.
        var pdfOutcomes = new[]
        {
            ("INV-1.pdf", ApStatus.MailNeedsReview, (string?)"'INV-1.pdf': Byte-identical PDF (same file content, e.g. a resend in the thread) - identical content already received on \"SanMar 76274 Week 8.22-8.28\" (mail message 99)."),
            ("INV-2.pdf", ApStatus.MailNeedsReview, (string?)"'INV-2.pdf': Byte-identical PDF (same file content, e.g. a resend in the thread) - identical content already received on \"SanMar 76274 Week 8.22-8.28\" (mail message 99)."),
            ("INV-3.pdf", ApStatus.MailNeedsReview, (string?)"'INV-3.pdf': Byte-identical PDF (same file content, e.g. a resend in the thread) - identical content already received on \"SanMar 76274 Week 8.22-8.28\" (mail message 99)."),
        };
        var result = new APProcessor.MessageOutcome(ApStatus.MailNeedsReview, "irrelevant", InvoiceCount: 3, AttachmentCount: 3, PdfCount: 3, SuccessCount: 0, pdfOutcomes);

        var lines = APProcessor.BuildDigestErrorDetail(result);

        var line = Assert.Single(lines);
        Assert.Equal(
            "&nbsp;&nbsp;&nbsp;&nbsp;3 PDF(s) failed: Byte-identical PDF (same file content, e.g. a resend in the thread) - identical content already received on &quot;SanMar 76274 Week 8.22-8.28&quot; (mail message 99).",
            line);
    }

    [Fact]
    public void BuildDigestErrorDetail_MixedReasons_ListsEachSeparately()
    {
        var pdfOutcomes = new[]
        {
            ("INV-1.pdf", ApStatus.MailError, (string?)"'INV-1.pdf' could not be parsed."),
            ("INV-2.pdf", ApStatus.MailNeedsReview, (string?)"'INV-2.pdf': missing InvoiceDate."),
            ("INV-3.pdf", ApStatus.MailProcessed, (string?)null),
        };
        var result = new APProcessor.MessageOutcome(ApStatus.MailError, "irrelevant", InvoiceCount: 3, AttachmentCount: 3, PdfCount: 3, SuccessCount: 1, pdfOutcomes);

        var lines = APProcessor.BuildDigestErrorDetail(result);

        Assert.Equal(
        [
            "&nbsp;&nbsp;&nbsp;&nbsp;&#39;INV-1.pdf&#39; could not be parsed.",
            "&nbsp;&nbsp;&nbsp;&nbsp;&#39;INV-2.pdf&#39;: missing InvoiceDate.",
            "&nbsp;&nbsp;&nbsp;&nbsp;1 other PDF(s) processed successfully: INV-3.pdf.",
        ], lines);
    }

    [Fact]
    public void BuildDigestErrorDetail_WithNoPdfOutcomes_FallsBackToTheMessageLevelReason()
    {
        var result = new APProcessor.MessageOutcome(ApStatus.MailNeedsReview, "Attempt cap of 3 reached.", InvoiceCount: 0, AttachmentCount: 1, PdfCount: 1, SuccessCount: 0);

        var lines = APProcessor.BuildDigestErrorDetail(result);

        Assert.Equal(["&nbsp;&nbsp;&nbsp;&nbsp;Attempt cap of 3 reached."], lines);
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

    private static readonly DateTimeOffset Day1 = new(2026, 7, 24, 13, 16, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 8, 5, 16, 58, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset Day3 = new(2026, 8, 28, 16, 5, 58, TimeSpan.Zero);

    [Fact]
    public void BuildDigestBody_IncludesIntroLineAndPutsTotalsBeforeTheGroups()
    {
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "line one.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>
        {
            [ApStatus.MailProcessed] = 2,
            [ApStatus.MailNeedsReview] = 1,
            [ApStatus.MailError] = 0,
            [ApStatus.MailSkipped] = 3,
        };

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        Assert.Contains("verify the", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 MailProcessed", body);
        Assert.Contains("1 MailNeedsReview", body);
        Assert.Contains("0 MailError", body);
        Assert.Contains("3 MailSkipped", body);
        Assert.True(body.IndexOf("Totals:", StringComparison.Ordinal) < body.IndexOf("=== Processed", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDigestBody_GroupsAreOrderedProcessedThenNeedsReviewThenErrors_RegardlessOfInputOrder()
    {
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Errors, Day1, "error line.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "processed line.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1, "review line.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        var processedIndex = body.IndexOf("=== Processed", StringComparison.Ordinal);
        var needsReviewIndex = body.IndexOf("=== NeedsReview", StringComparison.Ordinal);
        var errorsIndex = body.IndexOf("=== Errors", StringComparison.Ordinal);

        Assert.True(processedIndex < needsReviewIndex);
        Assert.True(needsReviewIndex < errorsIndex);
    }

    [Fact]
    public void BuildDigestBody_OrdersEntriesWithinAGroupByReceivedDateAscending_NullsLast()
    {
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day3, "latest.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, null, "unknown time.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "earliest.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day2, "middle.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        var earliest = body.IndexOf("earliest.", StringComparison.Ordinal);
        var middle = body.IndexOf("middle.", StringComparison.Ordinal);
        var latest = body.IndexOf("latest.", StringComparison.Ordinal);
        var unknown = body.IndexOf("unknown time.", StringComparison.Ordinal);

        Assert.True(earliest < middle);
        Assert.True(middle < latest);
        Assert.True(latest < unknown);
    }

    [Fact]
    public void BuildDigestBody_TwoEntriesThatRenderIdentically_CollapseToOne()
    {
        // Mirrors two dbo.MailMessage rows for what is actually one physical email (e.g. Graph
        // reissuing the message's id after a folder move) - received a millisecond apart, so the
        // summary line (formatted to whole seconds) and detail lines are byte-identical.
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1, "same message.", ["&nbsp;&nbsp;&nbsp;&nbsp;79 PDF(s) failed: same reason."]),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1.AddMilliseconds(1), "same message.", ["&nbsp;&nbsp;&nbsp;&nbsp;79 PDF(s) failed: same reason."]),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        Assert.Contains("=== NeedsReview (2) ===", body);
        Assert.Contains("Routed to NeedsReview (1).", body);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "same message\\."));
    }

    [Fact]
    public void BuildDigestBody_ProcessedGroup_HasNoBlankLinesAndOneTrailingRoutedLine()
    {
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "first message.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day2, "second message.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        Assert.Contains(
            "<p><b>=== Processed (2) ===</b></p>\n<p>first message.<br>\nsecond message.<br>\nRouted to Processed (2).</p>",
            body);
        Assert.DoesNotContain("first message. Routed to Processed.", body);
    }

    [Fact]
    public void BuildDigestBody_NeedsReviewGroup_SeparatesMessagesIntoTheirOwnParagraphAndKeepsPerMessageRouting()
    {
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1, "first message.", ["&nbsp;&nbsp;&nbsp;&nbsp;detail one."]),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day2, "second message.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        Assert.Contains(
            "<p><b>=== NeedsReview (2) ===</b></p>\n<p>first message. Routed to NeedsReview.<br>\n&nbsp;&nbsp;&nbsp;&nbsp;detail one.</p>\n<p>second message. Routed to NeedsReview.</p>\n<p>Routed to NeedsReview (2).</p>",
            body);
    }

    [Fact]
    public void BuildDigestBody_HeaderShowsRawRowCount_TrailingLineShowsDistinctRoutedCount()
    {
        // Two dbo.MailMessage rows for one physical email (e.g. Graph reissuing the message's id
        // after a move) collapse to one displayed block, but the header still states how many raw
        // rows are behind it - the two numbers are deliberately different and both visible.
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1, "same message.", []),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1.AddMilliseconds(1), "same message.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        Assert.Contains("<p><b>=== NeedsReview (2) ===</b></p>", body);
        Assert.Contains("<p>Routed to NeedsReview (1).</p>", body);
    }

    [Fact]
    public void BuildDigestBody_SectionHeaderIsBoldAndInItsOwnParagraph()
    {
        var entries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "a message.", []),
        };
        var outcomes = new Dictionary<ApStatus, int>();

        var body = APProcessor.BuildDigestBody(entries, outcomes);

        Assert.Contains("<p><b>=== Processed (1) ===</b></p>", body);
    }

    [Fact]
    public void BuildOutcomesSummary_NoCollapsing_MatchesTheOriginalPlainFormat()
    {
        var outcomes = new Dictionary<ApStatus, int>
        {
            [ApStatus.MailProcessed] = 4,
            [ApStatus.MailNeedsReview] = 1,
        };
        var digestEntries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "p1.", [], ApStatus.MailProcessed, "P1"),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day2, "p2.", [], ApStatus.MailProcessed, "P2"),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day3, "p3.", [], ApStatus.MailProcessed, "P3"),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "p4.", [], ApStatus.MailProcessed, "P4"),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, Day1, "n1.", [], ApStatus.MailNeedsReview, "N1"),
        };

        var summary = APProcessor.BuildOutcomesSummary(outcomes, digestEntries, Eastern);

        Assert.Equal("MailProcessed=4, MailNeedsReview=1", summary);
    }

    [Fact]
    public void BuildOutcomesSummary_WhenDuplicatesCollapse_AppendsRoutedCountAndIdentifiesTheMessage()
    {
        // 2 raw MailNeedsReview rows render identically (e.g. Graph reissuing a message's id after a
        // move), collapsing to 1 distinct message - the outcomes line must say both numbers and name
        // the one that was actually routed.
        var outcomes = new Dictionary<ApStatus, int>
        {
            [ApStatus.MailProcessed] = 4,
            [ApStatus.MailNeedsReview] = 2,
        };
        var receivedAt = new DateTimeOffset(2026, 8, 28, 16, 5, 58, TimeSpan.Zero);
        var digestEntries = new[]
        {
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "p1.", [], ApStatus.MailProcessed, "P1"),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day2, "p2.", [], ApStatus.MailProcessed, "P2"),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day3, "p3.", [], ApStatus.MailProcessed, "P3"),
            new APProcessor.DigestEntry(MailDestinationFolder.Processed, Day1, "p4.", [], ApStatus.MailProcessed, "P4"),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, receivedAt, "same message.", [], ApStatus.MailNeedsReview, "SanMar 76274 Week 8.22-8.28"),
            new APProcessor.DigestEntry(MailDestinationFolder.NeedsReview, receivedAt.AddMilliseconds(1), "same message.", [], ApStatus.MailNeedsReview, "SanMar 76274 Week 8.22-8.28"),
        };

        var summary = APProcessor.BuildOutcomesSummary(outcomes, digestEntries, Eastern);

        Assert.Equal(
            "MailProcessed=4, MailNeedsReview=2 (routed to MailNeedsReview=1: \"SanMar 76274 Week 8.22-8.28\" received 08/28/2026 12:05:58 -04:00)",
            summary);
    }
}
