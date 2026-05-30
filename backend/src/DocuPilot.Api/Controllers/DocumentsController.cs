using DocuPilot.Models.Contracts;
using DocuPilot.Services.Common;
using DocuPilot.Services.Documents;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-2 document endpoints: upload and paged library list. Thin controller —
/// it binds the request, adapts <see cref="IFormFile"/> into layer-agnostic inputs,
/// delegates to <see cref="IDocumentService"/>, and returns Contracts. No business logic.
/// </summary>
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentProcessingService _processingService;

    public DocumentsController(IDocumentService documentService, IDocumentProcessingService processingService)
    {
        _documentService = documentService;
        _processingService = processingService;
    }

    /// <summary>
    /// Uploads one or more documents (<c>multipart/form-data</c>). Each file is validated
    /// and stored independently. Returns <c>201</c> with the uploaded/failed breakdown when
    /// at least one file is stored; <c>400</c> when no files are supplied or every file fails.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(26_214_400)] // 25 MB — matches FileStorage:MaxBytes default; framework returns 413 if exceeded.
    [ProducesResponseType(typeof(UploadDocumentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(UploadDocumentResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(
        [FromForm(Name = "files")] List<IFormFile> files,
        CancellationToken ct)
    {
        if (files is null || files.Count == 0)
        {
            return BadRequest(new UploadDocumentResponse([], [new FailedDocument(string.Empty, "No files were supplied.")]));
        }

        var inputs = files
            .Select(f => new DocumentUploadInput(f.FileName, f.ContentType, f.Length, f.OpenReadStream))
            .ToList();

        var result = await _documentService.UploadAsync(inputs, ct);

        // 400 only if the whole batch failed validation; otherwise 201.
        if (result.Uploaded.Count == 0)
        {
            return BadRequest(result);
        }

        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// Returns a page of documents, newest-first. <c>page</c> defaults to 1,
    /// <c>pageSize</c> defaults to 20 and is capped at 100 (normalized in the service).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DocumentListItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DocumentListItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _documentService.ListAsync(page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns the detail view for a single document (metadata, status, failure reason,
    /// extracted-text summary). <c>404</c> if the id does not exist.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDetail>> GetById(Guid id, CancellationToken ct)
    {
        var detail = await _documentService.GetDetailAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>
    /// Returns the full extracted plain text for a document. <c>404</c> if the document
    /// or its extracted text does not exist (e.g. not yet processed, or processing failed).
    /// </summary>
    [HttpGet("{id:guid}/text")]
    [ProducesResponseType(typeof(DocumentTextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentTextResponse>> GetText(Guid id, CancellationToken ct)
    {
        var text = await _documentService.GetTextAsync(id, ct);
        return text is null ? NotFound() : Ok(text);
    }

    /// <summary>
    /// Returns a document's audit timeline, newest-first. Returns <c>200</c> with an empty
    /// array for a document with no events (or one that does not exist).
    /// </summary>
    [HttpGet("{id:guid}/audit")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditLogEntry>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditLogEntry>>> GetAudit(Guid id, CancellationToken ct)
    {
        var audit = await _documentService.GetAuditAsync(id, ct);
        return Ok(audit);
    }

    /// <summary>
    /// Manually (re)queues a document for processing — used to retry a <c>Failed</c> doc or
    /// re-extract a <c>TextExtracted</c>/<c>Uploaded</c> one. Returns <c>202 Accepted</c>
    /// (the work is queued for the Worker, not done synchronously), <c>404</c> if missing, or
    /// <c>409 Conflict</c> if the document is already <c>Queued</c>/<c>ExtractingText</c>.
    /// </summary>
    [HttpPost("{id:guid}/process")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Process(Guid id, CancellationToken ct)
    {
        var result = await _processingService.RequeueAsync(id, ct);
        return result switch
        {
            RequeueResult.Queued => Accepted(),
            RequeueResult.NotFound => NotFound(),
            RequeueResult.Conflict => Conflict(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
