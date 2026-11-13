using System.Collections.Immutable;
using Cohesive.Configuration;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Atomic, exact operation/connection/batch selection authored as a scoped default or local override.</summary>
/// <param name="Profile">Exact operation profile.</param>
/// <param name="Connection">Exact connection.</param>
/// <param name="BatchSize">Requested items per operation; policies and both physical limits still apply.</param>
public sealed record IntegrationChoice(ExecutionDefinitionReference Profile, ExecutionDefinitionReference Connection, int BatchSize)
{
    /// <summary>Authors a choice from immutable source declarations.</summary>
    /// <param name="profile">Operation source.</param>
    /// <param name="connection">Connection source.</param>
    /// <param name="batchSize">Explicit size or profile default.</param>
    /// <returns>An exact choice; it does not waive policy.</returns>
    /// <exception cref="ArgumentException">A source declaration is invalid.</exception>
    public static IntegrationChoice Select(ProviderOperationProfile profile, IntegrationConnection connection, int? batchSize = null) =>
        new(profile.Document.Reference, connection.Document.Reference,
            batchSize ?? profile.Document.GetDefinition<ProviderOperationProfileDefinition>().DefaultBatchSize);
}

/// <summary>Mandatory constraints. Null allowed sets are unrestricted; empty allowed sets deny every choice.</summary>
/// <param name="AllowedProviders">Optional provider allow-list.</param>
/// <param name="AllowedResources">Optional account/resource allow-list.</param>
/// <param name="RequiredEntitlements">Additional mandatory entitlements; behavioral coverage remains a Contract requirement.</param>
/// <param name="MinimumBatchSize">Optional lower bound.</param>
/// <param name="MaximumBatchSize">Optional upper bound.</param>
public sealed record IntegrationSelectionPolicy(ImmutableArray<string>? AllowedProviders = null,
    ImmutableArray<string>? AllowedResources = null, ImmutableArray<string> RequiredEntitlements = default,
    int? MinimumBatchSize = null, int? MaximumBatchSize = null)
{
    /// <summary>Normalized optional provider allow-list.</summary>
    public ImmutableArray<string>? AllowedProviders { get; } = NormalizeOptional(AllowedProviders);
    /// <summary>Normalized optional resource allow-list.</summary>
    public ImmutableArray<string>? AllowedResources { get; } = NormalizeOptional(AllowedResources);
    /// <summary>Normalized required entitlement set.</summary>
    public ImmutableArray<string> RequiredEntitlements { get; } = RequiredEntitlements.IsDefault ? [] : IntegrationConnectionDocuments.Names(RequiredEntitlements);
    static ImmutableArray<string>? NormalizeOptional(ImmutableArray<string>? values) => values is null ? null
        : values.Value.IsDefault ? [] : IntegrationConnectionDocuments.Names(values.Value);
}

/// <summary>Resolved source projection plus its pinned configuration evidence.</summary>
/// <param name="Profile">Chosen operation source, null on failure.</param>
/// <param name="Bindings">Explicit bindings carrying the admitted snapshot, null on failure.</param>
/// <param name="Resolution">Portable resolution and diagnostics.</param>
public sealed record IntegrationSelectionResult(ProviderOperationProfile? Profile, ConnectionBindings? Bindings, ScopedSelectionResult Resolution);

