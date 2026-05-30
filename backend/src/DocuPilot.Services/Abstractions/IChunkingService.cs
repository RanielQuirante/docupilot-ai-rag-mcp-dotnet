namespace DocuPilot.Services.Abstractions;

/// <summary>
/// Pure, synchronous, deterministic chunking port (ADR §1). The contract lives in Services and —
/// unlike <see cref="ILlmClient"/>/<see cref="IEmbeddingClient"/>/<see cref="IVectorStore"/> (which
/// wrap external systems and live in Infrastructure) — its single implementation
/// (<c>RecursiveCharacterChunker</c>) also lives in <c>DocuPilot.Services</c>, because chunking is
/// in-process CPU string manipulation with zero external dependency. The port still exists so the
/// embedding orchestrator depends on a contract and tests can stub it.
/// </summary>
public interface IChunkingService
{
    /// <summary>
    /// Splits <paramref name="content"/> into ordered, gap-free chunks using a separator-hierarchy
    /// recursive split (paragraph → line → sentence → whitespace → hard cut), a character budget
    /// (<see cref="ChunkingOptions.MaxChars"/>) and character-level overlap
    /// (<see cref="ChunkingOptions.OverlapChars"/>). <see cref="ChunkingOptions.MaxChunksPerDocument"/>
    /// caps a pathologically large document. Deterministic — same input always yields the same
    /// chunks. A document shorter than the budget yields exactly one chunk (index 0, no overlap);
    /// empty/whitespace yields zero chunks.
    /// </summary>
    /// <param name="content">The source text (e.g. <c>DocumentTexts.Content</c>).</param>
    /// <param name="options">Sizing overrides; <c>null</c> uses the injected <c>Chunking:*</c> config defaults.</param>
    /// <returns>Ordered chunks with 0-based <c>ChunkIndex</c> (0..n-1, gap-free).</returns>
    IReadOnlyList<DocumentChunkContent> Chunk(string content, ChunkingOptions? options = null);
}

/// <summary>
/// One produced chunk (the chunking output shape, ADR §1). Carries the text plus the derived
/// counts the orchestrator persists to <c>DocumentChunks</c>.
/// </summary>
/// <param name="ChunkIndex">0-based, sequential, gap-free order within the document.</param>
/// <param name="Content">The chunk text (including any overlap re-included from the previous chunk).</param>
/// <param name="CharCount">Character length of <paramref name="Content"/>.</param>
/// <param name="TokenEstimate">Token estimate ≈ <c>ceil(CharCount / 4)</c>.</param>
public sealed record DocumentChunkContent(
    int ChunkIndex,
    string Content,
    int CharCount,
    int TokenEstimate);

/// <summary>
/// Chunking sizing overrides. Pass <c>null</c> to <see cref="IChunkingService.Chunk"/> to use the
/// implementation's injected <c>Chunking:*</c> config defaults (ADR §1):
/// <c>MaxChars=4000</c>, <c>OverlapChars=600</c>, <c>MaxChunksPerDocument=1000</c>.
/// </summary>
/// <param name="MaxChars">Maximum characters per chunk (the size budget).</param>
/// <param name="OverlapChars">Characters of the previous chunk's tail re-included at the head of each subsequent chunk.</param>
/// <param name="MaxChunksPerDocument">Hard cap on the number of chunks produced for one document.</param>
public sealed record ChunkingOptions(
    int MaxChars,
    int OverlapChars,
    int MaxChunksPerDocument);
