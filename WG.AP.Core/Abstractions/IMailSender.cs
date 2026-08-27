namespace WG.AP.Core.Abstractions;

public interface IMailSender
{
    Task SendMailAsync(MailSendRequest request, CancellationToken cancellationToken);
}
