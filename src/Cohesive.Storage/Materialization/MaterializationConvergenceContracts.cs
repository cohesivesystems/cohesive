using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted while deciding whether a generation has converged.</summary>
public static class MaterializationConvergenceDiagnosticCodes
{
    /// <summary>A caught-up source read is older than the declared end-to-end lag demand.</summary>
    public const string LagExceeded = "materialization.convergence.lagExceeded";

    /// <summary>Explicit source settlement is required but no exact settlement evidence exists.</summary>
    public const string SettlementMissing = "materialization.convergence.settlementMissing";

    /// <summary>Checkpoint-to-settlement time exceeds the declared unsettled-work demand.</summary>
    public const string UnsettledAgeExceeded = "materialization.convergence.unsettledAgeExceeded";

    /// <summary>A previously valid convergence decision has aged beyond the declared proof lifetime.</summary>
    public const string ProofStale = "materialization.convergence.proofStale";

    /// <summary>The receipt synchronization fence does not identify the exact supplied rebuild plan.</summary>
    public const string PlanAffinityMismatch = "materialization.convergence.planAffinityMismatch";

    /// <summary>The receipt does not contain exact evidence for every planned change feed and scope.</summary>
    public const string FeedCatalogMismatch = "materialization.convergence.feedCatalogMismatch";
}

/// <summary>Whether convergence requires proof of an explicit source-settlement operation.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationConvergenceSettlementRequirement
{
    /// <summary>The selected source realization has no separate settlement obligation.</summary>
    NotRequired = 0,

    /// <summary>The feed must prove settlement of its exact latest application checkpoint and position.</summary>
    Explicit = 1
}

