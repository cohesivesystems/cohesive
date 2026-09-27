using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Execution;

/// <summary>Complete context of one canonical Process Transition invocation.</summary>
/// <param name="Process">Exact originating Process definition reference.</param>
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
    ExecutionDefinitionReference Process,
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
/// <param name="StartContext">Optional retained, server-admitted start command context. Durable runtimes
/// project this from their start receipt, independently of the current worker identity. It is attribution
/// evidence, not a new authorization grant or proof of continuing permission. Direct interpreters leave it null.</param>
public sealed record ProcessRelationEvaluation(
    ExecutionDefinitionReference Definition,
    PortableValue Input,
    ProcessContinuationIdentity Continuation,
    ActivationId Activation,
    TokenId Token,
    ExecutionNodeId Node,
    long Occurrence,
    DateTimeOffset ObservedAtUtc,
    ProcessActivationContext Context,
    ProcessControlCommandContext? StartContext = null)
{
    /// <summary>Projects retained admission attribution for this instance and authority, replacing any supplied context.</summary>
    /// <remarks>The caller must obtain this evidence from the authoritative retained start receipt. This projection
    /// does not create a grant or establish that the original caller still has permission.</remarks>
    /// <exception cref="ArgumentNullException">The retained context is null.</exception>
    /// <exception cref="InvalidOperationException">The retained start belongs to another instance or authority scope.</exception>
    public ProcessRelationEvaluation WithRetainedStartContext(ProcessControlCommandContext retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (retained.ProcessInstanceId != Continuation.ProcessInstanceId
            || retained.Authorization.AuthorityScope != Context.AuthorityScope)
            throw new InvalidOperationException("Retained start evidence must match the evaluation instance and authority scope.");
        return this with { StartContext = retained };
    }
}

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
        DocumentValidationDiagnostic? failure,
        PortableValue? receiptReference = null)
    {
        if (receiptReference is not null && (failure is not null
            || receiptReference.State != PortableValueState.Concrete
            || !PortableExecutionValidator.Validate(receiptReference).IsValid))
            throw new ArgumentException("Receipt references require a successful operation and a valid concrete portable contract.", nameof(receiptReference));
        ReceiptReference = receiptReference;
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

    /// <summary>Optional typed locator for retained authoritative commit evidence, distinct from the domain result.</summary>
    /// <remarks>The host owns its contract and resolution. A locator is not an authorization grant or current-state
    /// snapshot. Runtimes retain it for exact response reconciliation; domain continuations still bind only Value.
    /// Omission preserves the canonical encoding of older results and their receipt fingerprints.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PortableValue? ReceiptReference { get; }

    /// <summary>Attaches a typed retained-commit locator without changing the declared outcome or emissions.</summary>
    /// <param name="reference">Concrete, self-contained portable locator produced by the authoritative commit boundary.</param>
    /// <returns>A result retaining the same domain outcome and canonical interactions.</returns>
    /// <exception cref="ArgumentNullException">The reference is null.</exception>
    /// <exception cref="ArgumentException">The result failed or the reference is not concrete and valid.</exception>
    /// <exception cref="InvalidOperationException">Different receipt evidence is already attached.</exception>
    public ProcessOperationResult WithReceiptReference(PortableValue reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (ReceiptReference is not null && ReceiptReference != reference)
            throw new InvalidOperationException("A retained receipt reference cannot be replaced by different evidence.");
        return new(Value, Emissions, Failure, reference);
    }

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
    [JsonConstructor]
    ProcessSignalTargetResult(InteractionTarget? target, DocumentValidationDiagnostic? failure)
    {
        if ((target is null) == (failure is null))
        {
            throw new ArgumentException(
                "Signal target resolution requires exactly one resolved target or failure diagnostic.");
        }
        if (failure is not null && failure.Severity != DiagnosticSeverity.Error)
        {
            throw new ArgumentException("Failed target resolution requires an error diagnostic.", nameof(failure));
        }

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
/// cancellation callback. Semantic cancellation is observed only at declared Process safe points. Results must be
/// deterministic for the complete attempt-, activation-, token-, node-, and occurrence-scoped invocation. A host
/// call may be repeated after a crash that happened before its enclosing aggregate commit; externally impure or
/// long-running work must therefore be expressed as a durable Request rather than hidden behind this port.
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
