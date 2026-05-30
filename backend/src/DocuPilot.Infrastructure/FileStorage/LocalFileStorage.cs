using DocuPilot.Services.Abstractions;
using Microsoft.Extensions.Options;

namespace DocuPilot.Infrastructure.FileStorage;

/// <summary>
/// Local-filesystem implementation of <see cref="IFileStorage"/> (the Services port).
/// Stores files under the configured root as <c>{yyyy}/{MM}/{dd}/{documentId}{ext}</c>
/// and returns that relative key. The on-disk path is derived solely from the document
/// id and the file extension — the user-supplied filename is never used as a path
/// component, so the storage path is traversal-safe (ADR §1, DA-015 constraint #5).
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly FileStorageOptions _options;
    private readonly TimeProvider _timeProvider;

    public LocalFileStorage(IOptions<FileStorageOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<string> SaveAsync(Stream content, Guid documentId, string fileName, CancellationToken ct)
    {
        // Only the extension is taken from the user filename; the rest is server-controlled.
        var ext = Path.GetExtension(fileName) ?? string.Empty;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Relative key uses forward slashes regardless of host OS — it is a portable
        // storage key persisted in Documents.FilePath, not a host path.
        var relativeKey = $"{now:yyyy}/{now:MM}/{now:dd}/{documentId:N}{ext}";

        var fullPath = Path.Combine(_options.RootPath, relativeKey.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await content.CopyToAsync(fileStream, ct);

        return relativeKey;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var fullPath = Path.Combine(_options.RootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));
        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        return Task.FromResult(stream);
    }
}
