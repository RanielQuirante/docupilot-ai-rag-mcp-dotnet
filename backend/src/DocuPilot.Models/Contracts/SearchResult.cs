namespace DocuPilot.Models.Contracts;

/// <summary>
/// A single document-level search result (Phase-6 semantic search, DA-045 — mirrors spec §5.8). One
/// row per matching document: the chunk hits from Qdrant are grouped by <c>documentId</c> and the
/// single best-scoring chunk wins, so the same document never repeats. Field names match spec §5.8
/// verbatim (camelCased on the wire); <see cref="ChunkIndex"/> is the only additive field.
/// </summary>
/// <param name="DocumentId">The matching document's id (links to <c>/documents/:id</c>).</param>
/// <param name="FileName">The document's original filename (from <c>Documents.FileName</c>).</param>
/// <param name="Classification">
/// The category display string (e.g. "Contract"); <c>null</c> if the document is somehow
/// unclassified (won't happen for a searchable doc, but null-tolerant).
/// </param>
/// <param name="Score">The cosine similarity of the best-matching chunk (higher = closer, ~0..1).</param>
/// <param name="MatchedText">
/// The matched passage — the authoritative SQL <c>DocumentChunks.Content</c> of the winning chunk,
/// trimmed to <c>Search:MatchedTextMaxChars</c> (300); falls back to the Qdrant snippet if the SQL
/// chunk row is missing.
/// </param>
/// <param name="ChunkIndex">ADDITIVE: the 0-based index of the matched chunk within the document (FE link/debug).</param>
public sealed record SearchResult(
    Guid DocumentId,
    string FileName,
    string? Classification,
    float Score,
    string MatchedText,
    int ChunkIndex);
