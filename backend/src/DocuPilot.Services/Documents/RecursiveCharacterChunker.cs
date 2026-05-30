using DocuPilot.Services.Abstractions;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Documents;

/// <summary>
/// Deterministic, dependency-free recursive-character chunker (ADR §1). Splits text on a separator
/// hierarchy — paragraph breaks (<c>\n\n</c>) → single newlines (<c>\n</c>) → sentence terminators
/// (<c>.</c>/<c>!</c>/<c>?</c>) → whitespace → a hard character cut — accumulating pieces until adding
/// the next would exceed <see cref="ChunkingConfig.MaxChars"/>, then emitting a chunk. Each chunk
/// after the first re-includes the trailing <see cref="ChunkingConfig.OverlapChars"/> of the prior
/// chunk so a concept spanning a boundary is retrievable from either side. Produces a 0-based,
/// gap-free <c>ChunkIndex</c>; a document shorter than the budget yields exactly one chunk
/// (no overlap); the total is capped at <see cref="ChunkingConfig.MaxChunksPerDocument"/>.
/// <para>
/// Pure CPU string work, no external dependency — lives in Services (not Infrastructure) per ADR §1
/// so it is directly unit-testable with no network/IO. <c>TokenEstimate ≈ ceil(chars / 4)</c>.
/// </para>
/// </summary>
public sealed class RecursiveCharacterChunker : IChunkingService
{
    private readonly ChunkingConfig _config;

    public RecursiveCharacterChunker(IOptions<ChunkingConfig> config)
    {
        _config = config.Value;
    }

    /// <summary>Token estimate from a character count (the standard ~4-chars/token heuristic, ADR §1).</summary>
    public static int EstimateTokens(int charCount) => (int)Math.Ceiling(charCount / 4.0);

    public IReadOnlyList<DocumentChunkContent> Chunk(string content, ChunkingOptions? options = null)
    {
        var maxChars = Math.Max(1, options?.MaxChars ?? _config.MaxChars);
        // Overlap must be strictly less than the budget, otherwise chunks could never advance.
        var overlapChars = Math.Clamp(options?.OverlapChars ?? _config.OverlapChars, 0, maxChars - 1);
        var maxChunks = Math.Max(1, options?.MaxChunksPerDocument ?? _config.MaxChunksPerDocument);

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        // MaxChars is the TOTAL chunk size INCLUDING overlap (the standard recursive-splitter
        // semantic). So bodies are packed to a reduced budget that reserves room for the overlap
        // prefix, guaranteeing overlap + body <= MaxChars without ever clamping the overlap away.
        var bodyBudget = Math.Max(1, maxChars - overlapChars);

        // 1) Split into atomic pieces no larger than the body budget, on the separator hierarchy.
        var pieces = SplitRecursive(content, bodyBudget);

        // 2) Pack pieces greedily into bodies up to the body budget.
        var packed = Pack(pieces, bodyBudget);

        // 3) Apply character-level overlap (re-include the tail of the previous chunk's BODY) + emit,
        //    honoring the per-document cap. The Emit clamp to MaxChars is a safety net only.
        return Emit(packed, overlapChars, maxChars, maxChunks);
    }

    /// <summary>
    /// Recursively splits <paramref name="text"/> using the separator hierarchy until every piece is
    /// ≤ <paramref name="maxChars"/>. The final fallback is a hard character cut so a single
    /// separator-free run longer than the budget is still bounded.
    /// </summary>
    private static List<string> SplitRecursive(string text, int maxChars)
    {
        var result = new List<string>();
        SplitRecursiveInto(text, maxChars, 0, result);
        return result;
    }

    // Separators in descending preference. -1 indicates the hard-cut fallback.
    private static readonly string[] Separators = ["\n\n", "\n", ". ", "! ", "? ", " "];

    private static void SplitRecursiveInto(string text, int maxChars, int separatorIndex, List<string> result)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length <= maxChars)
        {
            result.Add(text);
            return;
        }

        if (separatorIndex >= Separators.Length)
        {
            // Hard cut: no separator left small enough — slice on the character boundary.
            for (var i = 0; i < text.Length; i += maxChars)
            {
                result.Add(text.Substring(i, Math.Min(maxChars, text.Length - i)));
            }

            return;
        }

        var separator = Separators[separatorIndex];
        var segments = SplitKeepingSeparator(text, separator);

        // If this separator does not actually divide the text, descend to the next separator.
        if (segments.Count <= 1)
        {
            SplitRecursiveInto(text, maxChars, separatorIndex + 1, result);
            return;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment.Length <= maxChars)
            {
                result.Add(segment);
            }
            else
            {
                // Segment still too big — recurse with the next-finer separator.
                SplitRecursiveInto(segment, maxChars, separatorIndex + 1, result);
            }
        }
    }

    /// <summary>
    /// Splits on <paramref name="separator"/> but re-appends it to each preceding segment so no
    /// characters are lost (sentence terminators / newlines stay attached to their sentence/line).
    /// </summary>
    private static List<string> SplitKeepingSeparator(string text, string separator)
    {
        var segments = new List<string>();
        var start = 0;

        while (true)
        {
            var idx = text.IndexOf(separator, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                if (start < text.Length)
                {
                    segments.Add(text[start..]);
                }

                break;
            }

            var end = idx + separator.Length;
            segments.Add(text[start..end]);
            start = end;
        }

        return segments;
    }

    /// <summary>
    /// Greedily packs pieces into chunk buffers up to <paramref name="maxChars"/>. A single piece
    /// is already guaranteed ≤ maxChars by <see cref="SplitRecursive"/>.
    /// </summary>
    private static List<string> Pack(List<string> pieces, int maxChars)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + piece.Length > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.Append(piece);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// Re-includes the trailing <paramref name="overlapChars"/> of the previous chunk at the head of
    /// each subsequent chunk (clamped so a chunk never exceeds <paramref name="maxChars"/>), emits a
    /// gap-free 0-based index, and honors the per-document cap.
    /// </summary>
    private static List<DocumentChunkContent> Emit(List<string> packed, int overlapChars, int maxChars, int maxChunks)
    {
        var emitted = new List<DocumentChunkContent>(Math.Min(packed.Count, maxChunks));
        string? previous = null;

        foreach (var body in packed)
        {
            if (emitted.Count >= maxChunks)
            {
                break;
            }

            string text;
            if (previous is null || overlapChars == 0)
            {
                text = body;
            }
            else
            {
                var overlap = previous.Length <= overlapChars ? previous : previous[^overlapChars..];
                // Never let overlap push the chunk past the budget.
                var combined = overlap + body;
                text = combined.Length > maxChars ? combined[^maxChars..] : combined;
            }

            emitted.Add(new DocumentChunkContent(
                ChunkIndex: emitted.Count,
                Content: text,
                CharCount: text.Length,
                TokenEstimate: EstimateTokens(text.Length)));

            previous = body;
        }

        return emitted;
    }
}
