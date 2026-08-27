using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.Email;

namespace WG.AP.Processor;

public sealed class APProcessor(
    IMailSource mailSource,
    MailboxSyncProcessor mailboxSyncProcessor,
    IOptions<MailboxOptions> mailboxOptions,
    ILogger<APProcessor> logger)
{
    public async Task ProcessInvoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await mailSource.ValidateAuthAsync(cancellationToken);
            await mailSource.EnsureFoldersExistAsync(cancellationToken);

            var batch = await mailboxSyncProcessor.GetNewMessagesAsync(cancellationToken);
            var attachmentCount = 0;

            foreach (var message in batch.Messages)
            {
                attachmentCount += message.Attachments.Count;

                logger.LogInformation(
                    "Message {MessageId} from {Sender} received {ReceivedAt}: {AttachmentCount} attachment(s).",
                    message.Id,
                    message.SenderAddress ?? "unknown",
                    message.ReceivedDateTime,
                    message.Attachments.Count);
            }

            await mailboxSyncProcessor.CommitAsync(batch, cancellationToken);

            logger.LogInformation(
                "Mailbox scan complete for {MailboxUser}: {MessageCount} new message(s), {AttachmentCount} attachment(s).",
                mailboxOptions.Value.MailboxUser,
                batch.Messages.Count,
                attachmentCount);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mailbox processing failed.");
            Environment.ExitCode = 1;
        }
    }
}
