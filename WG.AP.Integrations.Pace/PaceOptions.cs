namespace WG.AP.Integrations.Pace;

public sealed class PaceOptions
{
    public const string SectionName = "Pace";

    public required string BaseUrl { get; init; }

    public required string UserName { get; init; }

    public required string Password { get; init; }

    public int TimeoutSeconds { get; init; } = 100;

    public bool WriteEnabled { get; init; }

    /// <summary>
    /// How many times an invoice is tried while Pace cannot be reached (timeouts, 5xx/429, auth failures)
    /// before it is given up on as an Error. Until then the vendor email stays in the Inbox; after it, the
    /// email is routed to Errors so it cannot sit there unnoticed.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;
}
