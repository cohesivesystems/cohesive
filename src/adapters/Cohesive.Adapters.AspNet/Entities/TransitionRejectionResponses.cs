using Cohesive.Model.Serialization;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>Exhaustive registration-time classification of a transition's closed outcome codes.</summary>
/// <typeparam name="TCode">Closed outcome discriminator enum.</typeparam>
/// <remarks>Every declared member must be a conflict or explicitly not a rejection. Unknown, duplicate or
/// missing members fail setup. The resulting handler retains immutable prepared messages and wire codes.</remarks>
public sealed class TransitionRejectionResponses<TCode> where TCode : struct, Enum
{
    readonly Dictionary<TCode, Problem?> cases = [];
    internal TransitionRejectionResponses() { }

    /// <summary>Declares an HTTP 409 Problem Details response.</summary>
    /// <param name="code">Domain rejection case.</param>
    /// <param name="title">Public safe problem title.</param>
    /// <param name="detail">Public safe problem explanation.</param>
    /// <returns>This setup builder.</returns>
    /// <exception cref="ArgumentException">A message is blank, the member is undefined or already classified.</exception>
    public TransitionRejectionResponses<TCode> Conflict(TCode code, string title, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Add(code, new(title, detail));
        return this;
    }

    /// <summary>Classifies a case which must never be returned as a rejection.</summary>
    /// <param name="code">A success or other non-rejection outcome.</param>
    /// <returns>This setup builder.</returns>
    /// <exception cref="ArgumentException">The member is undefined or already classified.</exception>
    public TransitionRejectionResponses<TCode> NotRejected(TCode code) { Add(code, null); return this; }

    void Add(TCode code, Problem? problem)
    {
        if (!Enum.IsDefined(code) || !cases.TryAdd(code, problem))
            throw new ArgumentException("Outcome must be declared and classified exactly once.", nameof(code));
    }

    internal Func<TCode, IResult> Build(string codePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codePrefix);
        var prepared = new Dictionary<TCode, (Problem? Problem, string Code)>();
        foreach (var code in Enum.GetValues<TCode>())
        {
            if (!cases.TryGetValue(code, out var problem))
                throw new ArgumentException($"Missing rejection classification for '{code}'.");
            prepared.Add(code, (problem, $"{codePrefix}.{SerializedEnum<TCode>.GetWireName(code)}"));
        }
        return code =>
        {
            if (!prepared.TryGetValue(code, out var response) || response.Problem is not { } problem)
                return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "Invalid transition outcome", detail: "The transition produced an inconsistent rejection outcome.",
                    extensions: new Dictionary<string, object?> { ["code"] = "cohesive.transitions.rejection_mapping_invalid" });
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: problem.Title,
                detail: problem.Detail, extensions: new Dictionary<string, object?> { ["code"] = response.Code });
        };
    }

    sealed record Problem(string Title, string Detail);
}
