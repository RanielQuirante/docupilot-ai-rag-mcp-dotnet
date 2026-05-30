namespace DocuPilot.Services.Abstractions;

/// <summary>
/// External-service port for the vector store (ADR §3). The contract lives in Services; the gRPC
/// implementation (<c>QdrantVectorStore</c> over the official <c>Qdrant.Client</c>) lives in
/// Infrastructure, keeping the vector-DB client out of Services so the orchestrator depends only on
/// this contract and unit tests can stub it. One collection (<c>document_chunks</c>, Cosine) holds
/// ALL documents' chunks; per-document scoping is a payload filter on <c>documentId</c>. Point ids
/// are deterministic from <c>(documentId, chunkIndex)</c> so upsert is idempotent (ADR §3/§6).
/// <para>
/// <see cref="SearchAsync"/> is an INTERNAL verification seam only (QA proves vectors are queryable,
/// ADR §9) — the user-facing search endpoint is Phase 6, explicitly out of Phase-5 scope.
/// </para>
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Create-if-absent + dimension-validate (bootstrap, ADR §3). Creates the collection at
    /// <paramref name="dimensions"/> (Cosine) if it does not exist; if it exists, validates its
    /// configured vector size equals <paramref name="dimensions"/> and <b>fails loudly</b>
    /// (throws) on a mismatch rather than silently upserting garbage (the §2 silent-break guard).
    /// </summary>
    /// <exception cref="VectorStoreUnavailableException">Qdrant is unreachable / RPC error.</exception>
    /// <exception cref="InvalidOperationException">An existing collection's dimension does not match <paramref name="dimensions"/> (fail-loud).</exception>
    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);

    /// <summary>
    /// Batch-upserts all of a document's chunk vectors in ONE call. Deterministic point ids mean a
    /// re-upsert overwrites the same points rather than duplicating (idempotent, ADR §6).
    /// </summary>
    /// <exception cref="VectorStoreUnavailableException">Qdrant is unreachable / RPC error.</exception>
    Task UpsertChunksAsync(IReadOnlyList<ChunkVector> chunks, CancellationToken ct);

    /// <summary>
    /// Deletes all points whose payload <c>documentId</c> equals <paramref name="documentId"/>
    /// (a Qdrant filter delete) — the re-embed / idempotency primitive (delete-before-write, ADR §6).
    /// </summary>
    /// <exception cref="VectorStoreUnavailableException">Qdrant is unreachable / RPC error.</exception>
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Cosine ANN search (INTERNAL verification seam, ADR §3/§9 — not the Phase-6 user search).
    /// Returns the top <paramref name="limit"/> hits, optionally scoped to one
    /// <paramref name="documentId"/> via the payload filter.
    /// </summary>
    /// <exception cref="VectorStoreUnavailableException">Qdrant is unreachable / RPC error.</exception>
    Task<IReadOnlyList<ChunkHit>> SearchAsync(float[] query, int limit, Guid? documentId, CancellationToken ct);
}

/// <summary>A chunk vector to upsert (ADR §3). The full chunk text is NOT sent — only a short snippet.</summary>
/// <param name="PointId">Deterministic Qdrant point id (from <c>(DocumentId, ChunkIndex)</c>).</param>
/// <param name="Vector">The embedding (length = collection dimension).</param>
/// <param name="DocumentId">Owning document — the indexed payload filter/scope key.</param>
/// <param name="ChunkId">The SQL <c>DocumentChunks.Id</c> back-reference (payload).</param>
/// <param name="ChunkIndex">0-based order within the document (payload).</param>
/// <param name="Snippet">First ~200 chars of the chunk for debug/preview (payload) — NOT the full text.</param>
public sealed record ChunkVector(
    Guid PointId,
    float[] Vector,
    Guid DocumentId,
    Guid ChunkId,
    int ChunkIndex,
    string? Snippet);

/// <summary>A search hit (ADR §3 — internal verification seam).</summary>
/// <param name="PointId">The matched Qdrant point id.</param>
/// <param name="DocumentId">Owning document (from payload).</param>
/// <param name="ChunkId">The SQL chunk-row id (from payload).</param>
/// <param name="ChunkIndex">0-based order within the document (from payload).</param>
/// <param name="Score">Cosine similarity score (higher = closer).</param>
/// <param name="Snippet">The chunk snippet (from payload), if present.</param>
public sealed record ChunkHit(
    Guid PointId,
    Guid DocumentId,
    Guid ChunkId,
    int ChunkIndex,
    float Score,
    string? Snippet);

/// <summary>
/// Thrown by <see cref="IVectorStore"/> when Qdrant is unreachable / an RPC fails — i.e. the vector
/// store itself is unavailable, NOT a per-document content fault. The embedding orchestrator treats
/// this as <b>transient</b>: it leaves the document <c>Classified</c> (no <c>Failed</c>, nothing
/// written) so a temporarily-down Qdrant does not poison the backlog (ADR §6 / PM Q4), mirroring
/// <see cref="EmbeddingUnavailableException"/>.
/// </summary>
public sealed class VectorStoreUnavailableException : Exception
{
    public VectorStoreUnavailableException(string message)
        : base(message)
    {
    }

    public VectorStoreUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
