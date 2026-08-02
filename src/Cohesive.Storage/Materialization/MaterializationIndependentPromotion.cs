using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

static class MaterializationIndependentPromotionIntentProtocol
{
    internal const string Version = "v2";
    internal const string RequestSchema =
        "cohesive-materialization-independent-promotion-request/" + Version;
    internal const string CommandPrefix =
        "materialization-independent-promotion/" + Version;
    internal const string CommandDigestDomain =
        "cohesive-materialization-independent-promotion-command/" + Version;
    internal const string ConfigurationAuthorityPrefix =
        "cohesive.storage/materialization-independent-promotion/configuration/" + Version + "/plan-set/";
}

/// <summary>Durable exact intent to expose one independently promoted rebuild leaf through its backend pool.</summary>
/// <remarks>
/// Persist this request before invoking the executor. Exact recovery resubmits the same command identities,
/// revisions, fences, and timestamps; it must not reconstruct them from a later routing snapshot. A coordinator
/// that deliberately refreshes a rejected attempt from a later routing snapshot must create and persist a new
/// request, whose command identities include that attempt's revision, fence, and timestamps.
/// </remarks>
public sealed record MaterializationIndependentPromotionRequest
{
    /// <summary>Current portable independent-promotion request schema.</summary>
    public const string CurrentSchemaVersion = MaterializationIndependentPromotionIntentProtocol.RequestSchema;

    /// <summary>Creates one exact, replayable independent-promotion intent.</summary>
    /// <param name="schemaVersion">Exact portable request schema.</param>
    /// <param name="activeGeneration">Exact successful target activation evidence.</param>
    /// <param name="configuration">Exact resolved paired-routing configuration retained for replay.</param>
    /// <param name="expectedRoutingRevision">Placement-scoped revision observed before candidate admission.</param>
    /// <param name="fence">Placement-scoped routing authority fence.</param>
    /// <param name="admitCommandId">Stable candidate-admission command identity.</param>
    /// <param name="swapCommandId">Stable paired read/write swap command identity.</param>
    /// <param name="admitIssuedAtUtc">UTC candidate-admission issuance time retained on replay.</param>
    /// <param name="swapIssuedAtUtc">UTC route-swap issuance time retained on replay.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, affinity, identities, revision, fence, or timestamps are invalid.</exception>
    [JsonConstructor]
    public MaterializationIndependentPromotionRequest(
        string schemaVersion,
        MaterializationActiveGenerationReference activeGeneration,
        MaterializationBackendRoutingConfiguration configuration,
        MaterializationBackendRoutingRevision expectedRoutingRevision,
        MaterializationBackendRoutingFence fence,
        MaterializationBackendRoutingCommandId admitCommandId,
        MaterializationBackendRoutingCommandId swapCommandId,
        DateTimeOffset admitIssuedAtUtc,
        DateTimeOffset swapIssuedAtUtc)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Independent-promotion schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        ActiveGeneration = activeGeneration ?? throw new ArgumentNullException(nameof(activeGeneration));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        MaterializationContract.RequireDefinedIdentity(expectedRoutingRevision.Value, nameof(expectedRoutingRevision));
        if (expectedRoutingRevision.Ordinal == long.MaxValue)
        {
            throw new ArgumentException(
                "Independent promotion requires revision space for candidate admission and its exact follow-up swap.",
                nameof(expectedRoutingRevision));
        }
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireDefinedIdentity(admitCommandId.Value, nameof(admitCommandId));
        MaterializationContract.RequireDefinedIdentity(swapCommandId.Value, nameof(swapCommandId));
        MaterializationContract.RequireUtc(admitIssuedAtUtc, nameof(admitIssuedAtUtc));
        MaterializationContract.RequireUtc(swapIssuedAtUtc, nameof(swapIssuedAtUtc));
        if (admitCommandId == swapCommandId)
            throw new ArgumentException("Admission and route swap require distinct command identities.", nameof(swapCommandId));
        if (swapIssuedAtUtc < admitIssuedAtUtc)
            throw new ArgumentException("The route swap cannot predate candidate admission.", nameof(swapIssuedAtUtc));
        if (admitIssuedAtUtc < activeGeneration.ActivatedAtUtc)
            throw new ArgumentException("Candidate admission cannot predate target activation.", nameof(admitIssuedAtUtc));
        RequireConfiguration(
            configuration,
            activeGeneration.Authority.PlanSet,
            activeGeneration.Authority.PlacementSlice);

