using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted while compiling authoritative current-state delivery.</summary>
public static class MaterializationCurrentStateEnrichmentDiagnosticCodes
{
    /// <summary>No change-delivery evidence satisfies the semantic input requirement.</summary>
    public const string ChangeDeliveryUnavailable = "materialization.currentState.changeDeliveryUnavailable";

    /// <summary>A partial change image cannot be completed by a bounded authoritative point read.</summary>
    public const string CurrentStateReadUnavailable = "materialization.currentState.readUnavailable";
}

/// <summary>Closed realizations for obtaining authoritative current observations from change delivery.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationCurrentStateEnrichmentStrategyKind
{
    /// <summary>The selected change delivery already carries a complete authoritative logical observation.</summary>
    DeliveredChangeImage = 0,

    /// <summary>Distinct changed identities are reconciled through bounded authoritative point-read batches.</summary>
    BatchedIdentityRead = 1
}

/// <summary>Consistency boundary proved by one current-state enrichment realization.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationCurrentStateConsistencyKind
{
    /// <summary>The complete observation is the image attributed to the delivered source change.</summary>
    ChangePositioned = 0,

    /// <summary>
    /// The complete observation is the latest state returned by an independent authoritative read after delivery.
    /// </summary>
    /// <remarks>
    /// This realization intentionally makes no coordinated-snapshot claim with the source change position. Repeated
    /// baseline-plus-catch-up reconciliation converges to the authoritative current state.
    /// </remarks>
    ReconciledLatest = 1
}

/// <summary>Explicit finite policy used to compile aggregate current-state enrichment.</summary>
public sealed record MaterializationCurrentStateEnrichmentPolicy
{
    /// <summary>Creates bounded enrichment policy.</summary>
    /// <param name="maximumIdentitiesPerRead">Hard maximum distinct subjects in one enrichment read.</param>
    /// <param name="maximumReadBytes">Hard maximum encoded bytes returned by one enrichment read.</param>
    /// <param name="evidenceReference">Stable policy or compiler evidence reference.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="evidenceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="evidenceReference"/> is empty or white space.</exception>
    public MaterializationCurrentStateEnrichmentPolicy(
        long maximumIdentitiesPerRead,
        long maximumReadBytes,
        string evidenceReference)
    {
        MaximumIdentitiesPerRead = MaterializationContract.RequirePortablePositiveBound(
            maximumIdentitiesPerRead,
            nameof(maximumIdentitiesPerRead));
        MaximumReadBytes = MaterializationContract.RequirePortablePositiveBound(
            maximumReadBytes,
            nameof(maximumReadBytes));
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
    }

    /// <summary>Hard maximum distinct subjects in one enrichment read.</summary>
    public long MaximumIdentitiesPerRead { get; }

    /// <summary>Hard maximum encoded bytes returned by one enrichment read.</summary>
    public long MaximumReadBytes { get; }

    /// <summary>Stable policy or compiler evidence reference.</summary>
    public string EvidenceReference { get; }
}

/// <summary>Portable compiled realization of authoritative current-state change delivery.</summary>
public sealed record MaterializationCurrentStateEnrichmentPlan
{
    /// <summary>Current portable plan schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-current-state-enrichment/v1";

