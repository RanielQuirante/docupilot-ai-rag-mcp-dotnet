using DocuPilot.Models.Contracts;
using DocuPilot.Services.Search;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-6 semantic search endpoint (DA-045). Thin controller — it binds the request, adapts it into
/// a layer-agnostic <see cref="SearchQuery"/>, delegates to <see cref="ISearchService"/>, and maps
/// the discriminated <see cref="SearchOutcome"/> to a status code (200 / 400 / 503). No business
/// logic. <c>POST</c> (not <c>GET</c>) because natural-language queries are long free text that
/// belongs in a JSON body (ADR §2).
/// </summary>
[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private const int RetryAfterSeconds = 5;

    private readonly ISearchService _searchService;

    public SearchController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// Runs a natural-language semantic search over the embedded documents. Returns <c>200</c> with
    /// the ranked, document-level results (empty array when nothing matched — NOT a 404);
    /// <c>400</c> when the query is empty/whitespace; <c>503</c> (with <c>Retry-After</c>) when the
    /// embedder or Qdrant is temporarily unavailable.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(SearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        // Defensive: a missing/empty body binds to null or a blank query — both are 400 (the service
        // also guards empty/whitespace).
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new { error = "Query must not be empty." });
        }

        var outcome = await _searchService.SearchAsync(
            new SearchQuery(request.Query, request.Limit, request.Category),
            ct);

        switch (outcome.Kind)
        {
            case SearchOutcomeKind.EmptyQuery:
                return BadRequest(new { error = "Query must not be empty." });

            case SearchOutcomeKind.Unavailable:
                // Synchronous, user-facing read path: a down embedder/Qdrant is "try again", not a
                // server defect. 503 + Retry-After — never a 500-storm (ADR §3 / PM Q6).
                Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "Search is temporarily unavailable. Please try again shortly." });

            default:
                var results = outcome.Results
                    .Select(r => new SearchResult(
                        r.DocumentId,
                        r.FileName,
                        r.Classification,
                        r.Score,
                        r.MatchedText,
                        r.ChunkIndex))
                    .ToList();

                return Ok(new SearchResponse(request.Query, results));
        }
    }
}
