using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Supplies exact equivalence evidence required before a progressive promotion step may be compensated.</summary>
/// <remarks>
/// Compensation is an explicit later routing operation, not an atomic rollback claim. Implementations should return
/// <see langword="null"/> when equivalence cannot be proven at the supplied current routing revision.
/// </remarks>
public interface IMaterializationProgressiveCompensationProofProvider
{
    /// <summary>Attempts to prove that the prior route retained by a committed promotion may be restored.</summary>
    /// <param name="context">Operation context carrying cancellation and tracing.</param>
    /// <param name="promotion">Exact committed forward-promotion evidence.</param>
    /// <param name="current">Current placement routing snapshot against which the proof must be fenced.</param>
    /// <returns>Exact rollback evidence, or <see langword="null"/> when equivalence is not currently established.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationBackendRollbackProof?> ResolveAsync(
        OperationContext context,
        MaterializationIndependentPromotionResult promotion,
        MaterializationBackendRoutingSnapshot current);
}

/// <summary>Exact persisted intent for one explicit progressive-promotion compensation operation.</summary>
public sealed record MaterializationProgressivePromotionCompensationRequest
{
    /// <summary>Current portable compensation-request schema.</summary>
    public const string CurrentSchemaVersion =
        "cohesive-materialization-progressive-promotion-compensation-request/v1";

    /// <summary>Creates one replay-stable compensation intent.</summary>
    /// <param name="schemaVersion">Exact portable request schema.</param>
    /// <param name="promotion">Exact committed forward-promotion result being compensated.</param>
    /// <param name="read">Prior read route to restore.</param>
    /// <param name="write">Prior write route to restore.</param>
    /// <param name="configuration">Prior attributable route configuration to restore.</param>
    /// <param name="proof">Current-revision equivalence evidence authorizing the restoration.</param>
    /// <param name="fence">Placement-scoped routing authority fence.</param>
    /// <param name="commandId">Stable compensation swap command identity.</param>
    /// <param name="issuedAtUtc">Stable UTC command issuance boundary.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema, route, proof, command, fence, or chronology is inexact.</exception>
    [JsonConstructor]
    public MaterializationProgressivePromotionCompensationRequest(
        string schemaVersion,
        MaterializationIndependentPromotionResult promotion,
        MaterializationReadableBackendReference read,
        MaterializationBackendGenerationReference write,
        MaterializationBackendRoutingConfiguration configuration,
        MaterializationBackendRollbackProof proof,
        MaterializationBackendRoutingFence fence,
        MaterializationBackendRoutingCommandId commandId,
        DateTimeOffset issuedAtUtc)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Progressive compensation schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        Read = read ?? throw new ArgumentNullException(nameof(read));
        Write = write ?? throw new ArgumentNullException(nameof(write));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Proof = proof ?? throw new ArgumentNullException(nameof(proof));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireDefinedIdentity(commandId.Value, nameof(commandId));
        MaterializationContract.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));

        var prior = promotion.Admission.Snapshot;
        var current = promotion.Routing?.Snapshot;
        if (!promotion.IsCurrentlySelected
            || prior.ActiveRead != read
            || prior.ActiveWrite != write
            || prior.Configuration != configuration
            || current is null
            || proof.PlacementSlice != promotion.PlacementSlice
            || proof.Generation != read.Generation
            || write != read.Generation
            || proof.CurrentRead != current.ActiveRead
            || proof.CurrentWrite != current.ActiveWrite
            || proof.ExpectedRoutingRevision != current.Revision
            || configuration.ReadTarget != read.Generation.TargetId
            || configuration.WriteTarget != write.TargetId
            || issuedAtUtc < proof.ObservedAtUtc)
        {
            throw new ArgumentException(
                "Progressive compensation must restore one exact prior paired route from current-revision equivalence evidence.",
                nameof(proof));
        }

        Fence = fence;
        CommandId = commandId;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>Exact portable compensation-request schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact committed forward-promotion result being compensated.</summary>
    public MaterializationIndependentPromotionResult Promotion { get; }

    /// <summary>Prior read route to restore.</summary>
    public MaterializationReadableBackendReference Read { get; }

    /// <summary>Prior write route to restore.</summary>
    public MaterializationBackendGenerationReference Write { get; }

    /// <summary>Prior attributable route configuration to restore.</summary>
    public MaterializationBackendRoutingConfiguration Configuration { get; }

    /// <summary>Current-revision equivalence evidence authorizing the restoration.</summary>
    public MaterializationBackendRollbackProof Proof { get; }

    /// <summary>Placement-scoped routing authority fence.</summary>
    public MaterializationBackendRoutingFence Fence { get; }

    /// <summary>Stable compensation swap command identity.</summary>
    public MaterializationBackendRoutingCommandId CommandId { get; }

    /// <summary>Stable UTC command issuance boundary.</summary>
    public DateTimeOffset IssuedAtUtc { get; }
}

