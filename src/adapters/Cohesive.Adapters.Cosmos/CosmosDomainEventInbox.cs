using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Cosmos-backed durable target-deduplicating canonical domain-event inbox.</summary>
/// <remarks>
/// The container must use <c>/partitionKey</c> as its logical partition path. Entries are partitioned by authority
/// and optional tenant, and addressed within that boundary by a SHA-256 projection of the complete canonical
/// publication key. A point-create conflict is accepted only when the retained key and canonical envelope are exact.
/// </remarks>
public sealed class CosmosDomainEventInbox : IDomainEventInbox
{
    /// <summary>Stable diagnostic emitted when one scoped publication key is reused for different content.</summary>
    public const string IdentityConflictCode = "cosmos.domainEventInbox.identity.conflict";

    /// <summary>Stable diagnostic emitted when the configured container cannot preserve inbox semantics.</summary>
    public const string ContainerIncompatibleCode = "cosmos.domainEventInbox.container.incompatible";

    const int CurrentSchemaVersion = 1;
    static readonly ValueContract ReceiptContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    readonly Container container;
    readonly InteractionContractCatalog contracts;
    readonly CosmosDomainEventInboxOptions options;

    /// <summary>Creates an inbox over one Cosmos container and exact admitted event contracts.</summary>
    /// <param name="container">Cosmos container using <c>/partitionKey</c>.</param>
    /// <param name="contracts">Exact canonical interaction-contract catalog used to restore retained envelopes.</param>
    /// <param name="targetDeduplicatedContracts">Exact domain-event contracts admitted by this target.</param>
    /// <param name="options">Optional document conventions.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// No contract is supplied, a supplied reference does not resolve as an exact domain-event contract, or options
    /// are invalid.
    /// </exception>
    public CosmosDomainEventInbox(
        Container container,
        InteractionContractCatalog contracts,
        IEnumerable<DomainEventContractReference> targetDeduplicatedContracts,
        CosmosDomainEventInboxOptions? options = null)
    {
        this.container = Guard.RequireNotNull(container);
        this.contracts = Guard.RequireNotNull(contracts);
        ArgumentNullException.ThrowIfNull(targetDeduplicatedContracts);
        var admitted = targetDeduplicatedContracts.ToArray();
        if (admitted.Length == 0)
        {
            throw new ArgumentException(
                "A Cosmos domain-event inbox requires at least one exact admitted contract.",
                nameof(targetDeduplicatedContracts));
        }
        foreach (var contract in admitted)
        {
            if (contract is null
                || !contracts.TryResolve(contract, out var definition)
                || definition is not DomainEventContractDefinition)
            {
                throw new ArgumentException(
                    "Every Cosmos domain-event inbox capability must resolve as an exact domain-event contract.",
                    nameof(targetDeduplicatedContracts));
            }
        }

        Capabilities = new(admitted.ToImmutableArray());
        this.options = CosmosDomainEventInboxOptions.RequireValid(options ?? new());
    }

