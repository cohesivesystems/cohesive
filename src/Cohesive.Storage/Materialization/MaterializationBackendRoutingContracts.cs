using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Monotonic compare-and-swap revision of one exact placement slice's routing state.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationBackendRoutingRevision
{
    /// <summary>Gets the revision before any routing transition has committed.</summary>
    public static MaterializationBackendRoutingRevision Initial { get; } = new("0");

    /// <summary>Creates a nonnegative canonical routing revision.</summary>
    /// <param name="value">Canonical invariant-culture nonnegative 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical nonnegative integer.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingRevision(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: true, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical routing revision.</summary>
    public string Value { get; }

    /// <summary>Gets the numeric routing revision.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical routing revision.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal MaterializationBackendRoutingRevision Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
}

/// <summary>Monotonic ownership fence for one exact placement slice's routing authority.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationBackendRoutingFence
{
    /// <summary>Gets the first valid routing authority fence.</summary>
    public static MaterializationBackendRoutingFence Initial { get; } = new("1");

    /// <summary>Creates a positive canonical routing fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingFence(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical fence value.</summary>
    public string Value { get; }

    /// <summary>Gets the positive numeric fence.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical routing fence.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable idempotency identity of one placement-scoped backend routing command.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationBackendRoutingCommandId
{
    /// <summary>Creates a routing-command identity.</summary>
    /// <param name="value">Identity reused only for an exact replay of one canonical command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingCommandId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable command identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable command identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Exact physical backend and generation coordinate in a materialization pool.</summary>
public sealed record MaterializationBackendGenerationReference
{
    /// <summary>Creates one exact backend-generation coordinate.</summary>
    /// <param name="targetId">Stable physical backend identity.</param>
    /// <param name="generationId">Exact generation identity on the backend.</param>
    /// <param name="definitionFingerprint">Materialization definition implemented by the generation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definitionFingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetId"/> or <paramref name="generationId"/> is default.</exception>
    [JsonConstructor]
    public MaterializationBackendGenerationReference(
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        ExecutionDefinitionFingerprint definitionFingerprint)
    {
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        TargetId = targetId;
        GenerationId = generationId;
    }

    /// <summary>Stable physical backend identity.</summary>
    public MaterializationTargetId TargetId { get; }

    /// <summary>Exact generation identity on the backend.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Materialization definition implemented by the generation.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Returns a stable diagnostic coordinate.</summary>
    /// <returns>The target and generation identities separated by a slash.</returns>
    public override string ToString() => $"{TargetId.Value}/{GenerationId.Value}";
}

/// <summary>Readable route backed by exact successful activation evidence.</summary>
public sealed record MaterializationReadableBackendReference
{
    /// <summary>Creates one exact readable route.</summary>
    /// <param name="placementSlice">Exact placement authority under which the route is readable.</param>
    /// <param name="generation">Backend generation implementing the route.</param>
    /// <param name="activation">Exact target promotion and validation evidence.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Activation evidence addresses another target or generation, belongs to another materialization or pool, or
    /// covers another canonical placement-subject set.
    /// </exception>
    [JsonConstructor]
    public MaterializationReadableBackendReference(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendGenerationReference generation,
        MaterializationActiveGenerationReference activation)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Activation = activation ?? throw new ArgumentNullException(nameof(activation));
        if (generation.TargetId != activation.Target || generation.GenerationId != activation.Generation)
        {
            throw new ArgumentException(
                "Readable routing evidence must address the exact backend generation.",
                nameof(activation));
        }
        if (activation.PlacementSlice.Materialization != placementSlice.Materialization
            || activation.PlacementSlice.Pool != placementSlice.Pool
            || !activation.PlacementSlice.Subjects.SequenceEqual(placementSlice.Subjects))
        {
            throw new ArgumentException(
                "Readable routing evidence must retain the route's exact materialization, backend pool, and canonical placement subjects.",
                nameof(activation));
        }
        if (generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "A readable generation must implement the placement slice's exact materialization definition.",
                nameof(generation));
        }
    }

    /// <summary>Exact placement authority under which the route is readable.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Exact backend-generation coordinate.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Successful target activation evidence authorizing reads.</summary>
    public MaterializationActiveGenerationReference Activation { get; }
}

/// <summary>Placement-scoped role derived from independent routing slots and lifecycle membership.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationBackendRole
{
    /// <summary>The backend generation serves newly admitted reads.</summary>
    ActiveRead = 0,

    /// <summary>The backend generation accepts newly admitted incremental writes.</summary>
    ActiveWrite = 1,

    /// <summary>
    /// The designated rebuild generation, retained through population, validation, activation, or failed-work drain
    /// until exact success or abandonment evidence clears the designation.
    /// </summary>
    Candidate = 2,

    /// <summary>New admissions stopped and retained operations or rollback eligibility are draining.</summary>
    Draining = 3,

    /// <summary>The backend generation is logically retired and cannot be routed.</summary>
    Retired = 4
}

/// <summary>Exact quiescence evidence for one backend generation removed from routing.</summary>
public sealed record MaterializationBackendDrainProof
{
    /// <summary>Creates routing-revision-bound quiescence evidence.</summary>
    /// <param name="placementSlice">Exact placement authority whose admissions became quiescent.</param>
    /// <param name="generation">Backend generation proven quiescent.</param>
    /// <param name="admissionsClosedAtRevision">Revision at which the generation stopped receiving new admissions.</param>
    /// <param name="inFlightOperationCount">Authoritative in-flight count; must be zero.</param>
    /// <param name="quiescenceToken">Stable evidence token from the lease/admission authority.</param>
    /// <param name="observedAtUtc">UTC observation boundary.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placementSlice"/>, <paramref name="generation"/>, or <paramref name="quiescenceToken"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="admissionsClosedAtRevision"/> is not committed, <paramref name="quiescenceToken"/> is empty
    /// or ill-formed Unicode, or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlightOperationCount"/> is not zero.</exception>
    [JsonConstructor]
    public MaterializationBackendDrainProof(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendGenerationReference generation,
        MaterializationBackendRoutingRevision admissionsClosedAtRevision,
        long inFlightOperationCount,
        string quiescenceToken,
        DateTimeOffset observedAtUtc)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        if (generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint)
            throw new ArgumentException("Drain evidence must implement the placement slice's exact definition.", nameof(generation));
        MaterializationContract.RequireDefinedIdentity(admissionsClosedAtRevision.Value, nameof(admissionsClosedAtRevision));
        if (admissionsClosedAtRevision.Ordinal == 0)
            throw new ArgumentException("Drain evidence must follow a committed routing transition.", nameof(admissionsClosedAtRevision));
        if (inFlightOperationCount != 0)
            throw new ArgumentOutOfRangeException(nameof(inFlightOperationCount), inFlightOperationCount, "Quiescence requires an exact zero in-flight count.");
        QuiescenceToken = MaterializationContract.RequireUnicodeIdentity(quiescenceToken, nameof(quiescenceToken));
        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        AdmissionsClosedAtRevision = admissionsClosedAtRevision;
        InFlightOperationCount = inFlightOperationCount;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Exact placement authority whose admissions became quiescent.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Backend generation proven quiescent.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Revision at which new admissions stopped.</summary>
    public MaterializationBackendRoutingRevision AdmissionsClosedAtRevision { get; }