/// <summary>Exact result of one explicit progressive-promotion compensation operation.</summary>
public sealed record MaterializationProgressivePromotionCompensationResult
{
    /// <summary>Current portable compensation-result schema.</summary>
    public const string CurrentSchemaVersion =
        "cohesive-materialization-progressive-promotion-compensation-result/v1";

    /// <summary>Creates one exact compensation result.</summary>
    /// <param name="schemaVersion">Exact portable result schema.</param>
    /// <param name="request">Persisted compensation intent.</param>
    /// <param name="routing">Exact placement routing outcome.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema, placement, command receipt, or restored routes are inconsistent.</exception>
    [JsonConstructor]
    public MaterializationProgressivePromotionCompensationResult(
        string schemaVersion,
        MaterializationProgressivePromotionCompensationRequest request,
        MaterializationBackendRoutingResult routing)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Progressive compensation result schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        if (routing.Snapshot.PlacementSlice != request.Promotion.PlacementSlice
            || routing.Receipt is { } receipt
            && (receipt.CommandId != request.CommandId
                || receipt.Fence != request.Fence
                || receipt.Operation != MaterializationBackendRoutingOperation.Swap))
        {
            throw new ArgumentException(
                "Progressive compensation must retain the exact placement, command, fence, and routing outcome.",
                nameof(routing));
        }
    }

    /// <summary>Exact portable compensation-result schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Persisted compensation intent.</summary>
    public MaterializationProgressivePromotionCompensationRequest Request { get; }

    /// <summary>Exact placement routing outcome.</summary>
    public MaterializationBackendRoutingResult Routing { get; }

    /// <summary>Whether the exact prior read and write routes are selected by the resulting snapshot.</summary>
    public bool IsRestored =>
        Routing.Snapshot.ActiveRead == Request.Read
        && Routing.Snapshot.ActiveWrite == Request.Write
        && Routing.Snapshot.Configuration == Request.Configuration;
}

/// <summary>Strict canonical JSON persistence for progressive compensation intents and results.</summary>
public static class MaterializationProgressivePromotionCompensationJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact compensation request.</summary>
    /// <param name="request">Exact persisted compensation intent.</param>
    /// <returns>Canonical compact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The request cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The request contains a value unsupported by the configured converters.</exception>
    /// <exception cref="InvalidOperationException">The request has no portable canonical representation.</exception>
    public static string SerializeRequest(MaterializationProgressivePromotionCompensationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(request, Options));
    }

    /// <summary>Deserializes one exact compensation request.</summary>
    /// <param name="json">Strict canonical request JSON.</param>
    /// <returns>Constructor-validated compensation intent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, open, noncanonical, or invalid.</exception>
    public static MaterializationProgressivePromotionCompensationRequest DeserializeRequest(string json) =>
        Deserialize<MaterializationProgressivePromotionCompensationRequest>(json, "progressive compensation request");

    /// <summary>Serializes one exact compensation result.</summary>
    /// <param name="result">Exact compensation result.</param>
    /// <returns>Canonical compact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The result cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The result contains a value unsupported by the configured converters.</exception>
    /// <exception cref="InvalidOperationException">The result has no portable canonical representation.</exception>
    public static string SerializeResult(MaterializationProgressivePromotionCompensationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(result, Options));
    }

    /// <summary>Deserializes one exact compensation result.</summary>
    /// <param name="json">Strict canonical result JSON.</param>
    /// <returns>Constructor-validated compensation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, open, noncanonical, or invalid.</exception>
    public static MaterializationProgressivePromotionCompensationResult DeserializeResult(string json) =>
        Deserialize<MaterializationProgressivePromotionCompensationResult>(json, "progressive compensation result");

    static T Deserialize<T>(string json, string role)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(json, Options, role, out T? value, out var error)
            && value is not null)
        {
            return value;
        }
        throw new JsonException(error.Message);
    }
}

/// <summary>Storage-owned execution of one explicit progressive-promotion compensation command.</summary>
public sealed class MaterializationProgressivePromotionCompensationExecutor
{
    const string CommandDomain = "cohesive-materialization-progressive-promotion-compensation-command/v1";
    readonly MaterializationRebuildLeafExecutionAuthority authority;

