using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>Writes trusted canonical Process observation bytes without transport reserialization.</summary>
sealed class CanonicalProcessExecutionJsonResult(
    byte[] content,
    int statusCode,
    string contentType) : IResult
{
    readonly byte[] content = content ?? throw new ArgumentNullException(nameof(content));
    readonly string contentType = Guard.RequireNotNullOrWhiteSpace(contentType);

    /// <summary>Writes the exact canonical bytes to the current HTTP response.</summary>
    /// <param name="httpContext">Current ASP.NET request and response context.</param>
    /// <returns>A task that completes after the response body is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> is <see langword="null"/>.</exception>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = contentType;
        httpContext.Response.ContentLength = content.Length;
        await httpContext.Response.Body
            .WriteAsync(content, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }
}
