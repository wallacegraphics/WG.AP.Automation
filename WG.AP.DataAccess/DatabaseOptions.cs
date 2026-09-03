namespace WG.AP.DataAccess;

/// <summary>
/// Connection settings for the AP database. Bound from the <c>Database</c> configuration section
/// and validated at startup, following the same shape as the other options classes in this
/// solution.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public required string ConnectionString { get; init; }

    /// <summary>
    /// How many times a message may be attempted before it is routed to NeedsReview instead of
    /// being retried again.
    /// </summary>
    /// <remarks>
    /// A policy number, so it lives here rather than as a per-row column or a database constraint —
    /// it will change, and changing it should not need a schema publish. It exists so a message that
    /// fails for a reason the code cannot classify (a PDF that reliably breaks the extractor, an
    /// Ollama model that reliably times out on one document) stops consuming every run. NeedsReview
    /// rather than Error, deliberately: nobody should silently give up on a payable.
    /// </remarks>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Seconds to allow a single database command. 0 uses the provider default.</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Identifies this application in CreatedBy/ModifiedBy, in place of SUSER_SNAME(). The SQL
    /// login this app connects with carries no per-actor meaning, so the app stamps its own
    /// identity explicitly instead of letting the column default pick up the login name.
    /// </summary>
    public string AppIdentity { get; init; } = "AP.Processor";
}
