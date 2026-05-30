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
    /// <c>ExtractionStarted</c> audit older than this) is reset to <c>Queued</c>, OR stuck in
    /// <c>Classifying</c> (latest <c>ClassificationStarted</c> audit older than this) is reset to
    /// <c>TextExtracted</c>, by the stale-claim sweep. Default 15.
    /// </summary>
    public int StuckResetMinutes { get; set; } = 15;

    /// <summary>
    /// Phase-4 (DA-033) Transient backoff: how many subsequent ticks to SKIP the classification
    /// pass after <c>ClassifyAsync</c> returns <see cref="DocuPilot.Services.Documents.ProcessingOutcome.Transient"/>
    /// (the LLM was unreachable / the model was not loaded). Prevents the poller from spamming a
    /// down LLM every poll interval; the extraction pass keeps running normally. Default 6 (≈30s
    /// of classification quiet at the 5s default interval before retrying the LLM). A value ≤ 0
    /// disables the backoff (retry every tick).
    /// </summary>
    public int TransientBackoffTicks { get; set; } = 6;
}
