using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;

namespace WG.AP.Processor;

/// <summary>
/// Builds the one summary email per vendor email: parsing first, then Pace successes, then bills that need
/// review, then every error, and last the folder the email was routed to.
/// </summary>
internal static class RunSummaryEmailBuilder
{
    internal const string SubjectPrefix = "AP Automation - Summary";

    private const string Indent = "&nbsp;&nbsp;&nbsp;&nbsp;";

    // A stored Pace error starts by naming the invoice and PO; the summary line already names both.
    private static readonly Regex ReasonInvoiceAndPoPrefixRegex = new(@"^Pace invoice '.*?' for PO '.*?':\s*", RegexOptions.Compiled);

    internal static string BuildSubject(EmailRunSummary email, IReadOnlyList<string> failures)
    {
        var parts = new List<string>();

        if (email.Parse is { } parse)
        {
            var notParsed = parse.PdfCount - parse.ParsedFiles.Count;
            parts.Add(notParsed > 0
                ? $"{parse.ParsedFiles.Count} invoice(s) parsed, {notParsed} not parsed"
                : $"{parse.ParsedFiles.Count} invoice(s) parsed");
        }

        if (email.PaceEntries.Count > 0)
        {
            parts.Add($"Pace: {BuildPaceCounts(email.PaceEntries)}");
        }

        if (email.RoutedTo is { } routedTo)
        {
            parts.Add($"Routed to {routedTo}");
        }
        else if (email.AwaitingPace)
        {
            parts.Add("Not moved yet");
        }

        var subject = $"{SubjectPrefix}: \"{OneLine(email.Subject ?? "(no subject)")}\"";

        if (parts.Count > 0)
        {
            subject += $" - {string.Join(" | ", parts)}";
        }

        return failures.Count > 0 ? $"{subject} - processing failed" : subject;
    }

    internal static string BuildBody(EmailRunSummary email, IReadOnlyList<string> failures, DateTimeOffset completedAt, TimeZoneInfo timeZone)
    {
        var blocks = new List<string>
        {
            $"<p>This is the summary of the AP Automation run completed {Html(FormatDateTime(completedAt, timeZone))} ({Html(timeZone.StandardName)}).</p>",
            BuildHeader(email, timeZone)
        };

        if (email.Parse is { } parse)
        {
            blocks.Add(BuildParsingSection(parse));
        }

        var successes = Ordered(email.PaceEntries, PaceSummaryKind.Success);
        var needsReview = Ordered(email.PaceEntries, PaceSummaryKind.NeedsReview);
        var errors = Ordered(email.PaceEntries, PaceSummaryKind.Error);

        if (successes.Count > 0)
        {
            blocks.Add($"<p><b>=== 2. Pace – Success ({successes.Count}) ===</b></p>");
            blocks.AddRange(successes.Select(BuildPaceEntryBlock));
        }

        if (needsReview.Count > 0)
        {
            blocks.Add($"<p><b>=== 3. Pace – Bill created with default GL account/department – Needs review ({needsReview.Count}) ===</b></p>");
            blocks.Add(needsReview.Count == 1
                ? "<p>Please verify the coding and the PO number on this bill.</p>"
                : "<p>Please verify the coding and the PO number on these bills.</p>");
            blocks.AddRange(needsReview.Select(BuildPaceEntryBlock));
        }

        if (errors.Count > 0 || failures.Count > 0)
        {
            blocks.Add($"<p><b>=== 4. Errors ({errors.Count + failures.Count}) ===</b></p>");
            blocks.AddRange(errors.Select(BuildPaceEntryBlock));
            blocks.AddRange(failures.Select(failure => $"<p>{Html(failure)}</p>"));
        }

        if (BuildRoutedLine(email, timeZone) is { } routedLine)
        {
            blocks.Add($"<p><b>{Html(routedLine)}</b></p>");
        }

        return string.Join("\n", blocks);
    }

