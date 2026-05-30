namespace DocuPilot.Services.Abstractions;

/// <summary>
/// External-service port for persisted file storage. The interface (contract) lives
/// in Services; the implementation (<c>LocalFileStorage</c>) lives in Infrastructure
/// (DA-011 §2.5). This keeps the Services layer free of any filesystem concretion.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Persists the file content and returns the <b>relative</b> storage key to record
    /// in <c>Documents.FilePath</c> (e.g. <c>2026/05/30/{guid}.pdf</c>). The on-disk path
    /// is derived from <paramref name="documentId"/> and the extension of
    /// <paramref name="fileName"/> only — the user-supplied filename is never used as a
    /// path component (traversal-safe).
    /// </summary>
    /// <param name="content">The file content stream.</param>
    /// <param name="documentId">The pre-generated document id used to name the stored file.</param>
    /// <param name="fileName">Original filename — only its extension is used.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The relative storage key persisted in <c>Documents.FilePath</c>.</returns>
    Task<string> SaveAsync(Stream content, Guid documentId, string fileName, CancellationToken ct);

    /// <summary>
    /// Opens a read stream over a previously stored file by its relative storage key.
    /// Not used by upload/list in Phase 2 — defines the read seam for the Phase-3
    /// extraction worker so the interface does not need to change later.
    /// </summary>
    /// <param name="storageKey">The relative storage key returned by <see cref="SaveAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct);
}
