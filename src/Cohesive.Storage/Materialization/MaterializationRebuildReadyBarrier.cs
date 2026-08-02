using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Durable all-leaf readiness barrier for one exact rebuild plan set and parent Process attempt.
/// </summary>
/// <remarks>
/// The barrier retains exact leaf readiness receipts and child continuation references. It does not copy child
/// Process checkpoints or make any generation visible. Independent, progressive, and atomic promotion
/// interpretations can therefore consume the same validated cut.
/// </remarks>
public sealed record MaterializationRebuildReadyBarrier
{
    /// <summary>Current portable ready-barrier schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-ready-barrier/v1";

    /// <summary>
    /// Creates or structurally deserializes a ready-barrier claim. Complete plan membership and exact parent-child
    /// lineage require <see cref="ValidateAgainst"/>.
    /// </summary>
    /// <param name="schemaVersion">Exact portable barrier schema.</param>
    /// <param name="planSet">Exact linked rebuild plan-set authority.</param>
    /// <param name="parentContinuation">Exact parent Process attempt that joined the leaf work.</param>
    /// <param name="readyGenerations">Distinct linked leaf readiness receipts.</param>
    /// <exception cref="ArgumentNullException">A required authority is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema, parent identity, leaf authority, child attempt, or receipt identity is absent, duplicated, or
    /// belongs to another plan set.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildReadyBarrier(
        string schemaVersion,
        MaterializationRebuildPlanSetReference planSet,
        ProcessContinuationIdentity parentContinuation,
        ImmutableArray<MaterializationReadyGenerationReference> readyGenerations)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Rebuild ready-barrier schema '{schemaVersion}' is unsupported.",
                nameof(schemaVersion));
        }

        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        ParentContinuation = parentContinuation ?? throw new ArgumentNullException(nameof(parentContinuation));
        MaterializationContract.RequireDefinedIdentity(
            parentContinuation.ProcessInstanceId.Value,
            nameof(parentContinuation));
        MaterializationContract.RequireDefinedIdentity(
            parentContinuation.ProcessAttemptId.Value,
            nameof(parentContinuation));

        var normalized = readyGenerations.IsDefault ? [] : readyGenerations;
        if (normalized.Any(static ready => ready is null)
            || normalized.Any(ready => ready.Authority.PlanSet != planSet)
            || normalized.Any(ready => ready.Attempt.Continuation == parentContinuation)
            || normalized.GroupBy(static ready => ready.PlacementSlice.Id).Any(static group => group.Count() > 1)
            || normalized.GroupBy(static ready => ready.Plan).Any(static group => group.Count() > 1)
            || normalized.GroupBy(static ready => ready.Attempt.Continuation).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Ready-barrier receipts must be non-null, uniquely attempt-bound, and linked to the exact plan set.",
                nameof(readyGenerations));
        }

        ReadyGenerations =
        [
            .. normalized.OrderBy(static ready => ready.PlacementSlice.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Exact portable barrier schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact linked rebuild plan-set authority.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Exact parent Process attempt that joined the leaf work.</summary>
    public ProcessContinuationIdentity ParentContinuation { get; }

    /// <summary>Exact leaf readiness receipts in canonical placement-slice order.</summary>
    public ImmutableArray<MaterializationReadyGenerationReference> ReadyGenerations { get; }

    /// <summary>
    /// Creates a barrier only when every and only linked leaf is exactly ready. Call <see cref="ValidateAgainst"/>
    /// when durable parent-child lineage is available.
    /// </summary>
    /// <param name="planSet">Complete constructor-verified linked plan set.</param>
    /// <param name="parentContinuation">Exact parent Process attempt performing the join.</param>
    /// <param name="readyGenerations">Candidate readiness receipts to verify.</param>
    /// <returns>A canonical exact all-leaf readiness barrier.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A receipt is missing, extra, stale, duplicated, or linked to another plan set, leaf, placement, or parent.
    /// </exception>
    public static MaterializationRebuildReadyBarrier Create(
        MaterializationRebuildPlanSet planSet,
        ProcessContinuationIdentity parentContinuation,
        ImmutableArray<MaterializationReadyGenerationReference> readyGenerations)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        var barrier = new MaterializationRebuildReadyBarrier(
            CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet),
            parentContinuation,
            readyGenerations);
        if (barrier.ReadyGenerations.Length != planSet.LeafPlans.Length)
        {
            throw new ArgumentException(
                "The ready barrier requires one exact readiness receipt for every linked leaf.",
                nameof(readyGenerations));
        }

        var expectedBySlice = planSet.LeafPlans.ToDictionary(static binding => binding.Slice.Id);
        foreach (var observed in barrier.ReadyGenerations)
        {
            if (!expectedBySlice.TryGetValue(observed.PlacementSlice.Id, out var expected)
                || observed.Authority.Binding != expected
                || observed.PlacementSlice != expected.Slice
                || observed.Plan != expected.LeafPlan.Plan)
            {
                throw new ArgumentException(
                    "The ready barrier contains missing, extra, stale, or substituted linked-leaf evidence.",
                    nameof(readyGenerations));
            }
        }

        return barrier;
    }

    /// <summary>
    /// Validates this structurally deserialized barrier against the complete plan set and exact durable build-child
    /// ledger of its parent attempt.
    /// </summary>
    /// <param name="planSet">Complete constructor-verified linked plan set.</param>
    /// <param name="parentPlan">Exact compiled parent Process specialized to <paramref name="planSet"/>.</param>
    /// <param name="checkpoint">Durable parent checkpoint retaining the resolved build-child ledger.</param>
    /// <param name="atomicRealization">Exact atomic parent specialization, when applicable.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The barrier is incomplete, belongs to another parent attempt, or differs from exact retained build-child
    /// readiness evidence.
    /// </exception>
    public void ValidateAgainst(
        MaterializationRebuildPlanSet planSet,
        CompiledProcessPlan parentPlan,
        ProcessDurableCheckpoint checkpoint,
        MaterializationAtomicRoutingManifestRealization? atomicRealization = null)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(parentPlan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        PlanSetProjection.ValidateParentContext(planSet, parentPlan, checkpoint, atomicRealization);
        if (ParentContinuation != checkpoint.ContinuationIdentity)
        {
            throw new ArgumentException(
                "The readiness barrier belongs to another parent Process attempt.",
                nameof(checkpoint));
        }

        var validated = Create(planSet, ParentContinuation, ReadyGenerations);
        if (validated != this)
        {
            throw new ArgumentException("The readiness barrier is not in canonical contextual form.", nameof(planSet));
        }

        var leaves = PlanSetProjection.ProjectBuildLeaves(planSet, checkpoint, out var allReady);
        if (!allReady
            || !ReadyGenerations.SequenceEqual(leaves.Select(static leaf => leaf.Ready!)))
        {
            throw new ArgumentException(
                "The readiness barrier differs from the exact resolved build-child ledger.",
                nameof(checkpoint));
        }
    }

    /// <summary>Compares barriers by their complete canonical authority and ordered readiness evidence.</summary>
    /// <param name="other">Barrier to compare.</param>
    /// <returns><see langword="true"/> when every durable field is structurally equal.</returns>
    public bool Equals(MaterializationRebuildReadyBarrier? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && PlanSet == other.PlanSet
        && ParentContinuation == other.ParentContinuation
        && ReadyGenerations.SequenceEqual(other.ReadyGenerations);

    /// <summary>Returns a structural hash code for the exact barrier.</summary>
    /// <returns>A hash code consistent with structural equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(PlanSet);
        hash.Add(ParentContinuation);
        foreach (var ready in ReadyGenerations)
        {
            hash.Add(ready);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Strict canonical JSON persistence for <see cref="MaterializationRebuildReadyBarrier"/>.</summary>
public static class MaterializationRebuildReadyBarrierJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact ready barrier to canonical compact JSON.</summary>
    /// <param name="barrier">Exact durable ready barrier.</param>
    /// <returns>Canonical JSON retaining parent and leaf attempt authority.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="barrier"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The barrier cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The barrier has no canonical JSON representation.</exception>
    public static string Serialize(MaterializationRebuildReadyBarrier barrier)
    {
        ArgumentNullException.ThrowIfNull(barrier);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(barrier, Options));
    }

    /// <summary>
    /// Deserializes and structurally validates a ready-barrier claim without resolving complete plan membership or
    /// durable parent-child lineage.
    /// </summary>
    /// <param name="json">Strict canonical barrier JSON.</param>
    /// <returns>
    /// The structurally validated barrier. Call <see cref="MaterializationRebuildReadyBarrier.ValidateAgainst"/>
    /// before consuming it as an all-leaf parent-attempt barrier.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical, open, or structurally invalid.</exception>
    public static MaterializationRebuildReadyBarrier DeserializeStructural(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                "materialization rebuild ready barrier",
                out MaterializationRebuildReadyBarrier? barrier,
                out var error)
            && barrier is not null)
        {
            return barrier;
        }

        throw new JsonException(error.Message);
    }
}