    /// <summary>For a run that failed before any email could be summarized.</summary>
    internal static (string Subject, string Body) BuildFailureOnly(IReadOnlyList<string> failures, DateTimeOffset completedAt, TimeZoneInfo timeZone)
    {
        var body = string.Join("\n", new[]
            {
                $"<p>This is the summary of the AP Automation run completed {Html(FormatDateTime(completedAt, timeZone))} ({Html(timeZone.StandardName)}).</p>",
                $"<p><b>=== 4. Errors ({failures.Count}) ===</b></p>"
            }
            .Concat(failures.Select(failure => $"<p>{Html(failure)}</p>")));

        return ($"{SubjectPrefix}: processing failed", body);
    }

    private static string BuildHeader(EmailRunSummary email, TimeZoneInfo timeZone)
    {
        var lines = new List<string>
        {
            $"Email: \"{Html(email.Subject ?? "(no subject)")}\" from {Html(email.SenderAddress ?? "unknown")} received {Html(FormatReceivedAt(email.ReceivedOn, timeZone))}"
        };

        if (email.Parse is { } parse)
        {
            var notParsed = parse.PdfCount - parse.ParsedFiles.Count;
            lines.Add($"Totals: {parse.AttachmentCount} attachment(s), {parse.PdfCount} PDF(s) — {parse.ParsedFiles.Count} parsed, {notParsed} not parsed.");
        }

        if (email.PaceEntries.Count > 0)
        {
            lines.Add($"Pace: {BuildPaceCounts(email.PaceEntries)}.");
        }

        return $"<p>{string.Join("<br>\n", lines)}</p>";
    }

    private static string BuildParsingSection(EmailParseResult parse)
    {
        var lines = new List<string> { "<b>=== 1. Invoice parsing ===</b>" };

        if (parse.ParsedFiles.Count > 0)
        {
            lines.Add($"Parsed successfully ({parse.ParsedFiles.Count}): {Html(string.Join(", ", parse.ParsedFiles))}.");
        }

        if (parse.Problems.Count > 0)
        {
            var notParsed = parse.PdfCount - parse.ParsedFiles.Count;
            lines.Add(parse.PdfCount == 0 ? "Not parsed:" : $"Not parsed ({notParsed}):");
            lines.AddRange(parse.Problems.Select(problem => $"{Indent}{Html(problem)}"));
        }

        return $"<p>{string.Join("<br>\n", lines)}</p>";
    }

    private static string BuildPaceEntryBlock(PaceSummaryEntry entry)
    {
        var lines = new List<string>
        {
            $"<b>Invoice {Html(entry.InvoiceNumber ?? "unknown")}</b> | PO {Html(entry.CustomerPO ?? "unknown")} | Vendor {Html(VendorDisplay(entry))}",
            $"{Indent}Invoice date {Html(entry.InvoiceDate?.ToString("MM'/'dd'/'yyyy", CultureInfo.InvariantCulture) ?? "unknown")} | Invoice total {Html(entry.InvoiceTotal is { } total ? Money(total) : "unknown")}"
        };

        lines.AddRange(DescribePaceOutcome(entry).Select(line => $"{Indent}{Html(line)}"));

        return $"<p>{string.Join("<br>\n", lines)}</p>";
    }

