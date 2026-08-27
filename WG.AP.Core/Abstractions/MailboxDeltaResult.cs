namespace WG.AP.Core.Abstractions;

public sealed record MailboxDeltaResult(IReadOnlyList<MailMessageSummary> Messages, string DeltaLink);
