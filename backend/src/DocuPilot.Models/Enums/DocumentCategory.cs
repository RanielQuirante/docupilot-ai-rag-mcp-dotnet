namespace DocuPilot.Models.Enums;

/// <summary>
/// The fixed, closed taxonomy of document categories (spec §5.3 verbatim, 8 values incl. the
/// explicit <see cref="Unknown"/> escape hatch). The single source of truth shared by the
/// classification prompt's allowed-list and the orchestrator's validator: the model's answer is
/// validated against this set and anything off-list (or null/empty/unparseable-as-a-category) is
/// coerced to <see cref="Unknown"/> (DBA DA-031 §P4.5 / ADR §3).
/// <para>
/// Persisted as the spec <b>display string</b> (with spaces, e.g. <c>"Employee Record"</c>) via a
/// custom <c>ValueConverter</c> in <c>DocumentClassificationConfiguration</c> — C# enum members
/// cannot contain spaces, so the converter maps each member ↔ its spec text so the stored
/// <c>Classification</c> equals the spec/API/prompt category string with no re-mapping in the read
/// path (DBA DA-031 §P4.5, recommended option 1).
/// </para>
/// </summary>
public enum DocumentCategory
{
    /// <summary>Persisted as <c>"Contract"</c>.</summary>
    Contract,

    /// <summary>Persisted as <c>"Invoice"</c>.</summary>
    Invoice,

    /// <summary>Persisted as <c>"Employee Record"</c>.</summary>
    EmployeeRecord,

    /// <summary>Persisted as <c>"Legal Document"</c>.</summary>
    LegalDocument,

    /// <summary>Persisted as <c>"Compliance Document"</c>.</summary>
    ComplianceDocument,

    /// <summary>Persisted as <c>"Client Correspondence"</c>.</summary>
    ClientCorrespondence,

    /// <summary>Persisted as <c>"Policy Document"</c>.</summary>
    PolicyDocument,

    /// <summary>Explicit fallback / off-taxonomy coercion target. Persisted as <c>"Unknown"</c>.</summary>
    Unknown,
}
