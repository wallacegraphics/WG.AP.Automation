using System.Globalization;
using System.Text.RegularExpressions;
using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Pdf;

/// <summary>
/// Deterministically extracts invoice fields from SanMar's fixed header layout. In the PDF's
/// natural content-stream draw order (join <c>Page.GetWords()</c> with spaces - NOT the text from
/// <see cref="UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor"/>,
/// which reconstructs lines by Y-position and scrambles this layout), each label is followed
/// immediately by its own value - sometimes with unrelated header content (company info, phone
/// numbers) sitting between that value and the next label, except for "Customer PO:"/"Order
/// Account:" which always sit back-to-back. Each field is therefore matched independently, anchored
/// to its own label, rather than assuming one contiguous label-then-value block. Returns null, never
/// throws, whenever a label isn't found or a value fails a sanity check; callers should fall back to
/// the Ollama-based extractor in that case.
/// </summary>
public static class SanmarPdfHeaderExtractor
{
    private static readonly Regex InvoiceNumberRegex = new(@"Invoice\s+Number:\s*(?<v>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SalesOrderRegex = new(@"Sales\s+Order:\s*(?<v>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InvoiceDateRegex = new(@"Invoice\s+Date:\s*(?<v>\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DueDateRegex = new(@"Due\s+Date:\s*(?<v>\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CustomerNumberRegex = new(@"Customer\s+Number:\s*(?<v>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TermsRegex = new(@"Terms:\s*(?<v>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrderAccountRegex = new(@"Order\s+Account:\s*(?<v>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Customer PO's value can be multi-word free text (e.g. "2702-2225 Cobb Nutri Team"), so it's
    // captured lazily up to the "Order Account:" label that always immediately follows it, rather
    // than as a single token like the other fields. ".*?" (not ".+?") so a blank Customer PO doesn't
    // fail the whole match.
    private static readonly Regex CustomerPORegex = new(@"Customer\s+PO:\s*(?<v>.*?)\s*Order\s+Account:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Word-bounded so this can't match inside a differently-worded "Subtotal"-style label; the
    // negative lookahead skips the line-item case-count line ("Total 1.00 cases"). The last match in
    // the document is the grand total (confirmed against real invoices - it prints once more, alone,
    // near the end of the page, after the "Total <count> cases" / subtotal block).
    //
    // The amount group allows comma thousands-separators (e.g. "1,320.00") - a plain \d+ silently
    // fails to match any total >= $1,000 at all (not a wrong parse, zero matches), which sent every
    // such invoice to Ollama misclassified as "layout not recognized" rather than a bad total read.
    // decimal.Parse below needs no change: CultureInfo.InvariantCulture's default NumberStyles.Number
    // already accepts comma-grouped digits.
    private static readonly Regex TotalRegex = new(@"\bTotal\b\s+(?<amount>\d{1,3}(?:,\d{3})*\.\d{2})\b(?!\s+[Cc]ases)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static InvoiceFields? TryExtract(string naturalOrderText)
    {
        if (string.IsNullOrWhiteSpace(naturalOrderText))
        {
            return null;
        }

        var invoiceNumber = MatchValue(InvoiceNumberRegex, naturalOrderText);
        var salesOrder = MatchValue(SalesOrderRegex, naturalOrderText);
        var customerNumber = MatchValue(CustomerNumberRegex, naturalOrderText);
        var terms = MatchValue(TermsRegex, naturalOrderText);
        var orderAccount = MatchValue(OrderAccountRegex, naturalOrderText);

        if (invoiceNumber is null || salesOrder is null || customerNumber is null || terms is null || orderAccount is null)
        {
            return null;
        }

        if (!TryMatchDate(InvoiceDateRegex, naturalOrderText, out var invoiceDate)
            || !TryMatchDate(DueDateRegex, naturalOrderText, out var dueDate))
        {
            return null;
        }

        var customerPOMatch = CustomerPORegex.Match(naturalOrderText);

        if (!customerPOMatch.Success)
        {
            return null;
        }

        var customerPOValue = customerPOMatch.Groups["v"].Value.Trim();
        var customerPO = customerPOValue.Length == 0 ? null : customerPOValue;

        var total = FindTotal(naturalOrderText);

        if (total is null)
        {
            return null;
        }

        return new InvoiceFields(
            invoiceNumber,
            salesOrder,
            invoiceDate,
            dueDate,
            total.Value,
            "SanMar",
            customerPO,
            customerNumber,
            orderAccount,
            terms);
    }

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static bool TryMatchDate(Regex regex, string text, out DateOnly date)
    {
        var match = regex.Match(text);

        if (!match.Success)
        {
            date = default;
            return false;
        }

        return DateOnly.TryParseExact(match.Groups["v"].Value, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static decimal? FindTotal(string text)
    {
        Match? last = null;

        foreach (Match match in TotalRegex.Matches(text))
        {
            last = match;
        }

        return last is null ? null : decimal.Parse(last.Groups["amount"].Value, CultureInfo.InvariantCulture);
    }
}