/// <summary>
/// Exact application, source-head, and optional settlement evidence for one independently checkpointed change feed.
/// </summary>
/// <remarks>
/// A caught-up verification that observes no new changes may prove the same through-position as an already-durable
/// checkpoint. Consequently, checkpoint and settlement time may precede the fresh read; only their own ordering and
/// the read's start-to-completion ordering are semantic.
/// </remarks>
public sealed record MaterializationCatchUpFeedEvidence
{
    /// <summary>Creates exact catch-up evidence for one source feed.</summary>
    /// <param name="feed">Stable feed identity from the persisted rebuild plan.</param>
    /// <param name="scope">Exact physical source, dependency input, partition, and ordering scope.</param>
    /// <param name="latestChangeCheckpoint">Latest durable application checkpoint that covers <paramref name="throughPosition"/>.</param>
    /// <param name="throughPosition">Exact positioned replay boundary durably applied by the checkpoint.</param>
    /// <param name="caughtUpReadStartedAtUtc">UTC time at which the source-head verification read began.</param>
    /// <param name="caughtUpReadCompletedAtUtc">UTC time at which the source reported that the bounded read was caught up.</param>
    /// <param name="checkpointCommittedAtUtc">UTC time at which application effects and the checkpoint became durable.</param>
    /// <param name="settlementRequirement">Whether this exact source realization has a separate settlement obligation.</param>
    /// <param name="settlement">
    /// Optional cumulative settlement of the exact checkpoint and position; omitted when the source realization has
    /// no separate acknowledgement obligation.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="throughPosition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, a timestamp is not UTC or is out of order, the position belongs to another scope, or
    /// <paramref name="settlement"/> does not cumulatively cover the exact checkpoint and position.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="settlementRequirement"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationCatchUpFeedEvidence(
        MaterializationChangeFeedId feed,
        MaterializationSourceScope scope,
        MaterializationCheckpointId latestChangeCheckpoint,
        MaterializationSourcePosition throughPosition,
        DateTimeOffset caughtUpReadStartedAtUtc,
        DateTimeOffset caughtUpReadCompletedAtUtc,
        DateTimeOffset checkpointCommittedAtUtc,
        MaterializationConvergenceSettlementRequirement settlementRequirement,
        MaterializationSourceSettlement? settlement = null)
    {
        MaterializationContract.RequireDefinedIdentity(feed.Value, nameof(feed));
        Scope = Guard.RequireNotNull(scope);
        MaterializationContract.RequireDefinedIdentity(latestChangeCheckpoint.Value, nameof(latestChangeCheckpoint));
        ThroughPosition = Guard.RequireNotNull(throughPosition);
        if (throughPosition.Scope != scope)
        {
            throw new ArgumentException(
                "Catch-up progress must belong to the exact planned source-feed scope.",
                nameof(throughPosition));
        }

        MaterializationContract.RequireUtc(caughtUpReadStartedAtUtc, nameof(caughtUpReadStartedAtUtc));
        MaterializationContract.RequireUtc(caughtUpReadCompletedAtUtc, nameof(caughtUpReadCompletedAtUtc));
        MaterializationContract.RequireUtc(checkpointCommittedAtUtc, nameof(checkpointCommittedAtUtc));
        if (!Enum.IsDefined(settlementRequirement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settlementRequirement),
                settlementRequirement,
                "Unsupported convergence settlement requirement.");
        }
        if (caughtUpReadCompletedAtUtc < caughtUpReadStartedAtUtc)
        {
            throw new ArgumentException(
                "A caught-up read cannot complete before it starts.",
                nameof(caughtUpReadCompletedAtUtc));
        }
        if (settlement is not null)
        {
            if (settlement.Checkpoint != latestChangeCheckpoint
                || settlement.Scope != scope
                || settlement.Kind != ChannelSettlementKind.CumulativePrefix
                || settlement.Position != throughPosition)
            {
                throw new ArgumentException(
                    "Catch-up settlement evidence must cumulatively cover the exact latest checkpoint and through position.",
                    nameof(settlement));
            }
            if (settlement.SettledAtUtc < checkpointCommittedAtUtc)
            {
                throw new ArgumentException(
                    "A source settlement cannot precede its durable application checkpoint.",
                    nameof(settlement));
            }
        }

        Feed = feed;
        LatestChangeCheckpoint = latestChangeCheckpoint;
        CaughtUpReadStartedAtUtc = caughtUpReadStartedAtUtc;
        CaughtUpReadCompletedAtUtc = caughtUpReadCompletedAtUtc;
        CheckpointCommittedAtUtc = checkpointCommittedAtUtc;
        SettlementRequirement = settlementRequirement;
        Settlement = settlement;
    }

    /// <summary>Stable feed identity from the persisted rebuild plan.</summary>
    public MaterializationChangeFeedId Feed { get; }

    /// <summary>Exact physical source, dependency input, partition, and ordering scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Latest durable application checkpoint covering <see cref="ThroughPosition"/>.</summary>
    public MaterializationCheckpointId LatestChangeCheckpoint { get; }

    /// <summary>Exact positioned replay boundary durably applied by the latest checkpoint.</summary>
    public MaterializationSourcePosition ThroughPosition { get; }

    /// <summary>UTC time at which the source-head verification read began.</summary>
    public DateTimeOffset CaughtUpReadStartedAtUtc { get; }

    /// <summary>UTC time at which the source reported that the bounded read was caught up.</summary>
    public DateTimeOffset CaughtUpReadCompletedAtUtc { get; }

    /// <summary>
    /// UTC time at which application effects and the checkpoint became durable; this may precede a no-op caught-up
    /// verification read.
    /// </summary>
    public DateTimeOffset CheckpointCommittedAtUtc { get; }

    /// <summary>Whether this exact source realization has a separate settlement obligation.</summary>
    public MaterializationConvergenceSettlementRequirement SettlementRequirement { get; }

    /// <summary>Optional exact cumulative source settlement of the latest checkpoint and through position.</summary>
    public MaterializationSourceSettlement? Settlement { get; }
}

