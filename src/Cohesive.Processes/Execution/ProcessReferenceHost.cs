using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Execution;

/// <summary>Complete context of one canonical Process Transition invocation.</summary>
/// <param name="Definition">Exact Transition definition reference.</param>
/// <param name="Subject">Portable authoritative aggregate subject expression result.</param>
/// <param name="Input">Typed Transition invocation input.</param>
/// <param name="Continuation">Logical Process instance and attempt.</param>
/// <param name="Activation">Finite activation performing the invocation.</param>
/// <param name="Token">Durable token performing the invocation.</param>
/// <param name="Node">Canonical invocation node.</param>
/// <param name="Occurrence">Zero-based occurrence of the node in the token history.</param>
/// <param name="ObservedAtUtc">Explicit UTC observation time of the finite activation.</param>
/// <param name="Context">Authority, correlation, delivery, ordering, causation, and provenance evidence.</param>
public sealed record ProcessTransitionInvocation(
    ExecutionDefinitionReference Definition,
    PortableValue Subject,
    PortableValue Input,
    ProcessContinuationIdentity Continuation,
    ActivationId Activation,
    TokenId Token,
    ExecutionNodeId Node,
    long Occurrence,
    DateTimeOffset ObservedAtUtc,
    ProcessActivationContext Context);

/// <summary>Complete context of one canonical Process Relation or Query evaluation.</summary>
/// <param name="Definition">Exact Relation or Query definition reference.</param>
/// <param name="Input">Typed evaluation input.</param>
/// <param name="Continuation">Logical Process instance and attempt.</param>
/// <param name="Activation">Finite activation performing the evaluation.</param>
/// <param name="Token">Durable token performing the evaluation.</param>
/// <param name="Node">Canonical evaluation node.</param>
/// <param name="Occurrence">Zero-based occurrence of the node in the token history.</param>
/// <param name="ObservedAtUtc">Explicit UTC observation time of the finite activation.</param>
/// <param name="Context">Authority, correlation, delivery, ordering, causation, and provenance evidence.</param>
public sealed record ProcessRelationEvaluation(
    ExecutionDefinitionReference Definition,
    PortableValue Input,
    ProcessContinuationIdentity Continuation,
    ActivationId Activation,
    TokenId Token,
    ExecutionNodeId Node,
    long Occurrence,
    DateTimeOffset ObservedAtUtc,
    ProcessActivationContext Context);

/// <summary>Complete context for resolving a portable Signal-target expression.</summary>
/// <param name="Value">Materialized portable target value.</param>
/// <param name="Continuation">Logical Process instance and attempt.</param>
/// <param name="Activation">Finite activation resolving the target.</param>
/// <param name="Token">Durable token sending the Signal.</param>
/// <param name="Node">Canonical Signal node.</param>
/// <param name="Occurrence">Zero-based occurrence of the node in the token history.</param>
/// <param name="ObservedAtUtc">Explicit UTC observation time of the finite activation.</param>
/// <param name="Context">Authority, correlation, delivery, ordering, causation, and provenance evidence.</param>
public sealed record ProcessSignalTargetResolution(
    PortableValue Value,
    ProcessContinuationIdentity Continuation,
    ActivationId Activation,
    TokenId Token,
    ExecutionNodeId Node,
    long Occurrence,
    DateTimeOffset ObservedAtUtc,
    ProcessActivationContext Context);

/// <summary>Success or structured failure returned by a reference host operation.</summary>
public sealed record ProcessOperationResult
{
    [JsonConstructor]
    ProcessOperationResult(
        PortableValue? value,
        ImmutableArray<InteractionEnvelope> emissions,
        DocumentValidationDiagnostic? failure)
    {
        var normalizedEmissions = emissions.IsDefault ? [] : emissions;
        ValidateOutcome(value, normalizedEmissions, failure);
        Value = value;
        Emissions = normalizedEmissions;
        Failure = failure;
    }

    /// <summary>Typed operation result on success.</summary>
    public PortableValue? Value { get; }

    /// <summary>Canonical interactions produced by the interpreted operation.</summary>
    public ImmutableArray<InteractionEnvelope> Emissions { get; }

    /// <summary>Structured failure evidence when the operation did not complete.</summary>
    public DocumentValidationDiagnostic? Failure { get; }

    /// <summary>Whether the operation completed with a typed value.</summary>
    public bool IsSuccessful => Value is not null && Failure is null;

    /// <summary>Determines whether the result is one closed success or failure outcome.</summary>
    /// <returns>
    /// <see langword="true"/> when the result is a valid closed outcome; otherwise <see langword="false"/>.
    /// </returns>
    public bool IsValidOutcome() =>
        HasValidOutcomeState(Value, Emissions, Failure);

