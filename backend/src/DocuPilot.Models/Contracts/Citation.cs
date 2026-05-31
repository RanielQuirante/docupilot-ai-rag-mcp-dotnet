namespace DocuPilot.Models.Contracts;

/// <summary>
/// A single source citation (Phase-7 RAG, DA-049 — spec §5.10). One of the retrieved chunks the answer
/// was grounded in, surfaced so the UI can answer "where did the AI get that answer?". Cited chunks are
/// the FULL retrieved context set ranked by score (the orchestrator does NOT try to parse which chunks
/// the model "used"). Field names are camelCased on the wire.
/// </summary>
/// <param name="DocumentId">The source document's id (links to <c>/documents/:id</c> — the §5.10 trust target).</param>
/// <param name="FileName">The source document's original filename (spec §5.10 "Document: ...").</param>
/// <param name="ChunkIndex">The 0-based chunk index within the document (spec §5.10 "Chunk: ...").</param>
/// <param name="Page">
/// ALWAYS <c>null</c> for the POC — Phase-5 chunks do not persist page numbers/offsets (documented N/A,
/// ADR §2/§3). Kept in the shape for spec-fidelity + future-proofing; the FE shows "chunk #N" instead.
/// </param>
/// <param name="Score">The chunk's cosine similarity score (retrieval relevance; lets the UI rank citations).</param>
/// <param name="Snippet">The relevant passage — the chunk's authoritative SQL <c>Content</c>, trimmed to <c>Rag:SnippetMaxChars</c> (300).</param>
public sealed record Citation(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int? Page,
    float Score,
    string Snippet);
