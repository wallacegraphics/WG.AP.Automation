using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.Processor;

namespace WG.AP.Tests.Processing;

public class ErrorNotifierTests
{
    private sealed class FakeMailSender : IMailSender
    {
        public MailSendRequest? LastRequest { get; private set; }
        public Exception? ThrowOnSend { get; set; }

        public Task SendMailAsync(MailSendRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ThrowOnSend is null ? Task.CompletedTask : Task.FromException(ThrowOnSend);
        }
    }

    private sealed class CapturingLogger : ILogger<ErrorNotifier>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static ErrorNotifier CreateNotifier(FakeMailSender mailSender, ILogger<ErrorNotifier> logger, params string[] recipients)
    {
        var options = Options.Create(new AlertOptions { Recipients = recipients });
        return new ErrorNotifier(mailSender, options, logger);
    }

    [Fact]
    public async Task NotifyAsync_SendsToConfiguredRecipients_WithGivenSubjectAndBody()
    {
        var mailSender = new FakeMailSender();
        var notifier = CreateNotifier(mailSender, NullLogger(), "galina.zezetko@wallacegraphics.com");

        await notifier.NotifyAsync("Subject line", "Body text", CancellationToken.None);

        Assert.NotNull(mailSender.LastRequest);
        Assert.Equal("Subject line", mailSender.LastRequest!.Subject);
        Assert.Equal("Body text", mailSender.LastRequest.Body);
        Assert.Equal(["galina.zezetko@wallacegraphics.com"], mailSender.LastRequest.ToAddresses);
    }

    [Fact]
    public async Task NotifyAsync_SendFails_LogsButDoesNotThrow()
    {
        var mailSender = new FakeMailSender { ThrowOnSend = new InvalidOperationException("Graph is down.") };
        var logger = new CapturingLogger();
        var notifier = CreateNotifier(mailSender, logger, "galina.zezetko@wallacegraphics.com");

        await notifier.NotifyAsync("Subject line", "Body text", CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException && e.Message.Contains("Subject line"));
    }

    private static ILogger<ErrorNotifier> NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger<ErrorNotifier>.Instance;
}
