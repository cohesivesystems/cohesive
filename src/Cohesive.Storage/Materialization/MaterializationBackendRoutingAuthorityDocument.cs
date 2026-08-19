using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Portable canonical state of one complete materialization backend-routing authority.</summary>
/// <remarks>
/// This document is the provider-neutral durability boundary for routing state. It retains observable snapshots,
/// accepted command intents, exact replay receipts, pending follow-ups, and cross-placement physical-cleanup state.
/// Concrete adapters persist this document atomically; they do not reinterpret routing transitions.
/// </remarks>
public sealed record MaterializationBackendRoutingAuthorityDocument
{
    /// <summary>Current portable routing-authority document schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-backend-routing-authority/v1";

    /// <summary>Creates one canonical routing-authority document.</summary>
    /// <param name="schemaVersion">Portable document schema version.</param>
    /// <param name="pool">Exact backend-pool definition governed by this authority.</param>
    /// <param name="scopes">Placement-scoped state in canonical fingerprint order.</param>
    /// <param name="physicalCleanup">Cross-placement cleanup state in canonical generation order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported; entries are null or duplicated; or an entry belongs to another pool.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendRoutingAuthorityDocument(
        string schemaVersion,
        MaterializationBackendPoolReference pool,
        ImmutableArray<MaterializationBackendRoutingScopeDocument> scopes,
        ImmutableArray<MaterializationBackendPhysicalCleanupDocument> physicalCleanup)
    {
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported materialization backend-routing authority schema '{schemaVersion}'.",
                nameof(schemaVersion));
        }

        Pool = pool ?? throw new ArgumentNullException(nameof(pool));
        SchemaVersion = schemaVersion;
        Scopes = NormalizeScopes(scopes, pool);
        PhysicalCleanup = NormalizePhysicalCleanup(physicalCleanup, pool);
        ValidateCrossScopeCleanup(Scopes, PhysicalCleanup);
    }

    /// <summary>Portable document schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact backend-pool definition governed by this authority.</summary>
    public MaterializationBackendPoolReference Pool { get; }

    /// <summary>Placement-scoped authority state in canonical fingerprint order.</summary>
    public ImmutableArray<MaterializationBackendRoutingScopeDocument> Scopes { get; }

    /// <summary>Cross-placement physical-cleanup state in canonical generation order.</summary>
    public ImmutableArray<MaterializationBackendPhysicalCleanupDocument> PhysicalCleanup { get; }

    /// <summary>Creates an empty authority for one exact backend-pool document.</summary>
    /// <param name="document">Canonical backend-pool document governed by the authority.</param>
    /// <returns>An empty current-schema authority document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static MaterializationBackendRoutingAuthorityDocument Empty(MaterializationBackendPoolDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(
            schemaVersion: CurrentSchemaVersion,
            pool: MaterializationBackendPoolReference.FromDocument(document),
            scopes: [],
            physicalCleanup: []);
    }

    static ImmutableArray<MaterializationBackendRoutingScopeDocument> NormalizeScopes(
        ImmutableArray<MaterializationBackendRoutingScopeDocument> scopes,
        MaterializationBackendPoolReference pool)
    {
        var normalized = scopes.IsDefault ? [] : scopes;
        if (normalized.Any(static scope => scope is null)
            || normalized.GroupBy(static scope => scope.Snapshot.PlacementSlice.Fingerprint)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Routing scopes must be non-null and placement-unique.", nameof(scopes));
        }

        if (normalized.Any(scope => scope.Snapshot.PlacementSlice.Pool != pool))
        {
            throw new ArgumentException("Every routing scope must belong to the document's exact backend pool.", nameof(scopes));
        }

        return
        [
            .. normalized.OrderBy(
                static scope => scope.Snapshot.PlacementSlice.Fingerprint.Value,
                StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<MaterializationBackendPhysicalCleanupDocument> NormalizePhysicalCleanup(
        ImmutableArray<MaterializationBackendPhysicalCleanupDocument> physicalCleanup,
        MaterializationBackendPoolReference pool)
    {
        var normalized = physicalCleanup.IsDefault ? [] : physicalCleanup;
        if (normalized.Any(static cleanup => cleanup is null)
            || normalized.GroupBy(static cleanup => cleanup.Reservation.Generation)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Physical-cleanup entries must be non-null and generation-unique.",
                nameof(physicalCleanup));
        }

        if (normalized.Any(cleanup => cleanup.Reservation.Retirements.Any(
                retirement => retirement.PlacementSlice.Pool != pool)))
        {
            throw new ArgumentException(
                "Every physical-cleanup retirement claim must belong to the document's exact backend pool.",
                nameof(physicalCleanup));
        }

        return
        [
            .. normalized.OrderBy(static cleanup => cleanup.Reservation.Generation.TargetId.Value, StringComparer.Ordinal)
                .ThenBy(static cleanup => cleanup.Reservation.Generation.GenerationId.Value, StringComparer.Ordinal)
        ];
    }

    static void ValidateCrossScopeCleanup(
        ImmutableArray<MaterializationBackendRoutingScopeDocument> scopes,
        ImmutableArray<MaterializationBackendPhysicalCleanupDocument> physicalCleanup)
    {
        var scopesByFingerprint = scopes.ToDictionary(
            static scope => scope.Snapshot.PlacementSlice.Fingerprint);
        var cleanupByGeneration = physicalCleanup.ToDictionary(
            static cleanup => cleanup.Reservation.Generation);

        foreach (var scope in scopes)
        {
            if (scope.Snapshot.Cleaned.Any(generation => !cleanupByGeneration.ContainsKey(generation)))
            {
                throw new ArgumentException(
                    "Every placement cleanup tombstone requires retained cross-placement physical-cleanup state.",
                    nameof(physicalCleanup));
            }
        }

        foreach (var cleanup in physicalCleanup)
        {
            var reservation = cleanup.Reservation;
            foreach (var claim in reservation.Retirements)
            {
                if (!scopesByFingerprint.TryGetValue(claim.PlacementSlice.Fingerprint, out var scope)
                    || scope.Snapshot.PlacementSlice != claim.PlacementSlice
                    || !scope.Snapshot.Retired.Any(retirement =>
                            retirement.Generation == reservation.Generation
                            && retirement.RetiredAtRevision == claim.RetiredAtRevision)
                        && !scope.Snapshot.Cleaned.Contains(reservation.Generation))
                {
                    throw new ArgumentException(
                        "Every cleanup reservation claim requires its exact retained retirement or cleanup tombstone.",
                        nameof(physicalCleanup));
                }
            }

            if (!scopesByFingerprint.TryGetValue(reservation.Receipt.PlacementSlice.Fingerprint, out var owner)
                || !owner.Commands.Any(command =>
                    command.Receipt == reservation.Receipt
                    && command.Command is MaterializationReserveBackendCleanupRequest reserve
                    && reserve.Generation == reservation.Generation))
            {
                throw new ArgumentException(
                    "A physical-cleanup reservation requires its exact retained authority command and receipt.",
                    nameof(physicalCleanup));
            }

            if (cleanup.Completion is { } completion
                && (completion.ObservedAtUtc < reservation.Receipt.CommittedAtUtc
                    || !scopes.Any(scope => scope.Commands.Any(command =>
                        command.Receipt is not null
                        && command.Command is MaterializationCleanupBackendGenerationRequest acknowledged
                        && acknowledged.Proof.Generation == reservation.Generation
                        && string.Equals(
                            acknowledged.Proof.ReservationToken,
                            reservation.Token,
                            StringComparison.Ordinal)
                        && string.Equals(
                            acknowledged.Proof.CleanupFingerprint,
                            completion.CleanupFingerprint,
                            StringComparison.Ordinal)
                        && acknowledged.Proof.ObservedAtUtc == completion.ObservedAtUtc))))
            {
                throw new ArgumentException(
                    "Physical-cleanup completion requires an exact retained placement acknowledgement.",
                    nameof(physicalCleanup));
            }
        }
    }
}

/// <summary>Portable state of one placement-scoped routing authority.</summary>
public sealed record MaterializationBackendRoutingScopeDocument
{
    /// <summary>Creates one canonical placement-scoped authority state.</summary>
    /// <param name="snapshot">Complete observable routing snapshot.</param>
    /// <param name="commands">Accepted command intents and replay receipts in canonical command-id order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Commands are null, duplicated, out of scope, causally inconsistent, or contradict the snapshot.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendRoutingScopeDocument(
        MaterializationBackendRoutingSnapshot snapshot,
        ImmutableArray<MaterializationBackendRoutingCommandState> commands)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var normalized = commands.IsDefault ? [] : commands;
        if (normalized.Any(static command => command is null)
            || normalized.GroupBy(static command => command.Command.Header.CommandId)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Routing commands must be non-null and command-id unique.", nameof(commands));
        }

        foreach (var command in normalized)
        {
            if (command.Command.Header.PlacementSlice != snapshot.PlacementSlice)
            {
                throw new ArgumentException("Every routing command must belong to the containing placement scope.", nameof(commands));
            }
            if (command.Receipt is { } receipt
                && (receipt.PlacementSlice != snapshot.PlacementSlice
                    || receipt.CommandId != command.Command.Header.CommandId
                    || receipt.Operation != command.Command.Operation
                    || receipt.Revision.Ordinal > snapshot.Revision.Ordinal))
            {
                throw new ArgumentException("A routing command receipt contradicts its command or snapshot.", nameof(commands));
            }
        }

        var receipts = normalized
            .Where(static command => command.Receipt is not null)
            .Select(static command => command.Receipt!)
            .OrderBy(static receipt => receipt.Revision.Ordinal)
            .ToArray();
        if (receipts.LongLength != snapshot.Revision.Ordinal
            || receipts.Where((receipt, index) => receipt.Revision.Ordinal != index + 1L).Any()
            || receipts.Any(receipt => snapshot.LatestFence is not { } latest
                || receipt.Fence.Ordinal > latest.Ordinal))
        {
            throw new ArgumentException(
                "Committed routing receipts must form the snapshot's exact contiguous revision history.",
                nameof(commands));
        }

        var pendingCommands = normalized
            .Where(static command => command.IsExpectedFollowUp
                && !command.IsCancelled
                && command.Receipt is null)
            .ToArray();
        if (snapshot.PendingFollowUp is null && pendingCommands.Length != 0
            || snapshot.PendingFollowUp is { } pending
                && (pendingCommands.Length != 1
                    || pendingCommands[0].Command is not MaterializationSwapBackendRoutingRequest swap
                    || swap != pending.Request))
        {
            throw new ArgumentException(
                "A pending follow-up must retain its uncommitted exact command intent.",
                nameof(commands));
        }

        Commands =
        [
            .. normalized.OrderBy(static command => command.Command.Header.CommandId.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Complete observable routing snapshot.</summary>
    public MaterializationBackendRoutingSnapshot Snapshot { get; }

    /// <summary>Accepted command intents and exact replay receipts in canonical command-id order.</summary>
    public ImmutableArray<MaterializationBackendRoutingCommandState> Commands { get; }
}

/// <summary>Durable intent and optional committed receipt for one exact routing command.</summary>
public sealed record MaterializationBackendRoutingCommandState
{
    /// <summary>Creates one durable routing-command state.</summary>
    /// <param name="command">Closed, typed routing command.</param>
    /// <param name="isExpectedFollowUp">Whether candidate admission reserved this exact command.</param>
    /// <param name="isCancelled">Whether candidate abandonment permanently cancelled that reservation.</param>
    /// <param name="receipt">Exact commit receipt, when the command committed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Flags or receipt contradict the command state.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingCommandState(
        IMaterializationBackendRoutingCommand command,
        bool isExpectedFollowUp,
        bool isCancelled,
        MaterializationBackendRoutingReceipt? receipt = null)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        if (isExpectedFollowUp && command is not MaterializationSwapBackendRoutingRequest)
        {
            throw new ArgumentException("Only a swap command can be a reserved follow-up.", nameof(isExpectedFollowUp));
        }
        if (isCancelled && (!isExpectedFollowUp || receipt is not null))
        {
            throw new ArgumentException("Only an uncommitted expected follow-up can be cancelled.", nameof(isCancelled));
        }
        if (receipt is not null
            && (receipt.CommandId != command.Header.CommandId
                || receipt.Operation != command.Operation
                || command.Header.ExpectedRevision.Ordinal == long.MaxValue
                || receipt.Revision.Ordinal != command.Header.ExpectedRevision.Ordinal + 1
                || receipt.Fence != command.Header.Fence
                || receipt.CommittedAtUtc < command.Header.IssuedAtUtc))
        {
            throw new ArgumentException("The receipt does not commit the exact retained command.", nameof(receipt));
        }

        IsExpectedFollowUp = isExpectedFollowUp;
        IsCancelled = isCancelled;
        Receipt = receipt;
    }

    /// <summary>Closed, typed routing command.</summary>
    public IMaterializationBackendRoutingCommand Command { get; }

    /// <summary>Whether candidate admission reserved this exact command.</summary>
    public bool IsExpectedFollowUp { get; }

    /// <summary>Whether candidate abandonment permanently cancelled that reservation.</summary>
    public bool IsCancelled { get; }

    /// <summary>Exact commit receipt, when the command committed.</summary>
    public MaterializationBackendRoutingReceipt? Receipt { get; }
}