/// <summary>Deterministic fingerprint of one complete materialization convergence receipt.</summary>
public sealed record MaterializationConvergenceReceiptFingerprint
{
    /// <summary>Creates a convergence-receipt fingerprint.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization profile identity.</param>
    /// <param name="value">Lower-case hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is empty or the digest is not lower-case hexadecimal.</exception>
    [JsonConstructor]
    public MaterializationConvergenceReceiptFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = MaterializationContract.RequireUnicodeIdentity(algorithm, nameof(algorithm));
        Canonicalization = MaterializationContract.RequireUnicodeIdentity(canonicalization, nameof(canonicalization));
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));
        if (value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A convergence-receipt fingerprint must be lower-case hexadecimal.",
                nameof(value));
        }
    }

    /// <summary>Digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lower-case hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>
/// Fingerprinted promotion evidence proving that every planned source feed converged on one exact generation.
/// </summary>
/// <remarks>
/// The receipt separates source position, durable application checkpoint, and optional source settlement. A valid
/// receipt is a local decision at <see cref="EvaluatedAtUtc"/>; callers must use <see cref="ValidateAgainst"/>
/// immediately before a seal, target validation, or promotion so catalog completeness, plan affinity, and dynamic
/// freshness are evaluated together. <see cref="ValidateFreshness"/> is available when plan affinity and catalog
/// coverage are already fenced by a surrounding operation.
/// </remarks>
public sealed record MaterializationConvergenceReceipt
{
    /// <summary>Current persisted convergence-receipt schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-convergence-receipt/v1";

