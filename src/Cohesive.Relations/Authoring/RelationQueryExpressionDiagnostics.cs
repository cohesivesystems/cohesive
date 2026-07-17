using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Cohesive.Relations.Authoring;

/// <summary>Stable diagnostic codes emitted while lowering C# expressions to canonical relation/query expressions.</summary>
public static class RelationQueryExpressionDiagnosticCodes
{
    /// <summary>The top-level lambda parameter count does not match the supplied semantic bindings.</summary>
    public const string LambdaBindingCountMismatch = "relationQuery.authoring.expression.lambdaBindingCountMismatch";

    /// <summary>A supplied semantic binding handle is invalid or belongs to a different authoring core.</summary>
    public const string BindingInvalid = "relationQuery.authoring.expression.bindingInvalid";

    /// <summary>The expression-tree node has no exact canonical lowering.</summary>
    public const string NodeUnsupported = "relationQuery.authoring.expression.nodeUnsupported";

    /// <summary>The unary or binary operator has no exact canonical lowering.</summary>
    public const string OperatorUnsupported = "relationQuery.authoring.expression.operatorUnsupported";

    /// <summary>The invoked CLR method is not in the exact semantic allowlist.</summary>
    public const string MethodUnsupported = "relationQuery.authoring.expression.methodUnsupported";

    /// <summary>A CLR conversion cannot be removed without changing observable semantics.</summary>
    public const string ConversionUnsupported = "relationQuery.authoring.expression.conversionUnsupported";

    /// <summary>A captured CLR value is not an explicitly declared relation/query parameter marker.</summary>
    public const string CapturedValueUnsupported = "relationQuery.authoring.expression.capturedValueUnsupported";

    /// <summary>A CLR member chain could not be mapped to a canonical field path.</summary>
    public const string MemberPathUnavailable = "relationQuery.authoring.expression.memberPathUnavailable";

    /// <summary>A literal cannot be represented without executing user code or losing value semantics.</summary>
    public const string LiteralUnsupported = "relationQuery.authoring.expression.literalUnsupported";

    /// <summary>An object or DTO projection is structurally invalid or unsupported.</summary>
    public const string ProjectionInvalid = "relationQuery.authoring.expression.projectionInvalid";

    /// <summary>A constructor parameter cannot be mapped unambiguously to one projected CLR member.</summary>
    public const string ProjectionMemberAmbiguous = "relationQuery.authoring.expression.projectionMemberAmbiguous";

    /// <summary>A framework-owned query-parameter marker is malformed or used outside its supported form.</summary>
    public const string ParameterMarkerInvalid = "relationQuery.authoring.expression.parameterMarkerInvalid";

    /// <summary>A contextual key selector uses a CLR domain whose runtime observation carrier is not portable.</summary>
    public const string KeyDomainUnsupported = "relationQuery.authoring.expression.keyDomainUnsupported";
}

/// <summary>One actionable problem encountered while translating a C# expression tree.</summary>
public sealed record RelationQueryExpressionDiagnostic
{
    /// <summary>Creates an expression-authoring diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable explanation of the problem.</param>
    /// <param name="expressionPath">Stable path to the failing node within the supplied lambda.</param>
    /// <param name="sourceReference">Producer-defined source reference for the containing authoring expression.</param>
    /// <param name="symbol">Optional CLR member, method, operator, or node display associated with the failure.</param>
    /// <param name="suggestion">Optional actionable way to express the same intent using supported semantics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="code"/>, <paramref name="message"/>, <paramref name="expressionPath"/>, or
    /// <paramref name="sourceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/>, <paramref name="message"/>, <paramref name="expressionPath"/>, or
    /// <paramref name="sourceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is not defined.</exception>
    public RelationQueryExpressionDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        string expressionPath,
        string sourceReference,
        string? symbol = null,
        string? suggestion = null)
    {
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");

        Code = Guard.RequireNotNullOrWhiteSpace(code);
        Severity = severity;
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        ExpressionPath = Guard.RequireNotNullOrWhiteSpace(expressionPath);
        SourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);
        Symbol = symbol.TrimmedEmptyOrWhiteSpaceAs();
        Suggestion = suggestion.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation of the problem.</summary>
    public string Message { get; }

    /// <summary>Stable path to the failing node within the supplied lambda.</summary>
    public string ExpressionPath { get; }

    /// <summary>Producer-defined source reference for the containing authoring expression.</summary>
    public string SourceReference { get; }

    /// <summary>Optional CLR member, method, operator, or node display associated with the failure.</summary>
    public string? Symbol { get; }

    /// <summary>Optional actionable way to express the same intent using supported semantics.</summary>
    public string? Suggestion { get; }
}

/// <summary>Fail-closed result from translating one C# expression-authoring input.</summary>
/// <typeparam name="T">Successfully lowered immutable authoring value.</typeparam>
public sealed class RelationQueryExpressionLoweringResult<T>
    where T : class
{
    readonly T? value;

    internal RelationQueryExpressionLoweringResult(
        T? value,
        ImmutableArray<RelationQueryExpressionDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (value is null && Diagnostics.IsDefaultOrEmpty)
            throw new ArgumentException("An unsuccessful lowering result requires at least one diagnostic.", nameof(diagnostics));
        if (value is not null && Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("A successful lowering result cannot contain error diagnostics.", nameof(diagnostics));

        this.value = value;
    }

    /// <summary>Diagnostics produced while translating the expression.</summary>
    public ImmutableArray<RelationQueryExpressionDiagnostic> Diagnostics { get; }

    /// <summary>Whether a complete canonical authoring value was produced without error diagnostics.</summary>
    public bool IsSuccess => value is not null;

    /// <summary>Attempts to obtain the completely lowered authoring value.</summary>
    /// <param name="lowered">Receives the lowered value on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when lowering succeeded; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue([NotNullWhen(true)] out T? lowered)
    {
        lowered = value;
        return lowered is not null;
    }

    /// <summary>Returns the completely lowered authoring value or throws the structured authoring failure.</summary>
    /// <returns>The immutable lowered authoring value.</returns>
    /// <exception cref="RelationQueryExpressionAuthoringException">Translation produced one or more error diagnostics.</exception>
    public T RequireValue() => value ?? throw new RelationQueryExpressionAuthoringException(Diagnostics);
}

/// <summary>Exception carrying structured diagnostics for a failed expression-authoring translation.</summary>
public sealed class RelationQueryExpressionAuthoringException : InvalidOperationException
{
    /// <summary>Creates an expression-authoring exception from structured diagnostics.</summary>
    /// <param name="diagnostics">Diagnostics explaining why no canonical value was produced.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="diagnostics"/> is empty, contains a <see langword="null"/> entry, or contains no error.
    /// </exception>
    public RelationQueryExpressionAuthoringException(
        ImmutableArray<RelationQueryExpressionDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Structured diagnostics explaining the failed lowering.</summary>
    public ImmutableArray<RelationQueryExpressionDiagnostic> Diagnostics { get; }

    static string CreateMessage(ImmutableArray<RelationQueryExpressionDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty
            || diagnostics.Any(static diagnostic => diagnostic is null)
            || !diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new ArgumentException("At least one error diagnostic is required.", nameof(diagnostics));
        }

        var first = diagnostics.First(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return $"C# relation/query expression lowering failed with {first.Code} at '{first.ExpressionPath}': {first.Message}";
    }
}
