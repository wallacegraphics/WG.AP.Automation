namespace WG.AP.Core.Abstractions;

public sealed record MailMessageSummary(
    string Id,
    DateTimeOffset? ReceivedDateTime,
    string? SenderAddress,
    string? Subject,
    IReadOnlyList<MailAttachmentSummary> Attachments);