        ExpectedRoutingRevision = expectedRoutingRevision;
        Fence = fence;
        AdmitCommandId = admitCommandId;
        SwapCommandId = swapCommandId;
        AdmitIssuedAtUtc = admitIssuedAtUtc;
        SwapIssuedAtUtc = swapIssuedAtUtc;
    }

    /// <summary>Exact portable request schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact successful target activation evidence.</summary>
    public MaterializationActiveGenerationReference ActiveGeneration { get; }

    /// <summary>Exact linked plan-set, leaf-plan, and placement authority intrinsic to the activation evidence.</summary>
    [JsonIgnore]
    public MaterializationRebuildLeafExecutionAuthority Authority => ActiveGeneration.Authority;

    /// <summary>Exact resolved paired-routing configuration retained rather than recomputed during recovery.</summary>
    public MaterializationBackendRoutingConfiguration Configuration { get; }

    /// <summary>Placement-scoped revision observed before candidate admission.</summary>
    public MaterializationBackendRoutingRevision ExpectedRoutingRevision { get; }

    /// <summary>Placement-scoped routing authority fence.</summary>
    public MaterializationBackendRoutingFence Fence { get; }

    /// <summary>Stable candidate-admission command identity.</summary>
    public MaterializationBackendRoutingCommandId AdmitCommandId { get; }

    /// <summary>Stable paired read/write swap command identity.</summary>
    public MaterializationBackendRoutingCommandId SwapCommandId { get; }

    /// <summary>UTC candidate-admission issuance time retained on replay.</summary>
    public DateTimeOffset AdmitIssuedAtUtc { get; }

    /// <summary>UTC route-swap issuance time retained on replay.</summary>
    public DateTimeOffset SwapIssuedAtUtc { get; }

    static void RequireConfiguration(
        MaterializationBackendRoutingConfiguration configuration,
        MaterializationRebuildPlanSetReference planSet,
        MaterializationPlacementSliceReference placementSlice)
    {
        var expectedAuthority = MaterializationIndependentPromotionExecutor.ConfigurationAuthority(planSet);
        if (configuration.ReadTarget != placementSlice.Target
            || configuration.WriteTarget != placementSlice.Target
            || configuration.Configuration.Any(decision =>
                decision.Origin != EffectiveConfigurationOrigin.Explicit
                || !string.Equals(decision.Authority, expectedAuthority, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Independent promotion requires exact paired placement routing attributed to its plan set.",
                nameof(configuration));
        }
    }
}

/// <summary>Strict canonical JSON persistence for <see cref="MaterializationIndependentPromotionRequest"/>.</summary>
public static class MaterializationIndependentPromotionRequestJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact independent-promotion request to canonical JSON.</summary>
    /// <param name="request">Exact durable promotion intent.</param>
    /// <returns>Canonical JSON preserving the complete replay authority.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The request cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The request contains a value unsupported by the configured converters.</exception>
    /// <exception cref="InvalidOperationException">The request has no portable canonical representation.</exception>
    public static string Serialize(MaterializationIndependentPromotionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(request, Options));
    }

    /// <summary>Deserializes and validates one exact independent-promotion request.</summary>
    /// <param name="json">Strict JSON document containing the retained promotion intent.</param>
    /// <returns>The constructor-validated request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical in shape, or violates the request contract.</exception>
    public static MaterializationIndependentPromotionRequest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                "independent-promotion request",
                out MaterializationIndependentPromotionRequest? request,
                out var error)
            && request is not null)
        {
            return request;
        }

        throw new JsonException(error.Message);
    }
}

