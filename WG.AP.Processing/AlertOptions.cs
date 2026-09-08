namespace WG.AP.Processor;

public sealed class AlertOptions
{
    public const string SectionName = "Alert";

    public required IReadOnlyList<string> Recipients { get; init; }
}
