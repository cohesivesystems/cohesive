using System.Collections.Immutable;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

/// <summary>A prepared execution binding for one canonical query and its local typed result projection.</summary>
/// <typeparam name="TInput">Canonical invocation parameter type.</typeparam>
/// <typeparam name="TResult">Application result type, including the definition's empty-result policy.</typeparam>
/// <remarks>
/// Retain at host lifetime; implementations must support concurrent reads with invocation-local parameters,
/// cancellation and results. Preparation and backend selection belong to host registration, not API routing.
/// This complete-result convenience contract does not replace <see cref="IRelationQueryEvaluator"/> or its
/// phase evidence. An implementation must reject incomplete execution rather than present partial rows as complete.
/// Backend consistency and resource limits remain explicit properties of the selected execution binding.
/// </remarks>
public interface IRelationQueryReader<TInput, TResult>
{
    /// <summary>The exact authored query and result projection realized by this reader.</summary>
    RelationQuery<TInput, TResult> Definition { get; }

    /// <summary>Reads one complete result without recompiling the query or retaining invocation values.</summary>
    /// <param name="input">Value for the definition's invocation parameter.</param>
    /// <param name="cancellationToken">Cancels delegated backend work and result acquisition.</param>
    /// <returns>The definition's typed result; absence follows its declared projection policy.</returns>
    /// <exception cref="OperationCanceledException">The invocation was canceled.</exception>
    /// <remarks>Validation, provider and projection failures propagate. No implicit retry or partial-success fallback
    /// is permitted by this contract. The caller retains ownership of resources supplied at registration.</remarks>
    Task<TResult> ReadAsync(TInput input, CancellationToken cancellationToken = default);
}

/// <summary>Prepared execution of one bounded, unpaged canonical query-row branch.</summary>
/// <remarks>
/// Implementations preserve missing fields separately from explicit null, fail on incomplete or over-budget
/// results, and support concurrent calls. This is a complete row-result boundary, not a primitive source reader:
/// <see cref="Cohesive.Relations.Acquisition.IRelationQuerySourceReader"/> supplies acquisition evidence to the
/// existing composed evaluator. It must not be adapted here by discarding completeness or requirement gaps.
/// </remarks>
public interface IRelationQueryRowsReader
{
    /// <summary>Executes the already-prepared branch with invocation-local parameters.</summary>
    /// <param name="parameters">Canonical parameter identities and values validated by the prepared binding.</param>
    /// <param name="cancellationToken">Cancels all delegated reads.</param>
    /// <returns>Complete canonical observations, retaining outer-join absence until typed projection.</returns>
    /// <exception cref="ArgumentException">Parameters violate the prepared binding.</exception>
    /// <exception cref="InvalidOperationException">Complete execution cannot be established within its bounds.</exception>
    /// <exception cref="OperationCanceledException">The invocation was canceled.</exception>
    /// <remarks>Provider failures propagate without automatic retry. No paging, global snapshot or evidence-model
    /// conversion is implied; use the full evaluator when phase and source-read evidence is required.</remarks>
    Task<ImmutableArray<ObservationValue>> ReadAsync(
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters,
        CancellationToken cancellationToken = default);
}