    /// <summary>Authoritative zero in-flight operation count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long InFlightOperationCount { get; }

    /// <summary>Stable evidence token from the lease/admission authority.</summary>
    public string QuiescenceToken { get; }

    /// <summary>UTC quiescence observation boundary.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Exact evidence authorizing a route to return to a draining generation.</summary>
public sealed record MaterializationBackendRollbackProof
{
    /// <summary>Creates current-revision-bound rollback equivalence evidence.</summary>
    /// <param name="placementSlice">Exact placement authority for which equivalence was observed.</param>
    /// <param name="generation">Draining generation to restore.</param>
    /// <param name="currentRead">Current exact read route against which equivalence was established.</param>
    /// <param name="currentWrite">Current exact write route against which equivalence was established.</param>
    /// <param name="expectedRoutingRevision">Current placement-scoped revision fenced by the proof.</param>
    /// <param name="equivalenceFingerprint">Opaque durable fingerprint of the exact source cut and synchronization evidence.</param>
    /// <param name="observedAtUtc">UTC equivalence observation boundary.</param>
    /// <exception cref="ArgumentNullException">
    /// A required reference or <paramref name="equivalenceFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="expectedRoutingRevision"/> is default, <paramref name="equivalenceFingerprint"/> is empty
    /// or ill-formed Unicode, or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendRollbackProof(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendGenerationReference generation,
        MaterializationReadableBackendReference currentRead,
        MaterializationBackendGenerationReference currentWrite,
        MaterializationBackendRoutingRevision expectedRoutingRevision,
        string equivalenceFingerprint,
        DateTimeOffset observedAtUtc)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        CurrentRead = currentRead ?? throw new ArgumentNullException(nameof(currentRead));
        CurrentWrite = currentWrite ?? throw new ArgumentNullException(nameof(currentWrite));
        if (generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint
            || currentRead.Generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint
            || currentWrite.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "Rollback evidence must implement one exact placement-slice definition.",
                nameof(generation));
        }
        MaterializationContract.RequireDefinedIdentity(expectedRoutingRevision.Value, nameof(expectedRoutingRevision));
        EquivalenceFingerprint = MaterializationContract.RequireUnicodeIdentity(equivalenceFingerprint, nameof(equivalenceFingerprint));
        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        ExpectedRoutingRevision = expectedRoutingRevision;
        ObservedAtUtc = observedAtUtc;
        if (currentRead.PlacementSlice != placementSlice)
        {
            throw new ArgumentException(
                "Rollback read evidence must belong to the exact placement authority.",
                nameof(currentRead));
        }
    }

    /// <summary>Exact placement authority for which equivalence was observed.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Draining generation to restore.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Current read route used by the equivalence proof.</summary>
    public MaterializationReadableBackendReference CurrentRead { get; }

    /// <summary>Current write route used by the equivalence proof.</summary>
    public MaterializationBackendGenerationReference CurrentWrite { get; }

    /// <summary>Placement-scoped revision at which equivalence was observed.</summary>
    public MaterializationBackendRoutingRevision ExpectedRoutingRevision { get; }

    /// <summary>Opaque fingerprint covering source cut, synchronization, and validation evidence.</summary>
    public string EquivalenceFingerprint { get; }

    /// <summary>UTC equivalence observation boundary.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Retained drain lifecycle for one backend generation.</summary>
public sealed record MaterializationBackendDrainState
{
    /// <summary>Creates one drain state.</summary>
    /// <param name="generation">Backend generation being drained.</param>
    /// <param name="admissionsClosedAtRevision">Placement-scoped revision that stopped new admissions.</param>
    /// <param name="proof">Exact quiescence evidence when drain completed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="admissionsClosedAtRevision"/> is not committed or <paramref name="proof"/> addresses another
    /// generation or revision.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendDrainState(
        MaterializationBackendGenerationReference generation,
        MaterializationBackendRoutingRevision admissionsClosedAtRevision,
        MaterializationBackendDrainProof? proof = null)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        if (admissionsClosedAtRevision.Ordinal == 0)
            throw new ArgumentException("A drain begins only after a committed routing transition.", nameof(admissionsClosedAtRevision));
        if (proof is not null
            && (proof.Generation != generation || proof.AdmissionsClosedAtRevision != admissionsClosedAtRevision))
        {
            throw new ArgumentException("Drain proof must address the exact generation and admission boundary.", nameof(proof));
        }

        AdmissionsClosedAtRevision = admissionsClosedAtRevision;
        Proof = proof;
    }

    /// <summary>Backend generation being drained.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Placement-scoped revision at which new admissions stopped.</summary>
    public MaterializationBackendRoutingRevision AdmissionsClosedAtRevision { get; }

    /// <summary>Exact quiescence evidence, or <see langword="null"/> while draining remains incomplete.</summary>
    public MaterializationBackendDrainProof? Proof { get; }
}

/// <summary>Placement-scoped retirement evidence retained until exact physical cleanup is acknowledged.</summary>
public sealed record MaterializationBackendRetirementState
{
    /// <summary>Creates one placement-scoped retirement state.</summary>
    /// <param name="generation">Quiescent backend generation removed from placement routing.</param>
    /// <param name="retiredAtRevision">Exact placement-scoped revision that committed retirement.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="retiredAtRevision"/> is not a committed revision.</exception>
    [JsonConstructor]
    public MaterializationBackendRetirementState(
        MaterializationBackendGenerationReference generation,
        MaterializationBackendRoutingRevision retiredAtRevision)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        if (retiredAtRevision.Ordinal == 0)
            throw new ArgumentException("Placement retirement requires a committed routing revision.", nameof(retiredAtRevision));
        RetiredAtRevision = retiredAtRevision;
    }

    /// <summary>Quiescent backend generation removed from placement routing.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Exact placement-scoped revision that committed retirement.</summary>
    public MaterializationBackendRoutingRevision RetiredAtRevision { get; }
}

/// <summary>One exact placement-scoped retirement captured by a physical cleanup reservation.</summary>
public sealed record MaterializationBackendCleanupRetirementClaim
{
    /// <summary>Creates one exact retirement claim.</summary>
    /// <param name="placementSlice">Placement authority retaining the retirement.</param>
    /// <param name="retiredAtRevision">Exact placement-scoped revision that committed retirement.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placementSlice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="retiredAtRevision"/> is not a committed revision.</exception>
    [JsonConstructor]
    public MaterializationBackendCleanupRetirementClaim(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendRoutingRevision retiredAtRevision)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        if (retiredAtRevision.Ordinal == 0)
            throw new ArgumentException("A cleanup retirement claim requires a committed routing revision.", nameof(retiredAtRevision));
        RetiredAtRevision = retiredAtRevision;
    }

    /// <summary>Placement authority retaining the retirement.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Exact placement-scoped revision that committed retirement.</summary>
    public MaterializationBackendRoutingRevision RetiredAtRevision { get; }
}

