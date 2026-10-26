using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Derived endpoint link; the original operation and canonical references remain authoritative.</summary>
/// <param name="Endpoint">Original API operation, including transport and response semantics.</param>
/// <param name="Execution">Selected query, process or service reference; null when not supplied by this binding.</param>
/// <param name="Repository">Entity observation contract; null for non-entity bindings.</param>
public sealed record ServiceEndpointImplementation(ApiOperation Endpoint, ExecutionDefinitionReference? Execution, ValueContract? Repository);

/// <summary>Payload-free query ownership and admitted consistency, projected from successful offline preparation.</summary>
/// <param name="Query">Exact selected query definition.</param>
/// <param name="Plan">Exact compiled input/shape/catalog/demand identity.</param>
/// <param name="Sources">All source and traversal shapes required by the plan.</param>
/// <param name="Storage">Contributing storage model name; a display label, not a new semantic identity.</param>
/// <param name="Consistency">Declared consistency accepted by the provider's offline preparation.</param>
/// <param name="MaximumRows">Complete-result row bound.</param>
/// <param name="MaximumBytes">Decoded-value byte bound.</param>
public sealed record ServiceQueryPlacement(ExecutionDefinitionReference Query, RelationQueryCompiledPlanReference Plan,
    ImmutableArray<QualifiedShapeId> Sources, string Storage, RelationQueryConsistency Consistency, int MaximumRows, long MaximumBytes);

/// <summary>Canonical entity contract and its admitted storage owner.</summary>
/// <param name="Observation">Original observation contract.</param><param name="Storage">Contributing model label.</param>
public sealed record ServiceRepositoryPlacement(ValueContract Observation, string Storage);

/// <summary>Inspection projection of successful offline admission. No native clients, credentials or runtime observations are retained.</summary>
/// <remarks>Not a new executable authority or a claim of deployment readiness. Canonical referenced documents own fingerprints.</remarks>
/// <param name="Queries">Query placements in ordinal definition identity order.</param>
/// <param name="Repositories">Repository placements in ordinal contract order.</param>
public sealed record ServiceStorageExplanation(ImmutableArray<ServiceQueryPlacement> Queries,
    ImmutableArray<ServiceRepositoryPlacement> Repositories);
