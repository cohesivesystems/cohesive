using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>A provider's bounded acquisition offer. It supplies observations for the requested scope.
/// A returned collection alone cannot establish that every expected observation was accounted for.</summary>
/// <param name="Acquire">Exact Request the adapter implements, including input and response contracts.</param>
/// <param name="Operation">Provider operation identity within this profile revision.</param>
/// <param name="Medium">Communication medium used by the adapter.</param>
/// <param name="Properties">Declared property offers or other roles using shared assertion identities.</param>
/// <param name="Profile">Exact provider-operation profile for a connection-bound offer; null retains the legacy profile.</param>
public sealed record ObservationProviderProfile(RequestContractReference Acquire, string Operation, string Medium,
    ImmutableArray<ContractProperty> Properties = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExecutionDefinitionReference? Profile = null);

/// <summary>Property uses attached to one exact ingestion interaction.</summary>
/// <param name="Ingestion">Exact ingestion declaration supplying the operation Requests.</param>
/// <param name="Properties">Contract property uses with explicit roles; order is declaration order.</param>
public sealed record IngestionContract(ExecutionDefinitionReference Ingestion, ImmutableArray<ContractProperty> Properties)
{
    /// <summary>Binds the acquisition contribution to one configured resource and consumer.</summary>
    /// <param name="provider">Exact observation-provider profile revision.</param>
    /// <param name="resource">Configured account or quota identity.</param>
    /// <param name="consumer">Independent application progress address.</param>
    /// <param name="connection">Exact connection-resolution document; null retains the legacy account-only binding.</param>
    /// <returns>Editable binding source.</returns>
    public IngestionProviderBinding Bind(ExecutionDefinitionReference provider, string resource, IngestionLedgerAddress consumer, ExecutionDefinitionReference? connection = null) =>
        new(this, provider, resource, consumer, connection);
}

/// <summary>Structural source and typed projection of one scoped observation-provider binding.
/// The profile contributes acquired observations to Contract; the linked application's publication
/// Request owns the decision and receipt. Lowering records this obligation without certifying its runtime realization.</summary>
/// <param name="Contract">Exact interaction and its role-specific property uses.</param>
/// <param name="Provider">Exact selected profile contributing observations.</param>
/// <param name="Resource">External account/quota identity shared across eligible consumers.</param>
/// <param name="Consumer">Logical application scope with independently advancing progress.</param>
/// <param name="Connection">Exact admitted connection resolution, or null for a legacy account-only binding.</param>
public sealed record IngestionProviderBinding(IngestionContract Contract, ExecutionDefinitionReference Provider,
    string Resource, IngestionLedgerAddress Consumer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExecutionDefinitionReference? Connection = null);

/// <summary>Resolved explanation of an ingestion binding. Acquire contributes observations; Publish owns the declared publication obligations.
/// A compatible declaration supplies no evidence that a particular interval is complete.</summary>
/// <param name="Binding">Validated selection, including external resource and consumer progress address.</param>
/// <param name="Offer">Exact selected provider operation, medium and acquisition Request.</param>
/// <param name="Properties">Resolved interaction properties in declaration order.</param>
/// <param name="ProviderProperties">Resolved provider property uses; these are declarations awaiting qualification.</param>
/// <param name="Publish">Exact Request responsible for publication and its receipt.</param>
public sealed record IngestionProviderBindingInspection(IngestionProviderBinding Binding, ObservationProviderProfile Offer,
    ImmutableArray<ResolvedContractProperty> Properties, ImmutableArray<ResolvedContractProperty> ProviderProperties, RequestContractReference Publish)
{
    /// <summary>Declared strategy: acquisition and application publication jointly realize the
    /// contract. Qualification of either participant remains a separate obligation.</summary>
    public CapabilityRealizationKind Strategy => CapabilityRealizationKind.Composed;
}