    /// <summary>What the Pace step did with this invoice, in the AP team's case wording.</summary>
    internal static IReadOnlyList<string> DescribePaceOutcome(PaceSummaryEntry entry)
    {
        var summary = entry.Summary;

        if (summary is null || summary.Case == PaceInvoiceCase.Other)
        {
            return [DescribeWithoutCase(entry)];
        }

        var created = DescribeCreatedBill(entry, summary);
        var totalsMatch = $"Bill total {Money(summary.BillTotal ?? summary.InvoiceTotal)} matches the invoice total.";

        return (entry.Kind, summary.Case) switch
        {
            (PaceSummaryKind.Success, PaceInvoiceCase.AllReceiptsBilled) =>
            [
                $"All {summary.ApprovedReceiptCount} approved PO receipt(s) were already billed on Pace bill(s) {JoinOrNone(summary.ExistingBillIds)} (batch {JoinOrNone(summary.ExistingBillBatchIds)}). No new bill was created.",
                totalsMatch
            ],

            (PaceSummaryKind.Success, _) when entry.StatusCode == PaceSubmissionStatus.AlreadyEntered =>
            [
                $"Already entered: Pace bill(s) {JoinOrNone(summary.ExistingBillIds)} (batch {JoinOrNone(summary.ExistingBillBatchIds)}) already exist for vendor {summary.VendorId} and this invoice. No new bill was created.",
                totalsMatch
            ],

            (PaceSummaryKind.Success, PaceInvoiceCase.PartiallyBilled) =>
            [
                $"PO partially billed. {created} with {summary.ReceiptIdsAdded.Count} unbilled PO receipt line(s) (receipts {JoinOrNone(summary.ReceiptIdsAdded)}); {DescribeAlreadyBilled(summary)} not added.",
                totalsMatch
            ],

            (PaceSummaryKind.Success, PaceInvoiceCase.NotBilled) =>
            [
                $"{created} with {summary.ReceiptIdsAdded.Count} PO receipt line(s) (receipts {JoinOrNone(summary.ReceiptIdsAdded)}).",
                totalsMatch
            ],

            (PaceSummaryKind.NeedsReview, PaceInvoiceCase.PoNotFound) =>
            [
                $"PO {entry.CustomerPO ?? "unknown"} was not found in Pace for this invoice.",
                $"{created} with 1 line of {Money(summary.InvoiceTotal)} coded to the vendor default GL account {summary.GlAccount} / department {summary.GlDepartment}."
            ],

            (PaceSummaryKind.NeedsReview, PaceInvoiceCase.NoApprovedReceipts) =>
            [
                $"PO {entry.CustomerPO ?? "unknown"} was found in Pace but has no approved receipts ({summary.ReceiptCount} receipt(s) found, none with status R).",
                $"{created} with 1 line of {Money(summary.InvoiceTotal)} coded to the PO vendor default GL account {summary.GlAccount} / department {summary.GlDepartment}."
            ],

            (PaceSummaryKind.Error, PaceInvoiceCase.AllReceiptsBilled) when summary.ExistingBillIds.Count == 0 =>
            [
                $"All {summary.ApprovedReceiptCount} approved PO receipt(s) are already billed{(summary.AlreadyBilledOnBillIds.Count > 0 ? $" (on bill(s) {string.Join(", ", summary.AlreadyBilledOnBillIds)})" : string.Empty)}, but no Pace bill exists for vendor {summary.VendorId} and invoice {entry.InvoiceNumber}. No new bill was created."
            ],

            (PaceSummaryKind.Error, PaceInvoiceCase.AllReceiptsBilled) when summary.BillTotal is { } billTotal =>
            [
                $"All {summary.ApprovedReceiptCount} approved PO receipt(s) were already billed on Pace bill(s) {JoinOrNone(summary.ExistingBillIds)} (batch {JoinOrNone(summary.ExistingBillBatchIds)}) with bill total {Money(billTotal)}; invoice total {Money(summary.InvoiceTotal)} — difference {Money(Math.Abs(summary.InvoiceTotal - billTotal))}. No new bill was created."
            ],

            (PaceSummaryKind.Error, PaceInvoiceCase.PartiallyBilled or PaceInvoiceCase.NotBilled) when IsTotalMismatch(entry, summary) =>
                DescribeTotalMismatch(entry, summary),

            _ => [DescribeWithoutCase(entry)]
        };
    }