/// <summary>Integration interpretation of the shared scoped selection mechanism.</summary>
public static class IntegrationSelection
{
    /// <summary>Resolves scoped defaults and intersects every applicable restriction before explicit binding.</summary>
    /// <param name="logical">Application connection name.</param>
    /// <param name="scope">Requesting tenant/user.</param>
    /// <param name="layers">Current published contributions.</param>
    /// <param name="grants">Trusted host publication grants.</param>
    /// <param name="profiles">Available operation sources.</param>
    /// <param name="connections">Available connection sources.</param>
    /// <returns>Selected source and binding or structured diagnostics; no silent fallback.</returns>
    /// <exception cref="ArgumentException">A declaration or catalog is invalid.</exception>
    public static IntegrationSelectionResult Resolve(ConnectionReference logical, SelectionScope scope,
        IEnumerable<ExecutionDefinitionDocument> layers, IReadOnlyCollection<SelectionGrant> grants,
        IReadOnlyCollection<ProviderOperationProfile> profiles, IReadOnlyCollection<IntegrationConnection> connections)
    {
        var documents = profiles.Select(p => p.Document).Concat(connections.Select(c => c.Document)).ToArray();
        var validity = ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var catalog);
        if (!validity.IsValid) return new(null, null, new(null, [], validity));
        var result = ScopedSelection<IntegrationChoice, IntegrationSelectionPolicy>.Resolve(
            "integration/configuration/" + Uri.EscapeDataString(logical.Name) + (scope.Tenant is null ? "/system"
                : "/tenants/" + Uri.EscapeDataString(scope.Tenant) + (scope.User is null ? "/tenant" : "/users/" + Uri.EscapeDataString(scope.User))), "v1", scope, layers, grants,
            (choice, policies) => Validate(choice, policies, catalog!));
        if (result.Snapshot is null) return new(null, null, result);
        var selected = result.Snapshot.GetDefinition<ScopedSelectionSnapshot<IntegrationChoice>>().Choice;
        var profile = profiles.Single(p => p.Document.Reference == selected.Profile);
        var connection = connections.Single(c => c.Document.Reference == selected.Connection);
        var bindings = new ConnectionBindings().Bind(logical, connection).WithSelection(logical, selected.BatchSize,
            result.Snapshot, [.. result.Contributions, .. documents.Where(d => d.Reference != selected.Profile && d.Reference != selected.Connection)
                .OrderBy(d => d.Metadata.DefinitionId.Value, StringComparer.Ordinal)]);
        return new(profile, bindings, result);
    }

    /// <summary>Checks current enforced restrictions for a new effect using the original choice. Defaults cannot rebind it.</summary>
    /// <param name="snapshot">Retained admitted configuration.</param>
    /// <param name="currentLayers">Complete current contribution set from a trusted policy store.</param>
    /// <param name="grants">Current publisher grants.</param>
    /// <param name="originalDefinitions">Original profile/connection closure.</param>
    /// <returns>Dispatch permission diagnostics. A denied effect must not be sent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static DocumentValidationResult CheckDispatch(ExecutionDefinitionDocument snapshot,
        IEnumerable<ExecutionDefinitionDocument> currentLayers, IReadOnlyCollection<SelectionGrant> grants,
        ExecutionDefinitionDocumentCatalog originalDefinitions)
    {
        var retained = ScopedSelection<IntegrationChoice, IntegrationSelectionPolicy>.Inspect(snapshot, originalDefinitions,
            (choice, policies) => Validate(choice, policies, originalDefinitions));
        return retained.IsValid ? ScopedSelection<IntegrationChoice, IntegrationSelectionPolicy>.CheckDispatch(snapshot, currentLayers, grants,
            (choice, policies) => Validate(choice, policies, originalDefinitions)) : retained;
    }

    internal static DocumentValidationResult Inspect(ExecutionDefinitionDocument snapshot, ExecutionDefinitionDocumentCatalog catalog,
        IntegrationConnectionResolution resolution)
    {
        if (snapshot.Kind.Value != "configuration.selection-snapshot.v1" || !snapshot.Extensions.IsDefaultOrEmpty)
            return Error("snapshot", "Unsupported configuration snapshot.");
        ScopedSelectionSnapshot<IntegrationChoice> admitted;
        try { admitted = snapshot.GetDefinition<ScopedSelectionSnapshot<IntegrationChoice>>(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { return Error("snapshot", "Invalid configuration snapshot."); }
        if (admitted.Choice is null || admitted.Contributions.IsDefaultOrEmpty || admitted.Scope is null
            || admitted.Choice.Profile != resolution.Profile || admitted.Choice.Connection != resolution.Connection
            || admitted.Choice.BatchSize != resolution.EffectiveBatchSize)
            return Error("snapshot", "The binding differs from its admitted configuration.");
        return ScopedSelection<IntegrationChoice, IntegrationSelectionPolicy>.Inspect(snapshot, catalog,
            (choice, policies) => Validate(choice, policies, catalog));
    }

    static DocumentValidationResult Validate(IntegrationChoice choice, ImmutableArray<IntegrationSelectionPolicy> policies,
        ExecutionDefinitionDocumentCatalog catalog)
    {
        if (choice.Profile is null || choice.Connection is null) return Error("choice", "Missing exact profile or connection.");
        var result = catalog.ValidateReference(choice.Profile, "/profile", out var profile);
        if (!result.IsValid) return result;
        result = catalog.ValidateReference(choice.Connection, "/connection", out var connection);
        if (!result.IsValid) return result;
        result = IntegrationConnectionDocuments.Match(profile!, connection!, choice.BatchSize, out _);
        if (!result.IsValid) return result;
        var configured = connection!.GetDefinition<IntegrationConnectionDefinition>();
        foreach (var policy in policies)
        {
            if (policy.AllowedProviders is { } providers && (providers.IsDefault || !providers.Contains(configured.Provider, StringComparer.Ordinal)))
                return Error("provider", "An enforced provider allow-list rejects the choice.");
            if (policy.AllowedResources is { } resources && (resources.IsDefault || !resources.Contains(configured.Resource, StringComparer.Ordinal)))
                return Error("resource", "An enforced resource allow-list rejects the choice.");
            if (!policy.RequiredEntitlements.IsDefaultOrEmpty && policy.RequiredEntitlements.Any(e => !configured.Entitlements.Contains(e, StringComparer.Ordinal)))
                return Error("entitlement", "The choice lacks an enforced entitlement.");
            if (policy.MinimumBatchSize is < 1 || policy.MaximumBatchSize is < 1
                || choice.BatchSize < policy.MinimumBatchSize || choice.BatchSize > policy.MaximumBatchSize)
                return Error("batch", "The choice violates an enforced batch bound.");
        }
        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Error(string code, string message) => new([new("integrations.policy." + code,
        DiagnosticSeverity.Error, message, "/configuration")]);
}
