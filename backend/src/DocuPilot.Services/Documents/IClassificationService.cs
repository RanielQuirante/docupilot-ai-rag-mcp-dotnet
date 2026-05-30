namespace DocuPilot.Services.Documents;

/// <summary>
/// Reusable Phase-4 orchestrator owning the classification + metadata stage of the pipeline
/// (ADR §2 option B / §5). Mirrors <see cref="IDocumentProcessingService"/>: it claims a
/// <c>TextExtracted</c> document internally (atomic <c>TextExtracted → Classifying</c> CAS), runs
/// the classification LLM call then the metadata LLM call (the metadata prompt takes the
/// classification as input), validates/coerces both responses, and commits classification +
/// metadata + status + audit atomically via <see cref="IUnitOfWork"/>. Exposed as a service (NOT
/// buried in a controller or the Worker) so the Worker host (DA-033) calls the same
/// <see cref="ClassifyAsync"/> in a per-document scope — claim semantics live here, not in the
/// caller (lessons.md DA-024/DA-021).
/// </summary>
public interface IClassificationService
{
    /// <summary>
    /// Claims and classifies a single document end-to-end. Atomically claims
    /// <c>TextExtracted → Classifying</c> (returns <see cref="ProcessingOutcome.NotClaimed"/> if
    /// the claim loses), loads its extracted text, calls the LLM to classify then to extract
    /// metadata, validates/coerces (off-taxonomy → <c>Unknown</c>, confidence clamped to [0,1],
    /// non-object metadata → <c>{}</c>), then commits both child rows + <c>Status = Classified</c>
    /// + <c>ProcessedAt</c> + a <c>ClassificationSucceeded</c> audit in ONE transaction.
    /// <para>
    /// Failure semantics distinguish the two faults the Worker (DA-033) handles differently:
    /// <list type="bullet">
    /// <item>An unparseable classification / content fault (after retries) → the document is set
    /// <c>Failed</c> with a <c>FailureReason</c> and returns <see cref="ProcessingOutcome.Failed"/>.</item>
    /// <item>An LLM-unreachable / model-not-found fault → the document is LEFT <c>TextExtracted</c>
    /// (the claim is rolled back) and returns <see cref="ProcessingOutcome.Transient"/>, so a
    /// temporarily-down LLM does not poison the backlog (ADR §6 / PM Q3).</item>
    /// </list>
    /// </para>
    /// </summary>
    Task<ProcessingOutcome> ClassifyAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Stale-claim recovery for the Phase-4 stage (DA-033 will call this): resets documents stuck
    /// in <c>Classifying</c> — whose latest <c>ClassificationStarted</c> audit is older than
    /// <paramref name="staleThreshold"/> (a crash/cancel left them claimed) — back to
    /// <c>TextExtracted</c>, writing an audit row per reset. Returns the number reset. Idempotent
    /// across restarts (upsert-by-DocumentId means a re-run never duplicates child rows).
    /// </summary>
    Task<int> RecoverStaleClassifyingAsync(TimeSpan staleThreshold, CancellationToken ct);
}