    /// <summary>Creates one exact compiled current-state realization.</summary>
    /// <param name="schemaVersion">Exact portable plan schema version.</param>
    /// <param name="input">Canonical Relations acquisition input whose changes are completed.</param>
    /// <param name="shape">Exact complete logical observation shape.</param>
    /// <param name="source">Exact physical source instance supplying changes and reads.</param>
    /// <param name="strategy">Selected native or composed realization.</param>
    /// <param name="consistency">Consistency boundary proved by the realization.</param>
    /// <param name="signalEvidence">Raw source change-delivery evidence selected as the change signal.</param>
    /// <param name="effectiveChangeEvidence">Effective change-delivery evidence exposed after composition.</param>
    /// <param name="currentStateReadEvidence">Point-read evidence used by composed enrichment; otherwise null.</param>
    /// <param name="maximumIdentitiesPerRead">Hard maximum distinct subjects in one composed read.</param>
    /// <param name="maximumReadBytes">Hard maximum encoded bytes in one composed read.</param>
    /// <param name="evidenceReference">Stable compiler evidence reference retained at runtime.</param>
    /// <exception cref="ArgumentException">An identity, strategy, consistency, or evidence combination is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum or bound is unsupported.</exception>
    [JsonConstructor]
    public MaterializationCurrentStateEnrichmentPlan(
        string schemaVersion,
        RelationQueryInputId input,
        QualifiedShapeId shape,
        RelationQuerySourceInstanceId source,
        MaterializationCurrentStateEnrichmentStrategyKind strategy,
        MaterializationCurrentStateConsistencyKind consistency,
        MaterializationCapabilityEvidenceId signalEvidence,
        MaterializationCapabilityEvidenceId effectiveChangeEvidence,
        MaterializationCapabilityEvidenceId? currentStateReadEvidence,
        long maximumIdentitiesPerRead,
        long maximumReadBytes,
        string evidenceReference)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Current-state enrichment schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }
        MaterializationContract.RequireDefinedIdentity(input.Value, nameof(input));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("Current-state enrichment requires a graph-qualified shape.", nameof(shape));
        MaterializationContract.RequireDefinedIdentity(source.Value, nameof(source));
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported current-state enrichment strategy.");
        if (!Enum.IsDefined(consistency))
            throw new ArgumentOutOfRangeException(nameof(consistency), consistency, "Unsupported current-state consistency.");
        MaterializationContract.RequireDefinedIdentity(signalEvidence.Value, nameof(signalEvidence));
        MaterializationContract.RequireDefinedIdentity(effectiveChangeEvidence.Value, nameof(effectiveChangeEvidence));

        var isComposed = strategy == MaterializationCurrentStateEnrichmentStrategyKind.BatchedIdentityRead;
        if (isComposed != currentStateReadEvidence.HasValue)
        {
            throw new ArgumentException(
                "Batched current-state enrichment requires point-read evidence; native change images permit none.",
                nameof(currentStateReadEvidence));
        }
        if (currentStateReadEvidence is { } readEvidence)
            MaterializationContract.RequireDefinedIdentity(readEvidence.Value, nameof(currentStateReadEvidence));
        if (isComposed != (consistency == MaterializationCurrentStateConsistencyKind.ReconciledLatest))
        {
            throw new ArgumentException(
                "Batched identity reads prove reconciled-latest consistency; native images prove change-positioned consistency.",
                nameof(consistency));
        }

        Input = input;
        Shape = shape;
        Source = source;
        Strategy = strategy;
        Consistency = consistency;
        SignalEvidence = signalEvidence;
        EffectiveChangeEvidence = effectiveChangeEvidence;
        CurrentStateReadEvidence = currentStateReadEvidence;
        MaximumIdentitiesPerRead = MaterializationContract.RequirePortablePositiveBound(
            maximumIdentitiesPerRead,
            nameof(maximumIdentitiesPerRead));
        MaximumReadBytes = MaterializationContract.RequirePortablePositiveBound(maximumReadBytes, nameof(maximumReadBytes));
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
    }

    /// <summary>Exact portable plan schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical Relations acquisition input whose changes are completed.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Exact complete logical observation shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Exact physical source instance supplying changes and reads.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Selected native or composed realization.</summary>
    public MaterializationCurrentStateEnrichmentStrategyKind Strategy { get; }

    /// <summary>Consistency boundary proved by the realization.</summary>
    public MaterializationCurrentStateConsistencyKind Consistency { get; }

    /// <summary>Raw source change-delivery evidence selected as the change signal.</summary>
    public MaterializationCapabilityEvidenceId SignalEvidence { get; }

    /// <summary>Effective complete-current-observation change evidence exposed after composition.</summary>
    public MaterializationCapabilityEvidenceId EffectiveChangeEvidence { get; }

    /// <summary>Point-read evidence used by composed enrichment, or null for a native change image.</summary>
    public MaterializationCapabilityEvidenceId? CurrentStateReadEvidence { get; }

    /// <summary>Hard maximum distinct subjects in one composed read.</summary>
    public long MaximumIdentitiesPerRead { get; }

    /// <summary>Hard maximum encoded bytes in one composed read.</summary>
    public long MaximumReadBytes { get; }

    /// <summary>Stable compiler evidence reference retained at runtime.</summary>
    public string EvidenceReference { get; }
}