/// <summary>Durable exclusion claim for every placement reference owned by one routing authority.</summary>
/// <remarks>
/// This reservation is necessary before physical deletion, but it is sufficient only when its router is the exclusive
/// authority capable of routing the generation. A cleanup coordinator must collect equivalent exclusion evidence from
/// every pool or router authority that can reference shared physical storage before instructing an adapter to delete.
/// </remarks>
public sealed class MaterializationBackendCleanupReservation : IEquatable<MaterializationBackendCleanupReservation>
{
    /// <summary>Creates one exact physical cleanup reservation.</summary>
    /// <param name="generation">Physical backend generation reserved for cleanup.</param>
    /// <param name="retirements">Complete canonical set of this router's placement retirements covered by the reservation.</param>
    /// <param name="receipt">Committed routing receipt that installed the reservation.</param>
    /// <param name="token">Opaque durable token that physical cleanup evidence must cite.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The retirement set is empty, duplicated, noncanonical, belongs to another definition or pool; the receipt does
    /// not causally commit <see cref="MaterializationBackendRoutingOperation.ReserveCleanup"/> after one covered
    /// placement retirement; or <paramref name="token"/> is empty or ill-formed Unicode.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendCleanupReservation(
        MaterializationBackendGenerationReference generation,
        ImmutableArray<MaterializationBackendCleanupRetirementClaim> retirements,
        MaterializationBackendRoutingReceipt receipt,
        string token)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        Token = MaterializationContract.RequireUnicodeIdentity(token, nameof(token));
        var normalized = retirements.IsDefault ? [] : retirements;
        if (normalized.IsEmpty || normalized.Any(static claim => claim is null))
            throw new ArgumentException("A cleanup reservation requires at least one retirement claim.", nameof(retirements));
        if (normalized.Any(claim =>
                claim.PlacementSlice.Materialization.DefinitionFingerprint != generation.DefinitionFingerprint))
        {
            throw new ArgumentException(
                "Every cleanup retirement claim must address the generation's exact materialization definition.",
                nameof(retirements));
        }
        if (normalized.Any(claim => claim.PlacementSlice.Pool != receipt.PlacementSlice.Pool))
        {
            throw new ArgumentException(
                "Every cleanup retirement claim must belong to the reservation receipt's exact backend-pool definition.",
                nameof(retirements));
        }
        if (normalized.Select(static claim => claim.PlacementSlice.Fingerprint).Distinct().Count() != normalized.Length)
            throw new ArgumentException("Cleanup retirement claims must be placement-unique.", nameof(retirements));

        var canonical = normalized
            .OrderBy(static claim => claim.PlacementSlice.Fingerprint.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!normalized.SequenceEqual(canonical))
            throw new ArgumentException("Cleanup retirement claims must use canonical placement-fingerprint order.", nameof(retirements));
        var receiptClaim = canonical.FirstOrDefault(claim => claim.PlacementSlice == receipt.PlacementSlice);
        if (receipt.Operation != MaterializationBackendRoutingOperation.ReserveCleanup || receiptClaim is null)
        {
            throw new ArgumentException(
                "The cleanup reservation receipt must commit reservation under one covered placement authority.",
                nameof(receipt));
        }
        if (receiptClaim.RetiredAtRevision.Ordinal >= receipt.Revision.Ordinal)
        {
            throw new ArgumentException(
                "The cleanup reservation receipt must causally follow its placement's retained retirement revision.",
                nameof(receipt));
        }

        Retirements = canonical;
    }

    /// <summary>Physical backend generation reserved for cleanup.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Complete canonical set of this router's placement retirements covered by the reservation.</summary>
    public ImmutableArray<MaterializationBackendCleanupRetirementClaim> Retirements { get; }

    /// <summary>Committed routing receipt that installed the reservation.</summary>
    public MaterializationBackendRoutingReceipt Receipt { get; }

    /// <summary>Opaque durable token that physical cleanup evidence must cite.</summary>
    public string Token { get; }

    /// <summary>Compares reservations by their complete durable semantic content.</summary>
    /// <param name="other">Reservation to compare.</param>
    /// <returns><see langword="true"/> when every authority, claim, receipt, and token is equal.</returns>
    public bool Equals(MaterializationBackendCleanupReservation? other) =>
        ReferenceEquals(this, other)
        || other is not null
            && Generation == other.Generation
            && Receipt == other.Receipt
            && string.Equals(Token, other.Token, StringComparison.Ordinal)
            && Retirements.SequenceEqual(other.Retirements);

    /// <summary>Compares this reservation with an arbitrary value.</summary>
    /// <param name="obj">Value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is semantically equal.</returns>
    public override bool Equals(object? obj) => Equals(obj as MaterializationBackendCleanupReservation);

    /// <summary>Returns a hash code over the complete durable semantic content.</summary>
    /// <returns>A semantic hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Generation);
        hash.Add(Receipt);
        hash.Add(Token, StringComparer.Ordinal);
        foreach (var retirement in Retirements)
            hash.Add(retirement);
        return hash.ToHashCode();
    }
}

/// <summary>Exact external evidence that a reserved backend generation has been physically cleaned.</summary>
/// <remarks>
/// Placement retirement is deliberately orthogonal to target-local generation state. A target adapter or deployment
/// interpreter produces this evidence only after honoring an exact cleanup reservation. Placement routing consumes
/// the evidence without inventing target-specific cleanup semantics.
/// </remarks>
public sealed record MaterializationBackendCleanupProof
{
    /// <summary>Creates exact physical cleanup evidence.</summary>
    /// <param name="placementSlice">Exact placement authority that retired the generation.</param>
    /// <param name="generation">Reserved backend generation that was cleaned.</param>
    /// <param name="retiredAtRevision">Exact placement-retirement revision observed by the cleanup interpreter.</param>
    /// <param name="reservationToken">Exact durable reservation token honored before physical deletion.</param>
    /// <param name="cleanupFingerprint">Opaque durable fingerprint of the adapter-owned cleanup receipt.</param>
    /// <param name="observedAtUtc">
    /// UTC completion observation boundary. A routing authority rejects this proof when the boundary predates the
    /// cited reservation's commit time.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placementSlice"/>, <paramref name="generation"/>, <paramref name="reservationToken"/>, or
    /// <paramref name="cleanupFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="retiredAtRevision"/> is not committed; a string identity is empty or ill-formed Unicode; or
    /// <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendCleanupProof(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendGenerationReference generation,
        MaterializationBackendRoutingRevision retiredAtRevision,
        string reservationToken,
        string cleanupFingerprint,
        DateTimeOffset observedAtUtc)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        if (generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint)
            throw new ArgumentException("Cleanup evidence must implement the placement slice's exact definition.", nameof(generation));
        if (retiredAtRevision.Ordinal == 0)
            throw new ArgumentException("Physical cleanup must cite a committed placement-retirement revision.", nameof(retiredAtRevision));
        ReservationToken = MaterializationContract.RequireUnicodeIdentity(reservationToken, nameof(reservationToken));
        CleanupFingerprint = MaterializationContract.RequireUnicodeIdentity(cleanupFingerprint, nameof(cleanupFingerprint));
        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RetiredAtRevision = retiredAtRevision;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Exact placement authority that retired the generation.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Reserved backend generation that was cleaned.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Exact placement-retirement revision observed before physical cleanup.</summary>
    public MaterializationBackendRoutingRevision RetiredAtRevision { get; }

    /// <summary>Exact durable reservation token honored before physical deletion.</summary>
    public string ReservationToken { get; }

    /// <summary>Opaque durable fingerprint of the adapter-owned cleanup receipt.</summary>
    public string CleanupFingerprint { get; }

    /// <summary>UTC physical cleanup observation boundary, which must not predate the cited reservation commit.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Common immutable fence carried by every backend routing command.</summary>
