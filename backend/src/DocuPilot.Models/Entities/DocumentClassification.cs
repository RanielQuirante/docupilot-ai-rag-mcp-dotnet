using DocuPilot.Models.Enums;

namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for the LLM's classification of a document (table
/// <c>DocumentClassifications</c>), 1:1 with <see cref="Document"/>. The 1:1 + idempotent
/// upsert is enforced by a UNIQUE index on <c>DocumentId</c> and an ON DELETE CASCADE FK
/// (DBA DA-031 §P4.2). A plain POCO — all mapping (the <see cref="DocumentCategory"/>↔spec-string
/// converter, <c>DECIMAL(5,4)</c> confidence, the UNIQUE + cascade FK) is fluent in
/// <c>DocumentClassificationConfiguration</c>.
/// </summary>
public sealed class DocumentClassification
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> — no DB-side default.</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>Documents.Id</c> (ON DELETE CASCADE). UNIQUE — enforces the 1:1 and is the idempotent upsert key.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The category, always one of the fixed 8-value taxonomy (off-list coerced to <see cref="DocumentCategory.Unknown"/>). Stored as the spec display string.</summary>
    public DocumentCategory Classification { get; set; }

    /// <summary>Model confidence in <c>[0,1]</c> (<c>DECIMAL(5,4)</c>); clamped to the range before persist.</summary>
    public decimal Confidence { get; set; }

    /// <summary>The model's short justification (truncated upstream); may be null.</summary>
    public string? Reason { get; set; }

    /// <summary>The model name that produced the row (e.g. <c>llama3.2:3b</c>) — provenance; may be null.</summary>
    public string? Model { get; set; }

    /// <summary>Classification timestamp (UTC), set via <c>TimeProvider</c>. Updated on every (re-)classification upsert.</summary>
    public DateTime CreatedAt { get; set; }
}
