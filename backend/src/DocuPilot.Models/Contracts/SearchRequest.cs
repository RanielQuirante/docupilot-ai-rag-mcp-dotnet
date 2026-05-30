namespace DocuPilot.Models.Contracts;

/// <summary>
/// Request body for <c>POST /api/search</c> (Phase-6 semantic search, DA-045). The natural-language
/// query is embedded server-side and matched against the document-chunk vectors in Qdrant.
/// </summary>
/// <param name="Query">The natural-language search text (required; empty/whitespace → <c>400</c>).</param>
/// <param name="Limit">
/// Maximum number of documents to return; <c>null</c> ⇒ <c>Search:DefaultLimit</c> (10).
/// Server-clamped to <c>[1, Search:MaxLimit]</c> (50).
/// </param>
/// <param name="Category">
/// Optional exact-match filter on the document's classification display string (e.g. "Contract").
/// <c>null</c> ⇒ all categories. An unknown/blank value is ignored gracefully (treated as no filter).
/// </param>
public sealed record SearchRequest(string Query, int? Limit = null, string? Category = null);