public sealed record MaterializationBackendRoutingCommandHeader
{
    /// <summary>Creates one exact command fence.</summary>
    /// <param name="commandId">Stable command identity.</param>
    /// <param name="placementSlice">Exact placement authority whose routing state is mutated.</param>
    /// <param name="expectedRevision">Optimistic routing revision.</param>
    /// <param name="fence">Current routing authority fence.</param>
    /// <param name="issuedAtUtc">UTC command issuance time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placementSlice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or <paramref name="issuedAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingCommandHeader(
        MaterializationBackendRoutingCommandId commandId,
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendRoutingRevision expectedRevision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(commandId.Value, nameof(commandId));
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));
        CommandId = commandId;
        ExpectedRevision = expectedRevision;
        Fence = fence;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>Stable command identity.</summary>
    public MaterializationBackendRoutingCommandId CommandId { get; }

    /// <summary>Exact placement authority whose routing state is mutated.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Pinned backend pool containing the placement's concrete targets.</summary>
    [JsonIgnore]
    public MaterializationBackendPoolId PoolId => PlacementSlice.Pool.Pool;

    /// <summary>Exact canonical pool-definition fence.</summary>
    [JsonIgnore]
    public ExecutionDefinitionFingerprint PoolDefinitionFingerprint => PlacementSlice.Pool.DefinitionFingerprint;

    /// <summary>Optimistic routing revision.</summary>
    public MaterializationBackendRoutingRevision ExpectedRevision { get; }

    /// <summary>Current routing authority fence.</summary>
    public MaterializationBackendRoutingFence Fence { get; }

    /// <summary>UTC command issuance time.</summary>
    public DateTimeOffset IssuedAtUtc { get; }
}

/// <summary>Durable placement-scoped reservation for the exact swap expected to follow candidate admission.</summary>
public sealed record MaterializationBackendFollowUpReservation
{
    /// <summary>Creates one exact follow-up swap reservation.</summary>
    /// <param name="request">Complete immutable swap intent reserved by candidate admission.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The request does not route one paired candidate under one exact placement slice, its configuration selects
    /// another target, or it is a rollback.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendFollowUpReservation(MaterializationSwapBackendRoutingRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (request.Read.PlacementSlice != request.Header.PlacementSlice
            || request.Read.Generation != request.Write
            || request.Configuration.ReadTarget != request.Write.TargetId
            || request.Configuration.WriteTarget != request.Write.TargetId
            || request.Rollback is not null)
        {
            throw new ArgumentException(
                "A reserved follow-up must be a forward paired read/write swap to one exact candidate.",
                nameof(request));
        }
    }

    /// <summary>Complete immutable swap intent reserved by candidate admission.</summary>
    public MaterializationSwapBackendRoutingRequest Request { get; }

    /// <summary>Stable identity reserved for the follow-up command.</summary>
    [JsonIgnore]
    public MaterializationBackendRoutingCommandId CommandId => Request.Header.CommandId;

    /// <summary>Candidate whose admission established the reservation.</summary>
    [JsonIgnore]
    public MaterializationBackendGenerationReference Candidate => Request.Write;
}

/// <summary>Admits one physical generation as the placement's sole rebuild candidate.</summary>
public sealed record MaterializationAdmitBackendCandidateRequest
{
    /// <summary>Creates one candidate-admission command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="candidate">Generation to admit as the sole candidate.</param>
    /// <param name="expectedFollowUp">
    /// Optional complete durable swap intent reserved to consume this admission.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The follow-up reuses the admission identity, does not bind the exact next revision, slice, fence, candidate,
    /// paired configuration, or causal timestamp, or attempts a rollback.
    /// </exception>
    [JsonConstructor]
    public MaterializationAdmitBackendCandidateRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationBackendGenerationReference candidate,
        MaterializationSwapBackendRoutingRequest? expectedFollowUp = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        if (expectedFollowUp is not null)
        {
            var reservation = new MaterializationBackendFollowUpReservation(expectedFollowUp);
            if (reservation.CommandId == header.CommandId
                || header.ExpectedRevision.Ordinal == long.MaxValue
                || expectedFollowUp.Header.ExpectedRevision.Ordinal != header.ExpectedRevision.Ordinal + 1
                || expectedFollowUp.Header.PlacementSlice != header.PlacementSlice
                || expectedFollowUp.Header.Fence != header.Fence
                || expectedFollowUp.Header.IssuedAtUtc < header.IssuedAtUtc
                || expectedFollowUp.Read.PlacementSlice != header.PlacementSlice
                || reservation.Candidate != candidate)
            {
                throw new ArgumentException(
                    "Candidate admission must reserve its exact causal next-revision paired swap intent.",
                    nameof(expectedFollowUp));
            }
        }
        ExpectedFollowUp = expectedFollowUp;
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Generation to admit as the sole candidate.</summary>
    public MaterializationBackendGenerationReference Candidate { get; }

    /// <summary>Optional complete durable swap intent reserved to consume this admission.</summary>
    public MaterializationSwapBackendRoutingRequest? ExpectedFollowUp { get; }
}

