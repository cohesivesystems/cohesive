using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Logical application connection name, resolved explicitly before admission.</summary>
/// <param name="Name">Stable logical name, independent of endpoint and credentials.</param>
public sealed record ConnectionReference(string Name)
{
    /// <summary>Declares a logical connection reference.</summary>
    /// <param name="name">Nonempty application-owned name.</param>
    /// <returns>The logical reference.</returns>
    /// <exception cref="ArgumentException">Name is blank.</exception>
    public static ConnectionReference Named(string name)
    { ArgumentException.ThrowIfNullOrWhiteSpace(name); return new(name); }
}

/// <summary>Authentication configuration containing references only. Resolution and secret values are host-owned.</summary>
/// <param name="Mechanism">Adapter-owned mechanism such as anonymous, api-key, oauth, certificate or managed-identity.</param>
/// <param name="Reference">Secret or identity locator; null only for anonymous access.</param>
public sealed record ConnectionAuthentication(string Mechanism, string? Reference);

/// <summary>Canonical provider-operation profile, independent of any account connection.</summary>
/// <param name="Provider">External provider identity.</param>
/// <param name="Operation">Adapter operation identity.</param>
/// <param name="Medium">Open transport/adapter identity.</param>
/// <param name="AuthenticationMechanisms">Supported authentication mechanisms.</param>
/// <param name="RequiredEntitlements">Required account permissions or product features; not behavioral guarantees.</param>
/// <param name="DefaultBatchSize">Overridable preferred number of items per operation.</param>
/// <param name="MaximumBatchSize">Hard upper bound on items per operation.</param>
public sealed record ProviderOperationProfileDefinition(string Provider, string Operation, string Medium,
    ImmutableArray<string> AuthenticationMechanisms, ImmutableArray<string> RequiredEntitlements,
    int DefaultBatchSize, int MaximumBatchSize);

/// <summary>Canonical configured access to an external resource. No executable clients or secret payloads.</summary>
/// <param name="Provider">External provider identity.</param>
/// <param name="Resource">Shared account/authority/quota identity.</param>
/// <param name="Medium">Open transport/adapter identity.</param>
/// <param name="Endpoint">Absolute endpoint URI without credentials, query or fragment.</param>
/// <param name="Authentication">Authentication mechanism and reference.</param>
/// <param name="Entitlements">Host-declared account permissions/features, awaiting remote qualification where required.</param>
/// <param name="MaximumBatchSize">Hard connection/account upper bound on items per operation.</param>
public sealed record IntegrationConnectionDefinition(string Provider, string Resource, string Medium, string Endpoint,
    ConnectionAuthentication Authentication, ImmutableArray<string> Entitlements, int MaximumBatchSize);

/// <summary>Exact admitted connection choice and its effective bounded configuration.</summary>
/// <param name="Logical">Application reference that was resolved.</param>
/// <param name="Profile">Exact provider-operation profile.</param>
/// <param name="Connection">Exact configured connection.</param>
/// <param name="RequestedBatchSize">Explicit local choice, or null when the profile default supplied it.</param>
/// <param name="Configuration">Optional exact scoped-selection snapshot.</param>
/// <param name="EffectiveBatchSize">Validated effective items per operation; never silently clamped.</param>
public sealed record IntegrationConnectionResolution(ConnectionReference Logical, ExecutionDefinitionReference Profile,
    ExecutionDefinitionReference Connection, int? RequestedBatchSize, int EffectiveBatchSize, ExecutionDefinitionReference? Configuration = null);

/// <summary>Immutable authoring source for a provider-operation profile.</summary>
public sealed record ProviderOperationProfile
{
    readonly ExecutionDefinitionId id;
    readonly ExecutionRevisionId revision;
    ProviderOperationProfileDefinition definition;
    ProviderOperationProfile(string id, string revision, ProviderOperationProfileDefinition definition)
    { this.id = new(id); this.revision = new(revision); this.definition = definition; }

    /// <summary>Declares an operation with anonymous authentication and a one-item batch convention.</summary>
    /// <param name="id">Stable profile identity.</param>
    /// <param name="revision">Explicit semantic revision.</param>
    /// <param name="provider">External provider identity.</param>
    /// <param name="operation">Adapter operation identity.</param>
    /// <param name="medium">Open transport/adapter identity.</param>
    /// <returns>Immutable source refinable through fluent methods.</returns>
    /// <exception cref="ArgumentException">Identity or revision is blank.</exception>
    public static ProviderOperationProfile Define(string id, string revision, string provider, string operation, string medium) =>
        new(id, revision, new(provider, operation, medium, ["anonymous"], [], 1, 1));

    /// <summary>Declares supported authentication mechanisms.</summary>
    /// <param name="mechanisms">Nonempty set of adapter-owned mechanism names.</param>
    /// <returns>A refined source; the original remains unchanged.</returns>
    public ProviderOperationProfile AuthenticateWith(params string[] mechanisms) =>
        this with { definition = definition with { AuthenticationMechanisms = IntegrationConnectionDocuments.Names(mechanisms) } };

    /// <summary>Declares required account entitlements.</summary>
    /// <param name="entitlements">Account permissions/features needed by the operation.</param>
    /// <returns>A refined source.</returns>
    public ProviderOperationProfile RequiresEntitlements(params string[] entitlements) =>
        this with { definition = definition with { RequiredEntitlements = IntegrationConnectionDocuments.Names(entitlements) } };

    /// <summary>Separates an overridable batch default from a hard operation limit.</summary>
    /// <param name="defaultSize">Default items per call.</param>
    /// <param name="maximumSize">Maximum items per call.</param>
    /// <returns>A refined source, validated when lowered.</returns>
    public ProviderOperationProfile Batching(int defaultSize, int maximumSize) =>
        this with { definition = definition with { DefaultBatchSize = defaultSize, MaximumBatchSize = maximumSize } };

