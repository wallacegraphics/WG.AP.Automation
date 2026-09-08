namespace WG.AP.Processor;

public sealed class AlertOptions
{
    public const string SectionName = "Alert";

    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();
}
