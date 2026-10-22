using Cohesive.Relations.Compilation;
using Cohesive.Relations.Infra;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    /// <summary>Prepares a composite storage model using each source's canonical acquisition declaration.</summary>
    /// <param name="bindings">Local native database mappings and receipt policy.</param>
    /// <param name="plan">Endpoint requirements, including consistency.</param>
    /// <param name="sources">Exact native contributions declared by the storage model.</param>
    /// <param name="policy">Explicit bounded query policy.</param>
    /// <param name="authority">Runtime authority; defaults to the canonical database identity.</param>
    /// <returns>One preparation for infrastructure evidence and DI runtime binding.</returns>
    public static PostgresComposedStorageComposition ComposeServices(this PostgresPersistenceBindings bindings,
        ServiceImplementationPlan plan, IEnumerable<IEntityStorageBinding> sources,
        Func<int, long, PostgresRelationQueryComposedPolicy> policy, string? authority = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var selected = sources.ToImmutableArray();
        return new(bindings, PrepareCore(bindings, plan, new(selected.Select(source => source.Source)), policy), selected, authority);
    }

    /// <summary>Derives source acquisition requirements and bounded evaluation requirements from actual physical preparation.</summary>
    /// <param name="bindings">Offline PostgreSQL mappings.</param><param name="plan">Authoritative endpoint selections.</param>
    /// <param name="sources">Offline remote source contracts.</param><param name="policy">Explicit bounds/partition policy for each selected query.</param>
    /// <returns>Prepared readers' metadata and attributable infrastructure evidence; no clients or IO.</returns>
    /// <exception cref="ServiceBindingValidationException">A query requires a shared snapshot.</exception>
    public static PostgresComposedServicePreparation PrepareComposedService(this PostgresPersistenceBindings bindings,
        ServiceImplementationPlan plan, EntityRelationQuerySourceCatalog sources,
        Func<int, long, PostgresRelationQueryComposedPolicy> policy)
    {
        return PrepareCore(bindings, plan, sources, policy);
    }

    static PostgresComposedServicePreparation PrepareCore(PostgresPersistenceBindings bindings,
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
        return new(bindings, queries);
    }
}

/// <summary>Exact selected plans and evidence for independently observed sources.</summary>
/// <remarks>Requirements describe bounded plan execution, not live access or schema readiness. A native prefix's
/// SQL snapshot never extends to remote reads. Physical-plan and source fingerprints remain evidence, not capability IDs.</remarks>
public sealed class PostgresComposedServicePreparation
{
    internal ImmutableArray<RelationQueryCompilationRequest> QueryRequests => [.. queries.Values.Select(item => item.Selection.Request)];
    readonly PostgresPersistenceBindings bindings;
    readonly Dictionary<object, (ServiceQuerySelection Selection, PostgresComposedQueryPreparation Prepared)> queries;
    /// <summary>Local database acquisition requirements and evidence.</summary>
    public ComposedQueryResourceEvidence Postgres { get; }
    /// <summary>Remote acquisition requirements keyed by canonical source identity.</summary>
    public ImmutableDictionary<RelationQuerySourceInstanceId, ComposedQueryResourceEvidence> Sources { get; }
    /// <summary>Bounded interpreter evidence to attach to the executing workload.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidence> ExecutionEvidence { get; }
    /// <summary>Explicit independent-source requirements for the workload.</summary>
    public ImmutableArray<InfrastructureCapabilityId> Requirements => [.. ExecutionEvidence.Select(item => item.Capability)];

    internal PostgresComposedServicePreparation(PostgresPersistenceBindings bindings,
        ImmutableArray<(ServiceQuerySelection Selection, PostgresComposedQueryPreparation Prepared)> queries)
    {
        if (queries.IsEmpty) throw new ArgumentException("At least one selected query is required.", nameof(queries));
        this.bindings = bindings;
        this.queries = queries.ToDictionary(item => (object)item.Selection.Request, ReferenceEqualityComparer.Instance);
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
        Postgres = new(bindings.Database.Value, "postgres", local);
        Sources = remote.ToImmutableDictionary(pair => pair.Key, pair => new ComposedQueryResourceEvidence(pair.Key.Value, "source", pair.Value));
        ExecutionEvidence = [.. execution];
    }

