using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage.Materialization;

/// <summary>Terminal outcome of one independently promoted rebuild plan-set Process attempt.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationRebuildPlanSetOutcome
{
    /// <summary>Every required placement slice is currently routed to its rebuilt generation.</summary>
    Completed = 0,

    /// <summary>At least one, but not every, required placement slice was independently promoted.</summary>
    PartiallyPromoted = 1,

    /// <summary>No placement slice was promoted and one or more required leaves failed.</summary>
    Failed = 2
}

/// <summary>Terminal phase retained for one exact linked leaf in a parent plan-set receipt.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationRebuildPlanSetLeafOutcome
{
    /// <summary>The leaf is exactly ready, but the parent barrier did not initiate promotion.</summary>
    Ready = 0,

    /// <summary>The leaf is currently selected for both reads and incremental writes.</summary>
    Promoted = 1,

    /// <summary>The leaf build, activation, or independent route transition failed.</summary>
    Failed = 2,

    /// <summary>The leaf ended through cooperative cancellation.</summary>
    Cancelled = 3,

    /// <summary>The leaf was forcibly terminated.</summary>
    Terminated = 4
}

/// <summary>Parent coordination phase in which one exact child produced terminal evidence.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationRebuildPlanSetLeafPhase
{
    /// <summary>The child was building and validating one target generation.</summary>
    Build = 0,

    /// <summary>The child was activating and independently routing one ready generation.</summary>
    Promotion = 1
}

/// <summary>
/// Exact portable terminal result retained from one failed, cancelled, or terminated plan-set child coordination.
/// </summary>
/// <remarks>
/// Projection copies the child result without translating target-specific evidence into a Storage diagnostic. The
/// parent checkpoint remains the authority that validated the result against the exact child Request contract.
/// </remarks>
public sealed record MaterializationRebuildPlanSetChildTerminalEvidence
{
    /// <summary>Creates exact terminal evidence from one parent-linked child Process.</summary>
    /// <param name="phase">Parent coordination phase that owned the child.</param>
    /// <param name="child">Exact attempt-bound child Process continuation.</param>
    /// <param name="terminalOutcome">Exact terminal Request outcome returned by the child.</param>
    /// <param name="terminalResult">Exact typed portable result returned for that outcome.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="child"/> or <paramref name="terminalResult"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="phase"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// A child-continuation or terminal-outcome identity is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildPlanSetChildTerminalEvidence(
        MaterializationRebuildPlanSetLeafPhase phase,
        ProcessContinuationIdentity child,
        RequestTerminalOutcomeId terminalOutcome,
        PortableValue terminalResult)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported plan-set child phase.");

        Child = child ?? throw new ArgumentNullException(nameof(child));
        MaterializationContract.RequireDefinedIdentity(child.ProcessInstanceId.Value, nameof(child));
        MaterializationContract.RequireDefinedIdentity(child.ProcessAttemptId.Value, nameof(child));
        MaterializationContract.RequireDefinedIdentity(terminalOutcome.Value, nameof(terminalOutcome));
        TerminalResult = terminalResult ?? throw new ArgumentNullException(nameof(terminalResult));
        Phase = phase;
        TerminalOutcome = terminalOutcome;
    }

    /// <summary>Parent coordination phase that owned the child.</summary>
    public MaterializationRebuildPlanSetLeafPhase Phase { get; }

    /// <summary>Exact attempt-bound child Process continuation.</summary>
    public ProcessContinuationIdentity Child { get; }

    /// <summary>Exact terminal Request outcome returned by the child.</summary>
    public RequestTerminalOutcomeId TerminalOutcome { get; }

    /// <summary>Exact typed portable result returned for the terminal outcome.</summary>
    public PortableValue TerminalResult { get; }
}

