namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for a single embedded chunk of a document (table <c>DocumentChunks</c>),
/// the FIRST 1:N child of <see cref="Document"/> (one document → many chunks). Uniqueness is the
/// composite <c>UNIQUE(DocumentId, ChunkIndex)</c> (NOT <c>UNIQUE(DocumentId)</c> like the 1:1
/// children) and the FK is ON DELETE CASCADE (DBA DA-038 §P5.2). SQL is authoritative for the
/// chunk <b>text</b> (Phase-7 RAG / Phase-6 rendering); Qdrant is authoritative for the
/// <b>vector</b>. A plain POCO — all mapping (composite UNIQUE index, cascade FK, <c>nvarchar(max)</c>
/// content) is fluent in <c>DocumentChunkConfiguration</c>.
/// </summary>
public sealed class DocumentChunk
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> — no DB-side default (<c>ValueGeneratedNever</c>). The SQL row id; distinct from <see cref="PointId"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>Documents.Id</c> (ON DELETE CASCADE). Leading column of the composite <c>UNIQUE(DocumentId, ChunkIndex)</c>. Many chunk rows share one <c>DocumentId</c> (1:N).</summary>
    public Guid DocumentId { get; set; }

    /// <summary>0-based, sequential, gap-free order within the document. Trailing column of the composite UNIQUE. The deterministic input to <see cref="PointId"/>.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>The chunk text (<c>nvarchar(max)</c>). SQL is authoritative for the chunk text.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Token estimate ≈ <c>ceil(chars / 4)</c> heuristic (no real tokenizer dependency).</summary>
    public int TokenEstimate { get; set; }

    /// <summary>
    /// The deterministic Qdrant point id for this chunk (a stable namespaced hash of
    /// <c>(DocumentId, ChunkIndex)</c>). The cross-store link SQL→Qdrant; stable across re-embeds.
    /// NOT a FK, NOT unique-indexed (uniqueness is transitive via the composite key) — DA-038 §P5.2.
    /// </summary>
    public Guid PointId { get; set; }

    /// <summary>Chunk-row (re-)persist timestamp (UTC), set via <c>TimeProvider</c>.</summary>
    public DateTime CreatedAt { get; set; }
}
