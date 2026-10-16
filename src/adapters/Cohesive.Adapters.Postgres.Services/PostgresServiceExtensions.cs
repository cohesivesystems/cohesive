using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Api.Execution.Services;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Infra;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>Prepares selected service queries through the existing PostgreSQL compiler, for runtime and Infra.</summary>
public static class PostgresServiceExtensions
{
    /// <summary>Derives native snapshot requirements and evidence from the service's selected query registrations.</summary>
    /// <param name="bindings">Offline database mappings.</param><param name="service">Service with explicit selected relational queries.</param>
    /// <param name="implementation">Stable target implementation identity.</param><returns>Database-affine prepared query evidence.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">There are no queries or a selected query cannot supply native snapshot evidence.</exception>
    /// <exception cref="RelationQueryPreparationException">A semantic query is invalid.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native compilation fails.</exception>
    public static PostgresPreparedQueryEvidence PrepareService(this PostgresPersistenceBindings bindings,
        ServiceBuilder service, InfrastructureTargetFacilityId implementation) => PrepareService(bindings, service.Dependencies, implementation);

    /// <summary>Prepares query evidence from API-derived dependency selections.</summary>
    /// <param name="bindings">Offline native mappings.</param><param name="service">Selected query requirements.</param>
    /// <param name="implementation">Target implementation identity.</param><returns>Native prepared evidence.</returns>
    public static PostgresPreparedQueryEvidence PrepareService(this PostgresPersistenceBindings bindings,
        ServiceDependencies service, InfrastructureTargetFacilityId implementation)
    {
        ArgumentNullException.ThrowIfNull(bindings); ArgumentNullException.ThrowIfNull(service);
        foreach (var query in service.Queries) RequireNative(query);
        return PostgresInfrastructure.Prepare(implementation, bindings, service.Queries.Select(query =>
            new QuerySnapshotRequirement(query.Reference.DefinitionId.Value, query.Reference.RevisionId.Value, query.Request)).ToArray());
    }

    /// <summary>Prepares a selected service query using the same declaration and bounds consumed by infrastructure preparation.</summary>
    /// <typeparam name="TInput">Input projection.</typeparam><typeparam name="TResult">Result projection.</typeparam>
    /// <param name="persistence">Bound native persistence.</param><param name="service">Service query selections.</param>
    /// <param name="query">Exact selected typed query.</param><returns>A host-lifetime prepared reader.</returns>
    /// <exception cref="ServiceBindingValidationException">The query is unselected or its exact semantic inputs differ.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native preparation fails.</exception>
    public static PostgresQueryReader<TInput, TResult> Query<TInput, TResult>(this PostgresPersistenceRegistration persistence,
        ServiceBuilder service, RelationQuery<TInput, TResult> query) => Query(persistence, service.Dependencies, query);

    /// <summary>Prepares a query selected by an API implementation model.</summary>
    /// <typeparam name="TInput">Input type.</typeparam><typeparam name="TResult">Result type.</typeparam>
    /// <param name="persistence">Native mappings and data source.</param><param name="service">Selected query bounds and requirements.</param>
    /// <param name="query">Exact query authority.</param><returns>A prepared reader.</returns>
    public static PostgresQueryReader<TInput, TResult> Query<TInput, TResult>(this PostgresPersistenceRegistration persistence,
        ServiceDependencies service, RelationQuery<TInput, TResult> query)
    {
        ArgumentNullException.ThrowIfNull(persistence); ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(query);
        var selected = service.Queries.SingleOrDefault(item =>
            item.Request.DefinitionDocument.DefinitionFingerprint == query.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        if (selected is null || Fingerprint(selected.Request) != Fingerprint(query.CompilationRequest))
            throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryUnselected, Cohesive.Model.DiagnosticSeverity.Error,
                "The exact query must be selected on the service before native preparation.", "/bindings/queries")]));
        RequireNative(selected);
        return persistence.Query(query, selected.MaximumRows, selected.MaximumBytes);
    }

    static void RequireNative(ServiceQuerySelection query)
    {
        if (query.Consistency != ServiceQueryConsistency.SingleSnapshot)
            throw new ServiceBindingValidationException(new([new(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported, Cohesive.Model.DiagnosticSeverity.Error,
                "Independent-source queries require an explicitly composed binding; native snapshot preparation cannot realize that selection.", "/bindings/queries")]));
    }

    static RelationQueryPlanComponentFingerprint Fingerprint(RelationQueryCompilationRequest request)
    {
        var result = RelationQueryStaticCompiler.Compile(request);
        if (!result.IsSuccessful) throw new RelationQueryPreparationException("service query", result);
        return RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(result.Plan!));
    }
}