/// <summary>Portable cross-placement reservation and optional physical-cleanup completion.</summary>
public sealed record MaterializationBackendPhysicalCleanupDocument
{
    /// <summary>Creates one physical-cleanup authority entry.</summary>
    /// <param name="reservation">Exact reservation issued before physical cleanup.</param>
    /// <param name="completion">Shared physical completion evidence, when first acknowledged.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reservation"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationBackendPhysicalCleanupDocument(
        MaterializationBackendCleanupReservation reservation,
        MaterializationBackendPhysicalCleanupCompletion? completion = null)
    {
        Reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
        Completion = completion;
    }

    /// <summary>Exact reservation issued before physical cleanup.</summary>
    public MaterializationBackendCleanupReservation Reservation { get; }

    /// <summary>Shared physical completion evidence, when first acknowledged.</summary>
    public MaterializationBackendPhysicalCleanupCompletion? Completion { get; }
}

/// <summary>Portable physical-cleanup completion shared by every placement acknowledgement.</summary>
public sealed record MaterializationBackendPhysicalCleanupCompletion
{
    /// <summary>Creates one exact physical-cleanup completion.</summary>
    /// <param name="cleanupFingerprint">Opaque adapter-owned cleanup receipt fingerprint.</param>
    /// <param name="observedAtUtc">UTC physical completion boundary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cleanupFingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="cleanupFingerprint"/> is empty or ill-formed, or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendPhysicalCleanupCompletion(
        string cleanupFingerprint,
        DateTimeOffset observedAtUtc)
    {
        CleanupFingerprint = MaterializationContract.RequireUnicodeIdentity(
            cleanupFingerprint,
            nameof(cleanupFingerprint));
        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Opaque adapter-owned cleanup receipt fingerprint.</summary>
    public string CleanupFingerprint { get; }

    /// <summary>UTC physical completion boundary.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Strict canonical JSON serialization for portable backend-routing authority documents.</summary>
public static class MaterializationBackendRoutingAuthorityJsonSerializer
{
    /// <summary>Creates strict routing-authority JSON options.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        MaterializationJsonSerializer.CreateOptions(formatting);

    /// <summary>Serializes one routing-authority document.</summary>
    /// <param name="document">Authority document to serialize.</param>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Deterministic authority-document JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document cannot be represented by the strict wire contract.</exception>
    public static string Serialize(
        MaterializationBackendRoutingAuthorityDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        ArgumentNullException.ThrowIfNull(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Returns the unique canonical compact UTF-8 representation.</summary>
    /// <param name="document">Authority document to encode.</param>
    /// <returns>Canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static byte[] GetCanonicalBytes(MaterializationBackendRoutingAuthorityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and validates one canonical routing-authority document.</summary>
    /// <param name="json">Canonical document JSON.</param>
    /// <returns>The normalized current-schema authority document.</returns>
    /// <exception cref="JsonException">The wire is malformed, open, non-canonical, or semantically invalid.</exception>
    public static MaterializationBackendRoutingAuthorityDocument Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "materialization backend-routing authority document",
                out MaterializationBackendRoutingAuthorityDocument? document,
                out var error)
            || document is null)
        {
            throw new JsonException(error.Message);
        }

        return document;
    }
}
