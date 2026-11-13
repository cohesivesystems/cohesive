using System.Collections.Immutable;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Execution;
using Cohesive.Api.Services;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>Portable endpoint coverage and query selections, independent of a host or persistence provider.</summary>
/// <remarks>Author on one thread. Reading Definition freezes the plan and validates complete coverage.</remarks>
public sealed class ServiceImplementationPlan
{
    readonly List<ApiDefinition> definitions = [];
    readonly HashSet<ApiEndpointId> supplied = [];
    readonly List<ServiceEndpointImplementation> implementations = [];
    ImmutableArray<ServiceEndpointImplementation> frozenImplementations;

    /// <summary>Exact endpoint-to-implementation links retained during binding, in endpoint identity order.</summary>
    public ImmutableArray<ServiceEndpointImplementation> Implementations
    { get { _ = Definition; return frozenImplementations; } }
    readonly AuthoringState<ApiDefinition> state;
    ServiceDependencies queries = new();
    int contributionDepth;
    ImmutableHashSet<ValueContract> repositories = [];
    /// <summary>Exact entity dependencies selected by entity endpoints and process bindings.</summary>
    public IReadOnlyCollection<ValueContract> RepositoryRequirements { get { _ = Definition; return repositories; } }
    /// <summary>Records repositories needed by an implementation contribution.</summary>
    /// <param name="observations">Canonical observation contracts.</param>
    public void RequireRepositories(IEnumerable<ValueContract> observations) => state.Edit(() => { repositories = repositories.Union(observations); return this; });

    /// <summary>Starts an empty portable implementation plan.</summary>
    public ServiceImplementationPlan() => state = new(() =>
    {
        var definition = ApiDefinition.Combine(definitions);
        var errors = definition.Endpoints.Where(endpoint => !supplied.Contains(endpoint.Id))
            .Select(endpoint => new DocumentValidationDiagnostic("services.api.missingImplementation", DiagnosticSeverity.Error,
                $"Endpoint '{endpoint.Id}' has no implementation.", "/api/implementations")).ToArray();
        var links = definition.Operations.Where(operation => operation.ResultOf is not null)
            .Where(operation => operation.Kind != ApiOperationKind.Query
                || !definition.TryGetOperation(operation.ResultOf!.Value, out var command)
                || command.Kind != ApiOperationKind.Command || command.PrimaryResult.Kind != ApiResultKind.Accepted)
            .Select(operation => new DocumentValidationDiagnostic("services.api.resultLinkInvalid", DiagnosticSeverity.Error,
                $"Result endpoint '{operation.Id}' must link to a declared admission command.",
                $"/api/endpoints/{operation.Id.Value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}/resultOf"));
        var diagnostics = errors.Concat(links).ToArray();
        if (diagnostics.Length != 0) throw new ServiceBindingValidationException(new([.. diagnostics]));
        frozenImplementations = [.. implementations.OrderBy(item => item.Endpoint.Id.Value, StringComparer.Ordinal)];
        return definition;
    });

    /// <summary>Atomically contributes endpoint and query selections; a failed callback leaves the plan unchanged.</summary>
    /// <param name="definition">API authority for the contribution.</param><param name="configure">Synchronous authoring callback; must not freeze the plan.</param>
    public void Contribute(ApiDefinition definition, Action configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        state.Edit(() =>
        {
            var previousDefinitions = definitions.ToArray();
            var previousSupplied = supplied.ToArray();
            var previousQueries = queries;
            var previousRepositories = repositories;
            var previousImplementationCount = implementations.Count;
            contributionDepth++;
            try { Declare(definition); configure(); }
            catch
            {
                definitions.Clear(); definitions.AddRange(previousDefinitions);
                supplied.Clear(); supplied.UnionWith(previousSupplied);
                queries = previousQueries;
                repositories = previousRepositories;
                implementations.RemoveRange(previousImplementationCount, implementations.Count - previousImplementationCount);
                throw;
            }
            finally { contributionDepth--; }
            return this;
        });
    }

    /// <summary>Registers a contract authority. Repeated contributions to the same instance are supported.</summary>
    /// <param name="definition">Original API definition.</param>
    public void Declare(ApiDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        state.Edit(() => { if (!definitions.Contains(definition)) definitions.Add(definition); return this; });
    }

    /// <summary>Admits an endpoint linked to an exact query, process or service authority.</summary>
    /// <param name="definition">Declared API authority.</param><param name="endpoint">Original endpoint.</param>
    /// <param name="execution">Required exact selected execution reference.</param>
    /// <exception cref="ArgumentNullException">The execution reference is null.</exception>
    /// <exception cref="ServiceBindingValidationException">Endpoint is foreign or already implemented.</exception>
    public void Implement(ApiDefinition definition, ApiEndpoint endpoint, ExecutionDefinitionReference execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ImplementCore(definition, endpoint, execution, null);
    }

    /// <summary>Admits an entity endpoint linked to its canonical repository observation contract.</summary>
    /// <param name="definition">Declared API authority.</param><param name="endpoint">Original endpoint.</param>
    /// <param name="repository">Required canonical entity observation contract.</param>
    /// <exception cref="ArgumentNullException">The repository contract is null.</exception>
    /// <exception cref="ServiceBindingValidationException">Endpoint is foreign or already implemented.</exception>
    public void Implement(ApiDefinition definition, ApiEndpoint endpoint, ValueContract repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(endpoint);
        ImplementCore(definition, endpoint, endpoint.Operation.TransitionReference, repository);
    }