/// <summary>Durable per-slice receipt for one exact independent promotion realization.</summary>
public sealed record MaterializationIndependentPromotionResult
{
    /// <summary>Current portable independent-promotion result schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-independent-promotion-result/v1";

    /// <summary>Creates one exact, replayable independent-promotion result.</summary>
    /// <param name="schemaVersion">Exact portable result schema.</param>
    /// <param name="request">Exact persisted promotion intent that produced this result.</param>
    /// <param name="admission">Placement-scoped candidate-admission outcome.</param>
    /// <param name="routing">Placement-scoped paired-routing outcome, or null when admission was rejected.</param>
    /// <exception cref="ArgumentNullException">A required value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema, placement, generation, command receipt, or admission/routing chronology is inexact.
    /// </exception>
    [JsonConstructor]
    public MaterializationIndependentPromotionResult(
        string schemaVersion,
        MaterializationIndependentPromotionRequest request,
        MaterializationBackendRoutingResult admission,
        MaterializationBackendRoutingResult? routing)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Independent-promotion result schema '{schemaVersion}' is unsupported.",
                nameof(schemaVersion));
        }

        Request = request ?? throw new ArgumentNullException(nameof(request));
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
        Routing = routing;
        MaterializationBackendGenerationReference generation = new(
            targetId: request.Authority.PlacementSlice.Target,
            generationId: request.ActiveGeneration.Generation,
            definitionFingerprint: request.Authority.PlacementSlice.Materialization.DefinitionFingerprint);
        if (admission.Snapshot.PlacementSlice != request.Authority.PlacementSlice
            || admission.Receipt is { } admissionReceipt
            && (admissionReceipt.CommandId != request.AdmitCommandId
                || admissionReceipt.Operation != MaterializationBackendRoutingOperation.AdmitCandidate
                || admissionReceipt.Fence != request.Fence)
            || routing is not null
            && (admission.Disposition is not (MaterializationBackendRoutingDisposition.Applied
                    or MaterializationBackendRoutingDisposition.Replayed)
                || routing.Snapshot.PlacementSlice != request.Authority.PlacementSlice
                || routing.Receipt is { } routingReceipt
                && (routingReceipt.CommandId != request.SwapCommandId
                    || routingReceipt.Operation != MaterializationBackendRoutingOperation.Swap
                    || routingReceipt.Fence != request.Fence)))
        {
            throw new ArgumentException(
                "Independent-promotion results must retain the exact placement, commands, and ordered routing outcomes.");
        }
        if (admission.Disposition == MaterializationBackendRoutingDisposition.Applied
            && admission.Snapshot.Candidate != generation
            || routing?.Disposition == MaterializationBackendRoutingDisposition.Applied
            && (routing!.Snapshot.ActiveRead?.Generation != generation
                || routing.Snapshot.ActiveWrite != generation))
        {
            throw new ArgumentException(
                "Independent-promotion result generation evidence differs from its exact retained request.");
        }
    }

    /// <summary>Exact portable result schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact persisted independent-promotion intent.</summary>
    public MaterializationIndependentPromotionRequest Request { get; }

    /// <summary>Exact independently promoted placement authority.</summary>
    [JsonIgnore]
    public MaterializationPlacementSliceReference PlacementSlice => Request.Authority.PlacementSlice;

    /// <summary>Exact backend generation proposed for paired routing.</summary>
    [JsonIgnore]
    public MaterializationBackendGenerationReference Generation => new(
        targetId: PlacementSlice.Target,
        generationId: Request.ActiveGeneration.Generation,
        definitionFingerprint: PlacementSlice.Materialization.DefinitionFingerprint);

    /// <summary>Placement-scoped candidate-admission outcome.</summary>
    public MaterializationBackendRoutingResult Admission { get; }

    /// <summary>Placement-scoped paired-routing outcome, or <see langword="null"/> when admission was rejected.</summary>
    public MaterializationBackendRoutingResult? Routing { get; }

    /// <summary>Whether the resulting current snapshot still selects this generation for both reads and writes.</summary>
    public bool IsCurrentlySelected =>
        Routing?.Snapshot.ActiveRead?.Generation == Generation
        && Routing.Snapshot.ActiveWrite == Generation;
}

