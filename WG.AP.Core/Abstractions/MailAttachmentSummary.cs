namespace WG.AP.Core.Abstractions;

public sealed record MailAttachmentSummary(string Id, string Name, long SizeInBytes, string ContentType);
