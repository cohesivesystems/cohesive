using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Offline logical container binding; the selected runtime connection remains native Cosmos configuration.</summary>
/// <remarks>The logical account must be associated with the same native resource supplying the runtime connection.
/// Evidence attests bounded acquisition and partition policy, never a snapshot across SDK requests or live access.</remarks>
public sealed class CosmosEntityQueryBinding
{
    /// <summary>Declares a container-backed source without creating an SDK client.</summary>
    /// <param name="shape">Canonical entity shape.</param><param name="account">Logical account resource identity.</param>
    /// <param name="database">Native database name.</param><param name="container">Native container name.</param>
    /// <param name="policy">Explicit partition and bounded acquisition policy.</param><param name="identity">Canonical identity field path.</param>
    public CosmosEntityQueryBinding(QualifiedShapeId shape, string account, string database, string container,
        CosmosRelationQuerySourcePolicy policy, FieldPath identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(policy);
        Account = account;
        Database = database;
        Container = container;
        Policy = policy;
        var limits = policy.GetEffectivePlacementLimits(CosmosRelationQuerySourceReader.DefaultLimits);
        var fingerprint = CosmosEntityRelationQuerySourceRegistration.FingerprintBinding("logical-account/" + account,
            database, container, shape, policy, limits, CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector,
            CosmosRelationQuerySourceReader.DefaultEntityDocumentKind, null, shape, null);
        var source = new RelationQuerySourceInstance(new("source/cosmos/" + fingerprint),
            new("domain/cosmos/" + Uri.EscapeDataString(account) + "/" + Uri.EscapeDataString(database) + "/" + Uri.EscapeDataString(container)),
            CosmosRelationQuerySourceReader.TargetProfile, limits);
        Binding = new(shape, source, new(source.Id, source.ExecutionDomain, source.TargetProfile, policy.LogicalPartition,
            policy.FixedPartitionKey is null ? null : new(policy.PartitionSourceSelector)),
            identitySourceSelector: CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector,
            identitySemanticPath: identity, fieldSourceSelector: CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector,
            relationshipKeySourceSelector: CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector);
    }
    /// <summary>Logical account resource supplying the selected native connection.</summary>
    public string Account { get; }
    /// <summary>Native database name.</summary>
    public string Database { get; }
    /// <summary>Native container name.</summary>
    public string Container { get; }
    /// <summary>Canonical source metadata consumed by offline planning.</summary>
    public EntityRelationQuerySourceBinding Binding { get; }
    /// <summary>Policy enforced by each runtime acquisition.</summary>
    public CosmosRelationQuerySourcePolicy Policy { get; }

    /// <summary>Binds the declared source to the native container supplied by its account association.</summary>
    /// <param name="container">Caller-owned SDK container; no connection is opened here.</param>
    /// <returns>Runtime registration retaining exact offline source identity and policy.</returns>
    /// <exception cref="ArgumentException">Native database or container identity differs.</exception>
    public EntityRelationQuerySourceRegistration Bind(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.Id != Container || container.Database.Id != Database)
            throw new ArgumentException("Container must match its declared database and container names.", nameof(container));
        var reader = new CosmosRelationQuerySourceReader(Binding.Shape, Binding.Source, container, Database, Container, Policy);
        return new(Binding.Shape, Binding.Source, reader, Binding.IdentitySourceSelector, Binding.IdentitySemanticPath,
            reader.FieldSourceSelector, reader.RelationshipKeySourceSelector);
    }
}
