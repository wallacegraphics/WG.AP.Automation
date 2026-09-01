using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WG.AP.Invoice.AI;

/// <summary>
/// Thin HTTP wrapper around a local Ollama server's <c>/api/generate</c> endpoint. No SDK is
/// needed — Ollama's API is plain JSON over HTTP.
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<OllamaOptions> _options;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(HttpClient httpClient, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger)
    {
        // HttpClient.Timeout defaults to 100s and must be set before the first request; local
        // structured-output extraction on a CPU-bound Ollama model routinely takes longer than that,
        // so the configured OllamaOptions.TimeoutSeconds needs to actually reach the HttpClient.
        httpClient.Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);

        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    /// <param name="format">
    /// Ollama's structured-output parameter — either the string "json" or a JSON schema object —
    /// used to constrain the model's output instead of relying on prompt wording alone.
    /// </param>
    /// <param name="model">
    /// A model pinned to the prompt being sent, or null to use the configured default. A prompt is
    /// tuned against a specific model, so without this an operational upgrade from one model to
    /// another would silently change extraction results with nothing recording that it had.
    /// </param>
    public async Task<string> GenerateAsync(string prompt, object? format, string? model, CancellationToken cancellationToken)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? _options.Value.Model : model;

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = effectiveModel,
                Prompt = prompt,
                Format = format
            };

            using var httpResponse = await _httpClient.PostAsJsonAsync("api/generate", request, cancellationToken);
            httpResponse.EnsureSuccessStatusCode();

            var response = await httpResponse.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);

            return response?.Response is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException("Ollama response did not include a non-empty 'response' field.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ollama generate call failed against {BaseUrl} using model {Model}.", _httpClient.BaseAddress, effectiveModel);
            throw;
        }
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; } = false;

        // Disables qwen3's chain-of-thought output — plain field extraction has no use for it, and
        // it would otherwise pollute (or entirely replace) the constrained JSON response.
        [JsonPropertyName("think")]
        public bool Think { get; init; } = false;

        [JsonPropertyName("format")]
        public object? Format { get; init; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; init; }
    }
}
