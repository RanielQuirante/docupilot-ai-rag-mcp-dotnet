namespace DocuPilot.Models.Contracts;

/// <summary>
/// Request body for <c>POST /api/ask</c> (Phase-7 RAG question-answering, DA-049). The natural-language
/// question is embedded server-side, matched against the document-chunk vectors in Qdrant, and answered
/// by the chat LLM grounded ONLY in the retrieved chunks (spec §5.9).
/// </summary>
/// <param name="Question">The natural-language question (required; empty/whitespace → <c>400</c>).</param>
/// <param name="TopK">
/// Number of chunks to retrieve as context; <c>null</c> ⇒ <c>Rag:TopK</c> (6). Server-clamped to
/// <c>[1, Rag:MaxTopK]</c> (12).
/// </param>
/// <param name="Category">
/// Optional exact-match filter on the document's classification display string (e.g. "Contract").
/// <c>null</c> ⇒ all categories. An unknown/blank value is ignored gracefully (treated as no filter).
/// </param>
public sealed record AskRequest(string Question, int? TopK = null, string? Category = null);