/// <summary>Clears a failed pool candidate only after target-owned permanent-abandonment evidence exists.</summary>
public sealed record MaterializationAbandonBackendCandidateRequest
{
    /// <summary>Creates one candidate-abandonment command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="candidate">Exact candidate generation whose placement-scoped role should be cleared.</param>
    /// <param name="abandonment">Target-owned permanent-abandonment receipt.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="abandonment"/> addresses another generation.</exception>
    [JsonConstructor]
    public MaterializationAbandonBackendCandidateRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationBackendGenerationReference candidate,
        MaterializationAbandonmentReceipt abandonment)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Abandonment = abandonment ?? throw new ArgumentNullException(nameof(abandonment));
        if (candidate.GenerationId != abandonment.GenerationId)
        {
            throw new ArgumentException(
                "Candidate-abandonment evidence must address the exact backend generation.",
                nameof(abandonment));
        }
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Exact candidate generation whose placement-scoped role should be cleared.</summary>
    public MaterializationBackendGenerationReference Candidate { get; }

    /// <summary>Target-owned permanent-abandonment receipt.</summary>
    public MaterializationAbandonmentReceipt Abandonment { get; }
}

/// <summary>Atomically replaces the independently addressable read and write routes.</summary>
public sealed record MaterializationSwapBackendRoutingRequest
{
    /// <summary>Creates one atomic route swap or rollback command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="read">Exact successfully activated read route.</param>
    /// <param name="write">Exact writable generation route.</param>
    /// <param name="configuration">Resolved target selection and complete precedence explanation.</param>
    /// <param name="rollback">Required current-revision equivalence proof when returning to a draining route.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationSwapBackendRoutingRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationReadableBackendReference read,
        MaterializationBackendGenerationReference write,
        MaterializationBackendRoutingConfiguration configuration,
        MaterializationBackendRollbackProof? rollback = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Read = read ?? throw new ArgumentNullException(nameof(read));
        Write = write ?? throw new ArgumentNullException(nameof(write));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Rollback = rollback;
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Exact successfully activated read route.</summary>
    public MaterializationReadableBackendReference Read { get; }

    /// <summary>Exact writable generation route.</summary>
    public MaterializationBackendGenerationReference Write { get; }

    /// <summary>Resolved target selection and complete precedence explanation.</summary>
    public MaterializationBackendRoutingConfiguration Configuration { get; }

    /// <summary>Current-revision equivalence proof for a rollback.</summary>
    public MaterializationBackendRollbackProof? Rollback { get; }
}

/// <summary>Completes drain only with exact revision-bound quiescence evidence.</summary>
public sealed record MaterializationCompleteBackendDrainRequest
{
    /// <summary>Creates one drain-completion command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="proof">Exact quiescence proof.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationCompleteBackendDrainRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationBackendDrainProof proof)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Proof = proof ?? throw new ArgumentNullException(nameof(proof));
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Exact quiescence proof.</summary>
    public MaterializationBackendDrainProof Proof { get; }
}

/// <summary>Retires one quiescent generation from placement routing while preserving target-local lifecycle state.</summary>
public sealed record MaterializationRetireBackendGenerationRequest
{
    /// <summary>Creates one placement-retirement command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="generation">Quiescent generation to retire from placement routing.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationRetireBackendGenerationRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationBackendGenerationReference generation)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Quiescent generation to retire from placement routing.</summary>
    public MaterializationBackendGenerationReference Generation { get; }
}

/// <summary>Reserves one generation after excluding every reference owned by one routing authority.</summary>
public sealed record MaterializationReserveBackendCleanupRequest
{
    /// <summary>Creates one physical cleanup reservation command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="generation">Retired physical generation to reserve for cleanup.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationReserveBackendCleanupRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationBackendGenerationReference generation)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Retired physical generation to reserve for cleanup.</summary>
    public MaterializationBackendGenerationReference Generation { get; }
}

/// <summary>Consumes reservation-bound cleanup evidence while retaining a placement routing tombstone.</summary>
public sealed record MaterializationCleanupBackendGenerationRequest
{
    /// <summary>Creates one placement-scoped cleanup acknowledgement command.</summary>
    /// <param name="header">Common exact command fence.</param>
    /// <param name="proof">Exact adapter-owned physical cleanup evidence.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationCleanupBackendGenerationRequest(
        MaterializationBackendRoutingCommandHeader header,
        MaterializationBackendCleanupProof proof)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Proof = proof ?? throw new ArgumentNullException(nameof(proof));
    }

    /// <summary>Common exact command fence.</summary>
    public MaterializationBackendRoutingCommandHeader Header { get; }

    /// <summary>Exact adapter-owned physical cleanup evidence.</summary>
    public MaterializationBackendCleanupProof Proof { get; }
}

/// <summary>Observable outcome of one placement-scoped backend routing command.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationBackendRoutingDisposition
{
    /// <summary>The command committed exactly once.</summary>
    Applied = 0,

    /// <summary>The exact prior command receipt was returned.</summary>
    Replayed = 1,

    /// <summary>The stable command identity was reused for different canonical content.</summary>
    IdentityConflict = 2,

    /// <summary>The command addresses another pool, definition, or routing revision.</summary>
    RevisionConflict = 3,

    /// <summary>A newer routing authority fence superseded the command.</summary>
    StaleFence = 4,

    /// <summary>The requested role transition is illegal from current state.</summary>
    StateConflict = 5,

    /// <summary>Activation, rollback, drain, or physical lifecycle evidence is absent or inexact.</summary>
    EvidenceConflict = 6,

    /// <summary>The addressed backend or generation is not present.</summary>
    NotFound = 7
}

/// <summary>Closed semantic discriminator for a committed placement-scoped backend routing transition.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationBackendRoutingOperation
{
    /// <summary>One generation was admitted as the placement's rebuild candidate.</summary>
    AdmitCandidate = 0,

    /// <summary>A permanently abandoned generation's candidate role was cleared.</summary>
    AbandonCandidate = 1,

    /// <summary>The independently addressable read and write routes were changed atomically.</summary>
    Swap = 2,

    /// <summary>Exact quiescence evidence completed one generation's drain.</summary>
    CompleteDrain = 3,

    /// <summary>One quiescent generation was retired from placement routing.</summary>
    Retire = 4,

    /// <summary>External physical-cleanup evidence was consumed and replaced by a placement tombstone.</summary>
    Cleanup = 5,

    /// <summary>One router authority's generation references were frozen before external physical cleanup.</summary>
    ReserveCleanup = 6
}

