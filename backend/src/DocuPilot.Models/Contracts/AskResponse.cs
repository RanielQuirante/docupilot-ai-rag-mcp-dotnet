namespace DocuPilot.Models.Contracts;

/// <summary>
/// Response for <c>POST /api/ask</c> (Phase-7 RAG question-answering, DA-049 — spec §5.9/§5.10). A
/// successful ask is ALWAYS a <c>200</c> — including the not-found case (<see cref="AnswerFound"/> =
/// <c>false</c> + the canned string + empty <see cref="Citations"/>), which is NOT a 404/error.
/// </summary>
/// <param name="Question">The question as received (echoed for the caller's convenience).</param>
/// <param name="Answer">
/// The LLM-generated grounded answer text (prose), OR the canned
/// "I could not find enough information in the uploaded documents." when not found.
/// </param>
/// <param name="AnswerFound">
/// <c>false</c> ⇒ the canned not-found path (empty/low-score retrieval OR the model returned the canned
/// string) — the FE renders the not-found warning; <c>true</c> ⇒ a real grounded answer with citations.
/// </param>
/// <param name="Citations">
/// The retrieved chunks the answer was grounded in, ranked by score (best first). EMPTY when
/// <see cref="AnswerFound"/> is <c>false</c>.
/// </param>
public sealed record AskResponse(
    string Question,
    string Answer,
    bool AnswerFound,
    IReadOnlyList<Citation> Citations);