    /// <summary>Creates a successful host-operation result.</summary>
    /// <param name="value">Typed materialized operation result.</param>
    /// <param name="emissions">Canonical interactions produced by the operation.</param>
    /// <returns>A successful immutable result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="emissions"/> contains a null entry.</exception>
    public static ProcessOperationResult Completed(
        PortableValue value,
        ImmutableArray<InteractionEnvelope> emissions = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, emissions, failure: null);
    }

    /// <summary>Creates a failed host-operation result.</summary>
    /// <param name="failure">Structured error diagnostic.</param>
    /// <returns>A failed immutable result with no value or emissions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="failure"/> is not an error diagnostic.</exception>
    public static ProcessOperationResult Failed(DocumentValidationDiagnostic failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(value: null, [], failure);
    }

    static void ValidateOutcome(
        PortableValue? value,
        ImmutableArray<InteractionEnvelope> emissions,
        DocumentValidationDiagnostic? failure)
    {
        if ((value is null) == (failure is null))
        {
            throw new ArgumentException(
                "An operation result requires exactly one typed value or structured failure.",
                nameof(value));
        }

        if (emissions.Any(static emission => emission is null))
        {
            throw new ArgumentException("Operation emissions cannot contain null entries.", nameof(emissions));
        }

        if (failure is not null && failure.Severity != DiagnosticSeverity.Error)
        {
            throw new ArgumentException("A failed operation requires an error diagnostic.", nameof(failure));
        }

        if (failure is not null && !emissions.IsEmpty)
        {
            throw new ArgumentException("A failed operation cannot emit interactions.", nameof(emissions));
        }
    }

    static bool HasValidOutcomeState(
        PortableValue? value,
        ImmutableArray<InteractionEnvelope> emissions,
        DocumentValidationDiagnostic? failure) =>
        !emissions.IsDefault
        && (value is null) != (failure is null)
        && !emissions.Any(static emission => emission is null)
        && (failure is null || failure.Severity == DiagnosticSeverity.Error && emissions.IsEmpty);
}

/// <summary>Success or structured failure from explicit Signal-target resolution.</summary>
public sealed record ProcessSignalTargetResult
{
    ProcessSignalTargetResult(InteractionTarget? target, DocumentValidationDiagnostic? failure)
    {
        Target = target;
        Failure = failure;
    }

    /// <summary>Resolved canonical interaction target on success.</summary>
    public InteractionTarget? Target { get; }

    /// <summary>Structured failure evidence when the target could not be resolved.</summary>
    public DocumentValidationDiagnostic? Failure { get; }

    /// <summary>Whether resolution produced a canonical target.</summary>
    public bool IsSuccessful => Target is not null && Failure is null;

    /// <summary>Creates a successful target-resolution result.</summary>
    /// <param name="target">Resolved closed canonical target.</param>
    /// <returns>A successful immutable result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public static ProcessSignalTargetResult Resolved(InteractionTarget target) =>
        new(target ?? throw new ArgumentNullException(nameof(target)), failure: null);

    /// <summary>Creates a failed target-resolution result.</summary>
    /// <param name="failure">Structured error diagnostic.</param>
    /// <returns>A failed immutable result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="failure"/> is not an error diagnostic.</exception>
    public static ProcessSignalTargetResult Failed(DocumentValidationDiagnostic failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Severity != DiagnosticSeverity.Error)
        {
            throw new ArgumentException("Failed target resolution requires an error diagnostic.", nameof(failure));
        }

        return new(target: null, failure);
    }
}

/// <summary>Explicit synchronous evidence port used by the pure Process reference interpreter.</summary>
/// <remarks>
/// Implementations may adapt infrastructure, but this contract performs no asynchronous suspension and exposes no
/// cancellation callback. Semantic cancellation is observed only at declared Process safe points.
/// </remarks>
public interface IProcessReferenceHost
{
    /// <summary>Invokes one exact canonical Transition.</summary>
    /// <param name="invocation">Complete semantic invocation context.</param>
    /// <returns>Typed outcome, produced interactions, or structured failure evidence.</returns>
    ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation);

    /// <summary>Evaluates one exact canonical Relation or Query.</summary>
    /// <param name="evaluation">Complete semantic evaluation context.</param>
    /// <returns>Typed result, produced interactions, or structured failure evidence.</returns>
    ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation);

    /// <summary>Resolves a portable Signal-target value into the closed canonical target union.</summary>
    /// <param name="resolution">Complete semantic target-resolution context.</param>
    /// <returns>A canonical target or structured failure evidence.</returns>
    ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution);
}