    /// <summary>Creates an executor for one exact leaf in a compensate-on-failure progressive plan set.</summary>
    /// <param name="planSet">Canonical linked plan set.</param>
    /// <param name="authority">Exact linked-leaf authority.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Policy or linked-leaf authority is inexact.</exception>
    public MaterializationProgressivePromotionCompensationExecutor(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildLeafExecutionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        if (planSet.Promotion is not
            {
                Mode: MaterializationRebuildPromotionMode.AllReadyProgressive,
                ProgressiveFailurePolicy: MaterializationProgressivePromotionFailurePolicy.CompensatePromoted
            }
            || MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, authority.Binding) != authority)
        {
            throw new ArgumentException(
                "Compensation requires the exact linked leaf of a compensate-on-failure progressive plan set.",
                nameof(planSet));
        }
    }

    /// <summary>Creates a replay-stable compensation intent from exact forward and rollback evidence.</summary>
    /// <param name="promotion">Exact successful forward-promotion result.</param>
    /// <param name="proof">Current-revision equivalence evidence for the prior route.</param>
    /// <param name="fence">Next placement-scoped routing fence.</param>
    /// <param name="issuedAtUtc">Stable UTC command issuance boundary.</param>
    /// <returns>Exact persisted compensation intent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Forward, prior-route, or proof evidence is inexact.</exception>
    public MaterializationProgressivePromotionCompensationRequest CreateRequest(
        MaterializationIndependentPromotionResult promotion,
        MaterializationBackendRollbackProof proof,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        ArgumentNullException.ThrowIfNull(proof);
        if (promotion.Request.Authority != authority)
            throw new ArgumentException("Forward promotion belongs to another exact linked leaf.", nameof(promotion));
        var prior = promotion.Admission.Snapshot;
        if (prior.ActiveRead is not { } read
            || prior.ActiveWrite is not { } write
            || prior.Configuration is not { } configuration)
        {
            throw new ArgumentException("The committed forward step has no initialized prior paired route to restore.", nameof(promotion));
        }
        var commandId = CommandIdentity(promotion, proof, fence, issuedAtUtc);
        return new(
            schemaVersion: MaterializationProgressivePromotionCompensationRequest.CurrentSchemaVersion,
            promotion: promotion,
            read: read,
            write: write,
            configuration: configuration,
            proof: proof,
            fence: fence,
            commandId: commandId,
            issuedAtUtc: issuedAtUtc);
    }

    /// <summary>Applies or exactly replays one persisted compensation command.</summary>
    /// <param name="context">Operation context carrying cancellation and tracing.</param>
    /// <param name="request">Exact persisted compensation intent.</param>
    /// <param name="router">Placement routing authority.</param>
    /// <returns>Exact routing outcome without claiming atomic rollback.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request is not canonically linked or derived.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<MaterializationProgressivePromotionCompensationResult> ExecuteAsync(
        OperationContext context,
        MaterializationProgressivePromotionCompensationRequest request,
        IMaterializationBackendRouter router)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(router);
        if (request.Promotion.Request.Authority != authority
            || request.CommandId != CommandIdentity(
                request.Promotion,
                request.Proof,
                request.Fence,
                request.IssuedAtUtc))
        {
            throw new ArgumentException("Compensation request is detached or not canonically derived.", nameof(request));
        }
        var routing = await router.SwapAsync(
            context,
            new(
                header: new(
                    commandId: request.CommandId,
                    placementSlice: authority.PlacementSlice,
                    expectedRevision: request.Proof.ExpectedRoutingRevision,
                    fence: request.Fence,
                    issuedAtUtc: request.IssuedAtUtc),
                read: request.Read,
                write: request.Write,
                configuration: request.Configuration,
                rollback: request.Proof)).ConfigureAwait(false);
        return new(
            schemaVersion: MaterializationProgressivePromotionCompensationResult.CurrentSchemaVersion,
            request: request,
            routing: routing);
    }

    static MaterializationBackendRoutingCommandId CommandIdentity(
        MaterializationIndependentPromotionResult promotion,
        MaterializationBackendRollbackProof proof,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc)
    {
        using MaterializationStableIdentity.DigestBuilder builder = new();
        builder.Append(CommandDomain);
        builder.Append(MaterializationRebuildIdentities.PlanSetIdentity(promotion.Request.Authority.PlanSet));
        builder.Append(promotion.Request.Authority.PlacementSlice.Fingerprint.Value);
        builder.Append(promotion.Request.SwapCommandId.Value);
        builder.Append(proof.ExpectedRoutingRevision.Value);
        builder.Append(proof.EquivalenceFingerprint);
        builder.Append(fence.Value);
        builder.Append(issuedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new($"materialization-progressive-promotion/compensate/v1/{builder.Complete()}");
    }
}