/// <summary>Immutable receipt for one committed placement-scoped backend routing transition.</summary>
public sealed record MaterializationBackendRoutingReceipt
{
    /// <summary>Creates one committed routing receipt.</summary>
    /// <param name="commandId">Stable command identity.</param>
    /// <param name="placementSlice">Exact placement authority under which the command committed.</param>
    /// <param name="operation">Closed semantic operation that committed.</param>
    /// <param name="revision">Committed placement-scoped revision.</param>
    /// <param name="fence">Accepted routing authority fence.</param>
    /// <param name="committedAtUtc">UTC linearization boundary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placementSlice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default, revision is zero, or time is not UTC.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingReceipt(
        MaterializationBackendRoutingCommandId commandId,
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendRoutingOperation operation,
        MaterializationBackendRoutingRevision revision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset committedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(commandId.Value, nameof(commandId));
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported backend-routing operation.");
        }
        if (revision.Ordinal == 0)
            throw new ArgumentException("A routing receipt requires a committed revision.", nameof(revision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireUtc(committedAtUtc, nameof(committedAtUtc));
        CommandId = commandId;
        Operation = operation;
        Revision = revision;
        Fence = fence;
        CommittedAtUtc = committedAtUtc;
    }

    /// <summary>Stable command identity.</summary>
    public MaterializationBackendRoutingCommandId CommandId { get; }

    /// <summary>Exact placement authority under which the command committed.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Closed semantic operation that committed.</summary>
    public MaterializationBackendRoutingOperation Operation { get; }

    /// <summary>Committed placement-scoped revision.</summary>
    public MaterializationBackendRoutingRevision Revision { get; }

    /// <summary>Accepted routing authority fence.</summary>
    public MaterializationBackendRoutingFence Fence { get; }

    /// <summary>UTC linearization boundary.</summary>
    public DateTimeOffset CommittedAtUtc { get; }
}

/// <summary>Immutable view of one placement slice's backend routes and retained lifecycle history.</summary>
public sealed record MaterializationBackendRoutingSnapshot
{
    /// <summary>Creates one canonical routing snapshot.</summary>
    /// <param name="placementSlice">Exact placement authority owning every route and lifecycle observation.</param>
    /// <param name="revision">Current routing revision.</param>
    /// <param name="latestFence">Greatest accepted routing authority fence.</param>
    /// <param name="activeRead">Exact readable route, when initialized.</param>
    /// <param name="activeWrite">Exact write route, when initialized.</param>
    /// <param name="candidate">Current rebuild candidate.</param>
    /// <param name="draining">Canonical draining generations.</param>
    /// <param name="retired">Canonical retained placement-retirement states.</param>
    /// <param name="cleaned">Canonical cleanup tombstones.</param>
    /// <param name="configuration">Effective target selection and precedence explanation for initialized routing.</param>
    /// <param name="pendingFollowUp">Durable exact follow-up reservation established by candidate admission.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placementSlice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default; a route, configuration, or lifecycle topology is contradictory; or retained lifecycle
    /// evidence follows <paramref name="revision"/>.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendRoutingSnapshot(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendRoutingRevision revision,
        MaterializationBackendRoutingFence? latestFence,
        MaterializationReadableBackendReference? activeRead,
        MaterializationBackendGenerationReference? activeWrite,
        MaterializationBackendGenerationReference? candidate,
        ImmutableArray<MaterializationBackendDrainState> draining,
        ImmutableArray<MaterializationBackendRetirementState> retired,
        ImmutableArray<MaterializationBackendGenerationReference> cleaned,
        MaterializationBackendRoutingConfiguration? configuration = null,
        MaterializationBackendFollowUpReservation? pendingFollowUp = null)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        if (latestFence is { } acceptedFence)
            MaterializationContract.RequireDefinedIdentity(acceptedFence.Value, nameof(latestFence));
        if (revision.Ordinal > 0 && latestFence is null)
            throw new ArgumentException("Committed routing state requires an accepted authority fence.", nameof(latestFence));
        if ((activeRead is null) != (activeWrite is null))
            throw new ArgumentException("Initialized routing requires both an active read and active write slot.", nameof(activeWrite));
        if ((activeRead is null) != (configuration is null))
            throw new ArgumentException("Only initialized routing carries effective target configuration.", nameof(configuration));
        if (configuration is not null
            && (configuration.ReadTarget != activeRead!.Generation.TargetId
                || configuration.WriteTarget != activeWrite!.TargetId))
        {
            throw new ArgumentException("Effective configuration must select the exact active read and write targets.", nameof(configuration));
        }
        if (activeRead is not null && activeRead.PlacementSlice != placementSlice)
            throw new ArgumentException("The readable route belongs to another placement authority.", nameof(activeRead));
        if (candidate is not null && candidate.TargetId != placementSlice.Target)
            throw new ArgumentException("The candidate belongs to another placement target.", nameof(candidate));
        if (pendingFollowUp is not null && pendingFollowUp.Candidate != candidate)
        {
            throw new ArgumentException(
                "A follow-up reservation must bind the exact currently admitted candidate.",
                nameof(pendingFollowUp));
        }
        if (pendingFollowUp is not null
            && (pendingFollowUp.Request.Header.PlacementSlice != placementSlice
                || pendingFollowUp.Request.Read.PlacementSlice != placementSlice
                || pendingFollowUp.Request.Header.ExpectedRevision != revision
                || latestFence is null
                || pendingFollowUp.Request.Header.Fence != latestFence.Value))
        {
            throw new ArgumentException(
                "A follow-up reservation must retain this snapshot's exact placement, revision, and accepted fence.",
                nameof(pendingFollowUp));
        }

        var normalizedDraining = NormalizeDraining(draining);
        var normalizedRetired = NormalizeRetired(retired);
        var normalizedCleaned = NormalizeReferences(cleaned, nameof(cleaned));
        if (revision.Ordinal == 0
            && (activeRead is not null
                || candidate is not null
                || !normalizedDraining.IsEmpty
                || !normalizedRetired.IsEmpty
                || !normalizedCleaned.IsEmpty))
        {
            throw new ArgumentException(
                "Pre-commit routing state cannot contain routes or generation lifecycle roles.",
                nameof(revision));
        }
        if (normalizedDraining.Any(drain => drain.AdmissionsClosedAtRevision.Ordinal > revision.Ordinal))
            throw new ArgumentException("A drain boundary cannot follow the containing routing revision.", nameof(draining));
        if (normalizedRetired.Any(retirement => retirement.RetiredAtRevision.Ordinal > revision.Ordinal))
            throw new ArgumentException("A retirement boundary cannot follow the containing routing revision.", nameof(retired));
        RequirePlacementDefinition(activeRead?.Generation);
        RequirePlacementDefinition(activeWrite);
        RequirePlacementDefinition(candidate);
        foreach (var drain in normalizedDraining)
            RequirePlacementDefinition(drain.Generation);
        foreach (var retirement in normalizedRetired)
            RequirePlacementDefinition(retirement.Generation);
        foreach (var generation in normalizedCleaned)
            RequirePlacementDefinition(generation);

        var terminal = new HashSet<MaterializationBackendGenerationReference>(
            normalizedRetired.Select(static retirement => retirement.Generation));
        terminal.UnionWith(normalizedCleaned);
        if (terminal.Count != normalizedRetired.Length + normalizedCleaned.Length)
            throw new ArgumentException("A generation cannot be both retired and cleaned.", nameof(cleaned));
        if (normalizedDraining.Any(drain => terminal.Contains(drain.Generation)))
            throw new ArgumentException("A draining generation cannot be retired or cleaned.", nameof(draining));
        if (candidate is not null && terminal.Contains(candidate))
            throw new ArgumentException("A candidate cannot be retired or cleaned.", nameof(candidate));
        if (activeRead is { } read && terminal.Contains(read.Generation)
            || activeWrite is { } write && terminal.Contains(write))
        {
            throw new ArgumentException("A retired or cleaned generation cannot be routed.", nameof(activeRead));
        }
        if (candidate is not null && activeRead?.Generation == candidate)
            throw new ArgumentException("A candidate must be cleared before it becomes readable.", nameof(candidate));

        Revision = revision;
        LatestFence = latestFence;
        ActiveRead = activeRead;
        ActiveWrite = activeWrite;
        Candidate = candidate;
        PendingFollowUp = pendingFollowUp;
        Draining = normalizedDraining;
        Retired = normalizedRetired;
        Cleaned = normalizedCleaned;
        Configuration = configuration;

        foreach (var drain in normalizedDraining)
        {
            if (drain.Proof is { } proof && proof.PlacementSlice != placementSlice)
                throw new ArgumentException("Drain evidence belongs to another placement authority.", nameof(draining));
        }

        void RequirePlacementDefinition(MaterializationBackendGenerationReference? generation)
        {
            if (generation is null)
                return;
            if (generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint)
            {
                throw new ArgumentException(
                    "Every placement route and lifecycle reference must implement the placement slice's exact materialization definition.",
                    nameof(activeWrite));
            }
        }
    }

    /// <summary>Exact placement authority owning every route and lifecycle observation.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Stable backend-pool identity.</summary>
    [JsonIgnore]
    public MaterializationBackendPoolId PoolId => PlacementSlice.Pool.Pool;

    /// <summary>Exact canonical pool-definition fence.</summary>
    [JsonIgnore]
    public ExecutionDefinitionFingerprint PoolDefinitionFingerprint => PlacementSlice.Pool.DefinitionFingerprint;

    /// <summary>Current routing revision.</summary>
    public MaterializationBackendRoutingRevision Revision { get; }

    /// <summary>Greatest accepted routing authority fence.</summary>
    public MaterializationBackendRoutingFence? LatestFence { get; }

    /// <summary>Exact route serving newly admitted reads.</summary>
    public MaterializationReadableBackendReference? ActiveRead { get; }

    /// <summary>Exact route accepting newly admitted writes.</summary>
    public MaterializationBackendGenerationReference? ActiveWrite { get; }

    /// <summary>Current rebuild candidate.</summary>
    public MaterializationBackendGenerationReference? Candidate { get; }

    /// <summary>Durable exact follow-up command reservation established by candidate admission.</summary>
    public MaterializationBackendFollowUpReservation? PendingFollowUp { get; }

    /// <summary>Draining generations in canonical coordinate order.</summary>
    public ImmutableArray<MaterializationBackendDrainState> Draining { get; }

    /// <summary>Retained placement-retirement states in canonical coordinate order.</summary>
    public ImmutableArray<MaterializationBackendRetirementState> Retired { get; }

    /// <summary>Cleanup tombstones in canonical coordinate order.</summary>
    public ImmutableArray<MaterializationBackendGenerationReference> Cleaned { get; }

    /// <summary>Effective target selection and complete precedence explanation.</summary>
    public MaterializationBackendRoutingConfiguration? Configuration { get; }

    /// <summary>Gets the complete derived roles of one exact generation.</summary>
    /// <param name="generation">Generation whose roles should be projected.</param>
    /// <returns>Roles in stable enum order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    public ImmutableArray<MaterializationBackendRole> GetRoles(MaterializationBackendGenerationReference generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var roles = ImmutableArray.CreateBuilder<MaterializationBackendRole>(3);
        if (ActiveRead?.Generation == generation)
            roles.Add(MaterializationBackendRole.ActiveRead);
        if (ActiveWrite == generation)
            roles.Add(MaterializationBackendRole.ActiveWrite);
        if (Candidate == generation)
            roles.Add(MaterializationBackendRole.Candidate);
        if (Draining.Any(drain => drain.Generation == generation))
            roles.Add(MaterializationBackendRole.Draining);
        if (Retired.Any(retirement => retirement.Generation == generation))
            roles.Add(MaterializationBackendRole.Retired);
        return roles.ToImmutable();
    }

    static ImmutableArray<MaterializationBackendDrainState> NormalizeDraining(
        ImmutableArray<MaterializationBackendDrainState> values)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null)
            || normalized.GroupBy(static value => value.Generation).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Draining generations must be non-null and unique.", nameof(values));
        }

        return
        [
            .. normalized.OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<MaterializationBackendGenerationReference> NormalizeReferences(
        ImmutableArray<MaterializationBackendGenerationReference> values,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null)
            || normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("Backend generation references must be non-null and unique.", parameterName);
        }

        return
        [
            .. normalized.OrderBy(static value => value.TargetId.Value, StringComparer.Ordinal)
                .ThenBy(static value => value.GenerationId.Value, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<MaterializationBackendRetirementState> NormalizeRetired(
        ImmutableArray<MaterializationBackendRetirementState> values)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null)
            || normalized.GroupBy(static value => value.Generation).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Retirement states must be non-null and generation-unique.", nameof(values));
        }

        return
        [
            .. normalized.OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal)
        ];
    }
}

