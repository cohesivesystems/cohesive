using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Configuration;
using Cohesive.Integrations.Authoring;

namespace Cohesive.Integrations;

/// <summary>Immutable runtime composition of exact connection/profile revisions and host-owned implementations.</summary>
/// <remarks>This registry does not dispatch, retry, dispose clients, or resolve secret values. Hosts own those lifetimes;
/// Requests and Processes own execution. Registration is an explicit implementation claim requiring adapter conformance.</remarks>
public sealed class IntegrationRuntime
{
    readonly ImmutableDictionary<(ExecutionDefinitionReference Profile, ExecutionDefinitionReference Connection), object> handlers;
    /// <summary>Creates an empty runtime registry.</summary>
    public IntegrationRuntime() : this(ImmutableDictionary<(ExecutionDefinitionReference, ExecutionDefinitionReference), object>.Empty) { }
    IntegrationRuntime(ImmutableDictionary<(ExecutionDefinitionReference, ExecutionDefinitionReference), object> handlers) => this.handlers = handlers;

    /// <summary>Registers a host-owned implementation against exact profile and connection revisions.</summary>
    /// <typeparam name="THandler">Adapter or application port implemented by the handler.</typeparam>
    /// <param name="profile">Immutable operation profile.</param>
    /// <param name="connection">Immutable configured connection.</param>
    /// <param name="handler">Implementation containing the actual client and resolved authentication.</param>
    /// <returns>A new registry; the original is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The profile/connection is incompatible or already registered.</exception>
    public IntegrationRuntime Register<THandler>(ProviderOperationProfile profile, IntegrationConnection connection, THandler handler) where THandler : class
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(handler);
        var profileDocument = profile.Document; var connectionDocument = connection.Document;
        var validation = IntegrationConnectionDocuments.Match(profileDocument, connectionDocument, requestedBatchSize: 1, out _);
        if (!validation.IsValid) throw new ArgumentException(validation.Diagnostics[0].Message);
        var key = (profileDocument.Reference, connectionDocument.Reference);
        if (handlers.ContainsKey(key)) throw new ArgumentException("The exact profile and connection already have a runtime implementation.");
        return new(handlers.Add(key, handler));
    }

    /// <summary>Resolves the implementation of a validated pinned binding, including after canonical documents are reopened.</summary>
    /// <typeparam name="THandler">Expected application/adapter port.</typeparam>
    /// <param name="compilation">Bound canonical dependency closure; mutable registries or logical-name lookups are not consulted.</param>
    /// <returns>The host-owned implementation for the exact admitted profile and connection.</returns>
    /// <exception cref="ArgumentNullException">Compilation is null.</exception>
    /// <exception cref="InvalidOperationException">The closure is invalid, unconnected, unregistered or the handler type differs.</exception>
    public THandler Resolve<THandler>(IntegrationCompilation compilation) where THandler : class
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (!compilation.IsSuccessful) throw new InvalidOperationException("Cannot resolve an unsuccessful integration compilation.");
        if (compilation.Qualification is not null && !IntegrationQualification.Inspect(compilation).IsValid)
            throw new InvalidOperationException("Invalid capability qualification lineage.");
        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(compilation.Documents, out var catalog);
        if (!validation.IsValid || !catalog!.ValidateReference(compilation.Binding!.Reference, "/binding", out _).IsValid)
            throw new InvalidOperationException("Invalid runtime binding dependency closure.");
        validation = InteractionContractCatalog.TryCreate(compilation.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var requests);
        if (!validation.IsValid) throw new InvalidOperationException("Invalid runtime Request dependency closure.");
        validation = IngestionProviderBindingDocuments.Inspect(compilation.Binding!, catalog!, requests!, out var binding);
        if (!validation.IsValid || binding!.Binding.Connection is null)
            throw new InvalidOperationException("A valid connection-bound integration is required.");
        catalog!.TryResolve(binding.Binding.Connection, out var document);
        var resolution = document!.GetDefinition<IntegrationConnectionResolution>();
        return handlers.TryGetValue((resolution.Profile, resolution.Connection), out var handler) && handler is THandler typed ? typed
            : throw new InvalidOperationException("No matching implementation is registered for the exact profile, connection and requested handler type.");
    }
    /// <summary>Resolves a handler for a new effect only after checking current enforced restrictions against the pinned choice.</summary>
    /// <typeparam name="THandler">Application-owned handler port.</typeparam>
    /// <param name="compilation">Original admitted dependency closure.</param>
    /// <param name="currentPolicies">Complete current contributions from the trusted host policy store.</param>
    /// <param name="grants">Current authenticated publisher permissions.</param>
    /// <param name="qualificationAtUtc">Required current UTC boundary when the compilation carries capability qualification.</param>
    /// <returns>The original handler; no default or connection is reselected.</returns>
    /// <exception cref="InvalidOperationException">The binding is invalid, lacks scoped admission, or current restrictions deny dispatch.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>Call at each new-effect boundary. The host owns policy freshness and synchronization with dispatch.
    /// For reconciliation of an already-issued effect, Resolve retains the original identity; it does not authorize new effects.</remarks>
    public THandler ResolveForDispatch<THandler>(IntegrationCompilation compilation,
        IEnumerable<ExecutionDefinitionDocument> currentPolicies, IReadOnlyCollection<SelectionGrant> grants, DateTimeOffset? qualificationAtUtc = null) where THandler : class
    {
        if (compilation.Qualification is not null && (qualificationAtUtc is null || !IntegrationQualification.Inspect(compilation, qualificationAtUtc).IsValid))
            throw new InvalidOperationException("Fresh capability evidence is required for new dispatch.");
        var handler = Resolve<THandler>(compilation);
        ExecutionDefinitionDocumentCatalog.TryCreate(compilation.Documents, out var catalog);
        var binding = compilation.Binding!.GetDefinition<IngestionProviderBinding>();
        catalog!.TryResolve(binding.Connection!, out var resolutionDocument);
        var resolution = resolutionDocument!.GetDefinition<IntegrationConnectionResolution>();
        if (resolution.Configuration is null || !catalog.TryResolve(resolution.Configuration, out var snapshot))
            throw new InvalidOperationException("Dispatch policy checking requires a scoped admission snapshot.");
        var validation = IntegrationSelection.CheckDispatch(snapshot!, currentPolicies, grants, catalog);
        if (!validation.IsValid) throw new InvalidOperationException("Current enforced policy rejects dispatch: " + validation.Diagnostics[0].Code);
        return handler;
    }
    /// <summary>Checks a qualified explicit binding before a new effect, when no scoped policy snapshot is attached.</summary>
    /// <typeparam name="THandler">Application handler port.</typeparam>
    /// <param name="compilation">Qualified admitted closure.</param>
    /// <param name="atUtc">Current UTC dispatch boundary.</param>
    /// <returns>The originally registered handler; admission does not discharge pending runtime properties.</returns>
    /// <exception cref="InvalidOperationException">Qualification is absent, invalid or expired, or current scoped policy must also be checked.</exception>
    /// <exception cref="ArgumentNullException">Compilation is null.</exception>
    public THandler ResolveQualifiedForDispatch<THandler>(IntegrationCompilation compilation, DateTimeOffset atUtc) where THandler : class
    {
        if (!IntegrationQualification.Inspect(compilation, atUtc).IsValid)
            throw new InvalidOperationException("Fresh valid qualification is required for new dispatch.");
        var handler = Resolve<THandler>(compilation);
        var binding = compilation.Binding!.GetDefinition<IngestionProviderBinding>();
        var resolution = compilation.Documents.Single(d => d.Reference == binding.Connection).GetDefinition<IntegrationConnectionResolution>();
        if (resolution.Configuration is not null)
            throw new InvalidOperationException("Use scoped dispatch resolution with current policies and a qualification timestamp.");
        return handler;
    }
}
