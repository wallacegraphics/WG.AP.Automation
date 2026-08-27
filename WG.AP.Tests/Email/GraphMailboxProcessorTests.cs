using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using WG.AP.Core.Abstractions;
using WG.AP.Email;

namespace WG.AP.Tests.Email;

public class GraphMailboxProcessorTests
{
    private sealed class CapturingLogger : ILogger<GraphMailboxProcessor>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static (GraphMailboxProcessor Processor, FakeGraphHandler Handler) CreateProcessor(ILogger<GraphMailboxProcessor>? logger = null)
    {
        var handler = new FakeGraphHandler();
        var httpClient = new HttpClient(handler);
        var requestAdapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient)
        {
            BaseUrl = "https://graph.microsoft.com/v1.0"
        };
        var graphClient = new GraphServiceClient(requestAdapter);

        var options = Options.Create(new MailboxOptions
        {
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
            MailboxUser = "test-mailbox@wallacegraphics.com",
            IsTestMailbox = true
        });

        var processor = new GraphMailboxProcessor(graphClient, options, logger ?? NullLogger<GraphMailboxProcessor>.Instance);
        return (processor, handler);
    }

    private static string ReadJsonField(HttpRequestMessage request, string fieldName)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty(fieldName).GetString()!;
    }

    [Fact]
    public async Task EnumerateInboxAsync_ReturnsMessagesWithAttachmentMetadata()
    {
        var (processor, handler) = CreateProcessor();

        handler
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox/messages"),
                """{"value":[{"id":"AAMk-1","receivedDateTime":"2026-01-01T10:00:00Z","from":{"emailAddress":{"address":"vendor1@example.com"}},"subject":"Invoice 100","hasAttachments":true},{"id":"AAMk-2","receivedDateTime":"2026-01-02T11:00:00Z","from":{"emailAddress":{"address":"vendor2@example.com"}},"subject":"Invoice 200","hasAttachments":false}]}""")
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/messages/AAMk-1/attachments"),
                """{"value":[{"id":"attach-1","name":"invoice.pdf","size":12345,"contentType":"application/pdf"}]}""");

        var messages = new List<MailMessageSummary>();
        await foreach (var message in processor.EnumerateInboxAsync())
        {
            messages.Add(message);
        }

        Assert.Equal(2, messages.Count);

        var first = messages.Single(m => m.Id == "AAMk-1");
        Assert.Equal("vendor1@example.com", first.SenderAddress);
        Assert.Equal("Invoice 100", first.Subject);
        var attachment = Assert.Single(first.Attachments);
        Assert.Equal("invoice.pdf", attachment.Name);
        Assert.Equal(12345, attachment.SizeInBytes);
        Assert.Equal("application/pdf", attachment.ContentType);

        var second = messages.Single(m => m.Id == "AAMk-2");
        Assert.Empty(second.Attachments);
    }

    [Fact]
    public async Task GetInboxDeltaAsync_FirstSync_ReturnsMessagesAndDeltaLink()
    {
        var (processor, handler) = CreateProcessor();
        const string deltaLink = "https://graph.microsoft.com/v1.0/users/test-mailbox@wallacegraphics.com/mailFolders/inbox/messages/delta()?%24deltatoken=token-1";

        handler.On(
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox/messages/delta()"),
            """{"value":[{"id":"AAMk-1","receivedDateTime":"2026-01-01T10:00:00Z","from":{"emailAddress":{"address":"vendor1@example.com"}},"subject":"Invoice 100","hasAttachments":false}],"@odata.deltaLink":"__DELTA_LINK__"}""".Replace("__DELTA_LINK__", deltaLink));

        var result = await processor.GetInboxDeltaAsync(deltaLink: null, CancellationToken.None);

        var message = Assert.Single(result.Messages);
        Assert.Equal("AAMk-1", message.Id);
        Assert.Equal(deltaLink, result.DeltaLink);
    }

    [Fact]
    public async Task GetInboxDeltaAsync_TombstoneEntries_AreFilteredOut()
    {
        var (processor, handler) = CreateProcessor();
        const string deltaLink = "https://graph.microsoft.com/v1.0/users/test-mailbox@wallacegraphics.com/mailFolders/inbox/messages/delta()?%24deltatoken=token-1";

        handler.On(
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox/messages/delta()"),
            """{"value":[{"id":"AAMk-1","receivedDateTime":"2026-01-01T10:00:00Z","from":{"emailAddress":{"address":"vendor1@example.com"}},"subject":"Invoice 100","hasAttachments":false},{"@removed":{"reason":"deleted"},"id":"AAMk-2"}],"@odata.deltaLink":"__DELTA_LINK__"}""".Replace("__DELTA_LINK__", deltaLink));

        var result = await processor.GetInboxDeltaAsync(deltaLink: null, CancellationToken.None);

        var message = Assert.Single(result.Messages);
        Assert.Equal("AAMk-1", message.Id);
    }

    [Fact]
    public async Task GetInboxDeltaAsync_ResumesFromSavedDeltaLink_InsteadOfStartingANewSync()
    {
        var (processor, handler) = CreateProcessor();
        const string savedDeltaLink = "https://graph.microsoft.com/v1.0/users/test-mailbox@wallacegraphics.com/mailFolders/inbox/messages/delta()?%24deltatoken=token-1";
        const string nextDeltaLink = "https://graph.microsoft.com/v1.0/users/test-mailbox@wallacegraphics.com/mailFolders/inbox/messages/delta()?%24deltatoken=token-2";

        handler.On(
            r => r.Method == HttpMethod.Get && r.RequestUri!.ToString() == savedDeltaLink,
            """{"value":[{"id":"AAMk-3","receivedDateTime":"2026-01-03T10:00:00Z","from":{"emailAddress":{"address":"vendor3@example.com"}},"subject":"Invoice 300","hasAttachments":false}],"@odata.deltaLink":"__DELTA_LINK__"}""".Replace("__DELTA_LINK__", nextDeltaLink));

        var result = await processor.GetInboxDeltaAsync(savedDeltaLink, CancellationToken.None);

        var message = Assert.Single(result.Messages);
        Assert.Equal("AAMk-3", message.Id);
        Assert.Equal(nextDeltaLink, result.DeltaLink);
        Assert.Single(handler.Requests, r => r.RequestUri!.ToString() == savedDeltaLink);
    }

    [Fact]
    public async Task EnumerateInboxAsync_WhenGraphCallFails_LogsTheErrorAndStillPropagatesIt()
    {
        var logger = new CapturingLogger();
        var (processor, _) = CreateProcessor(logger);
        // No route registered for "/mailFolders/inbox/messages" — the fake handler 404s, which the
        // Kiota adapter turns into an exception when fetching the first page.

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in processor.EnumerateInboxAsync())
            {
            }
        });

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }

    [Fact]
    public async Task MoveMessageAsync_WhenGraphCallFails_LogsTheErrorAndStillPropagatesIt()
    {
        var logger = new CapturingLogger();
        var (processor, handler) = CreateProcessor(logger);

        handler
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox"),
                """{"id":"inbox-id"}""")
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox-id/childFolders"),
                """{"value":[{"id":"folder-processed","displayName":"Processed"},{"id":"folder-errors","displayName":"Errors"},{"id":"folder-needsreview","displayName":"NeedsReview"}]}""");
        // No route registered for the "/messages/AAMk-1/move" POST — the fake handler 404s.

        await processor.EnsureFoldersExistAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            processor.MoveMessageAsync("AAMk-1", MailDestinationFolder.Processed, CancellationToken.None));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is not null && e.Message.Contains("AAMk-1"));
    }

    [Fact]
    public async Task GetAttachmentContentAsync_ReturnsBytesMatchingReportedSize()
    {
        var (processor, handler) = CreateProcessor();
        var expectedBytes = Encoding.UTF8.GetBytes("Hello World");
        var contentBase64 = Convert.ToBase64String(expectedBytes);

        handler.On(
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/messages/AAMk-1/attachments/attach-1"),
            $$"""{"@odata.type":"#microsoft.graph.fileAttachment","id":"attach-1","name":"invoice.pdf","contentType":"application/pdf","size":{{expectedBytes.Length}},"contentBytes":"{{contentBase64}}"}""");

        var content = await processor.GetAttachmentContentAsync("AAMk-1", "attach-1", CancellationToken.None);

        Assert.Equal(expectedBytes.Length, content.Length);
        Assert.Equal(expectedBytes, content);
    }

    [Fact]
    public async Task EnsureFoldersExistAsync_CreatesAllThreeFolders_WhenMailboxHasNone()
    {
        var (processor, handler) = CreateProcessor();
        var createdDisplayNames = new List<string>();

        handler
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox"),
                """{"id":"inbox-id"}""")
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox-id/childFolders"),
                """{"value":[]}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox-id/childFolders"),
                r =>
                {
                    var displayName = ReadJsonField(r, "displayName");
                    createdDisplayNames.Add(displayName);
                    return $$"""{"id":"folder-{{displayName}}","displayName":"{{displayName}}"}""";
                });

        await processor.EnsureFoldersExistAsync(CancellationToken.None);

        Assert.Equal(["Processed", "Errors", "NeedsReview"], createdDisplayNames.OrderBy(n => n switch
        {
            "Processed" => 0,
            "Errors" => 1,
            _ => 2
        }));
    }

    [Fact]
    public async Task MoveMessageAsync_PreservesTheImmutableId_AndTheMessageIsReadableByItAfterTheMove()
    {
        var (processor, handler) = CreateProcessor();

        handler
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox"),
                """{"id":"inbox-id"}""")
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/mailFolders/inbox-id/childFolders"),
                """{"value":[{"id":"folder-processed","displayName":"Processed"},{"id":"folder-errors","displayName":"Errors"},{"id":"folder-needsreview","displayName":"NeedsReview"}]}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/messages/AAMk-1/move"),
                """{"id":"AAMk-1"}""")
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/messages/AAMk-1"),
                """{"id":"AAMk-1","receivedDateTime":"2026-01-01T10:00:00Z","from":{"emailAddress":{"address":"vendor1@example.com"}},"subject":"Invoice 100","hasAttachments":false}""");

        await processor.EnsureFoldersExistAsync(CancellationToken.None);
        var preMoveId = "AAMk-1";

        var postMoveId = await processor.MoveMessageAsync(preMoveId, MailDestinationFolder.Processed, CancellationToken.None);
        Assert.Equal(preMoveId, postMoveId);

        var rereadMessage = await processor.GetMessageAsync(preMoveId, CancellationToken.None);
        Assert.NotNull(rereadMessage);
        Assert.Equal(preMoveId, rereadMessage!.Id);
    }

    [Fact]
    public async Task SendMailAsync_PostsToTheSendMailEndpoint()
    {
        var (processor, handler) = CreateProcessor();
        string? capturedBody = null;

        handler.On(
            r =>
            {
                var isSendMail = r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/sendMail");
                if (isSendMail)
                {
                    capturedBody = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                }

                return isSendMail;
            },
            "{}");

        await processor.SendMailAsync(
            new MailSendRequest("Test notification", "A test notification body.", ["ap-team@wallacegraphics.com"]),
            CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.Contains("Test notification", capturedBody);
        Assert.Contains("ap-team@wallacegraphics.com", capturedBody);
    }
}