    private static List<string> DescribeTotalMismatch(PaceSummaryEntry entry, PaceBillSummary summary)
    {
        var billTotal = summary.BillTotal ?? 0m;
        var difference = summary.InvoiceTotal - billTotal;
        var higher = difference > 0 ? "invoice higher" : "bill higher";
        var receipts = $"{summary.ReceiptIdsAdded.Count} PO receipt line(s) (receipts {JoinOrNone(summary.ReceiptIdsAdded)})";
        var skipped = summary.Case == PaceInvoiceCase.PartiallyBilled ? $" {Capitalize(DescribeAlreadyBilled(summary))} not added." : string.Empty;

        var detail = summary.DryRun || entry.PaceBillId is null
            ? $"Pace writes are disabled; the bill that would be created with {receipts} totals {Money(billTotal)} — difference {Money(Math.Abs(difference))} ({higher}). No bill was created.{skipped}"
            : $"{DescribeCreatedBill(entry, summary)} with {receipts} totalling {Money(billTotal)} — difference {Money(Math.Abs(difference))} ({higher}). The bill was left Open in Pace; please correct it before posting.{skipped}";

        return ["Bill total does not match the invoice total.", detail];
    }

    private static bool IsTotalMismatch(PaceSummaryEntry entry, PaceBillSummary summary) =>
        summary.BillTotal is not null && summary.ReceiptIdsAdded.Count > 0 && !summary.TotalsMatch;

    private static string DescribeCreatedBill(PaceSummaryEntry entry, PaceBillSummary summary)
    {
        var batchDescription = summary.BillBatchDescription is null ? string.Empty : $" ({summary.BillBatchDescription})";

        return summary.DryRun || entry.PaceBillId is null
            ? $"Pace writes are disabled; a bill would be created{(summary.BillBatchDescription is null ? string.Empty : $" in batch {summary.BillBatchDescription}")}"
            : $"Bill {entry.PaceBillId} created in batch {entry.PaceBillBatchId}{batchDescription}";
    }

    private static string DescribeAlreadyBilled(PaceBillSummary summary)
    {
        var count = summary.ReceiptIdsAlreadyBilled.Count;
        var onBills = summary.AlreadyBilledOnBillIds.Count > 0 ? $" on bill(s) {string.Join(", ", summary.AlreadyBilledOnBillIds)}" : string.Empty;

        return $"{count} receipt(s) already billed ({JoinOrNone(summary.ReceiptIdsAlreadyBilled)}{onBills}) {(count == 1 ? "was" : "were")}";
    }