/// <summary>Strict canonical JSON persistence for <see cref="MaterializationIndependentPromotionResult"/>.</summary>
public static class MaterializationIndependentPromotionResultJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact independent-promotion result to canonical JSON.</summary>
    /// <param name="result">Exact durable result.</param>
    /// <returns>Canonical JSON retaining the intent and both routing outcomes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The result cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The result has no canonical JSON representation.</exception>
    public static string Serialize(MaterializationIndependentPromotionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(result, Options));
    }

    /// <summary>Deserializes and validates one exact independent-promotion result.</summary>
    /// <param name="json">Strict canonical result JSON.</param>
    /// <returns>The constructor-validated durable result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical, open, or inexact.</exception>
    public static MaterializationIndependentPromotionResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                "independent-promotion result",
                out MaterializationIndependentPromotionResult? result,
                out var error)
            && result is not null)
        {
            return result;
        }

        throw new JsonException(error.Message);
    }
}

/// <summary>Storage-owned one-leaf interpretation of independent plan-set promotion semantics.</summary>
public sealed class MaterializationIndependentPromotionExecutor
{
    const string CommandPrefix = MaterializationIndependentPromotionIntentProtocol.CommandPrefix;
    readonly MaterializationRebuildPlanSet planSet;
    readonly MaterializationRebuildLeafExecutionAuthority authority;

    MaterializationRebuildLeafPlanBinding Binding => authority.Binding;

