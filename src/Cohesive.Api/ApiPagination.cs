namespace Cohesive.Api;

/// <summary>
/// Standard API page request shape.
/// </summary>
/// <param name="Limit">Maximum number of items to return.</param>
/// <param name="Cursor">Opaque cursor from a previous page response.</param>
/// <param name="ContinuationToken">Alternate cursor field used by continuation-token APIs.</param>
/// <param name="Offset">Offset into the ordered result set when offset pagination is requested.</param>
/// <param name="Skip">Alternate skip count for skip/limit pagination.</param>
/// <param name="Page">One-based page number for page-number pagination.</param>
/// <param name="PageNumber">Alternate one-based page-number field.</param>
/// <param name="Mode">Optional pagination mode hint. Cursor pagination is preferred for externally visible APIs.</param>
public sealed record ApiPageRequest(
    int? Limit = null,
    string? Cursor = null,
    string? ContinuationToken = null,
    int? Offset = null,
    int? Skip = null,
    int? Page = null,
    int? PageNumber = null,
    ApiPaginationMode? Mode = null
);

/// <summary>
/// Standard API page response shape.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed record ApiPage<T>(
    IReadOnlyList<T> Items,
    ApiPageInfo PageInfo
);

/// <summary>
/// Standard API page metadata.
/// </summary>
/// <param name="HasNextPage">Indicates whether another page is available.</param>
/// <param name="StartCursor">Cursor for the first returned item.</param>
/// <param name="EndCursor">Cursor for the last returned item and the usual value to send as the next request cursor.</param>
/// <param name="ContinuationToken">Alternate cursor field used by continuation-token APIs.</param>
/// <param name="Limit">Applied page size.</param>
/// <param name="Offset">Applied offset when offset pagination is requested.</param>
/// <param name="Skip">Applied skip count when skip/limit pagination is requested.</param>
/// <param name="Page">Applied one-based page number.</param>
/// <param name="PageNumber">Alternate applied one-based page number.</param>
/// <param name="TotalCount">Total matching item count, when the backing store can compute it.</param>
/// <param name="TotalPageCount">Total page count, when a total count is available.</param>
public sealed record ApiPageInfo(
    bool HasNextPage,
    string? StartCursor = null,
    string? EndCursor = null,
    string? ContinuationToken = null,
    int? Limit = null,
    int? Offset = null,
    int? Skip = null,
    int? Page = null,
    int? PageNumber = null,
    int? TotalCount = null,
    int? TotalPageCount = null
);

/// <summary>
/// API pagination mode.
/// </summary>
public enum ApiPaginationMode
{
    /// <summary>
    /// Cursor-based paging. This is the preferred mode for stable public API pagination.
    /// </summary>
    Cursor = 0,

    /// <summary>
    /// Offset-based paging. This remains available for local development, small deterministic lists, and compatible backends.
    /// </summary>
    Offset = 1,

    /// <summary>
    /// Skip/limit paging. This is semantically equivalent to offset paging but matches APIs that expose "skip".
    /// </summary>
    Skip = 2,

    /// <summary>
    /// One-based page-number paging.
    /// </summary>
    PageNumber = 3
}