/// <summary>Successful compiled plan and its effective source profile, or structured failure diagnostics.</summary>
public sealed record MaterializationCurrentStateEnrichmentCompilationResult
{
    /// <summary>Creates one coherent current-state compilation result.</summary>
    /// <param name="plan">Compiled plan on success; otherwise null.</param>
    /// <param name="profile">Effective source profile on success; otherwise null.</param>
    /// <param name="diagnostics">Complete deterministic diagnostics.</param>
    /// <exception cref="ArgumentException">Success, failure, and diagnostics are incoherent.</exception>
    public MaterializationCurrentStateEnrichmentCompilationResult(
        MaterializationCurrentStateEnrichmentPlan? plan,
        MaterializationCapabilityProfile? profile,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        var normalized = MaterializationContract.NormalizeDiagnostics(
            diagnostics.IsDefault ? [] : diagnostics,
            nameof(diagnostics));
        if ((plan is null) != (profile is null))
            throw new ArgumentException("A current-state plan and effective profile are produced together.", nameof(profile));
        if (plan is not null && normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("Successful current-state compilation cannot retain an error.", nameof(diagnostics));
        if (plan is null && normalized.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error))
            throw new ArgumentException("Failed current-state compilation requires an error diagnostic.", nameof(diagnostics));
        Plan = plan;
        Profile = profile;
        Diagnostics = normalized;
    }

    /// <summary>Compiled plan on success; otherwise null.</summary>
    public MaterializationCurrentStateEnrichmentPlan? Plan { get; }

    /// <summary>Effective source capability profile on success; otherwise null.</summary>
    public MaterializationCapabilityProfile? Profile { get; }

    /// <summary>Complete deterministic diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether compilation produced a plan and profile without errors.</summary>
    public bool IsSuccessful => Plan is not null
        && Profile is not null
        && Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

/// <summary>Compiles partial physical changes into authoritative logical current-state delivery.</summary>
public static class MaterializationCurrentStateEnrichmentCompiler
{
    const string CompilationStage = "materialization-current-state-enrichment";

