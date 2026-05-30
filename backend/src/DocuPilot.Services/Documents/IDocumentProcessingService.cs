namespace DocuPilot.Services.Documents;

/// <summary>
/// The outcome of a <see cref="IDocumentProcessingService.ProcessAsync"/> call.
/// </summary>
public enum ProcessingOutcome
{
    /// <summary>Text extracted and persisted; document advanced to <c>TextExtracted</c>.</summary>
    Succeeded,

    /// <summary>Extraction failed after retries; document moved to <c>Failed</c> with a reason.</summary>
    Failed,

    /// <summary>The document could not be claimed (not in <c>Queued</c>, or already claimed by another worker).</summary>
    NotClaimed,

    /// <summary>The document id does not exist.</summary>
    NotFound,

    /// <summary>
    /// A transient, retryable-later fault (Phase 4: the LLM was unreachable / the model was not
    /// loaded). The document is LEFT in its pre-claim state (e.g. <c>TextExtracted</c>) — NOT
    /// <c>Failed</c> — so a temporarily-down dependency does not poison the backlog; the Worker
    /// (DA-033) retries it on a later tick (ADR §6 / PM Q3). Never returned by Phase-3
    /// <see cref="IDocumentProcessingService.ProcessAsync"/>.
    /// </summary>
    Transient,
}

/// <summary>
/// Reusable processing orchestrator owning the Phase-3 state machine: claim
/// (<c>Queued → ExtractingText</c>) → extract (timeout + bounded transient retry + char cap)
/// → persist text + advance status + write audit, all transactionally (ADR §2/§5/§6). Exposed
/// as a service (NOT buried in a controller) so the Worker host (DA-025) calls the same
/// <see cref="ProcessAsync"/>. Also offers a manual (re)queue trigger for the API endpoint.
/// </summary>
public interface IDocumentProcessingService
{
    /// <summary>
    /// Claims and processes a single document end-to-end. Atomically claims
    /// <c>Queued → ExtractingText</c> (returns <see cref="ProcessingOutcome.NotClaimed"/> if the
    /// claim loses), extracts text, then commits one terminal transition —
    /// <c>TextExtracted</c> (success) or <c>Failed</c> (after retries) — together with the text
    /// upsert and an audit row in a single transaction.
    /// </summary>
    Task<ProcessingOutcome> ProcessAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Manual (re)process trigger backing <c>POST /api/documents/{id}/process</c>. Moves a
    /// document in <c>Failed</c>/<c>Uploaded</c>/<c>TextExtracted</c> back to <c>Queued</c>
    /// (clears <c>FailureReason</c>, writes a <c>ReprocessQueued</c> audit row). Returns
    /// <see cref="RequeueResult.NotFound"/> if missing, or <see cref="RequeueResult.Conflict"/>
    /// if already <c>Queued</c>/<c>ExtractingText</c>.
    /// </summary>
    Task<RequeueResult> RequeueAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Stale-claim recovery (ADR §6, PM Q4 — audit-timestamp, no <c>ClaimedAt</c> column). Resets
    /// documents stuck in <c>ExtractingText</c> — whose latest <c>ExtractionStarted</c> audit is
    /// older than <paramref name="staleThreshold"/> (i.e. the Worker crashed or was cancelled mid
    /// extraction) — back to <c>Queued</c>, writing a <c>ReprocessQueued</c> audit row per reset.
    /// Makes processing idempotent across restarts: no document is permanently stranded, and the
    /// <c>DocumentTexts</c> upsert keeps a re-run from duplicating text. Called by the Worker
    /// poller (DA-025) on a schedule. Returns the number of documents reset.
    /// </summary>
    Task<int> RecoverStaleClaimsAsync(TimeSpan staleThreshold, CancellationToken ct);
}

/// <summary>Outcome of <see cref="IDocumentProcessingService.RequeueAsync"/>.</summary>
public enum RequeueResult
{
    /// <summary>The document was re-queued (now <c>Queued</c>).</summary>
    Queued,

    /// <summary>The document id does not exist (→ 404).</summary>
    NotFound,

    /// <summary>The document is already <c>Queued</c>/<c>ExtractingText</c> (→ 409).</summary>
    Conflict,
}
