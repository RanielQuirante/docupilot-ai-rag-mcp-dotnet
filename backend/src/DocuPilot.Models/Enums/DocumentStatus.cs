namespace DocuPilot.Models.Enums;

/// <summary>
/// Lifecycle status of an uploaded document. The full set is declared up front
/// (spec §5.1) so the enum does not churn each phase; Phase 2 only ever writes
/// <see cref="Uploaded"/>. Persisted as the enum <b>name</b> (string) — see
/// DocumentConfiguration's <c>HasConversion&lt;string&gt;()</c>.
/// </summary>
public enum DocumentStatus
{
    /// <summary>File stored and metadata persisted. The only value written in Phase 2.</summary>
    Uploaded,

    /// <summary>Queued for AI processing (Phase 3+).</summary>
    Queued,

    /// <summary>Text extraction in progress (Phase 3+).</summary>
    ExtractingText,

    /// <summary>Text successfully extracted and persisted — Phase-3 terminal success; the hand-off marker Phase 4 claims on (Phase 3+).</summary>
    TextExtracted,

    /// <summary>Document classification in progress (Phase 4+).</summary>
    Classifying,

    /// <summary>Classification + metadata extraction succeeded and persisted — Phase-4 terminal success; the hand-off marker Phase 5 claims on (Phase 4+).</summary>
    Classified,

    /// <summary>Embedding generation in progress (Phase 5+).</summary>
    GeneratingEmbeddings,

    /// <summary>Indexed and ready for semantic search (Phase 5/6+).</summary>
    ReadyForSearch,

    /// <summary>Terminal failure at any phase.</summary>
    Failed
}
