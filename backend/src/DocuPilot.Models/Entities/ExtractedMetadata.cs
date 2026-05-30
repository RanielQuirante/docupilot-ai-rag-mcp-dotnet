namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for the LLM's extracted structured metadata (table
/// <c>ExtractedMetadata</c>), 1:1 with <see cref="Document"/>. The metadata is stored
/// <b>schemaless</b> as a single JSON object string (<c>NVARCHAR(MAX)</c>, spec §9.4 verbatim) —
/// doc-type-agnostic, no migration per new field (DBA DA-031 §P4.3). 1:1 + idempotent upsert is
/// enforced by a UNIQUE index on <c>DocumentId</c> and an ON DELETE CASCADE FK. A plain POCO —
/// mapping is fluent in <c>ExtractedMetadataConfiguration</c>.
/// </summary>
public sealed class ExtractedMetadata
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> — no DB-side default.</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>Documents.Id</c> (ON DELETE CASCADE). UNIQUE — enforces the 1:1 and is the idempotent upsert key.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    /// The validated JSON <b>object</b> string the LLM returned (<c>NVARCHAR(MAX)</c>, NOT NULL).
    /// On a metadata-extraction failure (but classification success) this is <c>"{}"</c> — never
    /// NULL, never failing the doc on metadata alone (ADR §4/§6).
    /// </summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>The model name that produced the row — provenance; may be null.</summary>
    public string? Model { get; set; }

    /// <summary>Extraction timestamp (UTC), set via <c>TimeProvider</c>. Updated on every upsert.</summary>
    public DateTime CreatedAt { get; set; }
}
