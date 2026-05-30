namespace DocuPilot.Infrastructure.FileStorage;

/// <summary>
/// Options for <see cref="LocalFileStorage"/>, bound from the <c>FileStorage</c>
/// configuration section (env keys <c>FileStorage__RootPath</c> / <c>FileStorage__MaxBytes</c>).
/// </summary>
public sealed class FileStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FileStorage";

    /// <summary>Absolute root directory under which files are stored. Default <c>/app/files</c> (the container mount).</summary>
    public string RootPath { get; set; } = "/app/files";

    /// <summary>Maximum allowed file size in bytes. Default 25 MB.</summary>
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;
}
