namespace DocuPilot.Services.Documents;

/// <summary>
/// Layer-agnostic representation of a single uploaded file, produced by the controller
/// from an <c>IFormFile</c>. Keeps the Services layer free of any ASP.NET Core dependency.
/// </summary>
/// <param name="FileName">Original user-supplied filename (display only).</param>
/// <param name="ContentType">MIME content type reported by the client.</param>
/// <param name="Length">File size in bytes.</param>
/// <param name="OpenReadStream">Factory that opens a fresh read stream over the file content.</param>
public sealed record DocumentUploadInput(
    string FileName,
    string ContentType,
    long Length,
    Func<Stream> OpenReadStream);
