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
    /// <c>TextExtracted</c>, OR stuck in <c>GeneratingEmbeddings</c> (latest <c>EmbeddingStarted</c>
    /// audit older than this) is reset to <c>Classified</c> (DA-040), by the stale-claim sweep.
    /// Default 15.
    /// </summary>
    public int StuckResetMinutes { get; set; } = 15;

    /// <summary>
    /// Phase-4 (DA-033) / Phase-5 (DA-040) Transient backoff: how many subsequent ticks to SKIP the
    /// affected pass after a slow downstream pass returns
    /// <see cref="DocuPilot.Services.Documents.ProcessingOutcome.Transient"/> — i.e.
    /// <c>ClassifyAsync</c> when the LLM was unreachable / the model was not loaded (pass 2), OR
    /// <c>EmbedDocumentAsync</c> when the embedder or Qdrant was unreachable (pass 3). Each pass
    /// backs off independently. Prevents the poller from spamming a down dependency every poll
    /// interval; the other passes keep running normally. Default 6 (≈30s of quiet at the 5s default
    /// interval before retrying). A value ≤ 0 disables the backoff (retry every tick).
    /// </summary>
    public int TransientBackoffTicks { get; set; } = 6;
}
