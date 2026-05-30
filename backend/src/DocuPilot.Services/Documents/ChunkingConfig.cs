namespace DocuPilot.Services.Documents;

/// <summary>
/// Chunking sizing bounds, bound from the <c>Chunking</c> config section (env keys
/// <c>Chunking__MaxChars</c> / <c>Chunking__OverlapChars</c> / <c>Chunking__MaxChunksPerDocument</c>).
/// Defaults per ADR §1: <c>MaxChars=4000</c> (~1,000 tokens), <c>OverlapChars=600</c>
/// (~150 tokens), <c>MaxChunksPerDocument=1000</c>. Lives in Services (the chunker is co-located in
/// Services). DevOps (DA-043) wires these on api + worker and documents them in <c>.env.example</c>.
/// </summary>
public sealed class ChunkingConfig
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Chunking";

    /// <summary>Maximum characters per chunk (the size budget). Default 4,000 (≈ 1,000 tokens).</summary>
    public int MaxChars { get; set; } = 4_000;

    /// <summary>Characters of the previous chunk's tail re-included at the head of each subsequent chunk. Default 600 (≈ 150 tokens).</summary>
    public int OverlapChars { get; set; } = 600;

    /// <summary>Hard cap on the number of chunks produced for one document. Default 1,000.</summary>
    public int MaxChunksPerDocument { get; set; } = 1_000;
}
