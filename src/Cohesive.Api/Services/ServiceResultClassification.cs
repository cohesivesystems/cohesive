using Cohesive.Model;

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Api.Services;

/// <summary>Business disposition produced by a declared terminal-result classifier Query.</summary>
/// <remarks>Success permits the declared result projection; it does not itself establish a committed entity result.
/// Rejections expose only diagnostics explicitly selected by the application-owned classifier.</remarks>
public sealed record ServiceResultClassification
{
    /// <summary>Creates a terminal business classification with standard API semantics.</summary>
    /// <exception cref="ArgumentException">The kind is nonterminal or diagnostics contradict the disposition.</exception>
    [JsonConstructor]
    public ServiceResultClassification(ApiResultKind kind, ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (kind is not (ApiResultKind.Success or ApiResultKind.ValidationFailed or ApiResultKind.Forbidden
            or ApiResultKind.NotFound or ApiResultKind.Conflict or ApiResultKind.PreconditionFailed or ApiResultKind.DomainError))
            throw new ArgumentException("A classifier must select success or a terminal business rejection.", nameof(kind));
        diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (diagnostics.Any(item => item is null)
            || (kind == ApiResultKind.Success ? !diagnostics.IsEmpty : !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)))
            throw new ArgumentException("Success has no rejection diagnostics; rejection requires error evidence.", nameof(diagnostics));
        Kind = kind;
        Diagnostics = diagnostics;
    }
    /// <summary>Standard business disposition; the declared result source remains authoritative.</summary>
    public ApiResultKind Kind { get; }
    /// <summary>Application-selected diagnostics safe for the authorized result reader.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
