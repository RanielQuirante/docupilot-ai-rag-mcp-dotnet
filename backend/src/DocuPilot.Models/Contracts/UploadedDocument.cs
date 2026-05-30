namespace DocuPilot.Models.Contracts;

/// <summary>
/// A successfully uploaded document, returned in the <c>uploaded[]</c> array of
/// <see cref="UploadDocumentResponse"/>. Omits the internal <c>FilePath</c>.
/// </summary>
/// <param name="Id">Generated document identifier.</param>
/// <param name="FileName">Original user-supplied filename.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Status">Lifecycle status (enum name, e.g. "Uploaded").</param>
/// <param name="UploadedAt">Upload timestamp (UTC).</param>
public sealed record UploadedDocument(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    DateTime UploadedAt);