/// <summary>Durable terminal receipt for one exact linked leaf of a parent rebuild attempt.</summary>
public sealed record MaterializationRebuildPlanSetLeafReceipt
{
    /// <summary>Creates one exact linked-leaf terminal receipt.</summary>
    /// <param name="authority">Exact plan-set, leaf-plan, and placement authority.</param>
    /// <param name="buildChild">Attempt-bound child Process that performed build and validation.</param>
    /// <param name="outcome">Terminal leaf phase observed by the parent.</param>
    /// <param name="ready">Exact readiness evidence when the build child succeeded.</param>
    /// <param name="promotionChild">Attempt-bound promotion child, when promotion was initiated.</param>
    /// <param name="promotion">Exact independent-promotion result, when the route operation returned evidence.</param>
    /// <param name="terminalEvidence">
    /// Exact terminal outcome and typed result for a failed, cancelled, or terminated child.
    /// </param>
    /// <param name="failure">Structured terminal failure evidence for a failed leaf.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="buildChild"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Child, readiness, promotion, outcome, or failure evidence is absent, contradictory, or inexact.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildPlanSetLeafReceipt(
        MaterializationRebuildLeafExecutionAuthority authority,
        ProcessContinuationIdentity buildChild,
        MaterializationRebuildPlanSetLeafOutcome outcome,
        MaterializationReadyGenerationReference? ready = null,
        ProcessContinuationIdentity? promotionChild = null,
        MaterializationIndependentPromotionResult? promotion = null,
        MaterializationRebuildPlanSetChildTerminalEvidence? terminalEvidence = null,
        DocumentValidationDiagnostic? failure = null)
    {
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        BuildChild = buildChild ?? throw new ArgumentNullException(nameof(buildChild));
        RequireContinuation(buildChild, nameof(buildChild));
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported plan-set leaf outcome.");
        if (ready is not null
            && (ready.Authority != authority || ready.Attempt.Continuation != buildChild))
        {
            throw new ArgumentException(
                "Leaf readiness evidence must come from the exact linked build child.",
                nameof(ready));
        }
        if (promotionChild is not null)
        {
            RequireContinuation(promotionChild, nameof(promotionChild));
            if (promotionChild == buildChild)
                throw new ArgumentException("Build and promotion require distinct child Process continuations.", nameof(promotionChild));
        }
        if (promotion is not null
            && (ready is null
                || promotion.Request.Authority != authority
                || !ready.MatchesActiveGeneration(promotion.Request.ActiveGeneration)))
        {
            throw new ArgumentException(
                "Independent-promotion evidence must consume this exact linked leaf readiness result.",
                nameof(promotion));
        }
        if (failure is not null && failure.Severity != DiagnosticSeverity.Error)
            throw new ArgumentException("Leaf failure evidence must be an error diagnostic.", nameof(failure));
        if (terminalEvidence is not null)
        {
            var expectedChild = terminalEvidence.Phase switch
            {
                MaterializationRebuildPlanSetLeafPhase.Build => buildChild,
                MaterializationRebuildPlanSetLeafPhase.Promotion => promotionChild,
                _ => null
            };
            if (expectedChild is null || terminalEvidence.Child != expectedChild)
            {
                throw new ArgumentException(
                    "Child terminal evidence must identify the exact linked child for its parent coordination phase.",
                    nameof(terminalEvidence));
            }
        }

        var terminalPhaseValid = terminalEvidence?.Phase switch
        {
            MaterializationRebuildPlanSetLeafPhase.Build =>
                ready is null && promotionChild is null && promotion is null,
            MaterializationRebuildPlanSetLeafPhase.Promotion =>
                ready is not null && promotionChild is not null,
            null => true,
            _ => false
        };
        var terminalOutcomeValid = outcome switch
        {
            MaterializationRebuildPlanSetLeafOutcome.Failed => terminalEvidence is not null
                && terminalEvidence.TerminalOutcome != MaterializationRebuildPlanSetProcessFactory.CancelledOutcome
                && terminalEvidence.TerminalOutcome != MaterializationRebuildPlanSetProcessFactory.TerminatedOutcome,
            MaterializationRebuildPlanSetLeafOutcome.Cancelled =>
                terminalEvidence?.TerminalOutcome == MaterializationRebuildPlanSetProcessFactory.CancelledOutcome,
            MaterializationRebuildPlanSetLeafOutcome.Terminated =>
                terminalEvidence?.TerminalOutcome == MaterializationRebuildPlanSetProcessFactory.TerminatedOutcome,
            _ => terminalEvidence is null
        };

        var valid = outcome switch
        {
            MaterializationRebuildPlanSetLeafOutcome.Ready =>
                ready is not null && promotionChild is null && promotion is null
                && terminalEvidence is null && failure is null,
            MaterializationRebuildPlanSetLeafOutcome.Promoted =>
                ready is not null && promotionChild is not null && promotion is { IsCurrentlySelected: true }
                && terminalEvidence is null && failure is null,
            MaterializationRebuildPlanSetLeafOutcome.Failed =>
                terminalEvidence is not null && failure is not null
                && promotion is not { IsCurrentlySelected: true },
            MaterializationRebuildPlanSetLeafOutcome.Cancelled or
                MaterializationRebuildPlanSetLeafOutcome.Terminated =>
                promotion is null && terminalEvidence is not null && failure is null,
            _ => false
        };
        if (!valid || !terminalPhaseValid || !terminalOutcomeValid)
        {
            throw new ArgumentException(
                "Leaf outcome contradicts its readiness, promotion-child, route-result, child-terminal, or failure evidence.",
                nameof(outcome));
        }

        Outcome = outcome;
        Ready = ready;
        PromotionChild = promotionChild;
        Promotion = promotion;
        TerminalEvidence = terminalEvidence;
        Failure = failure;
    }

    /// <summary>Exact plan-set, leaf-plan, and placement authority.</summary>
    public MaterializationRebuildLeafExecutionAuthority Authority { get; }

    /// <summary>Attempt-bound child Process that performed build and validation.</summary>
    public ProcessContinuationIdentity BuildChild { get; }

    /// <summary>Terminal leaf phase observed by the parent.</summary>
    public MaterializationRebuildPlanSetLeafOutcome Outcome { get; }

    /// <summary>Exact readiness evidence when the build child succeeded.</summary>
    public MaterializationReadyGenerationReference? Ready { get; }

    /// <summary>Attempt-bound child Process that performed target activation and independent routing.</summary>
    public ProcessContinuationIdentity? PromotionChild { get; }

    /// <summary>Exact independent-promotion result when routing returned conclusive evidence.</summary>
    public MaterializationIndependentPromotionResult? Promotion { get; }

    /// <summary>
    /// Exact child terminal outcome and typed result when the leaf failed, was cancelled, or was terminated.
    /// </summary>
    public MaterializationRebuildPlanSetChildTerminalEvidence? TerminalEvidence { get; }

    /// <summary>Structured terminal failure evidence, when the leaf failed.</summary>
    public DocumentValidationDiagnostic? Failure { get; }

    static void RequireContinuation(ProcessContinuationIdentity continuation, string parameterName)
    {
        MaterializationContract.RequireDefinedIdentity(continuation.ProcessInstanceId.Value, parameterName);
        MaterializationContract.RequireDefinedIdentity(continuation.ProcessAttemptId.Value, parameterName);
    }
}

