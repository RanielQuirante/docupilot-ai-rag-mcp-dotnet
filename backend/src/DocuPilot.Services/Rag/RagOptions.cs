namespace DocuPilot.Services.Rag;

/// <summary>
/// Bounds for the Phase-7 RAG question-answering read path (DA-049), bound from the <c>Rag</c> config
/// section (env keys <c>Rag__TopK</c> / <c>Rag__MaxTopK</c> / <c>Rag__ContextMaxChars</c> /
/// <c>Rag__PerChunkMaxChars</c> / <c>Rag__SnippetMaxChars</c> / <c>Rag__MinScore</c>). ALL keys have
/// sensible code defaults, so ask works with ZERO env changes (DevOps DA-052 is optional, docs-only).
/// API-only — the Worker does NOT do RAG. Lives in Services (mirrors <c>SearchOptions</c>) so the
/// orchestrator binds the same options (ADR §4).
/// </summary>
public sealed class RagOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Rag";

    /// <summary>Chunks retrieved as context when the request omits <c>topK</c>. Default 6 (ADR §4).</summary>
    public int TopK { get; set; } = 6;

    /// <summary>Hard clamp on the request <c>topK</c>. Default 12 (ADR §4).</summary>
    public int MaxTopK { get; set; } = 12;

    /// <summary>
    /// Total char budget for the assembled context block (≈1.5k tokens at ~4 chars/tok). Leaves
    /// headroom in <c>llama3.2:3b</c>'s context for the system instruction + question + the generated
    /// answer. Default 6000 (ADR §4).
    /// </summary>
    public int ContextMaxChars { get; set; } = 6000;

    /// <summary>Per-chunk cap before assembly so one giant chunk can't eat the whole budget. Default 2000 (ADR §4).</summary>
    public int PerChunkMaxChars { get; set; } = 2000;

    /// <summary>Citation <c>snippet</c> display trim, in characters (the chunk's authoritative <c>Content</c>). Default 300 (ADR §2/§4).</summary>
    public int SnippetMaxChars { get; set; } = 300;

    /// <summary>
    /// Low cosine floor: when EVERY hit scores below this, the orchestrator short-circuits to the
    /// canned not-found answer WITHOUT calling the LLM (don't feed noise to the model — the grounding
    /// guarantee). Default 0.25, deliberately stricter than search's 0 (ADR §4 / Q4).
    /// </summary>
    public double MinScore { get; set; } = 0.25;
}
