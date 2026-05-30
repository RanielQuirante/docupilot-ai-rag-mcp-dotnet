namespace DocuPilot.Worker;

/// <summary>
/// Configuration for the document-processing poller (bound from the <c>Worker</c> section,
/// env keys <c>Worker__PollIntervalSeconds</c> / <c>Worker__StuckResetMinutes</c> — DA-028).
/// Defaults match the Phase-3 ADR (§9) so the Worker runs correctly with no config supplied.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Worker";

    /// <summary>Seconds between polls for the oldest <c>Queued</c> document. Default 5.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Minutes after which a document stuck in <c>ExtractingText</c> (latest
    /// <c>ExtractionStarted</c> audit older than this) is reset to <c>Queued</c> by the
    /// stale-claim sweep. Default 15.
    /// </summary>
    public int StuckResetMinutes { get; set; } = 15;
}