    /// <summary>Admits both operations of a fingerprint-pinned admission/result pair atomically.</summary>
    /// <typeparam name="TRequest">Command input.</typeparam><typeparam name="TResult">Terminal result.</typeparam>
    /// <param name="definition">Owning API.</param><param name="pair">Original pair retaining its exact service document.</param>
    public void ImplementProcess<TRequest, TResult>(ApiDefinition definition, ServiceAcceptedProcessApi<TRequest, TResult> pair) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(pair);
        Contribute(definition, () =>
        {
            ImplementServiceOperation(definition, pair.Start, pair.Declaration, pair.StartOperationId);
            ImplementServiceOperation(definition, pair.Result, pair.Declaration, pair.ResultOperationId);
        });
    }

    /// <summary>Admits a typed terminal process endpoint against its exact service authority.</summary>
    /// <typeparam name="TRequest">Command input.</typeparam><typeparam name="TResult">Terminal result.</typeparam>
    /// <param name="definition">Owning API.</param><param name="endpoint">Original typed process endpoint.</param>
    /// <param name="service">Exact canonical service document retained by the binding.</param>
    /// <exception cref="ServiceBindingValidationException">The endpoint belongs to another service revision.</exception>
    public void ImplementProcess<TRequest, TResult>(ApiDefinition definition, ServiceProcessEndpoint<TRequest, TResult> endpoint, ExecutionDefinitionDocument service)
    {
        ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(service);
        if (endpoint.Service != service.Reference)
            throw Error("services.api.processMismatch", $"Endpoint '{endpoint.Id}' belongs to another service revision.");
        ImplementServiceOperation(definition, endpoint, service, endpoint.OperationId);
    }

    void ImplementServiceOperation(ApiDefinition definition, ApiEndpoint endpoint, ExecutionDefinitionDocument service, string operationId)
    {
        ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(service);
        var operation = service.GetDefinition<ServiceDefinition>().Operations.SingleOrDefault(item => item.Id == operationId);
        var execution = operation switch
        {
            ServiceProcessOperation start => start.Process,
            ServiceProcessResultOperation result => result.Process,
            _ => throw Error("services.api.operationMismatch", $"'{operationId}' is not a process start or result operation.")
        };
        if (endpoint.Id.Value != ServiceApiProjection.OperationIdentity(service.Reference, operationId))
            throw Error("services.api.operationMismatch", $"Endpoint '{endpoint.Id}' does not project service operation '{operationId}'.");
        ImplementCore(definition, endpoint, execution, null, new(service, operation!));
    }

    void ImplementCore(ApiDefinition definition, ApiEndpoint endpoint, ExecutionDefinitionReference? execution, ValueContract? repository, ServiceEndpointOperation? service = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        state.Edit(() =>
        {
            if (!definitions.Contains(definition) || !definition.Endpoints.Any(item => ReferenceEquals(item.Operation, endpoint.Operation)))
                throw Error("services.api.foreignEndpoint", $"Endpoint '{endpoint.Id}' is not from the declared API.");
            if (!supplied.Add(endpoint.Id))
                throw Error("services.api.duplicateImplementation", $"Endpoint '{endpoint.Id}' has competing implementations.");
            implementations.Add(new(endpoint.Operation, execution, repository) { Service = service });
            return this;
        });
    }

    /// <summary>Includes native queries selected by a process service in storage admission.</summary>
    /// <param name="dependencies">Exact process dependency selections.</param>
    /// <exception cref="ServiceBindingValidationException">Query bounds, authority or consistency conflict.</exception>
    /// <exception cref="ArgumentNullException">Dependencies are null.</exception>
    public void RequireQueries(ServiceDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        state.Edit(() =>
        {
            foreach (var selection in dependencies.Queries) queries = queries.Select(selection);
            return this;
        });
    }

    /// <summary>Records one bounded query selection for runtime and infrastructure preparation.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="query">Canonical query authority.</param><param name="revision">Semantic revision.</param>
    /// <param name="rows">Maximum result rows.</param><param name="bytes">Maximum decoded bytes.</param>
    /// <param name="consistency">Required consistency across every read contributing to the result.</param>
    /// <returns>The exact selected query reference, reusable by endpoint inspection.</returns>
    /// <exception cref="ServiceBindingValidationException">The same query has competing bounds or revisions.</exception>
    public ExecutionDefinitionReference Query<TInput, TResult>(RelationQuery<TInput, TResult> query, ExecutionRevisionId revision, int rows, long bytes, RelationQueryConsistency consistency) =>
        state.Edit(() =>
        {
            var reference = ServiceQueryBinding.GetReference(revision, query.CompilationRequest);
            queries = queries.Select(new(reference, query.CompilationRequest, rows, bytes, consistency), "services.api.querySelectionMismatch");
            return reference;
        });

    /// <summary>Freezes and validates complete coverage, without any persistence or host.</summary>
    public ApiDefinition Definition => contributionDepth == 0 ? state.Value
        : throw new InvalidOperationException("Complete the contribution before freezing its plan.");
    /// <summary>Portable query requirements derived from admitted implementations.</summary>
    public ServiceDependencies QueryDependencies { get { _ = Definition; return queries; } }

    /// <summary>Creates a coded implementation diagnostic shared by admission and host attachment.</summary>
    /// <param name="code">Stable diagnostic code.</param><param name="message">Actionable non-secret explanation.</param>
    /// <returns>The structured validation exception.</returns>
    public static ServiceBindingValidationException Error(string code, string message) =>
        new(new([new(code, DiagnosticSeverity.Error, message, "/api/implementations")]));
}
