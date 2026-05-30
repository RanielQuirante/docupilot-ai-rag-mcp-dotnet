namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for a document's extracted plain text (table <c>DocumentTexts</c>),
/// 1:1 with <see cref="Document"/>. The large <c>NVARCHAR(MAX)</c> content lives in its own
/// table so the hot <c>Documents</c> poll/list paths stay lean (DA-023 §P3.2). A plain POCO —
/// all mapping (column types, the UNIQUE <c>DocumentId</c>, the cascade FK) is fluent in
/// <c>DocumentTextConfiguration</c>.
/// </summary>
public sealed class DocumentText
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> — no DB-side default.</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>Documents.Id</c> (ON DELETE CASCADE). UNIQUE — enforces the 1:1 and is the idempotent upsert key.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The extracted plain text (<c>NVARCHAR(MAX)</c>). Truncated to <c>Extraction:MaxTextChars</c> upstream.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Length of <see cref="Content"/> — lets the UI show a char count without dragging the LOB.</summary>
    public int CharCount { get; set; }

    /// <summary>Extraction timestamp (UTC), set via <c>TimeProvider</c>. Updated on every (re-)extraction upsert.</summary>
    public DateTime ExtractedAt { get; set; }
}
