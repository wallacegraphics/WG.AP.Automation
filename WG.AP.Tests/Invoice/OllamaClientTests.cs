using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Invoice.AI;
using WG.AP.Tests.Email;

namespace WG.AP.Tests.Invoice;

public class OllamaClientTests
{
    private sealed class CapturingLogger : ILogger<OllamaClient>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    private static (OllamaClient Client, FakeGraphHandler Handler) CreateClient(ILogger<OllamaClient>? logger = null)
    {
        var handler = new FakeGraphHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var options = Options.Create(new OllamaOptions { BaseUrl = "http://localhost:11434", Model = "qwen3:14b" });
        var client = new OllamaClient(httpClient, options, logger ?? NullLogger<OllamaClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsTheResponseField()
    {
        var (client, handler) = CreateClient();

        handler.On(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/generate"),
            """{"response": "{\"InvoiceNumber\":\"INV-1\"}", "done": true}""");

        var response = await client.GenerateAsync("extract fields", format: null, CancellationToken.None);

        Assert.Equal("""{"InvoiceNumber":"INV-1"}""", response);
    }

    [Fact]
    public async Task GenerateAsync_SendsModelAndPrompt()
    {
        var (client, handler) = CreateClient();
        string? capturedBody = null;

        handler.On(
            r =>
            {
                var isGenerate = r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/generate");
                if (isGenerate)
                {
                    capturedBody = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                return isGenerate;
            },
            """{"response": "{}", "done": true}""");

        await client.GenerateAsync("extract fields from this text", format: null, CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"model\":\"qwen3:14b\"", capturedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extract fields from this text", capturedBody);
    }

    [Fact]
    public async Task GenerateAsync_WhenServerReturnsError_LogsAndThrows()
    {
        var logger = new CapturingLogger();
        var (client, _) = CreateClient(logger);
        // No route registered — the fake handler 404s, which EnsureSuccessStatusCode turns into an exception.

        await Assert.ThrowsAnyAsync<Exception>(() => client.GenerateAsync("prompt", format: null, CancellationToken.None));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }
}