    /// <summary>Compiles a native or bounded composed realization for one logical change input.</summary>
    /// <param name="input">Canonical Relations acquisition input.</param>
    /// <param name="shape">Complete logical observation shape required after enrichment.</param>
    /// <param name="source">Exact physical source instance.</param>
    /// <param name="changeRequirement">Canonical change-delivery requirement for the input.</param>
    /// <param name="profile">Raw or already-composed source capability profile.</param>
    /// <param name="policy">Explicit enrichment read bounds and attribution.</param>
    /// <returns>A deterministic plan and effective profile, or structured fail-closed diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentException">An identity, role, profile subject, or capability is inconsistent.</exception>
    public static MaterializationCurrentStateEnrichmentCompilationResult Compile(
        RelationQueryInputId input,
        QualifiedShapeId shape,
        RelationQuerySourceInstanceId source,
        MaterializationCapabilityRequirement changeRequirement,
        MaterializationCapabilityProfile profile,
        MaterializationCurrentStateEnrichmentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(changeRequirement);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(policy);
        MaterializationContract.RequireDefinedIdentity(input.Value, nameof(input));
        MaterializationContract.RequireDefinedIdentity(source.Value, nameof(source));
        if (changeRequirement.Capability != MaterializationCapabilityKind.SourceChangeDelivery)
            throw new ArgumentException("Current-state compilation requires a change-delivery requirement.", nameof(changeRequirement));
        if (profile.Role != MaterializationEndpointRole.Source
            || !string.Equals(profile.Subject, source.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("Current-state compilation requires the exact source profile.", nameof(profile));
        }

        var changeCandidates = profile.Evidence
            .Where(evidence => evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery
                && MaterializationCapabilityMatcher.Satisfies(changeRequirement, evidence))
            .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (changeCandidates.Length == 0)
        {
            return Failure(
                code: MaterializationCurrentStateEnrichmentDiagnosticCodes.ChangeDeliveryUnavailable,
                message: $"Source '{source.Value}' cannot deliver required changes for input '{input.Value}'.",
                input: input,
                source: source,
                policy: policy,
                expected: changeRequirement.Id.Value,
                observed: "no satisfying change-delivery evidence");
        }

        var native = changeCandidates.FirstOrDefault(static evidence =>
            evidence.Guarantees.Contains(MaterializationGuaranteeKind.CompleteCurrentObservation));
        if (native is not null)
        {
            return new(
                plan: CreatePlan(
                    input: input,
                    shape: shape,
                    source: source,
                    strategy: MaterializationCurrentStateEnrichmentStrategyKind.DeliveredChangeImage,
                    signalEvidence: native,
                    effectiveEvidence: native,
                    readEvidence: null,
                    policy: policy),
                profile: profile);
        }

        var signal = changeCandidates[0];
        var readRequirement = new MaterializationCapabilityRequirement(
            id: new($"{input.Value}/current-state-enrichment/read"),
            capability: MaterializationCapabilityKind.SourceBatchedPointRead,
            guarantees:
            [
                MaterializationGuaranteeKind.StableOrdering,
                MaterializationGuaranteeKind.RequestLocalCompleteness
            ],
            operatingLimits:
            [
                new(MaterializationLimitKind.ReadItems, policy.MaximumIdentitiesPerRead),
                new(MaterializationLimitKind.ReadBytes, policy.MaximumReadBytes)
            ],
            modes: MaterializationSynchronizationMode.Incremental);
        var read = profile.Evidence
            .Where(evidence => evidence.Capability == MaterializationCapabilityKind.SourceBatchedPointRead
                && MaterializationCapabilityMatcher.Satisfies(readRequirement, evidence))
            .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (read is null)
        {
            return Failure(
                code: MaterializationCurrentStateEnrichmentDiagnosticCodes.CurrentStateReadUnavailable,
                message: $"Partial changes for input '{input.Value}' require a complete bounded current-state point read.",
                input: input,
                source: source,
                policy: policy,
                expected: $"{MaterializationCapabilityKind.SourceBatchedPointRead}:{policy.MaximumIdentitiesPerRead}/{policy.MaximumReadBytes}",
                observed: "no satisfying point-read evidence");
        }

        MaterializationCapabilityEvidence effective = new(
            id: new($"{signal.Id.Value}/complete-current-observation/v1"),
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            realization: CapabilityRealizationKind.Composed,
            guarantees: [.. signal.Guarantees, MaterializationGuaranteeKind.CompleteCurrentObservation],
            operatingLimits: signal.OperatingLimits,
            sourceReferences:
            [
                .. signal.SourceReferences
                    .Concat(read.SourceReferences)
                    .Append(policy.EvidenceReference)
                    .Distinct(StringComparer.Ordinal)
            ],
            description: "Raw change delivery is reconciled with complete bounded identity-read batches; each distinct page identity is read once, and the current observation is authoritative but not change-positioned.");
        var effectiveEvidence = profile.Evidence
            .Append(effective)
            .ToImmutableArray();
        MaterializationCapabilityProfile effectiveProfile = new(
            id: new($"{profile.Id.Value}/current-state-enrichment/v1"),
            role: profile.Role,
            subject: profile.Subject,
            evidence: effectiveEvidence,
            description: "Source capabilities after compiled authoritative current-state enrichment.");
        return new(
            plan: CreatePlan(
                input: input,
                shape: shape,
                source: source,
                strategy: MaterializationCurrentStateEnrichmentStrategyKind.BatchedIdentityRead,
                signalEvidence: signal,
                effectiveEvidence: effective,
                readEvidence: read,
                policy: policy),
            profile: effectiveProfile);
    }

    /// <summary>Validates a persisted plan against its exact effective source profile.</summary>
    /// <param name="plan">Persisted plan to link.</param>
    /// <param name="profile">Effective runtime source profile.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The plan evidence is absent, stale, or semantically inconsistent.</exception>
    public static void Link(
        MaterializationCurrentStateEnrichmentPlan plan,
        MaterializationCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Role != MaterializationEndpointRole.Source
            || !string.Equals(profile.Subject, plan.Source.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("Current-state enrichment belongs to another source profile.", nameof(profile));
        }
        var effective = profile.Evidence.SingleOrDefault(evidence => evidence.Id == plan.EffectiveChangeEvidence);
        if (effective is null
            || effective.Capability != MaterializationCapabilityKind.SourceChangeDelivery
            || !effective.Guarantees.Contains(MaterializationGuaranteeKind.CompleteCurrentObservation))
        {
            throw new ArgumentException(
                "The effective source profile does not prove complete current observations for the persisted plan.",
                nameof(profile));
        }
        if (plan.Strategy == MaterializationCurrentStateEnrichmentStrategyKind.DeliveredChangeImage)
        {
            if (plan.SignalEvidence != plan.EffectiveChangeEvidence)
                throw new ArgumentException("Delivered current-state evidence must also be the selected change signal.", nameof(plan));
            return;
        }
        if (profile.Evidence.SingleOrDefault(evidence => evidence.Id == plan.SignalEvidence) is not
            {
                Capability: MaterializationCapabilityKind.SourceChangeDelivery
            })
        {
            throw new ArgumentException("The composed plan's exact raw change-signal evidence is absent.", nameof(profile));
        }
        if (plan.CurrentStateReadEvidence is not { } readId
            || profile.Evidence.SingleOrDefault(evidence => evidence.Id == readId) is not
            {
                Capability: MaterializationCapabilityKind.SourceBatchedPointRead
            } read
            || !read.Guarantees.Contains(MaterializationGuaranteeKind.RequestLocalCompleteness)
            || !read.Guarantees.Contains(MaterializationGuaranteeKind.StableOrdering))
        {
            throw new ArgumentException("The composed plan's exact complete point-read evidence is absent.", nameof(profile));
        }
        MaterializationCapabilityRequirement exactReadRequirement = new(
            id: new($"{plan.Input.Value}/current-state-enrichment/link"),
            capability: MaterializationCapabilityKind.SourceBatchedPointRead,
            guarantees:
            [
                MaterializationGuaranteeKind.StableOrdering,
                MaterializationGuaranteeKind.RequestLocalCompleteness
            ],
            operatingLimits:
            [
                new(MaterializationLimitKind.ReadItems, plan.MaximumIdentitiesPerRead),
                new(MaterializationLimitKind.ReadBytes, plan.MaximumReadBytes)
            ],
            modes: MaterializationSynchronizationMode.Incremental);
        if (!MaterializationCapabilityMatcher.Satisfies(exactReadRequirement, read))
        {
            throw new ArgumentException(
                "The composed plan's exact point-read evidence does not cover its persisted bounds.",
                nameof(profile));
        }
    }

    static MaterializationCurrentStateEnrichmentPlan CreatePlan(
        RelationQueryInputId input,
        QualifiedShapeId shape,
        RelationQuerySourceInstanceId source,
        MaterializationCurrentStateEnrichmentStrategyKind strategy,
        MaterializationCapabilityEvidence signalEvidence,
        MaterializationCapabilityEvidence effectiveEvidence,
        MaterializationCapabilityEvidence? readEvidence,
        MaterializationCurrentStateEnrichmentPolicy policy) => new(
            schemaVersion: MaterializationCurrentStateEnrichmentPlan.CurrentSchemaVersion,
            input: input,
            shape: shape,
            source: source,
            strategy: strategy,
            consistency: strategy == MaterializationCurrentStateEnrichmentStrategyKind.DeliveredChangeImage
                ? MaterializationCurrentStateConsistencyKind.ChangePositioned
                : MaterializationCurrentStateConsistencyKind.ReconciledLatest,
            signalEvidence: signalEvidence.Id,
            effectiveChangeEvidence: effectiveEvidence.Id,
            currentStateReadEvidence: readEvidence?.Id,
            maximumIdentitiesPerRead: policy.MaximumIdentitiesPerRead,
            maximumReadBytes: policy.MaximumReadBytes,
            evidenceReference: policy.EvidenceReference);

    static MaterializationCurrentStateEnrichmentCompilationResult Failure(
        string code,
        string message,
        RelationQueryInputId input,
        RelationQuerySourceInstanceId source,
        MaterializationCurrentStateEnrichmentPolicy policy,
        string expected,
        string observed) => new(
            plan: null,
            profile: null,
            diagnostics:
            [
                MaterializationContract.CreateDiagnostic(
                    code: code,
                    severity: DiagnosticSeverity.Error,
                    message: message,
                    location: $"/sources/{Uri.EscapeDataString(input.Value)}/currentState",
                    stage: CompilationStage,
                    subject: source.Value,
                    sourceReferences: [policy.EvidenceReference],
                    expected: expected,
                    observed: observed,
                    resolutionOptions:
                    [
                        "Advertise complete current observations from the native change image.",
                        "Bind a complete bounded point reader and compile batched identity enrichment."
                    ])
            ]);
}

/// <summary>Runtime source evidence for a compiled composed current-state plan.</summary>
public interface IMaterializationCurrentStateEnrichmentSource
{
    /// <summary>Composed plan implemented by the source, or null when that source view performs no enrichment.</summary>
    MaterializationCurrentStateEnrichmentPlan? CurrentStateEnrichment { get; }
}

/// <summary>Provider-neutral execution of a compiled batched current-state enrichment plan.</summary>
public sealed class MaterializationCurrentStateEnricher
{
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();
    readonly MaterializationObservationReader reader;

