using System.Collections.Immutable;
using Cohesive.Api.Execution.Services;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Storage;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>Prepares endpoint-selected heterogeneous queries once for native binding and infrastructure inference.</summary>
public static class PostgresComposedServiceExtensions
{
    /// <summary>Derives source acquisition requirements and bounded evaluation requirements from actual physical preparation.</summary>
    /// <param name="bindings">Offline PostgreSQL mappings.</param><param name="plan">Authoritative endpoint selections.</param>
    /// <param name="sources">Offline remote source contracts.</param><param name="policy">Explicit bounds/partition policy for each selected query.</param>
    /// <returns>Prepared readers' metadata and attributable infrastructure evidence; no clients or IO.</returns>
    /// <exception cref="ServiceBindingValidationException">A query requires a shared snapshot.</exception>
    public static PostgresComposedServicePreparation PrepareComposedService(this PostgresPersistenceBindings bindings,
        ServiceImplementationPlan plan, EntityRelationQuerySourceCatalog sources,
        Func<int, long, PostgresRelationQueryComposedPolicy> policy)
    {
        ArgumentNullException.ThrowIfNull(bindings); ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(policy);
        var queries = plan.QueryDependencies.Queries.Select(selection =>
        {
            if (selection.Consistency != RelationQueryConsistency.IndependentSources)
                throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported, DiagnosticSeverity.Error,
                    "Heterogeneous acquisition cannot satisfy a single-snapshot query requirement.", "/bindings/queries/consistency")]));
            var selectedPolicy = policy(selection.MaximumRows, selection.MaximumBytes);
            if (selectedPolicy.SourcePolicy.MaximumRowsPerRead != selection.MaximumRows
                || selectedPolicy.SourcePolicy.MaximumPageBytes != selection.MaximumBytes)
                throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported,
                    DiagnosticSeverity.Error, "Composed policy must preserve the selected result bounds.", "/bindings/queries/bounds")]));
            return (selection, bindings.PrepareComposed(selection.Request, sources, selectedPolicy));
        }).ToImmutableArray();
        return new(bindings.Database, queries);
    }
}

/// <summary>Exact selected plans and evidence for independently observed sources.</summary>
/// <remarks>Requirements describe bounded plan execution, not live access or schema readiness. A native prefix's
/// SQL snapshot never extends to remote reads. Physical-plan and source fingerprints remain evidence, not capability IDs.</remarks>
public sealed class PostgresComposedServicePreparation
{
    readonly ImmutableArray<(ServiceQuerySelection Selection, PostgresComposedQueryPreparation Prepared)> queries;
    /// <summary>Local database acquisition requirements and evidence.</summary>
    public ComposedQueryResourceEvidence Postgres { get; }
    /// <summary>Remote acquisition requirements keyed by canonical source identity.</summary>
    public ImmutableDictionary<RelationQuerySourceInstanceId, ComposedQueryResourceEvidence> Sources { get; }
    /// <summary>Bounded interpreter evidence to attach to the executing workload.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidence> ExecutionEvidence { get; }
    /// <summary>Explicit independent-source requirements for the workload.</summary>
    public ImmutableArray<InfrastructureCapabilityId> Requirements => [.. ExecutionEvidence.Select(item => item.Capability)];

