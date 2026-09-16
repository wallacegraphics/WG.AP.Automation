namespace WG.AP.Integrations.Pace;

public sealed class PaceOptions
{
    public const string SectionName = "Pace";

    public required string BaseUrl { get; init; }

    public required string UserName { get; init; }

    public required string Password { get; init; }

    public int TimeoutSeconds { get; init; } = 100;

    public bool WriteEnabled { get; init; }
}