/// <summary>Result of one placement-scoped backend routing command.</summary>
public sealed record MaterializationBackendRoutingResult
{
    /// <summary>Creates one routing command result.</summary>
    /// <param name="disposition">Observable command disposition.</param>
    /// <param name="snapshot">Complete resulting routing snapshot.</param>
    /// <param name="receipt">Committed or replayed receipt.</param>
    /// <param name="detail">Stable safe diagnostic detail for a rejected command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Receipt presence contradicts the disposition.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingResult(
        MaterializationBackendRoutingDisposition disposition,
        MaterializationBackendRoutingSnapshot snapshot,
        MaterializationBackendRoutingReceipt? receipt = null,
        string? detail = null)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported routing disposition.");
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if ((disposition is MaterializationBackendRoutingDisposition.Applied or MaterializationBackendRoutingDisposition.Replayed)
            != (receipt is not null))
        {
            throw new ArgumentException("Only applied or replayed routing results carry a receipt.", nameof(receipt));
        }
        if (receipt is not null && receipt.PlacementSlice != snapshot.PlacementSlice)
            throw new ArgumentException("A routing receipt must belong to the resulting snapshot's placement authority.", nameof(receipt));
        if (receipt is not null
            && (receipt.Revision.Ordinal > snapshot.Revision.Ordinal
                || disposition == MaterializationBackendRoutingDisposition.Applied
                    && receipt.Revision != snapshot.Revision
                || snapshot.LatestFence is not { } latestFence
                || receipt.Fence.Ordinal > latestFence.Ordinal))
        {
            throw new ArgumentException(
                "A routing receipt must be causally retained by the resulting revision and accepted fence.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
        Detail = detail;
    }

    /// <summary>Observable command disposition.</summary>
    public MaterializationBackendRoutingDisposition Disposition { get; }

    /// <summary>Complete resulting routing snapshot.</summary>
    public MaterializationBackendRoutingSnapshot Snapshot { get; }

    /// <summary>Committed or replayed receipt.</summary>
    public MaterializationBackendRoutingReceipt? Receipt { get; }

    /// <summary>Stable safe diagnostic detail for a rejected command.</summary>
    public string? Detail { get; }
}

