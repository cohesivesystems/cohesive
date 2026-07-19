using System.Globalization;
using System.Text;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Creates canonical entity-source registrations backed by one Cosmos container.</summary>
/// <remarks>
/// Convention-derived source identities include the Cosmos account endpoint, database, container, physical policy,
/// and other stable binding facts. A convention-derived execution domain records physical account/database/container
/// affinity only; callers that need a narrower routing or consistency domain must supply an explicit identity. No
/// derived or explicit domain establishes an atomic snapshot across separate SDK queries.
/// </remarks>
public static class CosmosEntityRelationQuerySourceRegistration
{
    const string SourceBindingProfile = "cohesive.adapters.cosmos/entity-source-binding/v2";

    /// <summary>Creates a deterministic Cosmos-backed canonical entity source registration.</summary>
    /// <param name="shape">Exact graph-qualified entity shape stored in the container.</param>
    /// <param name="container">Cosmos SDK container used for bounded acquisition.</param>
    /// <param name="databaseId">Stable Cosmos database identity used in source provenance.</param>
    /// <param name="containerId">Stable Cosmos container identity used in source provenance.</param>
    /// <param name="policy">Explicit partition and physical query limits.</param>
    /// <param name="source">
    /// Explicit source identity, or <see langword="null"/> for an account-, binding-, shape-, discriminator-, and
    /// policy-derived identity. Custom selector delegates require an explicit identity because delegates have no
    /// portable content identity.
    /// </param>
    /// <param name="executionDomain">
    /// Explicit execution domain, or <see langword="null"/> for a deterministic physical
    /// account/database/container domain. Supply an explicit domain when consistency level, preferred region, or
    /// another client-level routing fact must distinguish otherwise identical containers. Neither form implies that
    /// separate SDK queries share an atomic snapshot.
    /// </param>
    /// <param name="limits">
    /// Explicit canonical source limits, or <see langword="null"/> for Cosmos defaults. The returned source's
    /// effective batch limit is deterministically constrained by <paramref name="policy"/>'s keys-per-query and
    /// query-chunk limits, and its effective row buffer is constrained by the policy's enumeration limit and the
    /// runtime array limit.
    /// </param>
    /// <param name="identitySourceSelector">
    /// Physical property-only observation-identity path, or <see langword="null"/> for <c>observationId</c>.
    /// Returned documents must contain a nonempty JSON string at the effective path.
    /// </param>
    /// <param name="fieldSourceSelector">
    /// Semantic-to-physical field policy, or <see langword="null"/> to select below <c>observation</c>. A custom
    /// policy is evaluated and property-path-validated per requested semantic path, not exhaustively by this method.
    /// </param>
    /// <param name="relationshipKeySourceSelector">
    /// Semantic-to-physical relationship-reference policy, or <see langword="null"/> to select below
    /// <c>observation</c>. A custom policy is evaluated and property-path-validated per requested relationship
    /// reference, not exhaustively by this method.
    /// </param>
    /// <param name="entityDocumentKind">
    /// Entity-document discriminator, or <see langword="null"/> for
    /// <see cref="CosmosRelationQuerySourceReader.DefaultEntityDocumentKind"/>.
    /// </param>
    /// <returns>
    /// A canonical Storage registration whose reader, identity, domain, capability profile, limits, and selectors agree.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="container"/>, <paramref name="databaseId"/>, <paramref name="containerId"/>, or
    /// <paramref name="policy"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is incomplete; a database, container, source, domain, selector, or discriminator
    /// identity is empty; an SDK container identity disagrees with <paramref name="databaseId"/> or
    /// <paramref name="containerId"/>; a conventional source is requested with a custom selector delegate; or an
    /// identity or partition selector is not a property-only Cosmos path; cross-partition reads are prohibited
    /// without a fixed partition scope.
    /// </exception>
    public static EntityRelationQuerySourceRegistration Create(
        QualifiedShapeId shape,
        Container container,
        string databaseId,
        string containerId,
        CosmosRelationQuerySourcePolicy policy,
        RelationQuerySourceInstanceId? source = null,
        RelationQueryExecutionDomainId? executionDomain = null,
        RelationQuerySourcePlacementLimits? limits = null,
        string? identitySourceSelector = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        string? entityDocumentKind = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A Cosmos entity source requires a graph-qualified shape.", nameof(shape));

        var normalizedDatabase = Guard.RequireNotNullOrWhiteSpace(databaseId);
        var normalizedContainer = Guard.RequireNotNullOrWhiteSpace(containerId);
        var accountEndpoint = CosmosPhysicalAffinity.CanonicalAccountEndpointText(
            container.Database.Client.Endpoint);
        var effectiveIdentitySelector = identitySourceSelector is null
            ? CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector
            : CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                identitySourceSelector,
                nameof(identitySourceSelector)).ToString();
        var effectiveEntityDocumentKind = entityDocumentKind is null
            ? CosmosRelationQuerySourceReader.DefaultEntityDocumentKind
            : Guard.RequireNotNullOrWhiteSpace(entityDocumentKind);
        var configuredLimits = limits ?? CosmosRelationQuerySourceReader.DefaultLimits;
        var effectiveLimits = policy.GetEffectivePlacementLimits(configuredLimits);
        if (source is null && (fieldSourceSelector is not null || relationshipKeySourceSelector is not null))
        {
            throw new ArgumentException(
                "A conventional Cosmos source identity cannot fingerprint custom selector delegates; supply an explicit source identity.",
                nameof(source));
        }
        var bindingKey = string.Concat(
            Uri.EscapeDataString(normalizedDatabase),
            "/",
            Uri.EscapeDataString(normalizedContainer));
        var shapeKey = string.Concat(
            Uri.EscapeDataString(shape.GraphId.Value),
            "/",
            Uri.EscapeDataString(shape.ShapeId.Value));
        var accountKey = CosmosPhysicalAffinity.Fingerprint(accountEndpoint);
        var bindingFingerprint = FingerprintBinding(
            accountEndpoint,
            normalizedDatabase,
            normalizedContainer,
            shape,
            policy,
            effectiveLimits,
            effectiveIdentitySelector,
            effectiveEntityDocumentKind);
        var effectiveSource = source ?? new RelationQuerySourceInstanceId(
            $"source/cohesive.adapters.cosmos/{bindingFingerprint}/{shapeKey}");
        var effectiveDomain = executionDomain ?? new RelationQueryExecutionDomainId(
            $"domain/cohesive.adapters.cosmos/{accountKey}/{bindingKey}");
        var sourceInstance = new RelationQuerySourceInstance(
            effectiveSource,
            effectiveDomain,
            CosmosRelationQuerySourceReader.TargetProfile,
            effectiveLimits);
        var reader = new CosmosRelationQuerySourceReader(
            shape,
            sourceInstance,
            container,
            normalizedDatabase,
            normalizedContainer,
            policy,
            effectiveIdentitySelector,
            fieldSourceSelector,
            relationshipKeySourceSelector,
            effectiveEntityDocumentKind);
        return new(
            shape,
            sourceInstance,
            reader,
            reader.IdentitySourceSelector,
            reader.FieldSourceSelector,
            reader.RelationshipKeySourceSelector);
    }

    static string FingerprintBinding(
        string accountEndpoint,
        string databaseId,
        string containerId,
        QualifiedShapeId shape,
        CosmosRelationQuerySourcePolicy policy,
        RelationQuerySourcePlacementLimits limits,
        string identitySourceSelector,
        string entityDocumentKind)
    {
        StringBuilder canonical = new();
        Append(SourceBindingProfile);
        Append(accountEndpoint);
        Append(databaseId);
        Append(containerId);
        Append(shape.GraphId.Value);
        Append(shape.ShapeId.Value);
        Append(identitySourceSelector);
        Append(entityDocumentKind);
        Append(policy.PartitionSourceSelector);
        Append(((int)policy.CrossPartitionPolicy).ToString(CultureInfo.InvariantCulture));
        Append(policy.FixedPartitionKey?.ToString());
        Append(policy.MaximumEnumerationRows.ToString(CultureInfo.InvariantCulture));
        Append(policy.MaximumKeysPerQuery.ToString(CultureInfo.InvariantCulture));
        Append(policy.MaximumQueryChunks.ToString(CultureInfo.InvariantCulture));
        Append(policy.MaximumSdkPageSize.ToString(CultureInfo.InvariantCulture));
        Append(policy.RequestSizeLimits.MaximumSqlQueryUtf8Bytes.ToString(CultureInfo.InvariantCulture));
        Append(policy.RequestSizeLimits.MaximumRequestUtf8Bytes.ToString(CultureInfo.InvariantCulture));
        Append(limits.MaximumBatchSize.ToString(CultureInfo.InvariantCulture));
        Append(limits.MaximumBufferedRows.ToString(CultureInfo.InvariantCulture));
        Append(limits.MaximumFanOut.ToString(CultureInfo.InvariantCulture));
        Append(limits.MaximumConcurrency.ToString(CultureInfo.InvariantCulture));
        return CosmosPhysicalAffinity.Fingerprint(canonical.ToString());

        void Append(string? value) => canonical
            .Append(value?.Length ?? -1)
            .Append(':')
            .Append(value)
            .Append(';');
    }

}
