using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages.Item.Move;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Kiota.Abstractions;
using WG.AP.Core.Abstractions;

namespace WG.AP.Email;

/// <summary>
/// Graph-backed <see cref="IMailSource"/>/<see cref="IMailSender"/> implementation. Per SDP-178,
/// this type only surfaces mailbox state and mail-movement primitives — it does not decide which
/// attachments matter, whether a message succeeded, or which folder a message belongs in.
/// </summary>
public sealed class GraphMailboxProcessor : IMailSource, IMailSender
{
    public static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private const string ImmutableIdPreferHeader = "IdType=\"ImmutableId\"";

    private readonly MailboxOptions _options;
    private readonly ILogger<GraphMailboxProcessor> _logger;
    private readonly GraphServiceClient _graphClient;
    private readonly Dictionary<MailDestinationFolder, string> _folderIds = new();

    public GraphMailboxProcessor(GraphServiceClient graphClient, IOptions<MailboxOptions> options, ILogger<GraphMailboxProcessor> logger)
    {
        _graphClient = graphClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ValidateAuthAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running Graph authentication smoke test for {MailboxUser}.", _options.MailboxUser);

        try
        {
            var result = await _graphClient.Users[_options.MailboxUser].MailFolders["inbox"].Messages.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Top = 1;
                requestConfiguration.QueryParameters.Select = ["id"];
                ApplyImmutableId(requestConfiguration.Headers);
            }, cancellationToken);

            if (result is null)
            {
                throw CreateInvalidOperation("Graph authentication smoke test failed: no response from mailbox query.");
            }