    /// <summary>Creates and fingerprints one normalized convergence receipt.</summary>
    /// <param name="schemaVersion">Exact persisted receipt schema version.</param>
    /// <param name="synchronization">Exact materialization, definition, plan, impact, and generation fence.</param>
    /// <param name="feeds">Complete planned feed evidence, in any order.</param>
    /// <param name="evaluatedAtUtc">UTC time at which the convergence decision was evaluated.</param>
    /// <param name="freshnessDemand">Exact semantic maximum lag and optional maximum unsettled age.</param>
    /// <param name="validation">Additional structured validation diagnostics from hydration, reconciliation, or target checks.</param>
    /// <param name="fingerprint">Persisted exact receipt fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema or an identity is invalid, feeds are absent or repeated, evidence chronology exceeds
    /// <paramref name="evaluatedAtUtc"/>, validation is incomplete, or <paramref name="fingerprint"/> is stale.
    /// </exception>
    /// <exception cref="JsonException">Canonical fingerprint content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical fingerprint content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical fingerprint content has no unique JSON representation.</exception>
    [JsonConstructor]
    public MaterializationConvergenceReceipt(
        string schemaVersion,
        MaterializationSynchronizationWorkKey synchronization,
        ImmutableArray<MaterializationCatchUpFeedEvidence> feeds,
        DateTimeOffset evaluatedAtUtc,
        MaterializationFreshnessPolicy freshnessDemand,
        DocumentValidationResult validation,
        MaterializationConvergenceReceiptFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported materialization convergence-receipt schema version '{schemaVersion}'.",
                nameof(schemaVersion));
        }

        Synchronization = Guard.RequireNotNull(synchronization);
        FreshnessDemand = Guard.RequireNotNull(freshnessDemand);

        Feeds = NormalizeFeeds(feeds);
        MaterializationContract.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        foreach (var feed in Feeds)
        {
            if (feed.CaughtUpReadCompletedAtUtc > evaluatedAtUtc
                || feed.CheckpointCommittedAtUtc > evaluatedAtUtc
                || feed.Settlement?.SettledAtUtc > evaluatedAtUtc)
            {
                throw new ArgumentException(
                    "A convergence decision cannot precede its latest feed evidence.",
                    nameof(evaluatedAtUtc));
            }
        }

        ArgumentNullException.ThrowIfNull(validation);
        EvaluatedAtUtc = evaluatedAtUtc;
        Validation = BuildValidation(
            validation.Diagnostics,
            Feeds,
            evaluatedAtUtc,
            FreshnessDemand,
            Synchronization);

        var computed = MaterializationConvergenceReceiptFingerprinter.Compute(this);
        if (fingerprint is not null && fingerprint != computed)
        {
            throw new ArgumentException(
                "The supplied convergence-receipt fingerprint does not match canonical content.",
                nameof(fingerprint));
        }
        Fingerprint = computed;
    }

    /// <summary>Exact persisted receipt schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact materialization, definition, rebuild, impact, and generation fence.</summary>
    public MaterializationSynchronizationWorkKey Synchronization { get; }

    /// <summary>Logical materialization definition receiving the generation.</summary>
    [JsonIgnore]
    public MaterializationId Materialization => Synchronization.Materialization;

    /// <summary>Exact canonical materialization-definition content fence.</summary>
    [JsonIgnore]
    public ExecutionDefinitionFingerprint DefinitionFingerprint => Synchronization.DefinitionFingerprint;

    /// <summary>Exact persisted rebuild realization-plan fingerprint.</summary>
    [JsonIgnore]
    public MaterializationRebuildPlanFingerprint RebuildPlan => Synchronization.RebuildPlanFingerprint;

    /// <summary>Exact compiled impact-plan fingerprint used for incremental work.</summary>
    [JsonIgnore]
    public MaterializationImpactPlanFingerprint ImpactPlan => Synchronization.ImpactPlanFingerprint;

    /// <summary>Candidate generation proven by the receipt.</summary>
    [JsonIgnore]
    public MaterializationGenerationId Generation => Synchronization.Generation;

    /// <summary>Complete feed evidence in deterministic ordinal feed-identity order.</summary>
    public ImmutableArray<MaterializationCatchUpFeedEvidence> Feeds { get; }

    /// <summary>UTC time at which the convergence decision was evaluated.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }

    /// <summary>Exact semantic maximum lag and optional maximum unsettled age.</summary>
    public MaterializationFreshnessPolicy FreshnessDemand { get; }

    /// <summary>Structured normalized convergence diagnostics at <see cref="EvaluatedAtUtc"/>.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Deterministic fingerprint of every durable receipt field and decision diagnostic.</summary>
    public MaterializationConvergenceReceiptFingerprint Fingerprint { get; }

    /// <summary>
    /// Whether supplied feed evidence satisfied local convergence demands at <see cref="EvaluatedAtUtc"/>; this does
    /// not replace plan-affinity and catalog-completeness validation through <see cref="ValidateAgainst"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsValid => Validation.IsValid;

    /// <summary>Revalidates the bounded proof lifetime and all feed freshness demands at a later UTC time.</summary>
    /// <param name="observedAtUtc">UTC time of a seal, target validation, or promotion decision.</param>
    /// <returns>
    /// Structured deterministic diagnostics; the result is valid only while the receipt and every source-head proof
    /// remain within <see cref="MaterializationFreshnessPolicy.MaximumLagMilliseconds"/> and required settlement is
    /// present within its declared bound.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="observedAtUtc"/> is not UTC or precedes <see cref="EvaluatedAtUtc"/>.
    /// </exception>
    public DocumentValidationResult ValidateFreshness(DateTimeOffset observedAtUtc)
    {
        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < EvaluatedAtUtc)
        {
            throw new ArgumentException(
                "Convergence freshness cannot be evaluated before the receipt decision.",
                nameof(observedAtUtc));
        }

        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(Validation.Diagnostics.Length + Feeds.Length + 1);
        diagnostics.AddRange(Validation.Diagnostics);
        AddEvaluationDiagnostics(
            diagnostics,
            Feeds,
            observedAtUtc,
            FreshnessDemand,
            Synchronization);

        if (ExceedsMilliseconds(observedAtUtc - EvaluatedAtUtc, FreshnessDemand.MaximumLagMilliseconds))
        {
            AddIfAbsent(
                diagnostics,
                Diagnostic(
                    code: MaterializationConvergenceDiagnosticCodes.ProofStale,
                    message: "The convergence receipt is older than the maximum admitted proof lifetime.",
                    location: "/evaluatedAtUtc",
                    subject: Materialization.Value,
                    sourceReference: RebuildPlan.Value,
                    expected: $"age <= {Format(FreshnessDemand.MaximumLagMilliseconds)} ms",
                    observed: $"evaluatedAt={EvaluatedAtUtc:O}; observedAt={observedAtUtc:O}"));
        }

        var normalized = MaterializationContract.NormalizeDiagnostics(diagnostics.ToImmutable(), nameof(observedAtUtc));
        return normalized.IsDefaultOrEmpty
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(normalized);
    }

    /// <summary>
    /// Validates exact plan affinity, catalog-complete feed coverage, and dynamic freshness before promotion.
    /// </summary>
    /// <param name="plan">Constructor-verified rebuild plan that the candidate generation must realize.</param>
    /// <param name="observedAtUtc">UTC time of the seal, target validation, or promotion decision.</param>
    /// <returns>
    /// Structured deterministic diagnostics; the result is valid only when the receipt identifies the exact
    /// materialization, definition, rebuild plan, impact plan, and generation-scoped synchronization key, contains
    /// one exact entry for every planned feed ID and scope, and remains fresh.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="observedAtUtc"/> is not UTC or precedes <see cref="EvaluatedAtUtc"/>.
    /// </exception>
    public DocumentValidationResult ValidateAgainst(
        MaterializationRebuildPlan plan,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var freshness = ValidateFreshness(observedAtUtc);
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(freshness.Diagnostics.Length + 2);
        diagnostics.AddRange(freshness.Diagnostics);

        var expectedMaterialization = plan.Materialization.Definition.Id;
        var exactSynchronization = Materialization == expectedMaterialization
            && DefinitionFingerprint == plan.Materialization.DefinitionFingerprint
            && RebuildPlan == plan.Fingerprint
            && ImpactPlan == plan.ImpactPlan.Fingerprint
            && FreshnessDemand == plan.Materialization.Definition.FreshnessPolicy;
        if (!exactSynchronization)
        {
            AddIfAbsent(
                diagnostics,
                Diagnostic(
                    code: MaterializationConvergenceDiagnosticCodes.PlanAffinityMismatch,
                    message: "The convergence receipt does not identify the exact supplied rebuild realization.",
                    location: "/synchronization",
                    subject: Generation.Value,
                    sourceReference: plan.Fingerprint.Value,
                    expected: Describe(plan),
                    observed: Describe(Synchronization, FreshnessDemand)));
        }

        var exactFeedCatalog = Feeds.Length == plan.ChangeFeeds.Length;
        if (exactFeedCatalog)
        {
            for (var index = 0; index < Feeds.Length; index++)
            {
                if (Feeds[index].Feed != plan.ChangeFeeds[index].Id
                    || Feeds[index].Scope != plan.ChangeFeeds[index].Scope)
                {
                    exactFeedCatalog = false;
                    break;
                }
            }
        }
        if (!exactFeedCatalog)
        {
            AddIfAbsent(
                diagnostics,
                Diagnostic(
                    code: MaterializationConvergenceDiagnosticCodes.FeedCatalogMismatch,
                    message: "The convergence receipt does not prove every exact planned change feed and source scope.",
                    location: "/feeds",
                    subject: Generation.Value,
                    sourceReference: plan.Fingerprint.Value,
                    expected: Describe(plan.ChangeFeeds),
                    observed: Describe(Feeds)));
        }

        var normalized = MaterializationContract.NormalizeDiagnostics(diagnostics.ToImmutable(), nameof(plan));
        return normalized.IsDefaultOrEmpty
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(normalized);
    }

    /// <summary>Compares receipts by their constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Receipt to compare with this receipt.</param>
    /// <returns><see langword="true"/> when both receipts have identical canonical durable content.</returns>
    public bool Equals(MaterializationConvergenceReceipt? other) =>
        ReferenceEquals(this, other)
        || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Returns a hash code derived from the canonical receipt fingerprint.</summary>
    /// <returns>A hash code stable for semantically identical receipts.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();

    static ImmutableArray<MaterializationCatchUpFeedEvidence> NormalizeFeeds(
        ImmutableArray<MaterializationCatchUpFeedEvidence> feeds)
    {
        if (feeds.IsDefaultOrEmpty || feeds.Any(static feed => feed is null))
        {
            throw new ArgumentException(
                "A convergence receipt requires non-null evidence for every planned change feed.",
                nameof(feeds));
        }

        var ids = new HashSet<MaterializationChangeFeedId>();
        var scopes = new HashSet<MaterializationSourceScope>();
        foreach (var feed in feeds)
        {
            if (!ids.Add(feed.Feed))
                throw new ArgumentException("A convergence receipt cannot repeat a change-feed identity.", nameof(feeds));
            if (!scopes.Add(feed.Scope))
                throw new ArgumentException("A convergence receipt cannot alias one source scope through multiple feeds.", nameof(feeds));
        }

        var canonical = true;
        for (var index = 1; index < feeds.Length; index++)
        {
            if (StringComparer.Ordinal.Compare(feeds[index - 1].Feed.Value, feeds[index].Feed.Value) > 0)
            {
                canonical = false;
                break;
            }
        }
        if (canonical)
            return feeds;

        var sorted = ImmutableArray.CreateBuilder<MaterializationCatchUpFeedEvidence>(feeds.Length);
        sorted.AddRange(feeds);
        sorted.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Feed.Value, right.Feed.Value));
        return sorted.MoveToImmutable();
    }

    static DocumentValidationResult BuildValidation(
        ImmutableArray<DocumentValidationDiagnostic> supplied,
        ImmutableArray<MaterializationCatchUpFeedEvidence> feeds,
        DateTimeOffset evaluatedAtUtc,
        MaterializationFreshnessPolicy freshness,
        MaterializationSynchronizationWorkKey synchronization)
    {
        var normalizedSupplied = MaterializationContract.NormalizeDiagnostics(supplied, nameof(supplied));
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(normalizedSupplied.Length + (feeds.Length * 2));
        diagnostics.AddRange(normalizedSupplied);
        AddEvaluationDiagnostics(
            diagnostics,
            feeds,
            evaluatedAtUtc,
            freshness,
            synchronization);
        var normalized = MaterializationContract.NormalizeDiagnostics(diagnostics.ToImmutable(), nameof(supplied));
        return normalized.IsDefaultOrEmpty
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(normalized);
    }

    static void AddEvaluationDiagnostics(
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics,
        ImmutableArray<MaterializationCatchUpFeedEvidence> feeds,
        DateTimeOffset observedAtUtc,
        MaterializationFreshnessPolicy freshness,
        MaterializationSynchronizationWorkKey synchronization)
    {
        for (var feedIndex = 0; feedIndex < feeds.Length; feedIndex++)
        {
            var feed = feeds[feedIndex];
            if (ExceedsMilliseconds(observedAtUtc - feed.CaughtUpReadStartedAtUtc, freshness.MaximumLagMilliseconds))
            {
                AddIfAbsent(
                    diagnostics,
                    Diagnostic(
                        code: MaterializationConvergenceDiagnosticCodes.LagExceeded,
                        message: "The caught-up source read is older than the maximum admitted materialization lag.",
                        location: $"/feeds/{feedIndex}/caughtUpReadStartedAtUtc",
                        subject: feed.Feed.Value,
                        sourceReference: synchronization.RebuildPlanFingerprint.Value,
                        expected: $"lag <= {Format(freshness.MaximumLagMilliseconds)} ms",
                        observed: $"readStartedAt={feed.CaughtUpReadStartedAtUtc:O}; observedAt={observedAtUtc:O}"));
            }

            if (feed.SettlementRequirement == MaterializationConvergenceSettlementRequirement.Explicit
                && feed.Settlement is null)
            {
                AddIfAbsent(
                    diagnostics,
                    Diagnostic(
                        code: MaterializationConvergenceDiagnosticCodes.SettlementMissing,
                        message: "The source feed has no explicit settlement of its exact latest application checkpoint.",
                        location: $"/feeds/{feedIndex}/settlement",
                        subject: feed.Feed.Value,
                        sourceReference: synchronization.RebuildPlanFingerprint.Value,
                        expected: $"settlement of checkpoint '{feed.LatestChangeCheckpoint.Value}'",
                        observed: "missing"));
            }

            if (feed.SettlementRequirement == MaterializationConvergenceSettlementRequirement.Explicit
                && freshness.MaximumUnsettledMilliseconds is { } maximumUnsettled
                && feed.Settlement is { } settlement
                && ExceedsMilliseconds(settlement.SettledAtUtc - feed.CheckpointCommittedAtUtc, maximumUnsettled))
            {
                AddIfAbsent(
                    diagnostics,
                    Diagnostic(
                        code: MaterializationConvergenceDiagnosticCodes.UnsettledAgeExceeded,
                        message: "The latest application checkpoint remained unsettled longer than the declared maximum.",
                        location: $"/feeds/{feedIndex}/settlement/settledAtUtc",
                        subject: feed.Feed.Value,
                        sourceReference: settlement.Id.Value,
                        expected: $"unsettled age <= {Format(maximumUnsettled)} ms",
                        observed: $"checkpointCommittedAt={feed.CheckpointCommittedAtUtc:O}; settledAt={settlement.SettledAtUtc:O}"));
            }
        }
    }

    static bool ExceedsMilliseconds(TimeSpan elapsed, long maximumMilliseconds)
    {
        var maximumRepresentableMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        return maximumMilliseconds <= maximumRepresentableMilliseconds
            && elapsed.Ticks > maximumMilliseconds * TimeSpan.TicksPerMillisecond;
    }

    static string Describe(MaterializationRebuildPlan plan) =>
        $"materialization={plan.Materialization.Definition.Id.Value};"
        + $"definition={plan.Materialization.DefinitionFingerprint.Value};"
        + $"rebuild={plan.Fingerprint.Value};"
        + $"impact={plan.ImpactPlan.Fingerprint.Value};"
        + $"maximumLagMilliseconds={Format(plan.Materialization.Definition.FreshnessPolicy.MaximumLagMilliseconds)};"
        + $"maximumUnsettledMilliseconds={Format(plan.Materialization.Definition.FreshnessPolicy.MaximumUnsettledMilliseconds)}";

    static string Describe(
        MaterializationSynchronizationWorkKey synchronization,
        MaterializationFreshnessPolicy freshness) =>
        $"materialization={synchronization.Materialization.Value};"
        + $"definition={synchronization.DefinitionFingerprint.Value};"
        + $"rebuild={synchronization.RebuildPlanFingerprint.Value};"
        + $"impact={synchronization.ImpactPlanFingerprint.Value};"
        + $"generation={synchronization.Generation.Value};"
        + $"maximumLagMilliseconds={Format(freshness.MaximumLagMilliseconds)};"
        + $"maximumUnsettledMilliseconds={Format(freshness.MaximumUnsettledMilliseconds)}";

    static string Describe(ImmutableArray<MaterializationChangeFeedPlan> feeds) =>
        string.Join(',', feeds.Select(static feed => Describe(feed.Id, feed.Scope)));

    static string Describe(ImmutableArray<MaterializationCatchUpFeedEvidence> feeds) =>
        string.Join(',', feeds.Select(static feed => Describe(feed.Feed, feed.Scope)));

    static string Describe(MaterializationChangeFeedId feed, MaterializationSourceScope scope) =>
        $"{feed.Value}@{scope.PhysicalPlan.Value}/{scope.Placement.Id.Value}/{scope.Partition.Value}/{scope.OrderingScope.Value}";

    static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    static string Format(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";

    static void AddIfAbsent(
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics,
        DocumentValidationDiagnostic candidate)
    {
        if (!diagnostics.Contains(candidate))
            diagnostics.Add(candidate);
    }

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        string location,
        string subject,
        string sourceReference,
        string expected,
        string observed) =>
        MaterializationContract.CreateDiagnostic(
            code: code,
            severity: DiagnosticSeverity.Error,
            message: message,
            location: location,
            stage: "materialization-convergence-validation",
            subject: subject,
            sourceReferences: [sourceReference],
            expected: expected,
            observed: observed,
            resolutionOptions: ["Continue catch-up and produce fresh complete convergence evidence before promotion."]);
}

