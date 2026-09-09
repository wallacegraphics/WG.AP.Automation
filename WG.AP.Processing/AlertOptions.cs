namespace WG.AP.Processor;

public sealed class AlertOptions
{
    public const string SectionName = "Alert";

    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();

    // Graph reports ReceivedDateTime in UTC; alert/summary emails convert to this zone so the AP team
    // reads times the way they'd see them in Outlook, not in UTC.
    public string TimeZoneId { get; init; } = "Eastern Standard Time";
}