/// <summary>Result of atomically reserving one physical backend generation for cleanup.</summary>
public sealed record MaterializationBackendCleanupReservationResult
{
    /// <summary>Creates one cleanup reservation result.</summary>
    /// <param name="routing">Placement-scoped routing outcome.</param>
    /// <param name="reservation">Durable reservation returned only for an applied or replayed command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routing"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Reservation presence contradicts the routing disposition or its receipt is not the routing receipt.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendCleanupReservationResult(
        MaterializationBackendRoutingResult routing,
        MaterializationBackendCleanupReservation? reservation = null)
    {
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        var succeeded = routing.Disposition is MaterializationBackendRoutingDisposition.Applied
            or MaterializationBackendRoutingDisposition.Replayed;
        if (succeeded != (reservation is not null))
        {
            throw new ArgumentException(
                "Only an applied or replayed cleanup reservation command returns a reservation.",
                nameof(reservation));
        }
        if (reservation is not null && reservation.Receipt != routing.Receipt)
        {
            throw new ArgumentException(
                "The cleanup reservation must retain the exact routing receipt returned by the command.",
                nameof(reservation));
        }
        Reservation = reservation;
    }

    /// <summary>Placement-scoped routing outcome.</summary>
    public MaterializationBackendRoutingResult Routing { get; }

    /// <summary>Durable cleanup reservation for an applied or replayed command.</summary>
    public MaterializationBackendCleanupReservation? Reservation { get; }
}

/// <summary>Revision-pinned concrete target binding returned by read or write resolution.</summary>
public sealed record MaterializationBackendRouteBinding
{
    /// <summary>Creates one revision-pinned target binding.</summary>
    /// <param name="placementSlice">Exact placement authority under which the route was admitted.</param>
    /// <param name="revision">Placement-scoped revision observed at admission.</param>
    /// <param name="generation">Exact admitted backend generation.</param>
    /// <param name="target">Concrete target dependency resolved by exact ID.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="revision"/> is not committed, or the generation or concrete target belongs to another
    /// placement authority.
    /// </exception>
    public MaterializationBackendRouteBinding(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendRoutingRevision revision,
        MaterializationBackendGenerationReference generation,
        IMaterializationTarget target)
    {
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        if (revision.Ordinal == 0)
            throw new ArgumentException("An admitted route binding requires a committed routing revision.", nameof(revision));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (generation.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint
            || target.Descriptor.Id != generation.TargetId
            || target.Descriptor.MaterializationId != placementSlice.Materialization.Materialization)
        {
            throw new ArgumentException(
                "A route binding must retain the exact selected target dependency and placement definition.",
                nameof(target));
        }
        Revision = revision;
    }

    /// <summary>Exact placement authority under which the route was admitted.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Placement-scoped revision observed at admission.</summary>
    public MaterializationBackendRoutingRevision Revision { get; }

    /// <summary>Exact admitted backend generation.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Concrete target dependency pinned for the operation lifetime.</summary>
    [JsonIgnore]
    public IMaterializationTarget Target { get; }
}

/// <summary>Linearizable placement-scoped backend routing and lifecycle authority.</summary>
/// <remarks>
/// Routing revision, fence, evidence, and lifecycle conflicts are returned as
/// <see cref="MaterializationBackendRoutingDisposition"/> values. They are not exceptional control flow.
/// </remarks>
public interface IMaterializationBackendRouter
{
    /// <summary>Reads the complete routing snapshot, including retained lifecycle history.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="placementSlice">Exact placement authority to inspect.</param>
    /// <returns>The current immutable snapshot.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="placementSlice"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="placementSlice"/> belongs to another backend pool.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingSnapshot> InspectAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice);

    /// <summary>Resolves and pins the exact current readable backend generation.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="placementSlice">Exact placement authority whose read route is resolved.</param>
    /// <returns>The revision-pinned read binding.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="placementSlice"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="placementSlice"/> belongs to another backend pool.</exception>
    /// <exception cref="InvalidOperationException">Routing has not been initialized.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRouteBinding> ResolveReadAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice);

    /// <summary>Resolves and pins the exact current writable backend generation.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="placementSlice">Exact placement authority whose write route is resolved.</param>
    /// <returns>The revision-pinned write binding.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="placementSlice"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="placementSlice"/> belongs to another backend pool.</exception>
    /// <exception cref="InvalidOperationException">Routing has not been initialized.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRouteBinding> ResolveWriteAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice);

    /// <summary>Admits one candidate generation.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced candidate command.</param>
    /// <returns>The observable routing result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingResult> AdmitCandidateAsync(
        OperationContext context,
        MaterializationAdmitBackendCandidateRequest request);

    /// <summary>Clears an exact failed candidate after target-owned permanent abandonment.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced candidate-abandonment command and target receipt.</param>
    /// <returns>The observable routing result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingResult> AbandonCandidateAsync(
        OperationContext context,
        MaterializationAbandonBackendCandidateRequest request);

    /// <summary>Atomically changes read and write routes.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced swap or rollback command.</param>
    /// <returns>The observable routing result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingResult> SwapAsync(
        OperationContext context,
        MaterializationSwapBackendRoutingRequest request);

    /// <summary>Records exact quiescence evidence for one draining generation.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced drain-completion command.</param>
    /// <returns>The observable routing result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingResult> CompleteDrainAsync(
        OperationContext context,
        MaterializationCompleteBackendDrainRequest request);

    /// <summary>Retires one quiescent generation from placement routing.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced placement-retirement command; target-local lifecycle state is unchanged.</param>
    /// <returns>The observable routing result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingResult> RetireAsync(
        OperationContext context,
        MaterializationRetireBackendGenerationRequest request);

    /// <summary>Atomically reserves one generation after excluding every reference owned by this router.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced reservation command for one placement-retired generation.</param>
    /// <returns>The routing outcome and durable reservation when the command applied or replayed.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendCleanupReservationResult> ReserveCleanupAsync(
        OperationContext context,
        MaterializationReserveBackendCleanupRequest request);

    /// <summary>Consumes reservation-bound physical cleanup evidence and retains a placement tombstone.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact fenced placement command carrying reservation-bound physical cleanup evidence.</param>
    /// <returns>The observable routing result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request's placement slice belongs to another routing authority.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The router implementation has been disposed.</exception>
    ValueTask<MaterializationBackendRoutingResult> CleanupAsync(
        OperationContext context,
        MaterializationCleanupBackendGenerationRequest request);
}