            _logger.LogInformation("Graph authentication smoke test succeeded for {MailboxUser}.", _options.MailboxUser);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Graph authentication smoke test failed for {MailboxUser}.", _options.MailboxUser);
            throw;
        }
    }

    public async Task EnsureFoldersExistAsync(CancellationToken cancellationToken)
    {
        try
        {
            var inbox = await _graphClient.Users[_options.MailboxUser].MailFolders["inbox"].GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Select = ["id"];
                ApplyImmutableId(requestConfiguration.Headers);
            }, cancellationToken) ?? throw CreateInvalidOperation("Unable to resolve the Inbox folder.");

            var inboxId = inbox.Id ?? throw CreateInvalidOperation("Inbox folder response did not include an id.");

            var childFolders = await _graphClient.Users[_options.MailboxUser].MailFolders[inboxId].ChildFolders.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Select = ["id", "displayName"];
                ApplyImmutableId(requestConfiguration.Headers);
            }, cancellationToken);

            var allChildFolders = new List<MailFolder>();

            while (childFolders is not null)
            {
                allChildFolders.AddRange(childFolders.Value ?? []);

                childFolders = string.IsNullOrEmpty(childFolders.OdataNextLink)
                    ? null
                    : await _graphClient.Users[_options.MailboxUser].MailFolders[inboxId].ChildFolders
                        .WithUrl(childFolders.OdataNextLink)
                        .GetAsync(requestConfiguration => ApplyImmutableId(requestConfiguration.Headers), cancellationToken);
            }

            var existingByName = allChildFolders
                .Where(folder => folder.DisplayName is not null && folder.Id is not null)
                .ToDictionary(folder => folder.DisplayName!, folder => folder.Id!, StringComparer.OrdinalIgnoreCase);

            foreach (var destination in Enum.GetValues<MailDestinationFolder>())
            {
                var displayName = destination.ToString();

                if (existingByName.TryGetValue(displayName, out var existingId))
                {
                    _folderIds[destination] = existingId;
                    continue;
                }

                _logger.LogInformation("Creating missing mail folder Inbox\\{FolderName} for {MailboxUser}.", displayName, _options.MailboxUser);

                var created = await _graphClient.Users[_options.MailboxUser].MailFolders[inboxId].ChildFolders.PostAsync(
                    new MailFolder { DisplayName = displayName },
                    cancellationToken: cancellationToken) ?? throw CreateInvalidOperation($"Failed to create mail folder '{displayName}'.");

                _folderIds[destination] = created.Id ?? throw CreateInvalidOperation($"Created mail folder '{displayName}' did not return an id.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to ensure destination mail folders exist for {MailboxUser}.", _options.MailboxUser);
            throw;
        }
    }

    public async IAsyncEnumerable<MailMessageSummary> EnumerateInboxAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await FetchInboxFirstPageAsync(cancellationToken);

        while (page is not null)
        {
            foreach (var message in page.Value ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (message.Id is null)
                {
                    continue;
                }

                yield return await ToSummaryAsync(message, cancellationToken);
            }

            page = string.IsNullOrEmpty(page.OdataNextLink)
                ? null
                : await FetchInboxNextPageAsync(page.OdataNextLink, cancellationToken);
        }
    }

    private async Task<MessageCollectionResponse?> FetchInboxFirstPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _graphClient.Users[_options.MailboxUser].MailFolders["inbox"].Messages.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Top = 50;
                requestConfiguration.QueryParameters.Select = ["id", "receivedDateTime", "from", "subject", "hasAttachments"];
                ApplyImmutableId(requestConfiguration.Headers);
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to fetch the first page of inbox messages for {MailboxUser}.", _options.MailboxUser);
            throw;
        }
    }

    private async Task<MessageCollectionResponse?> FetchInboxNextPageAsync(string nextLink, CancellationToken cancellationToken)
    {
        try
        {
            return await _graphClient.Users[_options.MailboxUser].MailFolders["inbox"].Messages
                .WithUrl(nextLink)
                .GetAsync(requestConfiguration => ApplyImmutableId(requestConfiguration.Headers), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to fetch the next page of inbox messages for {MailboxUser} ({NextLink}).", _options.MailboxUser, nextLink);
            throw;
        }
    }

    public async Task<MailboxDeltaResult> GetInboxDeltaAsync(string? deltaLink, CancellationToken cancellationToken)
    {
        try
        {
            var deltaBuilder = _graphClient.Users[_options.MailboxUser].MailFolders["inbox"].Messages.Delta;

            var response = string.IsNullOrEmpty(deltaLink)
                ? await deltaBuilder.GetAsDeltaGetResponseAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Select = ["id", "receivedDateTime", "from", "subject", "hasAttachments"];
                    ApplyImmutableId(requestConfiguration.Headers);
                }, cancellationToken)
                : await deltaBuilder.WithUrl(deltaLink).GetAsDeltaGetResponseAsync(
                    requestConfiguration => ApplyImmutableId(requestConfiguration.Headers),
                    cancellationToken);

            var messages = new List<MailMessageSummary>();
            string? newDeltaLink = null;

            while (response is not null)
            {
                foreach (var message in response.Value ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Messages that left the inbox (moved/deleted) show up as tombstones carrying only
                    // "@removed" plus an id — they aren't mail to act on, so they're dropped here rather
                    // than surfaced to callers.
                    if (message.Id is null || message.AdditionalData?.ContainsKey("@removed") == true)
                    {
                        continue;
                    }

                    messages.Add(await ToSummaryAsync(message, cancellationToken));
                }

                if (!string.IsNullOrEmpty(response.OdataDeltaLink))
                {
                    newDeltaLink = response.OdataDeltaLink;
                    break;
                }

                response = string.IsNullOrEmpty(response.OdataNextLink)
                    ? null
                    : await deltaBuilder.WithUrl(response.OdataNextLink).GetAsDeltaGetResponseAsync(
                        requestConfiguration => ApplyImmutableId(requestConfiguration.Headers),
                        cancellationToken);
            }

            if (newDeltaLink is null)
            {
                throw CreateInvalidOperation("Delta query completed without returning a delta link.");
            }

            return new MailboxDeltaResult(messages, newDeltaLink);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to fetch the inbox delta for {MailboxUser} (resuming: {IsResuming}).", _options.MailboxUser, deltaLink is not null);
            throw;
        }
    }

    public async Task<MailMessageSummary?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            var message = await _graphClient.Users[_options.MailboxUser].Messages[messageId].GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Select = ["id", "receivedDateTime", "from", "subject", "hasAttachments"];
                ApplyImmutableId(requestConfiguration.Headers);
            }, cancellationToken);

            return message is null ? null : await ToSummaryAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to fetch message {MessageId} for {MailboxUser}.", messageId, _options.MailboxUser);
            throw;
        }
    }

    public async Task<byte[]> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        try
        {
            var attachment = await _graphClient.Users[_options.MailboxUser].Messages[messageId].Attachments[attachmentId].GetAsync(
                requestConfiguration => ApplyImmutableId(requestConfiguration.Headers),
                cancellationToken);

            if (attachment is FileAttachment { ContentBytes: { Length: > 0 } contentBytes })
            {
                return contentBytes;
            }

            _logger.LogWarning(
                "Attachment {AttachmentId} on message {MessageId} returned no inline content bytes. Large attachments streamed via $value are not yet supported by this adapter.",
                attachmentId,
                messageId);

            return [];
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to fetch attachment {AttachmentId} on message {MessageId} for {MailboxUser}.", attachmentId, messageId, _options.MailboxUser);
            throw;
        }
    }

    public async Task<string> MoveMessageAsync(string messageId, MailDestinationFolder destination, CancellationToken cancellationToken)
    {
        try
        {
            if (!_folderIds.TryGetValue(destination, out var folderId))
            {
                throw CreateInvalidOperation($"Destination folder '{destination}' is unknown. Call {nameof(EnsureFoldersExistAsync)} first.");
            }

            var moved = await _graphClient.Users[_options.MailboxUser].Messages[messageId].Move.PostAsync(
                new MovePostRequestBody { DestinationId = folderId },
                requestConfiguration => ApplyImmutableId(requestConfiguration.Headers),
                cancellationToken);

            return moved?.Id ?? throw CreateInvalidOperation("Move operation did not return a message id.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to move message {MessageId} to {Destination} for {MailboxUser}.", messageId, destination, _options.MailboxUser);
            throw;
        }
    }

    public async Task SendMailAsync(MailSendRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var message = new Message
            {
                Subject = request.Subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Text,
                    Content = request.Body
                },
                ToRecipients = request.ToAddresses
                    .Select(address => new Recipient { EmailAddress = new EmailAddress { Address = address } })
                    .ToList()
            };

            await _graphClient.Users[_options.MailboxUser].SendMail.PostAsync(
                new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = true
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to send mail {Subject} from {MailboxUser}.", request.Subject, _options.MailboxUser);
            throw;
        }
    }

    private async Task<MailMessageSummary> ToSummaryAsync(Message message, CancellationToken cancellationToken)
    {
        var attachments = new List<MailAttachmentSummary>();

        if (message.HasAttachments is true)
        {
            AttachmentCollectionResponse? attachmentPage;

            try
            {
                attachmentPage = await _graphClient.Users[_options.MailboxUser].Messages[message.Id!].Attachments.GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Select = ["id", "name", "size", "contentType"];
                    ApplyImmutableId(requestConfiguration.Headers);
                }, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to fetch attachment metadata for message {MessageId} for {MailboxUser}.", message.Id, _options.MailboxUser);
                throw;
            }

            attachments.AddRange((attachmentPage?.Value ?? [])
                .Where(attachment => attachment.Id is not null)
                .Select(attachment => new MailAttachmentSummary(
                    attachment.Id!,
                    attachment.Name ?? "unnamed",
                    attachment.Size ?? 0,
                    attachment.ContentType ?? "application/octet-stream")));
        }

        return new MailMessageSummary(
            message.Id!,
            message.ReceivedDateTime,
            message.From?.EmailAddress?.Address,
            message.Subject,
            attachments);
    }

    private static void ApplyImmutableId(RequestHeaders headers) => headers.Add("Prefer", ImmutableIdPreferHeader);

    private static InvalidOperationException CreateInvalidOperation(string message) => new(message);
}
