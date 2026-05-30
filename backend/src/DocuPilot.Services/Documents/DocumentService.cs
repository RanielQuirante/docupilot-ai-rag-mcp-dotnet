using System.Text.Json;
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

    private const string DocumentEntityName = "Document";

    /// <summary>
    /// Allowed extension → set of acceptable content types. Phase 3 (PM Q2) DROPS legacy
    /// <c>.doc</c> — there is no clean pure-managed in-container extractor for binary OLE2.
    /// Allow-list is now <c>.pdf / .docx / .txt</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowList =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ["application/pdf"],
            [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            [".txt"] = ["text/plain"],
        };

    private readonly IDocumentRepository _repository;
    private readonly IDocumentTextRepository _textRepository;
    private readonly IAuditRepository _auditRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _fileStorage;
    private readonly TimeProvider _timeProvider;
    private readonly DocumentUploadOptions _options;

    public DocumentService(
        IDocumentRepository repository,
        IDocumentTextRepository textRepository,
        IAuditRepository auditRepository,
        IUnitOfWork unitOfWork,
        IFileStorage fileStorage,
        TimeProvider timeProvider,
        IOptions<DocumentUploadOptions> options)
    {
        _repository = repository;
        _textRepository = textRepository;
        _auditRepository = auditRepository;
        _unitOfWork = unitOfWork;
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

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var document = new Document
            {
                Id = id,
                FileName = file.FileName,
                ContentType = file.ContentType,
                FilePath = storageKey,
                SizeBytes = file.Length,
                // Q1 auto-enqueue: upload goes straight to Queued so the pipeline auto-runs.
                // "upload" and "enqueue" are a single transaction (no lost-enqueue window).
                Status = DocumentStatus.Queued,
                UploadedAt = now,
                ProcessedAt = null,
                FailureReason = null,
            };

            // Persist the row AND its first lifecycle audit event atomically (DA-023 §P3.8 #6).
            await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
            {
                await _repository.AddTrackedAsync(document, innerCt);
                await _auditRepository.AddAsync(new AuditLog
                {
                    Id = Guid.CreateVersion7(),
                    EntityName = DocumentEntityName,
                    EntityId = document.Id,
                    Action = AuditAction.Queued.ToString(),
                    DetailsJson = JsonSerializer.Serialize(new { toStatus = nameof(DocumentStatus.Queued) }),
                    CreatedAt = now,
                }, innerCt);
            }, ct);

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
                d.ProcessedAt,
                d.FailureReason))
            .ToList();

        return new PagedResult<DocumentListItem>(dtos, page, pageSize, totalCount);
    }

    public async Task<DocumentDetail?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var document = await _repository.GetByIdAsync(id, ct);
        if (document is null)
        {
            return null;
        }

        // Text summary (char count / extracted-at) is present only once text has been
        // extracted; the full LOB is fetched separately via GetTextAsync.
        var text = await _textRepository.GetByDocumentIdAsync(id, ct);

        return new DocumentDetail(
            document.Id,
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.Status.ToString(),
            document.UploadedAt,
            document.ProcessedAt,
            document.FailureReason,
            text?.CharCount,
            text?.ExtractedAt);
    }

    public async Task<DocumentTextResponse?> GetTextAsync(Guid id, CancellationToken ct)
    {
        var text = await _textRepository.GetByDocumentIdAsync(id, ct);
        if (text is null)
        {
            return null;
        }

        return new DocumentTextResponse(text.DocumentId, text.Content, text.CharCount, text.ExtractedAt);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditAsync(Guid id, CancellationToken ct)
    {
        var rows = await _auditRepository.ListByEntityAsync(id, ct);
        return rows
            .Select(a => new AuditLogEntry(a.Id, a.Action, a.DetailsJson, a.CreatedAt))
            .ToList();
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
