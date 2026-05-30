using DocuPilot.Models.Contracts;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Common;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Documents;

/// <summary>
/// Business logic for the Phase-2 upload + library use cases. Validates each file
/// (size cap + extension/content-type allow-list + non-empty), stores it via
/// <see cref="IFileStorage"/>, builds a <see cref="Document"/> entity (app-set
/// <c>Guid.CreateVersion7()</c> id, <see cref="DocumentStatus.Uploaded"/>, server-side
/// UTC <c>UploadedAt</c> via <see cref="TimeProvider"/>), persists it through
/// <see cref="IDocumentRepository"/>, and maps entities to Contracts. The entity never
/// leaves this class.
/// </summary>
public sealed class DocumentService : IDocumentService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    /// <summary>Allowed extension → set of acceptable content types (spec §5.1).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowList =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ["application/pdf"],
            [".doc"] = ["application/msword"],
            [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            [".txt"] = ["text/plain"],
        };

    private readonly IDocumentRepository _repository;
    private readonly IFileStorage _fileStorage;
    private readonly TimeProvider _timeProvider;
    private readonly DocumentUploadOptions _options;

    public DocumentService(
        IDocumentRepository repository,
        IFileStorage fileStorage,
        TimeProvider timeProvider,
        IOptions<DocumentUploadOptions> options)
    {
        _repository = repository;
        _fileStorage = fileStorage;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public async Task<UploadDocumentResponse> UploadAsync(IReadOnlyList<DocumentUploadInput> files, CancellationToken ct)
    {
        var uploaded = new List<UploadedDocument>();
        var failed = new List<FailedDocument>();

        foreach (var file in files)
        {
            var error = Validate(file);
            if (error is not null)
            {
                failed.Add(new FailedDocument(file.FileName, error));
                continue;
            }

            var id = Guid.CreateVersion7();

            string storageKey;
            await using (var stream = file.OpenReadStream())
            {
                storageKey = await _fileStorage.SaveAsync(stream, id, file.FileName, ct);
            }

            var document = new Document
            {
                Id = id,
                FileName = file.FileName,
                ContentType = file.ContentType,
                FilePath = storageKey,
                SizeBytes = file.Length,
                Status = DocumentStatus.Uploaded,
                UploadedAt = _timeProvider.GetUtcNow().UtcDateTime,
                ProcessedAt = null,
            };

            await _repository.AddAsync(document, ct);

            uploaded.Add(new UploadedDocument(
                document.Id,
                document.FileName,
                document.ContentType,
                document.SizeBytes,
                document.Status.ToString(),
                document.UploadedAt));
        }

        return new UploadDocumentResponse(uploaded, failed);
    }

    public async Task<PagedResult<DocumentListItem>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1)
        {
            pageSize = DefaultPageSize;
        }
        else if (pageSize > MaxPageSize)
        {
            pageSize = MaxPageSize;
        }

        var (items, totalCount) = await _repository.ListAsync(page, pageSize, ct);

        var dtos = items
            .Select(d => new DocumentListItem(
                d.Id,
                d.FileName,
                d.ContentType,
                d.SizeBytes,
                d.Status.ToString(),
                d.UploadedAt,
                d.ProcessedAt))
            .ToList();

        return new PagedResult<DocumentListItem>(dtos, page, pageSize, totalCount);
    }

    /// <summary>
    /// Validates a single file. Returns a human-readable error message if rejected,
    /// or <c>null</c> if the file is acceptable.
    /// </summary>
    private string? Validate(DocumentUploadInput file)
    {
        if (file.Length <= 0)
        {
            return "File is empty.";
        }

        if (file.Length > _options.MaxBytes)
        {
            var maxMb = _options.MaxBytes / (1024d * 1024d);
            return $"File exceeds the maximum size of {maxMb:0.#} MB.";
        }

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowList.TryGetValue(ext, out var allowedContentTypes))
        {
            return "Unsupported file type.";
        }

        // Content-type must also match the allow-list for that extension (reject
        // extension/content-type mismatches).
        var contentType = file.ContentType?.Split(';')[0].Trim() ?? string.Empty;
        if (!allowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return "File content type does not match its extension.";
        }

        return null;
    }
}