    /// <summary>An outcome the PO/receipt rules did not decide (an error before they ran, or an older row).</summary>
    private static string DescribeWithoutCase(PaceSummaryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Reason) && entry.Kind != PaceSummaryKind.Success)
        {
            return BuildReasonForSummary(entry.Reason);
        }

        return entry.StatusCode switch
        {
            PaceSubmissionStatus.AlreadyEntered => $"Already entered in Pace{(string.IsNullOrWhiteSpace(entry.PaceBillId) ? string.Empty : $" (bill {entry.PaceBillId})")}. No new bill was created.",
            PaceSubmissionStatus.BillCreated => $"Bill {entry.PaceBillId} created in batch {entry.PaceBillBatchId}.",
            PaceSubmissionStatus.DryRunPrepared => "Pace writes are disabled; a bill would be created.",
            _ => string.IsNullOrWhiteSpace(entry.Reason) ? $"Pace status {entry.StatusCode}." : BuildReasonForSummary(entry.Reason)
        };
    }

    /// <summary>The last line: where the vendor email ended up, or why it is still in the Inbox.</summary>
    private static string? BuildRoutedLine(EmailRunSummary email, TimeZoneInfo timeZone)
    {
        if (email.RoutedTo is { } routedTo)
        {
            return $"Routed to: {routedTo}";
        }

        if (email.WaitingForPace is { } waiting)
        {
            var nextTry = waiting.NextAttemptOnUtc is { } next
                ? $", next try after {TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Utc)), timeZone).ToString("hh':'mm tt", CultureInfo.InvariantCulture)}"
                : string.Empty;

            return $"Routed to: Not moved yet — still in Inbox, waiting for Pace (attempt {waiting.Attempt} of {waiting.MaxAttempts}{nextTry}).";
        }

        return email.AwaitingPace ? "Routed to: Not moved yet — still in Inbox, waiting for Pace." : null;
    }

    private static List<PaceSummaryEntry> Ordered(IEnumerable<PaceSummaryEntry> entries, PaceSummaryKind kind) =>
        entries
            .Where(entry => entry.Kind == kind)
            .OrderBy(entry => Rank(entry))
            .ThenBy(entry => entry.InvoiceId)
            .ToList();

    /// <summary>Success: case 5, 4, 3. Needs review: case 1, 2. Errors: the case 3/4/5 mismatches first.</summary>
    private static int Rank(PaceSummaryEntry entry) => (entry.Kind, entry.Summary?.Case) switch
    {
        (PaceSummaryKind.Success, PaceInvoiceCase.NotBilled) => 0,
        (PaceSummaryKind.Success, PaceInvoiceCase.PartiallyBilled) => 1,
        (PaceSummaryKind.Success, PaceInvoiceCase.AllReceiptsBilled) => 2,
        (PaceSummaryKind.NeedsReview, PaceInvoiceCase.PoNotFound) => 0,
        (PaceSummaryKind.NeedsReview, PaceInvoiceCase.NoApprovedReceipts) => 1,
        (PaceSummaryKind.Error, PaceInvoiceCase.AllReceiptsBilled) => 0,
        (PaceSummaryKind.Error, PaceInvoiceCase.PartiallyBilled) when IsTotalMismatch(entry, entry.Summary!) => 1,
        (PaceSummaryKind.Error, PaceInvoiceCase.NotBilled) when IsTotalMismatch(entry, entry.Summary!) => 2,
        _ => 10
    };

    private static string BuildPaceCounts(IReadOnlyCollection<PaceSummaryEntry> entries)
    {
        var succeeded = entries.Count(entry => entry.Kind == PaceSummaryKind.Success);
        var needsReview = entries.Count(entry => entry.Kind == PaceSummaryKind.NeedsReview);
        var errors = entries.Count(entry => entry.Kind == PaceSummaryKind.Error);

        return $"{succeeded} succeeded, {needsReview} {(needsReview == 1 ? "needs" : "need")} review, {errors} {(errors == 1 ? "error" : "errors")}";
    }

    private static string VendorDisplay(PaceSummaryEntry entry)
    {
        var id = string.IsNullOrWhiteSpace(entry.VendorId) ? "unknown" : entry.VendorId;

        return string.IsNullOrWhiteSpace(entry.VendorName) || string.Equals(entry.VendorName, entry.VendorId, StringComparison.OrdinalIgnoreCase)
            ? id
            : $"{id} ({entry.VendorName})";
    }

    /// <summary>
    /// The stored error without its leading "Pace invoice '...' for PO '...': ", since the summary line already
    /// names the invoice and PO, and with its first letter capitalised.
    /// </summary>
    internal static string BuildReasonForSummary(string reason)
    {
        var trimmed = ReasonInvoiceAndPoPrefixRegex.Replace(reason, string.Empty).Trim();

        return trimmed.Length == 0 ? reason : Capitalize(trimmed);
    }

    internal static string Money(decimal value) =>
        (value < 0 ? "-$" : "$") + Math.Abs(value).ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string JoinOrNone<T>(IEnumerable<T> values)
    {
        var joined = string.Join(", ", values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).Where(value => !string.IsNullOrWhiteSpace(value)));
        return joined.Length == 0 ? "none" : joined;
    }

    private static string FormatDateTime(DateTimeOffset value, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(value, timeZone).ToString("MM'/'dd'/'yyyy hh':'mm tt", CultureInfo.InvariantCulture);

    private static string FormatReceivedAt(DateTimeOffset? value, TimeZoneInfo timeZone) =>
        value is { } receivedAt ? FormatDateTime(receivedAt, timeZone) : "unknown time";

    private static string OneLine(string value) => value.ReplaceLineEndings(" ");

    private static string Html(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;
}