    /// <summary>Creates one exact stateless enrichment executor.</summary>
    /// <param name="plan">Persisted composed plan to execute.</param>
    /// <param name="reader">Provider binding for the exact bounded point read.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> does not select batched identity reads.</exception>
    public MaterializationCurrentStateEnricher(
        MaterializationCurrentStateEnrichmentPlan plan,
        MaterializationObservationReader reader)
    {
        Plan = Guard.RequireNotNull(plan);
        this.reader = Guard.RequireNotNull(reader);
        if (plan.Strategy != MaterializationCurrentStateEnrichmentStrategyKind.BatchedIdentityRead)
            throw new ArgumentException("A current-state enricher requires a batched-identity-read plan.", nameof(plan));
    }

    /// <summary>Exact persisted plan implemented by this executor.</summary>
    public MaterializationCurrentStateEnrichmentPlan Plan { get; }

    /// <summary>Replaces partial page images with authoritative current observations or absence.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="request">Original bounded change read request.</param>
    /// <param name="page">Raw source-ordered page whose delivery evidence is preserved.</param>
    /// <returns>An equivalent source page carrying authoritative current logical observations.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The request or page belongs to another compiled source scope.</exception>
    /// <exception cref="InvalidOperationException">The reader returns partial, excessive, duplicate, or unrequested evidence.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<MaterializationChangePage> EnrichAsync(
        OperationContext context,
        MaterializationChangeReadRequest request,
        MaterializationChangePage page)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(page);
        context.ThrowIfCancellationRequested();
        if (request.Scope.Input != Plan.Input
            || request.Scope.Shape != Plan.Shape
            || request.Scope.Source != Plan.Source
            || page.Deliveries.Any(delivery => delivery.Change.Scope != request.Scope))
        {
            throw new ArgumentException("Current-state enrichment requires the exact compiled source scope.", nameof(request));
        }
        if (page.Deliveries.IsDefaultOrEmpty)
            return page;

