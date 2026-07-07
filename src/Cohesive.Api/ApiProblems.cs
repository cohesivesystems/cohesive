namespace Cohesive.Api;

/// <summary>
/// Standard semantic API problem payload.
/// </summary>
/// <param name="Code">Stable machine-readable problem code.</param>
/// <param name="Message">Human-readable problem message.</param>
/// <param name="Target">Optional field, entity, or resource target associated with the problem.</param>
/// <param name="Extensions">Optional structured extension data keyed by stable semantic names.</param>
public sealed record ApiProblem(
    string Code,
    string Message,
    string? Target = null,
    IReadOnlyDictionary<string, string[]>? Extensions = null);

/// <summary>
/// Standard validation issue payload used inside validation problem results.
/// </summary>
/// <param name="Field">Optional field or path that failed validation.</param>
/// <param name="Code">Stable machine-readable issue code.</param>
/// <param name="Message">Human-readable validation issue message.</param>
public sealed record ApiValidationIssue(
    string? Field,
    string Code,
    string Message);

/// <summary>
/// Standard validation problem payload for invalid API requests.
/// </summary>
/// <param name="Code">Stable machine-readable validation problem code.</param>
/// <param name="Message">Human-readable validation problem message.</param>
/// <param name="Issues">Validation issues that explain why the request was rejected.</param>
/// <param name="Target">Optional field, entity, or resource target associated with the validation problem.</param>
public sealed record ApiValidationProblem(
    string Code,
    string Message,
    IReadOnlyList<ApiValidationIssue> Issues,
    string? Target = null);

/// <summary>
/// Standard conflict problem payload for state, version, or concurrency conflicts.
/// </summary>
/// <param name="Code">Stable machine-readable conflict code.</param>
/// <param name="Message">Human-readable conflict message.</param>
/// <param name="ConflictToken">Optional current token or version that caused the conflict.</param>
/// <param name="Target">Optional field, entity, or resource target associated with the conflict.</param>
public sealed record ApiConflictProblem(
    string Code,
    string Message,
    string? ConflictToken = null,
    string? Target = null);
