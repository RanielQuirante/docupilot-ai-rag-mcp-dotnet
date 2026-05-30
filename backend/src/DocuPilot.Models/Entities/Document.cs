using DocuPilot.Models.Enums;

namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for an uploaded document (table <c>Documents</c>).
/// A plain POCO — no EF attributes, no behavior. All mapping (column types,
/// lengths, indexes, enum-to-string conversion) is fluent in
/// <c>DocuPilot.Infrastructure.Persistence.Configurations.DocumentConfiguration</c>.
/// This entity must never leave <c>DocumentService</c> — controllers see
/// <c>Contracts/</c> types only (DA-011 §2.6).
/// </summary>
public sealed class Document
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> in the service — no DB-side default.</summary>
    public Guid Id { get; set; }

    /// <summary>Original user-supplied filename. Display only — never used as an on-disk path.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME type, validated against the allow-list in the service.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Relative storage key returned by <c>IFileStorage.SaveAsync</c> (e.g. <c>2026/05/30/{guid}.pdf</c>). Not an absolute path.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Lifecycle status. Phase 2 always writes <see cref="DocumentStatus.Uploaded"/>.</summary>
    public DocumentStatus Status { get; set; }

    /// <summary>Upload timestamp (UTC), set server-side via <c>TimeProvider</c>.</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>Processing-complete timestamp (UTC). Always <c>null</c> in Phase 2; set by the Phase-3 worker on either terminal state (TextExtracted or Failed).</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Short, human-readable failure summary (e.g. "Extraction timed out after 60s").
    /// <c>NULL</c> for non-failed documents; set only on <see cref="DocumentStatus.Failed"/>
    /// and cleared on a successful re-process. Full exception detail lives in
    /// <c>AuditLogs.DetailsJson</c>, not here (DA-023 §P3.4). Added in Phase 3.
    /// </summary>
    public string? FailureReason { get; set; }
}