/// <summary>Canonical aggregate receipt for one durable parent plan-set Process attempt.</summary>
public sealed record MaterializationRebuildPlanSetReceipt
{
    /// <summary>Current portable aggregate-receipt schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-plan-set-receipt/v1";

    /// <summary>Creates or deserializes one internally exact aggregate receipt.</summary>
    /// <param name="schemaVersion">Exact portable receipt schema.</param>
    /// <param name="planSet">Exact linked plan-set authority.</param>
    /// <param name="parentContinuation">Exact parent Process attempt producing the receipt.</param>
    /// <param name="outcome">Honest aggregate outcome.</param>
    /// <param name="leaves">Terminal linked-leaf receipts.</param>
    /// <param name="readyBarrier">Reusable all-leaf readiness barrier, when every build leaf became ready.</param>
    /// <param name="completedAtUtc">UTC aggregate terminal boundary.</param>
    /// <exception cref="ArgumentNullException">A required value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Schema, parent, leaf coverage, barrier affinity, aggregate outcome, or chronology is inconsistent.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildPlanSetReceipt(
        string schemaVersion,
        MaterializationRebuildPlanSetReference planSet,
        ProcessContinuationIdentity parentContinuation,
        MaterializationRebuildPlanSetOutcome outcome,
        ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> leaves,
        MaterializationRebuildReadyBarrier? readyBarrier,
        DateTimeOffset completedAtUtc)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Rebuild plan-set receipt schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        ParentContinuation = parentContinuation ?? throw new ArgumentNullException(nameof(parentContinuation));
        MaterializationContract.RequireDefinedIdentity(parentContinuation.ProcessInstanceId.Value, nameof(parentContinuation));
        MaterializationContract.RequireDefinedIdentity(parentContinuation.ProcessAttemptId.Value, nameof(parentContinuation));
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported plan-set aggregate outcome.");
        MaterializationContract.RequireUtc(completedAtUtc, nameof(completedAtUtc));

        var normalized = leaves.IsDefault ? [] : leaves;
        var buildChildren = normalized
            .Where(static leaf => leaf is not null)
            .Select(static leaf => leaf.BuildChild)
            .ToHashSet();
        var promotionChildren = normalized
            .Where(static leaf => leaf?.PromotionChild is not null)
            .Select(static leaf => leaf.PromotionChild!)
            .ToArray();
        if (normalized.Any(static leaf => leaf is null)
            || normalized.Any(leaf => leaf.Authority.PlanSet != planSet)
            || normalized.GroupBy(static leaf => leaf.Authority.PlacementSlice.Id).Any(static group => group.Count() > 1)
            || normalized.GroupBy(static leaf => leaf.Authority.LeafPlan.Plan).Any(static group => group.Count() > 1)
            || normalized.GroupBy(static leaf => leaf.BuildChild).Any(static group => group.Count() > 1)
            || promotionChildren.Length != promotionChildren.Distinct().Count()
            || promotionChildren.Any(buildChildren.Contains))
        {
            throw new ArgumentException(
                "Aggregate leaf receipts must be non-null, uniquely plan- and child-bound, and linked to the exact plan set.",
                nameof(leaves));
        }

        Leaves =
        [
            .. normalized.OrderBy(static leaf => leaf.Authority.PlacementSlice.Id.Value, StringComparer.Ordinal)
        ];
        ReadyBarrier = readyBarrier;
        if (readyBarrier is not null
            && (readyBarrier.PlanSet != planSet
                || readyBarrier.ParentContinuation != parentContinuation
                || readyBarrier.ReadyGenerations.Length != Leaves.Length
                || Leaves.Any(static leaf => leaf.Ready is null)
                || !readyBarrier.ReadyGenerations.SequenceEqual(Leaves.Select(static leaf => leaf.Ready!))))
        {
            throw new ArgumentException(
                "The aggregate ready barrier must retain every exact successful build-child receipt.",
                nameof(readyBarrier));
        }

        var promotedCount = Leaves.Count(static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Promoted);
        var expectedOutcome = promotedCount == Leaves.Length
            ? MaterializationRebuildPlanSetOutcome.Completed
            : promotedCount > 0
                ? MaterializationRebuildPlanSetOutcome.PartiallyPromoted
                : MaterializationRebuildPlanSetOutcome.Failed;
        if (outcome != expectedOutcome
            || outcome == MaterializationRebuildPlanSetOutcome.Completed && readyBarrier is null
            || outcome == MaterializationRebuildPlanSetOutcome.PartiallyPromoted && readyBarrier is null
            || outcome == MaterializationRebuildPlanSetOutcome.Failed
            && Leaves.Length > 0
            && Leaves.All(static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Ready))
        {
            throw new ArgumentException(
                "Aggregate outcome must report complete, partial, or failed promotion from the exact leaf receipts.",
                nameof(outcome));
        }

        var latestEvidenceAtUtc = Leaves
            .Select(static leaf => leaf.Promotion?.Routing?.Receipt?.CommittedAtUtc
                ?? leaf.Promotion?.Admission.Receipt?.CommittedAtUtc
                ?? leaf.Ready?.ReadyAtUtc)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (completedAtUtc < latestEvidenceAtUtc)
            throw new ArgumentException("Aggregate completion cannot predate retained leaf evidence.", nameof(completedAtUtc));

        Outcome = outcome;
        CompletedAtUtc = completedAtUtc;
    }

    /// <summary>Exact portable aggregate-receipt schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact linked plan-set authority.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Exact parent Process attempt producing the receipt.</summary>
    public ProcessContinuationIdentity ParentContinuation { get; }

    /// <summary>Honest complete, partial, or failed independent-promotion outcome.</summary>
    public MaterializationRebuildPlanSetOutcome Outcome { get; }

    /// <summary>Terminal linked-leaf receipts in canonical placement-slice order.</summary>
    public ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> Leaves { get; }

    /// <summary>Reusable all-leaf readiness barrier, when every build leaf became ready.</summary>
    public MaterializationRebuildReadyBarrier? ReadyBarrier { get; }

    /// <summary>UTC aggregate terminal boundary.</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Creates a receipt only when it covers every and only linked plan-set leaf.</summary>
    /// <param name="planSet">Complete constructor-verified linked plan set.</param>
    /// <param name="parentContinuation">Exact parent Process attempt.</param>
    /// <param name="outcome">Honest aggregate outcome.</param>
    /// <param name="leaves">Terminal linked-leaf receipts.</param>
    /// <param name="readyBarrier">Reusable exact all-leaf readiness barrier, when established.</param>
    /// <param name="completedAtUtc">UTC terminal boundary.</param>
    /// <returns>A canonical aggregate receipt with exact leaf coverage.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Leaf coverage or another receipt invariant is violated.</exception>
    public static MaterializationRebuildPlanSetReceipt Create(
        MaterializationRebuildPlanSet planSet,
        ProcessContinuationIdentity parentContinuation,
        MaterializationRebuildPlanSetOutcome outcome,
        ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> leaves,
        MaterializationRebuildReadyBarrier? readyBarrier,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        var receipt = new MaterializationRebuildPlanSetReceipt(
            CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet),
            parentContinuation,
            outcome,
            leaves,
            readyBarrier,
            completedAtUtc);
        if (receipt.Leaves.Length != planSet.LeafPlans.Length)
            throw new ArgumentException("The aggregate receipt requires every and only linked plan-set leaf.", nameof(leaves));

        var expectedBySlice = planSet.LeafPlans.ToDictionary(static binding => binding.Slice.Id);
        foreach (var leaf in receipt.Leaves)
        {
            if (!expectedBySlice.TryGetValue(leaf.Authority.PlacementSlice.Id, out var expected)
                || leaf.Authority.Binding != expected)
            {
                throw new ArgumentException(
                    "The aggregate receipt contains missing, extra, stale, or substituted linked-leaf evidence.",
                    nameof(leaves));
            }
        }
        return receipt;
    }

    /// <summary>
    /// Validates this structurally deserialized receipt against the complete plan set and exact durable parent child
    /// ledgers that produced it.
    /// </summary>
    /// <param name="planSet">Complete constructor-verified linked plan set.</param>
    /// <param name="parentPlan">Exact compiled parent Process specialized to <paramref name="planSet"/>.</param>
    /// <param name="checkpoint">Durable parent checkpoint retaining the build and promotion child ledgers.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The receipt is incomplete, belongs to another parent attempt, or differs from the exact retained child
    /// evidence.
    /// </exception>
    public void ValidateAgainst(
        MaterializationRebuildPlanSet planSet,
        CompiledProcessPlan parentPlan,
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(parentPlan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        PlanSetProjection.ValidateParentContext(planSet, parentPlan, checkpoint);
        if (ParentContinuation != checkpoint.ContinuationIdentity)
        {
            throw new ArgumentException(
                "The aggregate receipt belongs to another parent Process attempt.",
                nameof(checkpoint));
        }

        var validated = Create(
            planSet,
            ParentContinuation,
            Outcome,
            Leaves,
            ReadyBarrier,
            CompletedAtUtc);
        if (validated != this)
            throw new ArgumentException("The aggregate receipt is not in canonical contextual form.", nameof(planSet));

        ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> retainedLeaves;
        if (ReadyBarrier is null)
        {
            retainedLeaves = PlanSetProjection.ProjectBuildLeaves(planSet, checkpoint, out var allReady);
            if (allReady)
            {
                throw new ArgumentException(
                    "An all-ready parent child ledger requires its exact readiness barrier.",
                    nameof(checkpoint));
            }
        }
        else
        {
            ReadyBarrier.ValidateAgainst(planSet, parentPlan, checkpoint);
            retainedLeaves = PlanSetProjection.ProjectPromotionLeaves(planSet, ReadyBarrier, checkpoint);
        }

        if (!Leaves.SequenceEqual(retainedLeaves))
        {
            throw new ArgumentException(
                "The aggregate receipt differs from the exact retained parent child ledgers.",
                nameof(checkpoint));
        }
    }

    /// <summary>Compares receipts by complete canonical aggregate evidence.</summary>
    /// <param name="other">Receipt to compare.</param>
    /// <returns><see langword="true"/> when every durable field is structurally equal.</returns>
    public bool Equals(MaterializationRebuildPlanSetReceipt? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && PlanSet == other.PlanSet
        && ParentContinuation == other.ParentContinuation
        && Outcome == other.Outcome
        && Leaves.SequenceEqual(other.Leaves)
        && ReadyBarrier == other.ReadyBarrier
        && CompletedAtUtc == other.CompletedAtUtc;

    /// <summary>Returns a structural hash code for complete aggregate evidence.</summary>
    /// <returns>A hash code consistent with structural equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(PlanSet);
        hash.Add(ParentContinuation);
        hash.Add(Outcome);
        foreach (var leaf in Leaves)
            hash.Add(leaf);
        hash.Add(ReadyBarrier);
        hash.Add(CompletedAtUtc);
        return hash.ToHashCode();
    }
}