        HashSet<string> requested = new(StringComparer.Ordinal);
        foreach (var delivery in page.Deliveries)
            requested.Add(delivery.Change.SubjectIdentity);
        ImmutableArray<string> identities = [.. requested.Order(StringComparer.Ordinal)];
        Dictionary<string, RelationQuerySourceReadObservation> current = new(
            requested.Count,
            StringComparer.Ordinal);
        Dictionary<string, string> evidenceByIdentity = new(requested.Count, StringComparer.Ordinal);
        var maximumBatchCount = checked((int)Math.Min(Plan.MaximumIdentitiesPerRead, Array.MaxLength));
        for (var offset = 0; offset < identities.Length; offset += maximumBatchCount)
        {
            context.ThrowIfCancellationRequested();
            var count = Math.Min(maximumBatchCount, identities.Length - offset);
            var keys = identities.Slice(offset, count);
            var result = await reader(
                    context: context,
                    request: new(
                        kind: MaterializationObservationReadKind.IdentityLookup,
                        input: Plan.Input,
                        shape: Plan.Shape,
                        logicalPartition: request.Scope.LogicalPartition,
                        keys: keys,
                        maximumRows: count,
                        maximumBytes: Plan.MaximumReadBytes))
                .ConfigureAwait(false);
            context.ThrowIfCancellationRequested();
            if (result.State is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound))
            {
                throw new InvalidOperationException(
                    $"Current-state enrichment returned '{result.State}' instead of complete evidence ('{result.EvidenceReference}').");
            }