/// <summary>Computes deterministic content fingerprints for materialization convergence receipts.</summary>
public static class MaterializationConvergenceReceiptFingerprinter
{
    /// <summary>Digest algorithm used by convergence-receipt fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by the v1 convergence-receipt fence.</summary>
    public const string Canonicalization = "cohesive-materialization-convergence-receipt/v1-c14n/v1";

    /// <summary>Computes a deterministic fingerprint of every durable receipt field except its own fingerprint.</summary>
    /// <param name="receipt">Normalized convergence receipt to fingerprint.</param>
    /// <returns>Versioned SHA-256 fingerprint of the complete convergence evidence and decision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">Receipt content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Receipt content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Receipt content has no unique canonical JSON representation.</exception>
    public static MaterializationConvergenceReceiptFingerprint Compute(MaterializationConvergenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                SchemaVersion: receipt.SchemaVersion,
                Synchronization: receipt.Synchronization,
                Feeds: receipt.Feeds,
                EvaluatedAtUtc: receipt.EvaluatedAtUtc,
                FreshnessDemand: receipt.FreshnessDemand,
                Validation: receipt.Validation),
            MaterializationJsonSerializer.CreateOptions());
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        MaterializationSynchronizationWorkKey Synchronization,
        ImmutableArray<MaterializationCatchUpFeedEvidence> Feeds,
        DateTimeOffset EvaluatedAtUtc,
        MaterializationFreshnessPolicy FreshnessDemand,
        DocumentValidationResult Validation);
}

