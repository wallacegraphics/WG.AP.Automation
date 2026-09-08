using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;

namespace WG.AP.Processor;

/// <summary>
/// Sends an alert email to the configured team recipients. Meant to be reusable across whatever
/// future notification triggers get added, not just the one it was introduced for.
/// </summary>
public sealed class ErrorNotifier(IMailSender mailSender, IOptions<AlertOptions> alertOptions, ILogger<ErrorNotifier> logger)
{
    public async Task NotifyAsync(string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            await mailSender.SendMailAsync(
                new MailSendRequest(subject, body, alertOptions.Value.Recipients),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Deliberately swallowed, not rethrown: every current caller is already inside a
            // failure path that must still finish its own cleanup (recording the failed run,
            // setting the exit code) regardless of whether the alert itself could be sent - a
            // broken mail send must not mask or replace the original failure.
            logger.LogError(exception, "Failed to send alert email '{Subject}'.", subject);
        }
    }
}