/// <summary>Authoring and strict linked inspection for the bounded ingestion binding profile.</summary>
public static class IngestionProviderBindingDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<IngestionProviderBinding> Bindings = Projection<IngestionProviderBinding>("integration.ingestion-binding.v1");
    static readonly ExecutionDefinitionDocumentProjection<IngestionProviderBinding> ConnectedBindings = Projection<IngestionProviderBinding>("integration.ingestion-binding.v2");
    static readonly ExecutionDefinitionDocumentProjection<ObservationProviderProfile> ConnectedProfiles = Projection<ObservationProviderProfile>("integration.observation-provider.v3");
    static readonly ExecutionDefinitionDocumentProjection<ObservationProviderProfile> Profiles = Projection<ObservationProviderProfile>("integration.observation-provider.v2");

    static ExecutionDefinitionDocumentProjection<T> Projection<T>(string kind) where T : class =>
        new(new(kind), "integrations.binding.kind", "integrations.binding.projection", "integrations.binding.wire",
            "The persisted definition must match the declared profile.");

    /// <summary>Lowers a provider offer to canonical versioned IR.</summary>
    /// <param name="id">Stable provider profile identity.</param>
    /// <param name="revision">Exact profile revision.</param>
    /// <param name="source">Provider-authored observation offer.</param>
    /// <param name="origin">Attribution to the authored source.</param>
    /// <exception cref="ArgumentNullException">Source or origin is null.</exception>
    /// <exception cref="ArgumentException">Identity or revision is uninitialized.</exception>
    /// <returns>Fingerprint-bearing provider document for use in bindings.</returns>
    public static ExecutionDefinitionDocument DeclareProvider(ExecutionDefinitionId id, ExecutionRevisionId revision,
        ObservationProviderProfile source, ExecutionProvenance origin)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = source.Properties.IsDefault ? source with { Properties = [] } : source;
        return ExecutionDefinitionDocument.Create(source.Profile is null ? Profiles.Kind : ConnectedProfiles.Kind, id, revision, normalized, origin,
            sourceMap: Map(origin, source.Profile is null ? ["acquire", "operation", "medium", "properties"] : ["acquire", "operation", "medium", "properties", "profile"]));
    }

    /// <summary>Lowers an explicit selection to canonical IR. Inspect against the exact dependency closure before use.</summary>
    /// <param name="id">Binding identity.</param>
    /// <param name="revision">Binding revision.</param>
    /// <param name="source">Authored selection.</param>
    /// <param name="origin">Attribution to the authored source.</param>
    /// <exception cref="ArgumentNullException">Source or origin is null.</exception>
    /// <exception cref="ArgumentException">Identity or revision is uninitialized.</exception>
    /// <returns>Portable document containing the scoped requirement and selected resource.</returns>
    public static ExecutionDefinitionDocument DeclareBinding(ExecutionDefinitionId id, ExecutionRevisionId revision,
        IngestionProviderBinding source, ExecutionProvenance origin) =>
        ExecutionDefinitionDocument.Create(source.Connection is null ? Bindings.Kind : ConnectedBindings.Kind, id, revision, source, origin,
            sourceMap: Map(origin, source.Connection is null ? ["contract", "provider", "resource", "consumer"] : ["contract", "provider", "resource", "consumer", "connection"]));

    static ExecutionSourceMap Map(ExecutionProvenance origin, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return new([.. fields.Select(field => new ExecutionSourceProvenance(origin.Source.Reference + "#/" + field, new([field])))]);
    }

    /// <summary>Resolves the binding, scope, exact provider and ingestion Requests; verifies that the offer
    /// implements the required acquisition. Success establishes compatibility of the declarations only.</summary>
    /// <param name="document">Persisted or freshly lowered binding.</param>
    /// <param name="definitions">Exact profiles, ingestion declarations and application scope documents.</param>
    /// <param name="requests">Exact Request catalog for the selected ingestion composition.</param>
    /// <param name="binding">Receives the binding only when every link validates.</param>
    /// <exception cref="ArgumentNullException">Document or either catalog is null.</exception>
    /// <returns>Structured integrity, wire and link diagnostics.</returns>
    public static DocumentValidationResult Inspect(ExecutionDefinitionDocument document, ExecutionDefinitionDocumentCatalog definitions,
        InteractionContractCatalog requests, out IngestionProviderBindingInspection? binding)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(requests);
        binding = null;
        var result = Read(document.Kind == ConnectedBindings.Kind ? ConnectedBindings : Bindings, document, ValidateBinding, out var candidate);
        if (!result.IsValid) return result;
        if ((document.Kind == ConnectedBindings.Kind) != (candidate!.Connection is not null))
            return Error("version", "Connection semantics require the connection-bound binding version.", "/kind");
        var requirement = candidate.Contract;
        result = ResolveProperties(requirement.Properties, definitions, out var properties);
        if (!result.IsValid) return result;
        result = definitions.ValidateReference(candidate.Provider, "/definition/provider", out var provider);
        if (!result.IsValid) return result;
        result = Read(provider!.Kind == ConnectedProfiles.Kind ? ConnectedProfiles : Profiles, provider, ValidateProvider, out var offer);
        if (!result.IsValid) return result;
        if ((provider.Kind == ConnectedProfiles.Kind) != (offer!.Profile is not null)
            || (candidate.Connection is not null) != (offer.Profile is not null))
            return Error("version", "Provider profile and connection binding versions must agree.", "/provider");
        if (candidate.Connection is { } connectionReference)
        {
            result = definitions.ValidateReference(connectionReference, "/connection", out var connectionDocument);
            if (!result.IsValid) return result;
            result = IntegrationConnectionDocuments.Inspect(connectionDocument!, definitions, out var resolution);
            if (!result.IsValid) return result;
            if (resolution!.Profile != offer.Profile)
                return Error("profile", "Connection resolution pins a different operation profile.", "/provider/profile");
            definitions.TryResolve(resolution.Profile, out var profileDocument);
            definitions.TryResolve(resolution.Connection, out var configuredDocument);
            var profile = profileDocument!.GetDefinition<ProviderOperationProfileDefinition>();
            var configured = configuredDocument!.GetDefinition<IntegrationConnectionDefinition>();
            if (offer.Operation != profile.Operation || offer.Medium != profile.Medium || candidate.Resource != configured.Resource)
                return Error("connection", "Offer operation, medium and resource must agree with the pinned profile and connection.", "/connection");
        }
        result = ResolveProperties(offer!.Properties, definitions, out var providerProperties);
        if (!result.IsValid) return result;
        result = definitions.ValidateReference(requirement.Ingestion, "/definition/contract/ingestion", out var ingestion);
        if (!result.IsValid) return result;
        result = IngestionDefinitionDocuments.TryLower(ingestion!, requests, out _);
        if (!result.IsValid) return result;
        var composition = ingestion!.GetDefinition<IngestionDefinition>();
        if (composition.Acquire != offer!.Acquire)
            return Error("acquire", "The provider must implement the exact acquisition Request required by this binding.", "/definition/provider");
        binding = new(candidate, offer, properties, providerProperties, composition.Publish);
        return result;
    }

    static DocumentValidationResult ResolveProperties(ImmutableArray<ContractProperty> uses,
        ExecutionDefinitionDocumentCatalog catalog, out ImmutableArray<ResolvedContractProperty> properties)
    {
        properties = [];
        if (uses.IsDefaultOrEmpty) return DocumentValidationResult.Valid;
        var builder = ImmutableArray.CreateBuilder<ResolvedContractProperty>(uses.Length);
        HashSet<ContractProperty> seen = [];
        foreach (var use in uses)
        {
            if (use is null || !seen.Add(use))
                return Error("property", "Declare distinct non-null property uses.", "/properties");
            var validation = ContractPropertyDocuments.Inspect(use, catalog, out var resolved);
            if (!validation.IsValid) return validation;
            builder.Add(resolved!);
        }
        properties = builder.MoveToImmutable();
        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Read<T>(ExecutionDefinitionDocumentProjection<T> projection, ExecutionDefinitionDocument document,
        Func<T, DocumentValidationResult> validate, out T? value) where T : class
    {
        value = null;
        if (!document.Extensions.IsDefaultOrEmpty)
            return Error("extension", "The observation profile cannot interpret semantic extensions.", "/extensions");
        return projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document, validate, out value);
    }

    static DocumentValidationResult ValidateProvider(ObservationProviderProfile profile) =>
        profile.Acquire?.Definition is null || string.IsNullOrWhiteSpace(profile.Operation) || string.IsNullOrWhiteSpace(profile.Medium)
            ? Error("offer", "Declare the acquisition Request, provider operation and medium.", "/")
            : DocumentValidationResult.Valid;

    static DocumentValidationResult ValidateBinding(IngestionProviderBinding binding)
    {
        if (binding.Contract?.Ingestion is null || binding.Contract.Properties.IsDefaultOrEmpty || binding.Provider is null)
            return Error("reference", "Declare the exact ingestion, at least one property use, and provider reference.", "/");
        if (binding.Consumer is null || string.IsNullOrWhiteSpace(binding.Resource))
            return Error("identity", "Declare both an external resource and an independent consumer address.", "/");
        try
        {
            binding.Consumer.Validate();
        }
        catch (ArgumentException ex)
        {
            return Error("consumer", ex.Message, "/consumer");
        }
        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Error(string code, string message, string path) =>
        new([new("integrations.binding." + code, DiagnosticSeverity.Error, message, path)]);
}