    internal PostgresComposedServicePreparation(PostgresRelationQueryDatabaseId database,
        ImmutableArray<(ServiceQuerySelection Selection, PostgresComposedQueryPreparation Prepared)> queries)
    {
        if (queries.IsEmpty) throw new ArgumentException("At least one selected query is required.", nameof(queries));
        this.queries = queries;
        List<InfrastructureCapabilityEvidence> local = [], execution = [];
        Dictionary<RelationQuerySourceInstanceId, List<InfrastructureCapabilityEvidence>> remote = [];
        foreach (var (selection, prepared) in queries)
        {
            var key = Uri.EscapeDataString(selection.Reference.DefinitionId.Value) + "/" + Uri.EscapeDataString(selection.Reference.RevisionId.Value);
            var root = "relations/query-independent-sources/v1/" + key;
            var proof = prepared.Physical.Plan!.Fingerprint;
            var references = ImmutableArray.Create(SourceReference.Create("relations-physical-plan", proof.Value),
                SourceReference.Create("relations-placement", prepared.Placement.Fingerprint.Value),
                SourceReference.Create("query-definition", selection.Reference.Fingerprint.Value),
                SourceReference.Create("query-result-bounds", FormattableString.Invariant($"rows/{selection.MaximumRows}/bytes/{selection.MaximumBytes}")));
            if (prepared.Prefix is { } prefix)
                references = references.Add(SourceReference.Create("postgres-artifact", prefix.Fingerprint.Value))
                    .Add(SourceReference.Create("postgres-binding", prefix.StorageBinding.Fingerprint.Value));
            else if (prepared.Storage is { } storage)
                references = references.Add(SourceReference.Create("postgres-binding", storage.Fingerprint.Value));
            InfrastructureCapabilityEvidence Leaf(string suffix, string kind)
            {
                var capability = new InfrastructureCapabilityId(root + "/" + suffix);
                return new(new(root + "/evidence/" + suffix), capability, CapabilityRealizationKind.Native,
                    sourceReferences: references.Add(SourceReference.Create("adapter", kind)));
            }
            // A local leaf is a native prefix statement or bounded source acquisition, never a whole-query snapshot.
            local.Add(Leaf("postgres-acquisition", prepared.Prefix is null ? "postgres/bounded-acquisition/v1" : "postgres/native-prefix/v1"));
            foreach (var source in prepared.Sources)
            {
                if (!remote.TryGetValue(source.Source.Id, out var evidence)) remote.Add(source.Source.Id, evidence = []);
                evidence.Add(Leaf("source/" + Uri.EscapeDataString(source.Shape.ToString()), "relations/source-profile/" + source.Source.Id.Value));
            }
            execution.Add(Leaf("bounded-evaluation", "relations/physical-executor/v1"));
        }
        Postgres = new(database.Value, "postgres", local);
        Sources = remote.ToImmutableDictionary(pair => pair.Key, pair => new ComposedQueryResourceEvidence(pair.Key.Value, "source", pair.Value));
        ExecutionEvidence = [.. execution];
    }

    /// <summary>Binds an exactly selected query using its already prepared plan and requested bounds.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="persistence">Native PostgreSQL runtime.</param><param name="query">Selected query authority.</param>
    /// <param name="sources">Runtime remote registrations.</param><param name="rows">Requested row bound.</param><param name="bytes">Requested payload bound.</param>
    /// <returns>Prepared reader with independent-source consistency.</returns>
    public IRelationQueryReader<TInput, TResult> Bind<TInput, TResult>(PostgresPersistenceRegistration persistence,
        RelationQuery<TInput, TResult> query, EntityRelationQuerySourceCatalog sources, int rows, long bytes)
    {
        var entry = queries.SingleOrDefault(item => ReferenceEquals(item.Selection.Request, query.CompilationRequest));
        if (entry.Selection is null || entry.Selection.MaximumRows != rows || entry.Selection.MaximumBytes != bytes)
            throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryUnselected, DiagnosticSeverity.Error,
                "Query authority and bounds must match the prepared endpoint selection.", "/bindings/queries")]));
        return persistence.BindComposed(query, entry.Prepared, sources);
    }
}

/// <summary>Plan-derived acquisition evidence for one explicitly associated native resource.</summary>
public sealed class ComposedQueryResourceEvidence : IInfrastructureResourcePreparation
{
    readonly InfrastructureTargetImplementation implementation;
    readonly string identity;
    internal ComposedQueryResourceEvidence(string identity, string kind, IEnumerable<InfrastructureCapabilityEvidence> evidence)
    {
        this.identity = identity;
        implementation = InfrastructureTargetImplementation.ResourceImplementation(new("query-source/" + kind + "/" + Uri.EscapeDataString(identity)), evidence.ToArray());
        Requirements = [.. implementation.Evidence.Select(item => item.Capability)];
    }
    /// <inheritdoc />
    public ImmutableArray<InfrastructureCapabilityId> Requirements { get; }
    /// <summary>Checks resource affinity before exposing the implementation.</summary>
    /// <param name="identity">Exact native database name or canonical remote source identity.</param>
    /// <returns>Acquisition evidence for this resource only.</returns>
    public InfrastructureTargetImplementation ForSource(string identity) => this.identity == identity ? implementation
        : throw new ArgumentException("Prepared acquisition evidence belongs to a different source.", nameof(identity));
}