    /// <inheritdoc />
    public DomainEventPublisherCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask ValidateAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var response = await container.ReadContainerAsync(cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        var properties = response.Resource;
        if (!string.Equals(properties.PartitionKeyPath, "/partitionKey", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ContainerIncompatibleCode}: Cosmos domain-event inbox container "
                + $"'{container.Database.Id}/{container.Id}' uses partition path "
                + $"'{properties.PartitionKeyPath}', but exact authority scoping requires '/partitionKey'.");
        }
        if (properties.DefaultTimeToLive is > 0)
        {
            throw new InvalidOperationException(
                $"{ContainerIncompatibleCode}: Cosmos domain-event inbox container "
                + $"'{container.Database.Id}/{container.Id}' expires entries after "
                + $"'{properties.DefaultTimeToLive}' seconds and cannot preserve durable target deduplication.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<DomainEventPublicationAcknowledgement> PublishAsync(
        OperationContext context,
        DomainEventPublicationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        RequireSupported(invocation.DeduplicationKey.Contract);

        var document = CreateDocument(invocation, context.UtcNow);
        try
        {
            _ = await container.CreateItemAsync(
                    document,
                    new PartitionKey(document.PartitionKey),
                    cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
            return Acknowledgement(document.Receipt);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            var retained = await ReadDocumentAsync(context, invocation.DeduplicationKey).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Cosmos reported a domain-event inbox identity conflict, but the retained entry was absent.",
                    exception);
            RequireExactReplay(document, retained);
            return Acknowledgement(retained.Receipt);
        }
    }

    /// <inheritdoc />
    public async ValueTask<DomainEventInboxEntry?> TryReadAsync(
        OperationContext context,
        DomainEventPublicationDeduplicationKey deduplicationKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(deduplicationKey);
        context.ThrowIfCancellationRequested();
        RequireSupported(deduplicationKey.Contract);
        var document = await ReadDocumentAsync(context, deduplicationKey).ConfigureAwait(false);
        return document is null ? null : Restore(document, deduplicationKey);
    }

    async ValueTask<CosmosDomainEventInboxDocument?> ReadDocumentAsync(
        OperationContext context,
        DomainEventPublicationDeduplicationKey key)
    {
        var identity = Identity(key);
        try
        {
            var response = await container.ReadItemAsync<CosmosDomainEventInboxDocument>(
                    identity.Id,
                    new PartitionKey(identity.PartitionKey),
                    cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal CosmosDomainEventInboxDocument CreateDocument(
        DomainEventPublicationInvocation invocation,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (acceptedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Inbox acceptance time must use the UTC offset.", nameof(acceptedAtUtc));

        var key = invocation.DeduplicationKey;
        var identity = Identity(key);
        var envelope = InteractionEnvelopeJsonSerializer.Serialize(invocation.DomainEvent);
        var fingerprint = InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(invocation.DomainEvent);
        return new(
            identity.Id,
            identity.PartitionKey,
            options.DocumentKind,
            CurrentSchemaVersion,
            key.AuthorityScope.Authority,
            key.AuthorityScope.Tenant,
            key.Contract.Definition.DefinitionId.Value,
            key.Contract.Definition.RevisionId.Value,
            key.Contract.Definition.Fingerprint.Algorithm,
            key.Contract.Definition.Fingerprint.Canonicalization,
            key.Contract.Definition.Fingerprint.Value,
            key.IdempotencyKey.Value,
            envelope,
            fingerprint.Value,
            acceptedAtUtc,
            $"cosmos-domain-event-inbox:{identity.Id}");
    }

    internal DomainEventInboxEntry Restore(
        CosmosDomainEventInboxDocument document,
        DomainEventPublicationDeduplicationKey requestedKey)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(requestedKey);
        var retainedKey = Key(document);
        var identity = Identity(retainedKey);
        if (document.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(document.DocumentKind, options.DocumentKind, StringComparison.Ordinal)
            || !string.Equals(document.Id, identity.Id, StringComparison.Ordinal)
            || !string.Equals(document.PartitionKey, identity.PartitionKey, StringComparison.Ordinal)
            || retainedKey != requestedKey)
        {
            throw Conflict(requestedKey, "the retained storage identity or scoped publication key differs");
        }

        var restored = InteractionEnvelopeJsonSerializer.Deserialize(document.Envelope, contracts);
        if (restored is not DomainEventEnvelope domainEvent)
            throw Conflict(requestedKey, "the retained canonical envelope is not a domain event");

        var entry = new DomainEventInboxEntry(
            retainedKey,
            domainEvent,
            new(document.EnvelopeFingerprint),
            document.AcceptedAtUtc,
            Acknowledgement(document.Receipt));
        if (!string.Equals(
                document.Envelope,
                InteractionEnvelopeJsonSerializer.Serialize(entry.DomainEvent),
                StringComparison.Ordinal))
        {
            throw Conflict(requestedKey, "the retained canonical envelope bytes are unstable");
        }
        return entry;
    }

    internal static void RequireExactReplay(
        CosmosDomainEventInboxDocument candidate,
        CosmosDomainEventInboxDocument retained)
    {
        if (candidate with { AcceptedAtUtc = retained.AcceptedAtUtc, Receipt = retained.Receipt } != retained)
        {
            throw Conflict(
                Key(candidate),
                "the same scoped publication key is already retained with different canonical content");
        }
    }

    void RequireSupported(DomainEventContractReference contract)
    {
        if (!Capabilities.Supports(contract))
        {
            throw new InvalidOperationException(
                $"Cosmos domain-event inbox does not admit exact contract '{Describe(contract.Definition)}'.");
        }
    }

    static DomainEventPublicationAcknowledgement Acknowledgement(string receipt) => new(
        PortableValue.Concrete(ReceiptContract, ObservationValue.FromString(receipt)));

    static DomainEventPublicationDeduplicationKey Key(CosmosDomainEventInboxDocument document) => new(
        new(document.Authority, document.Tenant),
        new(new(
            new(document.ContractDefinitionId),
            new(document.ContractRevisionId),
            new(
                document.ContractFingerprintAlgorithm,
                document.ContractFingerprintCanonicalization,
                document.ContractFingerprintValue))),
        new(document.IdempotencyKey));

    static (string Id, string PartitionKey) Identity(DomainEventPublicationDeduplicationKey key)
    {
        var authorityScope = LengthPrefixed(
            key.AuthorityScope.Authority,
            key.AuthorityScope.Tenant ?? string.Empty);
        var authorityDigest = SHA256.HashData(Encoding.UTF8.GetBytes(authorityScope));
        var partitionKey = $"authority-{Convert.ToHexStringLower(authorityDigest)}";
        var canonical = LengthPrefixed(
            key.AuthorityScope.Authority,
            key.AuthorityScope.Tenant ?? string.Empty,
            key.Contract.Definition.DefinitionId.Value,
            key.Contract.Definition.RevisionId.Value,
            key.Contract.Definition.Fingerprint.Algorithm,
            key.Contract.Definition.Fingerprint.Canonicalization,
            key.Contract.Definition.Fingerprint.Value,
            key.IdempotencyKey.Value);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return ($"domain-event-{Convert.ToHexStringLower(digest)}", partitionKey);
    }

    static string LengthPrefixed(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
            _ = builder.Append(value.Length).Append(':').Append(value);
        return builder.ToString();
    }

    static InvalidOperationException Conflict(
        DomainEventPublicationDeduplicationKey key,
        string detail) => new(
        $"{IdentityConflictCode}: Domain-event inbox key '{Describe(key.Contract.Definition)}' / "
        + $"'{key.AuthorityScope.Authority}' / '{key.AuthorityScope.Tenant ?? "-"}' / "
        + $"'{key.IdempotencyKey.Value}' conflicts because {detail}.");

    static string Describe(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";
}