            var keySet = keys.ToHashSet(StringComparer.Ordinal);
            long readBytes = 0;
            foreach (var observation in result.Observations)
            {
                if (!keySet.Contains(observation.Identity))
                    throw new InvalidOperationException("Current-state enrichment returned an unrequested observation.");
                if (observation.Shape != Plan.Shape)
                    throw new InvalidOperationException("Current-state enrichment returned an observation of another shape.");
                if (!current.TryAdd(observation.Identity, observation))
                    throw new InvalidOperationException("Current-state enrichment returned a duplicate observation identity.");
                readBytes = checked(readBytes
                    + StrictDocumentJson.GetCanonicalBytes(observation, CanonicalJsonOptions).LongLength);
                if (readBytes > Plan.MaximumReadBytes)
                    throw new InvalidOperationException("Current-state enrichment exceeded its persisted read-byte bound.");
            }
            foreach (var key in keys)
                evidenceByIdentity.Add(key, result.EvidenceReference ?? "source-read/no-evidence");
        }

        var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(page.Deliveries.Length);
        foreach (var delivery in page.Deliveries)
        {
            var change = delivery.Change;
            current.TryGetValue(change.SubjectIdentity, out var observation);
            MaterializationChangeEnvelope enriched = new(
                id: change.Id,
                subjectIdentity: change.SubjectIdentity,
                scope: change.Scope,
                shape: change.Shape,
                position: change.Position,
                kind: observation is null ? MaterializationChangeKind.Delete : MaterializationChangeKind.Upsert,
                before: change.Before,
                after: observation,
                occurredAtUtc: change.OccurredAtUtc,
                observedAtUtc: change.ObservedAtUtc,
                evidenceReference: string.Concat(
                    change.EvidenceReference ?? "source-change/no-evidence",
                    "|current-state:",
                    Plan.EvidenceReference,
                    "|read:",
                    evidenceByIdentity[change.SubjectIdentity]));
            deliveries.Add(new(
                id: delivery.Id,
                change: enriched,
                deliveredAtUtc: delivery.DeliveredAtUtc,
                evidenceReference: delivery.EvidenceReference));
        }
        return new(
            deliveries: deliveries.MoveToImmutable(),
            throughPosition: page.ThroughPosition,
            state: page.State);
    }
}
