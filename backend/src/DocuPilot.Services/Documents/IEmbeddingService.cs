namespace DocuPilot.Services.Documents;

/// <summary>
/// Reusable Phase-5 embedding orchestrator owning the chunk → embed → vector-upsert → persist stage
/// (ADR §5/§6). Mirrors <see cref="IClassificationService"/>: it claims a <c>Classified</c> document
/// internally (atomic <c>Classified → GeneratingEmbeddings</c> CAS — no DB transaction is held
/// across the slow embedding round-trips), chunks the extracted text, embeds each chunk, then writes
/// <b>Qdrant first</b> and finally commits the SQL chunk rows + <c>Status = ReadyForSearch</c> +
/// audit in ONE <see cref="Repository.Abstractions.IUnitOfWork"/> transaction. Exposed as a service
/// (NOT buried in the Worker) so the Worker host (DA-040) calls the same <see cref="EmbedDocumentAsync"/>
/// in a per-document scope — claim semantics live here, not in the caller.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Claims and embeds a single document end-to-end. Atomically claims
    /// <c>Classified → GeneratingEmbeddings</c> (returns <see cref="ProcessingOutcome.NotClaimed"/>
    /// if the claim loses), loads its extracted text, chunks it, embeds every chunk into an
    /// in-memory vector set, then writes Qdrant (delete-by-document then batch upsert) and finally
    /// commits the SQL chunk rows (delete-prior + insert-new with <c>PointId</c>) +
    /// <c>Status = ReadyForSearch</c> + <c>ProcessedAt</c> + clears <c>FailureReason</c> + an
    /// <c>EmbeddingSucceeded</c> audit in ONE transaction. Status flips to <c>ReadyForSearch</c>
    /// LAST, only after both stores succeed (the dual-store consistency invariant, ADR §6).
    /// <para>
    /// Failure semantics (the Worker, DA-040, treats them differently):
    /// <list type="bullet">
    /// <item>A down embedder OR down Qdrant → the document is LEFT <c>Classified</c> (the claim is
    /// rolled back), NOTHING is written to either store, and it returns
    /// <see cref="ProcessingOutcome.Transient"/> — so a temporarily-down dependency does not poison
    /// the backlog (ADR §6 / PM Q4).</item>
    /// <item>A content fault (no extracted text) → the document is set <c>Failed</c> with a
    /// <c>FailureReason</c> and returns <see cref="ProcessingOutcome.Failed"/>.</item>
    /// </list>
    /// </para>
    /// </summary>
    Task<ProcessingOutcome> EmbedDocumentAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Stale-claim recovery for the Phase-5 stage (DA-040 will call this): resets documents stuck in
    /// <c>GeneratingEmbeddings</c> — whose latest <c>EmbeddingStarted</c> audit is older than
    /// <paramref name="staleThreshold"/> (a crash/cancel left them claimed) — back to
    /// <c>Classified</c>, writing an audit row per reset. Returns the number reset. Idempotent across
    /// restarts (delete-first + deterministic point ids + composite UNIQUE mean a re-run never
    /// duplicates chunks/points).
    /// </summary>
    Task<int> RecoverStaleGeneratingEmbeddingsAsync(TimeSpan staleThreshold, CancellationToken ct);
}
