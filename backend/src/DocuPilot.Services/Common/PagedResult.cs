namespace DocuPilot.Services.Common;

/// <summary>
/// A generic paginated result wrapper. Lives in <c>DocuPilot.Services/Common/</c>
/// (DA-011 §2.2 — pagination types). Surfaced directly as the response body of
/// paged list endpoints (e.g. <c>GET /api/documents</c>).
/// </summary>
/// <typeparam name="T">The item type (a Contract DTO).</typeparam>
/// <param name="Items">The page of items.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="TotalCount">Total number of items across all pages.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount)
{
    /// <summary>Total number of pages given <see cref="TotalCount"/> and <see cref="PageSize"/>.</summary>
    public int TotalPages => PageSize <= 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
