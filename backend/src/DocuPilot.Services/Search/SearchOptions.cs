namespace DocuPilot.Services.Search;

/// <summary>
/// Bounds for the Phase-6 semantic search read path (DA-045), bound from the <c>Search</c> config
/// section (env keys <c>Search__DefaultLimit</c> / <c>Search__MaxLimit</c> /
/// <c>Search__ChunkOverFetchFactor</c> / <c>Search__MaxChunkFetch</c> /
/// <c>Search__MatchedTextMaxChars</c> / <c>Search__MinScore</c>). ALL keys have sensible code
/// defaults, so search works with ZERO env changes (DevOps DA-048 is optional, docs-only). Lives in
/// Services (mirrors <c>EmbeddingOptions</c>) so the orchestrator binds the same options.
/// </summary>
public sealed class SearchOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Search";

    /// <summary>Documents returned when the request omits <c>limit</c>. Default 10 (ADR §4).</summary>
    public int DefaultLimit { get; set; } = 10;

    /// <summary>Hard clamp on the request <c>limit</c>. Default 50 (ADR §4).</summary>
    public int MaxLimit { get; set; } = 50;

    /// <summary>
    /// Over-fetch factor: fetch <c>limit × this</c> chunk hits from Qdrant so that, after collapsing
    /// same-document chunks to one row each, ≥ <c>limit</c> distinct documents remain. Default 5 (ADR §4).
    /// </summary>
    public int ChunkOverFetchFactor { get; set; } = 5;

    /// <summary>Absolute cap on chunk hits fetched (bounds the Qdrant limit + the SQL <c>IN</c> set). Default 200 (ADR §4).</summary>
    public int MaxChunkFetch { get; set; } = 200;

    /// <summary><c>matchedText</c> display trim, in characters. Default 300 (ADR §2).</summary>
    public int MatchedTextMaxChars { get; set; } = 300;

    /// <summary>
    /// Optional cosine floor: results scoring below this are dropped. Default 0 = OFF (no filtering),
    /// since a model-dependent cutoff risks silently hiding relevant docs at POC scale (ADR §4 / Q7).
    /// </summary>
    public double MinScore { get; set; }
}
