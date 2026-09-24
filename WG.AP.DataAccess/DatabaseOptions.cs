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

    /// <summary>
    /// How long a run owns a completed Pace submission's mail routing before another run's recovery
    /// sweep may take it over.
    /// </summary>
    /// <remarks>
    /// Required, with no code default: it must come from configuration, and startup fails if it is
    /// missing. A run that fails to route a submission hands the lease back at its end, so the very next
    /// run retries it regardless of this value; the lease only decides how long a submission waits when
    /// the run that owned it crashed before getting that far. Keep it above how long one run takes: the
    /// lease is what stops an overlapping run from routing (and alerting on) a submission the first run
    /// is still working on, and a needs-review submission is only done once the digest goes out at the
    /// end of the run.
    /// </remarks>
    public required int PaceRoutingLeaseMinutes { get; init; }

    /// <summary>
    /// How far back the Pace mail-routing recovery sweep looks for completed submissions whose routing
    /// was never confirmed. Anything older is left for a person rather than retried automatically.
    /// </summary>
    /// <remarks>
    /// Required, with no code default: it must come from configuration, and startup fails if it is
    /// missing. Only limits how long an automated retry keeps going; it is not what keeps pre-existing
    /// history out of the sweep (RoutingClaimedOn does that, so any value here is safe on the first run
    /// after deploy). 7 covers a weekend plus a holiday of failed scheduled runs. Startup caps it at 30
    /// because a message still failing after a month needs a person, not another retry.
    /// </remarks>
    public required int PaceRecoveryWindowDays { get; init; }

    /// <summary>Seconds to allow a single database command. 0 uses the provider default.</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Identifies this application in CreatedBy/ModifiedBy, in place of SUSER_SNAME(). The SQL
    /// login this app connects with carries no per-actor meaning, so the app stamps its own
    /// identity explicitly instead of letting the column default pick up the login name.
    /// </summary>
    public string AppIdentity { get; init; } = "AP.Processor";
}
