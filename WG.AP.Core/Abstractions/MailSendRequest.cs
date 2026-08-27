namespace WG.AP.Core.Abstractions;

public sealed record MailSendRequest(string Subject, string Body, IReadOnlyList<string> ToAddresses);
