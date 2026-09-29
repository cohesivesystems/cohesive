using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

/// <summary>Runtime binding for an exact deterministic hosted Query, independent of an invocation medium.</summary>
/// <remarks>The canonical Query owns identity, contracts and configuration. The host attests that its
/// implementation performs no I/O or shared mutation and uses no ambient identity, clock or randomness.
/// This binding is not a sandbox. Successful results are not cached. Implementations must be thread safe.</remarks>
public abstract class DeterministicHostedQueryBinding
{
    private protected DeterministicHostedQueryBinding(ExecutionDefinitionReference reference,
        ValueContract input, ValueContract result)
    {
        Reference = reference;
        InputContract = input;
        ResultContract = result;
    }

    /// <summary>Exact canonical Query identity, revision and fingerprint.</summary>
    public ExecutionDefinitionReference Reference { get; }
    /// <summary>Canonical invocation contract.</summary>
    public ValueContract InputContract { get; }
    /// <summary>Canonical result contract.</summary>
    public ValueContract ResultContract { get; }

    /// <summary>Binds a valid deterministic Query to its explicitly attested implementation.</summary>
    /// <param name="query">Canonical authority for contracts and immutable configuration.</param>
    /// <param name="implementation">Exact implementation identity/version deployed by the host.</param>
    /// <param name="computation">Pure computation over admitted input and declared configuration.</param>
    /// <returns>A reusable binding with invocation-scoped input and result values.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The declaration or implementation affinity is invalid.</exception>
    public static DeterministicHostedQueryBinding Create<TInput, TResult>(HostedQuery<TInput, TResult> query,
        HostedQueryImplementationReference implementation,
        Func<TInput, PortableValue, CancellationToken, TResult> computation)
        where TInput : notnull where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(computation);
        return CreateOutcome(query, implementation, (input, configuration, cancellation) =>
            Result<TResult, DocumentValidationDiagnostic>.FromSuccess(computation(input, configuration, cancellation)));
    }

    /// <summary>Binds a deterministic computation that can explicitly report inability to produce its declared value.</summary>
    /// <param name="query">Canonical authority for contracts and immutable configuration.</param>
    /// <param name="implementation">Exact implementation identity/version deployed by the host.</param>
    /// <param name="computation">Pure computation returning a value or an error diagnostic; no invocation context is supplied.</param>
    /// <returns>A reusable binding with the same typed admission and result validation as Create.</returns>
    /// <remarks>Failure is runtime evaluation evidence, not an alternate business result schema. Business outcomes
    /// belong in the declared result. Failure diagnostics must also be deterministic and contain no ambient context.
    /// No exceptions are caught or classified by this binding.</remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The declaration or implementation affinity is invalid.</exception>
    public static DeterministicHostedQueryBinding CreateOutcome<TInput, TResult>(HostedQuery<TInput, TResult> query,
        HostedQueryImplementationReference implementation,
        Func<TInput, PortableValue, CancellationToken, Result<TResult, DocumentValidationDiagnostic>> computation)
        where TInput : notnull where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(computation);
        if (!query.IsValid)
            throw new ArgumentException("A deterministic binding requires a valid canonical Query.", nameof(query));
        var definition = query.Definition;
        if (definition.EvaluationSemantics != HostedQueryEvaluationSemantics.DeterministicComputation
            || definition.Implementation != implementation)
            throw new ArgumentException("The deployed implementation must match the exact deterministic Query contract.", nameof(implementation));
        return new Typed<TInput, TResult>(query.Reference, definition.Input, definition.Result,
            definition.Configuration, computation);
    }

    /// <summary>Validates input, invokes the computation, and validates its portable result.</summary>
    /// <returns>The canonical result or structured admission/computation failure evidence.</returns>
    /// <exception cref="ArgumentNullException">Input is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before or after computation.</exception>
    /// <exception cref="ArgumentException">A computation reports failure without an error diagnostic.</exception>
    /// <remarks>Unexpected implementation exceptions propagate. Expected business failures belong in the
    /// declared result value. This method establishes neither authorization nor durable execution evidence.</remarks>
    public abstract Result<PortableValue, DocumentValidationDiagnostic> Evaluate(PortableValue input,
        CancellationToken cancellationToken = default);

    sealed class Typed<TInput, TResult>(ExecutionDefinitionReference reference, ValueContract input,
        ValueContract result, PortableValue configuration,
        Func<TInput, PortableValue, CancellationToken, Result<TResult, DocumentValidationDiagnostic>> computation)
        : DeterministicHostedQueryBinding(reference, input, result)
        where TInput : notnull where TResult : notnull
    {
        public override Result<PortableValue, DocumentValidationDiagnostic> Evaluate(PortableValue input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = HostedQueryValueAdapter.Decode<TInput>(input, InputContract);
            if (decoded.Type == ResultType.Failure)
                return Result<PortableValue, DocumentValidationDiagnostic>.FromFailure(decoded.Failure!);
            var result = computation(decoded.Success!, configuration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Type == ResultType.Failure)
            {
                if (result.Failure is not { Severity: DiagnosticSeverity.Error } failure)
                    throw new ArgumentException("A failed deterministic computation requires an error diagnostic.");
                return Result<PortableValue, DocumentValidationDiagnostic>.FromFailure(failure);
            }
            return HostedQueryValueAdapter.Encode(result.Success!, ResultContract);
        }
    }
}
