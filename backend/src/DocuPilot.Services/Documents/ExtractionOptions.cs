namespace DocuPilot.Services.Documents;

/// <summary>
/// Bounds for text extraction, bound from the <c>Extraction</c> config section
/// (env keys <c>Extraction__TimeoutSeconds</c> / <c>Extraction__MaxAttempts</c> /
/// <c>Extraction__MaxTextChars</c>). Defaults per ADR §3/§6. DevOps (DA-028) wires these
/// in compose + documents them in <c>.env.example</c>.
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Extraction";

    /// <summary>Per-document extraction timeout in seconds (a timed-out attempt is transient). Default 60.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Max attempts for a single processing claim; transient faults only are retried. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Max characters of extracted text; text beyond this is truncated (audited), not failed. Default 5,000,000.</summary>
    public int MaxTextChars { get; set; } = 5_000_000;
}