    /// <summary>Validated deterministic canonical profile derived from this source.</summary>
    /// <exception cref="ArgumentException">The profile is invalid.</exception>
    public ExecutionDefinitionDocument Document => IntegrationConnectionDocuments.DeclareProfile(id, revision, definition);
}

/// <summary>Immutable connection source, shared by any number of compatible operation profiles.</summary>
public sealed record IntegrationConnection
{
    readonly ExecutionDefinitionId id;
    readonly ExecutionRevisionId revision;
    IntegrationConnectionDefinition definition;
    IntegrationConnection(string id, string revision, IntegrationConnectionDefinition definition)
    { this.id = new(id); this.revision = new(revision); this.definition = definition; }

    /// <summary>Declares a configured access relationship; anonymous authentication and one-item batches are initial conventions.</summary>
    /// <param name="id">Stable connection identity.</param>
    /// <param name="revision">Semantic configuration revision.</param>
    /// <param name="provider">External provider identity.</param>
    /// <param name="resource">Shared account/quota identity.</param>
    /// <param name="medium">Transport/adapter identity.</param>
    /// <param name="endpoint">Absolute endpoint URI with no credentials, query or fragment.</param>
    /// <returns>Immutable connection source.</returns>
    /// <exception cref="ArgumentException">Identity or revision is blank.</exception>
    public static IntegrationConnection Define(string id, string revision, string provider, string resource, string medium, string endpoint) =>
        new(id, revision, new(provider, resource, medium, endpoint, new("anonymous", null), [], 1));

    /// <summary>Declares how the host resolves authentication without storing its secret value.</summary>
    /// <param name="mechanism">Adapter-supported authentication mechanism.</param>
    /// <param name="reference">Absolute secret or identity locator, such as env:TIINGO_API_KEY.</param>
    /// <returns>A refined source.</returns>
    public IntegrationConnection AuthenticateWith(string mechanism, string reference) =>
        this with { definition = definition with { Authentication = new(mechanism, reference) } };

    /// <summary>Declares the account entitlements supplied by the host.</summary>
    /// <param name="entitlements">Permissions/features, not evidence of behavioral guarantees.</param>
    /// <returns>A refined source.</returns>
    public IntegrationConnection EntitledTo(params string[] entitlements) =>
        this with { definition = definition with { Entitlements = IntegrationConnectionDocuments.Names(entitlements) } };

    /// <summary>Declares a hard account/connection batch bound.</summary>
    /// <param name="maximumSize">Maximum items per operation.</param>
    /// <returns>A refined source, validated during lowering.</returns>
    public IntegrationConnection LimitBatchSize(int maximumSize) =>
        this with { definition = definition with { MaximumBatchSize = maximumSize } };

    /// <summary>Validated canonical configuration. Referenced secrets and physical clients remain external.</summary>
    /// <exception cref="ArgumentException">The connection is invalid.</exception>
    public ExecutionDefinitionDocument Document => IntegrationConnectionDocuments.DeclareConnection(id, revision, definition);
}

/// <summary>Explicit immutable logical-reference resolution. Hierarchical policy composition belongs to Configuration.</summary>
public sealed class ConnectionBindings
{
    readonly ImmutableDictionary<string, IntegrationConnection> connections;
    internal ExecutionDefinitionDocument? Configuration { get; private init; }
    internal ImmutableArray<ExecutionDefinitionDocument> ConfigurationDocuments { get; private init; } = [];
    internal ConnectionReference? ConfiguredLogical { get; private init; }
    internal int? ConfiguredBatch { get; private init; }
    internal ConnectionBindings WithSelection(ConnectionReference logical, int batch, ExecutionDefinitionDocument snapshot,
        ImmutableArray<ExecutionDefinitionDocument> documents) => new(connections)
        { ConfiguredLogical = logical, ConfiguredBatch = batch, Configuration = snapshot, ConfigurationDocuments = documents };
    /// <summary>Creates an empty resolution scope.</summary>
    public ConnectionBindings() : this(ImmutableDictionary<string, IntegrationConnection>.Empty.WithComparers(StringComparer.Ordinal)) { }
    ConnectionBindings(ImmutableDictionary<string, IntegrationConnection> connections) => this.connections = connections;

    /// <summary>Binds one logical reference without changing the original scope.</summary>
    /// <param name="reference">Logical application reference.</param>
    /// <param name="connection">Concrete immutable connection source.</param>
    /// <returns>A new explicit resolution scope.</returns>
    /// <exception cref="ArgumentException">The name is blank or already bound.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ConnectionBindings Bind(ConnectionReference reference, IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(reference); ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Name);
        if (connections.ContainsKey(reference.Name)) throw new ArgumentException("A logical connection name is already bound.", nameof(reference));
        return new(connections.Add(reference.Name, connection)) { Configuration = Configuration, ConfigurationDocuments = ConfigurationDocuments, ConfiguredLogical = ConfiguredLogical, ConfiguredBatch = ConfiguredBatch };
    }

    internal bool TryResolve(ConnectionReference reference, out IntegrationConnection? connection) =>
        connections.TryGetValue(reference.Name, out connection);

    /// <summary>Resolves exactly one configured connection; no fallback or registration-order selection.</summary>
    /// <param name="reference">Logical application reference.</param>
    /// <returns>The selected immutable source.</returns>
    /// <exception cref="ArgumentNullException">Reference is null.</exception>
    /// <exception cref="InvalidOperationException">The logical reference is unbound.</exception>
    public IntegrationConnection Resolve(ConnectionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return connections.TryGetValue(reference.Name, out var connection) ? connection
            : throw new InvalidOperationException("The logical connection reference is unbound.");
    }
}
