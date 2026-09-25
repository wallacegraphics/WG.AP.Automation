using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WG.AP.Processor;

/// <summary>
/// Sends the run's summary emails - one per vendor email that was parsed or had a Pace outcome reported in
/// this run - once both steps are done. Replaces the separate mailbox digest, Pace summary and
/// "mailbox processing failed" emails.
/// </summary>
public sealed class RunSummaryNotifier(
    RunSummary runSummary,
    ErrorNotifier errorNotifier,
    IOptions<AlertOptions> alertOptions,
    ILogger<RunSummaryNotifier> logger)
{
    /// <returns>
    /// The mail message ids whose summary was delivered. Only their Pace rows may be marked notified: a row
    /// whose summary did not go out is reported again by the next run's recovery sweep.
    /// </returns>
    public async Task<IReadOnlySet<long>> SendAsync(CancellationToken cancellationToken)
    {
        var delivered = new HashSet<long>();

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(alertOptions.Value.TimeZoneId);
            var completedAt = DateTimeOffset.UtcNow;
            var failures = runSummary.Failures;

            // An email moved by the recovery sweep alone, with nothing new to report, gets no summary of its own.
            var reportable = runSummary.Emails
                .Where(email => email.Parse is not null || email.PaceEntries.Count > 0)
                .ToList();

            if (reportable.Count == 0)
            {
                if (failures.Count > 0)
                {
                    var (subject, body) = RunSummaryEmailBuilder.BuildFailureOnly(failures, completedAt, timeZone);
                    await errorNotifier.NotifyAsync(subject, body, cancellationToken);
                }

                return delivered;
            }

            // The same physical email can be recorded twice (Graph reissuing a message's id after a move is a
            // known way this happens). Two summaries that render identically go out once.
            foreach (var group in reportable
                .Select(email => (Email: email, Subject: RunSummaryEmailBuilder.BuildSubject(email, failures), Body: RunSummaryEmailBuilder.BuildBody(email, failures, completedAt, timeZone)))
                .GroupBy(summary => (summary.Subject, summary.Body)))
            {
                var sent = await errorNotifier.NotifyAsync(group.Key.Subject, group.Key.Body, cancellationToken);

                if (sent)
                {
                    delivered.UnionWith(group.Select(summary => summary.Email.MailMessageId));
                }
                else
                {
                    logger.LogWarning(
                        "Run summary '{Subject}' could not be sent; its Pace invoices will be reported again by the next run.",
                        group.Key.Subject);
                }
            }
        }
        catch (Exception exception)
        {
            // Never let a summary failure mask the run's own outcome; the rows stay unnotified and are resent.
            logger.LogError(exception, "Failed to build or send the run summary emails.");
        }

        return delivered;
    }
}
