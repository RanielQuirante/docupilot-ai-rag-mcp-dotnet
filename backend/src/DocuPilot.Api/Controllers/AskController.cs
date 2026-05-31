using DocuPilot.Models.Contracts;
using DocuPilot.Services.Rag;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-7 RAG question-answering endpoint (DA-049). Thin controller — it binds the request, adapts it
/// into a layer-agnostic <see cref="AskQuery"/>, delegates to <see cref="IRagService"/>, and maps the
/// discriminated <see cref="AskOutcome"/> to a status code (200 / 400 / 503). No business logic.
/// <c>POST</c> (not <c>GET</c>) because natural-language questions are long free text that belongs in a
/// JSON body (ADR §2). A successful ask is always a <c>200</c> — including the not-found case
/// (<c>answerFound=false</c> + canned string + empty citations).
/// </summary>
[ApiController]
[Route("api/ask")]
public sealed class AskController : ControllerBase
{
    private const int RetryAfterSeconds = 5;

    private readonly IRagService _ragService;

    public AskController(IRagService ragService)
    {
        _ragService = ragService;
    }

    /// <summary>
    /// Answers a natural-language question grounded ONLY in the uploaded documents (spec §5.9). Returns
    /// <c>200</c> with the grounded answer + the source citations; <c>200</c> with
    /// <c>answerFound=false</c> + the canned not-found string + empty citations when the answer isn't in
    /// the documents (NOT a 404); <c>400</c> when the question is empty/whitespace; <c>503</c> (with
    /// <c>Retry-After</c>) when the embedder, Qdrant, or the chat LLM is temporarily unavailable.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        // Defensive: a missing/empty body binds to null or a blank question — both are 400 (the service
        // also guards empty/whitespace).
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "Question must not be empty." });
        }

        var outcome = await _ragService.AskAsync(
            new AskQuery(request.Question, request.TopK, request.Category),
            ct);

        switch (outcome.Kind)
        {
            case AskOutcomeKind.EmptyQuestion:
                return BadRequest(new { error = "Question must not be empty." });

            case AskOutcomeKind.Unavailable:
                // Synchronous, user-facing read path: a down embedder/Qdrant/LLM is "try again", not a
                // server defect. 503 + Retry-After — never a 500-storm (ADR §6 / PM Q8).
                Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "The assistant is temporarily unavailable. Please try again shortly." });

            default:
                var result = outcome.Result!;
                var citations = result.Citations
                    .Select(c => new Citation(
                        c.DocumentId,
                        c.FileName,
                        c.ChunkIndex,
                        c.Page,
                        c.Score,
                        c.Snippet))
                    .ToList();

                return Ok(new AskResponse(request.Question, result.Answer, result.AnswerFound, citations));
        }
    }
}
