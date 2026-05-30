namespace DocuPilot.Models.Contracts;

/// <summary>
/// Response for <c>POST /api/search</c> (Phase-6 semantic search, DA-045). Echoes the query and the
/// ranked, document-level results (best score first). A successful search that found nothing is a
/// <c>200</c> with an empty <see cref="Results"/> array — never a <c>404</c>.
/// </summary>
/// <param name="Query">The query as received (echoed for the caller's convenience).</param>
/// <param name="Results">The ranked results, one per matching document, ordered by score descending.</param>
public sealed record SearchResponse(string Query, IReadOnlyList<SearchResult> Results);
