namespace DocuPilot.Services.Documents;

/// <summary>
/// Validation limits for document uploads, bound from the <c>FileStorage</c> config
/// section (shares <c>FileStorage__MaxBytes</c> with storage). The allow-list of
/// extensions and content types is fixed in code for Phase 2 (spec §5.1).
/// </summary>
public sealed class DocumentUploadOptions
{
    /// <summary>Configuration section name (shared with file storage).</summary>
    public const string SectionName = "FileStorage";

    /// <summary>Maximum allowed file size in bytes. Default 25 MB.</summary>
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;
}