    /// <summary>Binds all endpoint-selected queries and declared repositories without repeating the query catalog.</summary>
    /// <param name="persistence">Caller-owned PostgreSQL runtime for the prepared mappings.</param>
    /// <param name="receipts">Optional validated native receipt storage.</param>
    /// <param name="sources">Remote repositories and acquisition readers from their storage declarations.</param>
    /// <returns>Host-scoped resolver caching readers by exact query. Results and invocation context are never cached.</returns>
    /// <exception cref="ArgumentNullException">Persistence or source registrations are null.</exception>
    /// <exception cref="ArgumentException">Runtime mappings differ from preparation, or source registrations duplicate a repository or source.</exception>
    /// <remarks>Query resolution validates the selected authority, bounds, source affinity and consistency before IO.
    /// Unselected queries never fall back to a different execution strategy.</remarks>
    public IServicePersistence Bind(PostgresPersistenceRegistration persistence,
        PostgresTransitionReceiptStorage? receipts = null, params EntityStorageRegistration[] sources)
    {
        return BindCore(persistence, receipts, sources, null, null);
    }

    internal IServicePersistence BindCore(PostgresPersistenceRegistration persistence, PostgresTransitionReceiptStorage? receipts,
        EntityStorageRegistration[] sources, PostgresEntityHistoryStorage? history, PostgresEntityOutboxStorage? outbox)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(sources);
        RequirePersistence(persistence);
        var repositories = new ServicePersistenceBuilder(persistence.ForServices(receipts, history, outbox));
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            repositories.Repository(source.Repository);
        }
        return new Persistence(this, persistence, new(sources.Select(source => source.Source)), repositories.Build());
    }

    sealed class Persistence(PostgresComposedServicePreparation preparation, PostgresPersistenceRegistration postgres,
        EntityRelationQuerySourceCatalog sources, IServicePersistence repositories) : IServicePersistence
    {
        readonly Dictionary<object, object> readers = new(ReferenceEqualityComparer.Instance);
        public IEntityRepository Repository(ValueContract observation) => repositories.Repository(observation);
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
            int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
        {
            ArgumentNullException.ThrowIfNull(query);
            // Check every request, including cached readers: a later caller cannot widen bounds or guarantees.
            preparation.RequireSelection(query, maximumRows, maximumBytes);
            if (!Enum.IsDefined(consistency)) throw new ArgumentOutOfRangeException(nameof(consistency));
            if (consistency != RelationQueryConsistency.IndependentSources)
                throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported,
                    DiagnosticSeverity.Error, "Composed readers cannot satisfy a shared snapshot.", "/bindings/queries/consistency")]));
            lock (readers)
            {
                if (!readers.TryGetValue(query, out var reader))
                    readers.Add(query, reader = preparation.Bind(postgres, query, sources, maximumRows, maximumBytes));
                return (IRelationQueryReader<TInput, TResult>)reader;
            }
        }
    }

    /// <summary>Binds an exactly selected query using its already prepared plan and requested bounds.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="persistence">Native PostgreSQL runtime.</param><param name="query">Selected query authority.</param>
    /// <param name="sources">Runtime remote registrations.</param><param name="rows">Requested row bound.</param><param name="bytes">Requested payload bound.</param>
    /// <returns>Prepared reader with independent-source consistency.</returns>
    public IRelationQueryReader<TInput, TResult> Bind<TInput, TResult>(PostgresPersistenceRegistration persistence,
        RelationQuery<TInput, TResult> query, EntityRelationQuerySourceCatalog sources, int rows, long bytes)
    {
        RequirePersistence(persistence);
        var entry = RequireSelection(query, rows, bytes);
        return persistence.BindComposed(query, entry.Prepared, sources);
    }

    void RequirePersistence(PostgresPersistenceRegistration persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        if (!ReferenceEquals(persistence.Bindings, bindings))
            throw new ArgumentException("Runtime must be bound from the exact prepared PostgreSQL mappings.", nameof(persistence));
    }

    (ServiceQuerySelection Selection, PostgresComposedQueryPreparation Prepared) RequireSelection<TInput, TResult>(
        RelationQuery<TInput, TResult> query, int rows, long bytes)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!queries.TryGetValue(query.CompilationRequest, out var entry)
            || entry.Selection.MaximumRows != rows || entry.Selection.MaximumBytes != bytes)
            throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryUnselected, DiagnosticSeverity.Error,
                "Query authority and bounds must match the prepared endpoint selection.", "/bindings/queries")]));
        return entry;
    }
}
