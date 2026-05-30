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

    public DocumentsController(IDocumentService documentService)
    {
        _documentService = documentService;
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
}
