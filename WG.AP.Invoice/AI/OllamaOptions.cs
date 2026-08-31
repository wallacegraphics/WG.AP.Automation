namespace WG.AP.Invoice.AI;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    public int TimeoutSeconds { get; init; } = 300;
}
