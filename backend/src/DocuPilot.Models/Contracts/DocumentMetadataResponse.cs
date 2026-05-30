using System.Text.Json;

namespace DocuPilot.Models.Contracts;

/// <summary>
/// Metadata view returned by <c>GET /api/documents/{id}/metadata</c>. The API parses the stored
/// <c>MetadataJson</c> into a real JSON object (<see cref="JsonElement"/>) so the client does not
/// double-parse a string. An empty extraction is the object <c>{}</c>, never null.
/// </summary>
/// <param name="DocumentId">The owning document id.</param>
/// <param name="Metadata">The parsed metadata JSON object (serialized inline by the API).</param>
/// <param name="Model">The model that produced it (provenance); may be null.</param>
/// <param name="CreatedAt">Extraction timestamp (UTC).</param>
public sealed record DocumentMetadataResponse(
    Guid DocumentId,
    JsonElement Metadata,
    string? Model,
    DateTime CreatedAt);
