namespace DocuPilot.Models.Enums;

/// <summary>
/// Single source of truth for the bidirectional map between <see cref="DocumentCategory"/>
/// members and their spec §5.3 <b>display strings</b> (with spaces, e.g.
/// <c>EmployeeRecord ↔ "Employee Record"</c>). Used by BOTH the EF <c>ValueConverter</c>
/// (persistence) and the classification orchestrator's validator/coercion (DBA DA-031 §P4.5).
/// Keeping one map here means the stored column text, the prompt allowed-list, and the parser
/// all agree exactly — no re-mapping in the read path.
/// </summary>
public static class DocumentCategoryNames
{
    private static readonly IReadOnlyDictionary<DocumentCategory, string> ToDisplayMap =
        new Dictionary<DocumentCategory, string>
        {
            [DocumentCategory.Contract] = "Contract",
            [DocumentCategory.Invoice] = "Invoice",
            [DocumentCategory.EmployeeRecord] = "Employee Record",
            [DocumentCategory.LegalDocument] = "Legal Document",
            [DocumentCategory.ComplianceDocument] = "Compliance Document",
            [DocumentCategory.ClientCorrespondence] = "Client Correspondence",
            [DocumentCategory.PolicyDocument] = "Policy Document",
            [DocumentCategory.Unknown] = "Unknown",
        };

    private static readonly IReadOnlyDictionary<string, DocumentCategory> FromDisplayMap =
        ToDisplayMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The spec display string for a category (e.g. <c>EmployeeRecord → "Employee Record"</c>).</summary>
    public static string ToDisplay(DocumentCategory category) =>
        ToDisplayMap.TryGetValue(category, out var name) ? name : "Unknown";

    /// <summary>
    /// Parses a model-supplied category string against the fixed taxonomy, coercing anything
    /// off-list / null / whitespace to <see cref="DocumentCategory.Unknown"/>. Tolerant of the
    /// spec display form ("Employee Record"), the PascalCase member name ("EmployeeRecord"), and
    /// surrounding whitespace/case (ADR §3 robustness).
    /// </summary>
    public static DocumentCategory Coerce(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DocumentCategory.Unknown;
        }

        var trimmed = value.Trim();

        // Exact spec display string (case-insensitive) — the expected, prompt-emitted form.
        if (FromDisplayMap.TryGetValue(trimmed, out var byDisplay))
        {
            return byDisplay;
        }

        // Fall back to the PascalCase enum member name (e.g. the model echoed "EmployeeRecord"
        // or "employee_record" with separators stripped). Defined members only — Enum.TryParse
        // would otherwise accept numeric strings, so guard with IsDefined.
        var compact = trimmed.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        if (Enum.TryParse<DocumentCategory>(compact, ignoreCase: true, out var byName)
            && Enum.IsDefined(byName))
        {
            return byName;
        }

        return DocumentCategory.Unknown;
    }

    /// <summary>The allowed-category display list, newline-bulleted, for prompt templating.</summary>
    public static IReadOnlyList<string> DisplayNames { get; } = ToDisplayMap.Values.ToArray();
}