/// <summary>Strict canonical JSON persistence for <see cref="MaterializationRebuildPlanSetReceipt"/>.</summary>
public static class MaterializationRebuildPlanSetReceiptJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact aggregate receipt to canonical compact JSON.</summary>
    /// <param name="receipt">Exact durable aggregate receipt.</param>
    /// <returns>Canonical JSON preserving plan-set, child, readiness, routing, and failure evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The receipt cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The receipt has no canonical JSON representation.</exception>
    public static string Serialize(MaterializationRebuildPlanSetReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(receipt, Options));
    }

    /// <summary>
    /// Deserializes and structurally validates an aggregate receipt without resolving its complete plan-set or parent
    /// child-ledger context.
    /// </summary>
    /// <param name="json">Strict canonical receipt JSON.</param>
    /// <returns>
    /// The structurally validated receipt. Call <see cref="MaterializationRebuildPlanSetReceipt.ValidateAgainst"/>
    /// before consuming its aggregate outcome as contextual evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical, open, or structurally invalid.</exception>
    public static MaterializationRebuildPlanSetReceipt DeserializeStructural(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                "materialization rebuild plan-set receipt",
                out MaterializationRebuildPlanSetReceipt? receipt,
                out var error)
            && receipt is not null)
        {
            return receipt;
        }

        throw new JsonException(error.Message);
    }
}
