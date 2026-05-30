namespace DocuPilot.Models.Contracts;

/// <summary>
/// A file that failed validation/storage during an upload batch, returned in the
/// <c>failed[]</c> array of <see cref="UploadDocumentResponse"/>.
/// </summary>
/// <param name="FileName">Original user-supplied filename of the rejected file.</param>
/// <param name="Error">Human-readable reason the file was rejected.</param>
public sealed record FailedDocument(
    string FileName,
    string Error);