/// <summary>Strict canonical JSON serialization for durable materialization convergence receipts.</summary>
public static class MaterializationConvergenceReceiptJsonSerializer
{
    /// <summary>Creates strict receipt JSON options including canonical Relations converters.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        MaterializationJsonSerializer.CreateOptions(formatting);

    /// <summary>Serializes one exactly fingerprinted convergence receipt.</summary>
    /// <param name="receipt">Receipt to serialize.</param>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Deterministic convergence-receipt JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The receipt cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The receipt contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The receipt has no unique canonical JSON representation.</exception>
    public static string Serialize(
        MaterializationConvergenceReceipt receipt,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(receipt))
            : JsonSerializer.Serialize(receipt, CreateOptions(formatting));
    }

    /// <summary>Gets the unique canonical compact UTF-8 representation of one convergence receipt.</summary>
    /// <param name="receipt">Receipt to encode.</param>
    /// <returns>Canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The receipt cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The receipt contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The receipt has no unique canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(MaterializationConvergenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return StrictDocumentJson.GetCanonicalBytes(receipt, CreateOptions());
    }

    /// <summary>Deserializes and verifies one current-version canonical convergence receipt.</summary>
    /// <param name="json">Persisted receipt JSON.</param>
    /// <returns>An exactly normalized receipt whose persisted fingerprint matches all canonical content.</returns>
    /// <exception cref="JsonException">
    /// The wire is empty, malformed, open, duplicate, non-canonical, uses an unsupported schema, violates a receipt
    /// invariant, or carries a stale or forged fingerprint.
    /// </exception>
    public static MaterializationConvergenceReceipt Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "materialization convergence receipt",
                out MaterializationConvergenceReceipt? receipt,
                out var error)
            || receipt is null)
        {
            throw new JsonException(error.Message);
        }

        return receipt;
    }
}
