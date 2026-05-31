namespace DocuPilot.Models.Enums;

/// <summary>
/// Audit event names for the document lifecycle (stored as the enum <b>name</b> string in
/// <c>AuditLogs.Action</c>, same human-readable rationale as <see cref="DocumentStatus"/>).
/// The Phase-3 set per DA-023 §P3.3 / ADR §5.1.
/// </summary>
public enum AuditAction
{
    /// <summary>Document queued for processing at upload time (written by the API in the upload transaction).</summary>
    Queued,

    /// <summary>Worker successfully claimed the document (Queued → ExtractingText).</summary>
    ExtractionStarted,

    /// <summary>Text extraction succeeded and was persisted (ExtractingText → TextExtracted).</summary>
    ExtractionSucceeded,

    /// <summary>Text extraction failed after retries (ExtractingText → Failed).</summary>
    ExtractionFailed,

    /// <summary>Document re-queued for processing via the manual /process trigger or a stale-claim reset.</summary>
    ReprocessQueued,

    /// <summary>Worker successfully claimed the document for classification (TextExtracted → Classifying) (Phase 4).</summary>
    ClassificationStarted,

    /// <summary>Classification + metadata extraction succeeded and was persisted (Classifying → Classified) (Phase 4).</summary>
    ClassificationSucceeded,

    /// <summary>Classification failed after retries on a content/parse fault (Classifying → Failed) (Phase 4).</summary>
    ClassificationFailed,

    /// <summary>Worker successfully claimed the document for embedding (Classified → GeneratingEmbeddings) (Phase 5).</summary>
    EmbeddingStarted,

    /// <summary>Chunking + embedding + Qdrant upsert + chunk persist succeeded (GeneratingEmbeddings → ReadyForSearch) (Phase 5).</summary>
    EmbeddingSucceeded,

    /// <summary>Embedding failed on a content fault — no text to embed (GeneratingEmbeddings → Failed) (Phase 5).</summary>
    EmbeddingFailed,

    /// <summary>
    /// A controlled MCP-style tool was invoked (Phase 8). Written by <c>IToolDispatcher</c> on entry,
    /// AFTER schema validation passed, BEFORE the handler runs. <c>EntityName="WorkflowTool"</c>,
    /// <c>EntityId</c> = the args' documentId when present (else <c>Guid.Empty</c>),
    /// <c>DetailsJson = { tool, args }</c>. The "what the AI is doing" entry event.
    /// </summary>
    ToolInvoked,

    /// <summary>
    /// A tool invocation completed successfully (Phase 8). Written by <c>IToolDispatcher</c> on a
    /// successful handler return, in the SAME transaction as any write the handler staged.
    /// <c>DetailsJson = { tool, result }</c>.
    /// </summary>
    ToolSucceeded,

    /// <summary>
    /// A tool invocation was rejected or failed (Phase 8) — unknown tool, schema-invalid args
    /// (rejected BEFORE the handler, zero DB effect), a domain rejection (e.g. document not found),
    /// or a handler exception. <c>DetailsJson = { tool, error }</c>. The safety audit trail: every
    /// bad/blocked AI request leaves a record but never a raw write.
    /// </summary>
    ToolFailed,
}