    /// <summary>Creates an executor for one exact linked leaf in an independent-promotion plan set.</summary>
    /// <param name="planSet">Canonical linked plan set.</param>
    /// <param name="leafPlan">Exact leaf plan embedded by one plan-set binding.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan set is not independent or the leaf is detached, substituted, or inexact.</exception>
    public MaterializationIndependentPromotionExecutor(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlan leafPlan)
        : this(
            planSet,
            MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, leafPlan))
    {
    }

    /// <summary>
    /// Creates an executor from one exact linked-leaf authority without reloading target-independent leaf content.
    /// </summary>
    /// <param name="planSet">Canonical linked plan set.</param>
    /// <param name="authority">Exact leaf binding claimed against <paramref name="planSet"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The plan set is not independent, or <paramref name="authority"/> is detached, substituted, or inexact.
    /// </exception>
    public MaterializationIndependentPromotionExecutor(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildLeafExecutionAuthority authority)
    {
        this.planSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        if (planSet.Promotion.Mode != MaterializationRebuildPromotionMode.Independent)
            throw new ArgumentException("This executor realizes only explicitly independent promotion demand.", nameof(planSet));

        MaterializationRebuildLeafExecutionAuthority expected;
        try
        {
            expected = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, authority.Binding);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The leaf execution authority is detached from the exact linked plan set.",
                nameof(authority),
                exception);
        }
        if (authority != expected
            || authority.PlacementSlice.Pool != planSet.Placement.Pool
            || authority.PlacementSlice.Materialization != planSet.Placement.Materialization)
        {
            throw new ArgumentException(
                "The leaf execution authority is detached from the exact linked plan set.",
                nameof(authority));
        }
    }

    /// <summary>Creates a replay-stable promotion request from one pre-admission routing snapshot.</summary>
    /// <param name="activeGeneration">Exact successful activation evidence emitted by this leaf.</param>
    /// <param name="snapshot">Exact placement-scoped routing snapshot observed before admission.</param>
    /// <param name="fence">Placement-scoped routing authority fence.</param>
    /// <param name="issuedAtUtc">Stable UTC issuance boundary retained with the durable request.</param>
    /// <returns>
    /// A canonical request whose command identities are derived from its linked semantic authority and the retained
    /// attempt revision, fence, and timestamps.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Activation or snapshot evidence belongs to another leaf or placement, or an identity or UTC boundary is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="issuedAtUtc"/> cannot reserve the required later swap timestamp.
    /// </exception>
    public MaterializationIndependentPromotionRequest CreateRequest(
        MaterializationActiveGenerationReference activeGeneration,
        MaterializationBackendRoutingSnapshot snapshot,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(activeGeneration);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PlacementSlice != Binding.Slice)
            throw new ArgumentException("The routing snapshot belongs to another placement authority.", nameof(snapshot));
        RequireActiveGeneration(activeGeneration);
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));
        if (issuedAtUtc.Ticks == DateTimeOffset.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issuedAtUtc),
                issuedAtUtc,
                "Independent promotion requires a later timestamp for its exact follow-up swap.");
        }
        var swapIssuedAtUtc = issuedAtUtc.AddTicks(1);
        var commandIdentities = CreateCommandIdentities(
            authority.PlanSet,
            authority.LeafPlan,
            Binding.Slice.Fingerprint,
            activeGeneration,
            snapshot.Revision,
            fence,
            issuedAtUtc,
            swapIssuedAtUtc);
        var configuration = MaterializationBackendRoutingConfigurationResolver.Resolve(
            planSet.Placement.BackendPool.Definition,
            new MaterializationBackendRoutingConfigurationLayer(
                origin: EffectiveConfigurationOrigin.Explicit,
                authority: ConfigurationAuthority(authority.PlanSet),
                settings: new(readTarget: Binding.Slice.Target, writeTarget: Binding.Slice.Target)));
        return new(
            schemaVersion: MaterializationIndependentPromotionRequest.CurrentSchemaVersion,
            activeGeneration,
            configuration,
            expectedRoutingRevision: snapshot.Revision,
            fence,
            admitCommandId: commandIdentities.Admit,
            swapCommandId: commandIdentities.Swap,
            admitIssuedAtUtc: issuedAtUtc,
            swapIssuedAtUtc);
    }

    /// <summary>Admits and exposes one activated leaf using the existing placement-scoped backend router.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Exact persisted request to apply or replay.</param>
    /// <param name="router">Existing backend-pool routing authority.</param>
    /// <returns>Separate per-slice admission and paired-routing outcomes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request is detached, substituted, or belongs to another authority.</exception>
    /// <exception cref="InvalidOperationException">The router returns admission evidence for another revision.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The router has been disposed.</exception>
    public async ValueTask<MaterializationIndependentPromotionResult> ExecuteAsync(
        OperationContext context,
        MaterializationIndependentPromotionRequest request,
        IMaterializationBackendRouter router)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(router);
        ValidateRequest(request);

        MaterializationBackendGenerationReference generation = new(
            targetId: Binding.Slice.Target,
            generationId: request.ActiveGeneration.Generation,
            definitionFingerprint: Binding.Slice.Materialization.DefinitionFingerprint);
        var routingRequest = CreateRoutingRequest(request, generation);
        var admission = await router.AdmitCandidateAsync(
                context,
                new MaterializationAdmitBackendCandidateRequest(
                    header: new(
                        commandId: request.AdmitCommandId,
                        placementSlice: Binding.Slice,
                        expectedRevision: request.ExpectedRoutingRevision,
                        fence: request.Fence,
                        issuedAtUtc: request.AdmitIssuedAtUtc),
                    candidate: generation,
                    expectedFollowUp: routingRequest))
            .ConfigureAwait(false);
        if (admission.Disposition is not (MaterializationBackendRoutingDisposition.Applied
            or MaterializationBackendRoutingDisposition.Replayed))
        {
            return new(
                MaterializationIndependentPromotionResult.CurrentSchemaVersion,
                request,
                admission,
                routing: null);
        }
        if (admission.Receipt!.Revision != routingRequest.Header.ExpectedRevision)
        {
            throw new InvalidOperationException(
                "Candidate admission returned a revision other than the one reserved by its exact follow-up swap.");
        }

        var routing = await router.SwapAsync(context, routingRequest)
            .ConfigureAwait(false);
        return new(
            MaterializationIndependentPromotionResult.CurrentSchemaVersion,
            request,
            admission,
            routing);
    }

    MaterializationSwapBackendRoutingRequest CreateRoutingRequest(
        MaterializationIndependentPromotionRequest request,
        MaterializationBackendGenerationReference generation) =>
        new(
            header: new(
                commandId: request.SwapCommandId,
                placementSlice: Binding.Slice,
                expectedRevision: request.ExpectedRoutingRevision.Next(),
                fence: request.Fence,
                issuedAtUtc: request.SwapIssuedAtUtc),
            read: new(Binding.Slice, generation, request.ActiveGeneration),
            write: generation,
            configuration: request.Configuration);

    void ValidateRequest(MaterializationIndependentPromotionRequest request)
    {
        if (request.Authority != authority)
        {
            throw new ArgumentException("The promotion request belongs to another plan set, leaf, or placement.", nameof(request));
        }
        RequireActiveGeneration(request.ActiveGeneration);
        var commandIdentities = CreateCommandIdentities(
            authority.PlanSet,
            authority.LeafPlan,
            Binding.Slice.Fingerprint,
            request.ActiveGeneration,
            request.ExpectedRoutingRevision,
            request.Fence,
            request.AdmitIssuedAtUtc,
            request.SwapIssuedAtUtc);
        if (request.AdmitCommandId != commandIdentities.Admit
            || request.SwapCommandId != commandIdentities.Swap)
        {
            throw new ArgumentException("The promotion request command identities are not canonically derived.", nameof(request));
        }
    }

    internal static string ConfigurationAuthority(MaterializationRebuildPlanSetReference planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        return MaterializationIndependentPromotionIntentProtocol.ConfigurationAuthorityPrefix
            + MaterializationRebuildIdentities.PlanSetIdentity(planSet);
    }

    internal static (
        MaterializationBackendRoutingCommandId Admit,
        MaterializationBackendRoutingCommandId Swap) CreateCommandIdentities(
        MaterializationRebuildPlanSetReference planSet,
        MaterializationRebuildPlanReference leafPlan,
        MaterializationPlacementSliceFingerprint placementSlice,
        MaterializationActiveGenerationReference activeGeneration,
        MaterializationBackendRoutingRevision expectedRevision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset admitIssuedAtUtc,
        DateTimeOffset swapIssuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(leafPlan);
        ArgumentNullException.ThrowIfNull(placementSlice);
        ArgumentNullException.ThrowIfNull(activeGeneration);
        using MaterializationStableIdentity.DigestBuilder builder = new();
        builder.Append(MaterializationIndependentPromotionIntentProtocol.CommandDigestDomain);
        builder.Append(MaterializationRebuildIdentities.PlanSetIdentity(planSet));
        builder.Append(MaterializationStableIdentity.Digest(
            leafPlan.Plan.Algorithm,
            leafPlan.Plan.Canonicalization,
            leafPlan.Plan.Value));
        builder.Append(MaterializationStableIdentity.Digest(
            placementSlice.Algorithm,
            placementSlice.Canonicalization,
            placementSlice.Value));
        builder.Append(activeGeneration.Generation.Value);
        builder.Append(activeGeneration.TargetRevision.Value);
        builder.Append(activeGeneration.Promotion.Value);
        builder.Append(activeGeneration.PromotionFence.Value);
        builder.Append(activeGeneration.Validation.Value);
        builder.Append(activeGeneration.ActivatedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(expectedRevision.Value);
        builder.Append(fence.Value);
        builder.Append(admitIssuedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(swapIssuedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var digest = builder.Complete();
        return (
            new($"{CommandPrefix}/admit/{digest}"),
            new($"{CommandPrefix}/swap/{digest}"));
    }

    void RequireActiveGeneration(MaterializationActiveGenerationReference activeGeneration)
    {
        if (activeGeneration.Plan != authority.LeafPlan.Plan
            || activeGeneration.Authority != authority
            || activeGeneration.Materialization != authority.PlacementSlice.Materialization.Materialization
            || activeGeneration.Target != Binding.Slice.Target)
        {
            throw new ArgumentException(
                "Active-generation evidence does not belong to the exact linked leaf and placement.",
                nameof(activeGeneration));
        }
    }
}
