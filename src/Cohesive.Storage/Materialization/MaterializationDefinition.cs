using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable identity of one logical relation-derived materialization across all generations.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationId
{
    /// <summary>Creates a materialization identity.</summary>
    /// <param name="value">Stable identity independent of a source, target, generation, or Process attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Raw stable materialization identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Consistency protocol used to converge one materialized generation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationConsistencyKind
{
    /// <summary>Every participating input is proven to share one coordinated snapshot.</summary>
    CoordinatedSnapshot = 0,

    /// <summary>A bounded baseline is followed by durable change catch-up from an established source cut.</summary>
    BaselinePlusCatchUp = 1,

    /// <summary>Repeated bounded reconciliation converges without claiming one global snapshot.</summary>
    Reconciliation = 2
}

/// <summary>Idempotency and version contract required for target mutations.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationIdempotencyKind
{
    /// <summary>Stable output identity makes repeating equivalent upserts and deletes idempotent.</summary>
    StableOutputIdentity = 0,

    /// <summary>Stable output identity and monotonic semantic version reject stale mutations.</summary>
    StableOutputIdentityAndVersion = 1
}

/// <summary>Terminal disposition after the declared operation-attempt budget is exhausted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationFailureDisposition
{
    /// <summary>Stop the affected generation or incremental partition and require intervention.</summary>
    Stop = 0,

    /// <summary>Durably quarantine the failed item and continue other independent work.</summary>
    QuarantineAndContinue = 1
}

/// <summary>Semantic update and convergence policy shared by rebuild and incremental runs.</summary>
public sealed record MaterializationUpdatePolicy
{
    /// <summary>Creates a materialization update policy.</summary>
    /// <param name="supportedModes">Rebuild, incremental, or both complementary synchronization modes.</param>
    /// <param name="consistency">Declared convergence protocol.</param>
    /// <param name="idempotency">Target idempotency and version requirement.</param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied enum value or mode set is unsupported.</exception>
    [JsonConstructor]
    public MaterializationUpdatePolicy(
        MaterializationSynchronizationMode supportedModes,
        MaterializationConsistencyKind consistency,
        MaterializationIdempotencyKind idempotency)
    {
        MaterializationSynchronizationModes.RequireValid(supportedModes, nameof(supportedModes), allowCombined: true);
        if (!Enum.IsDefined(consistency))
        {
            throw new ArgumentOutOfRangeException(nameof(consistency), consistency, "Unsupported materialization consistency protocol.");
        }

        if (!Enum.IsDefined(idempotency))
        {
            throw new ArgumentOutOfRangeException(nameof(idempotency), idempotency, "Unsupported materialization idempotency contract.");
        }

        SupportedModes = supportedModes;
        Consistency = consistency;
        Idempotency = idempotency;
    }

    /// <summary>Synchronization modes supported by the definition.</summary>
    public MaterializationSynchronizationMode SupportedModes { get; }

    /// <summary>Declared convergence protocol.</summary>
    public MaterializationConsistencyKind Consistency { get; }

    /// <summary>Target idempotency and version requirement.</summary>
    public MaterializationIdempotencyKind Idempotency { get; }
}

/// <summary>Bounded failure policy for source, transformation, and target operations.</summary>
public sealed record MaterializationFailurePolicy
{
    /// <summary>Creates a bounded failure policy.</summary>
    /// <param name="maximumAttempts">Positive total attempt budget, including the initial operation.</param>
    /// <param name="exhaustedDisposition">Disposition after the attempt budget is exhausted.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumAttempts"/> is not positive or <paramref name="exhaustedDisposition"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationFailurePolicy(int maximumAttempts, MaterializationFailureDisposition exhaustedDisposition)
    {
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts), maximumAttempts, "A materialization attempt budget must be positive.");
        }

        if (!Enum.IsDefined(exhaustedDisposition))
        {
            throw new ArgumentOutOfRangeException(nameof(exhaustedDisposition), exhaustedDisposition, "Unsupported exhausted failure disposition.");
        }

        MaximumAttempts = maximumAttempts;
        ExhaustedDisposition = exhaustedDisposition;
    }

    /// <summary>Positive total attempt budget, including the initial operation.</summary>
    public int MaximumAttempts { get; }

    /// <summary>Disposition after the attempt budget is exhausted.</summary>
    public MaterializationFailureDisposition ExhaustedDisposition { get; }
}

/// <summary>End-to-end freshness objectives that remain semantic requirements rather than controller settings.</summary>
public sealed record MaterializationFreshnessPolicy
{
    /// <summary>Creates a freshness policy.</summary>
    /// <param name="maximumLagMilliseconds">Maximum desired age from authoritative change to visible projection.</param>
    /// <param name="maximumUnsettledMilliseconds">Optional maximum desired age of delivered but unsettled work.</param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied duration is not positive.</exception>
    [JsonConstructor]
    public MaterializationFreshnessPolicy(long maximumLagMilliseconds, long? maximumUnsettledMilliseconds = null)
    {
        if (maximumLagMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLagMilliseconds), maximumLagMilliseconds, "Maximum materialization lag must be positive.");
        }

        if (maximumUnsettledMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUnsettledMilliseconds), maximumUnsettledMilliseconds, "Maximum unsettled age must be positive when supplied.");
        }

        MaximumLagMilliseconds = maximumLagMilliseconds;
        MaximumUnsettledMilliseconds = maximumUnsettledMilliseconds;
    }

    /// <summary>Maximum desired end-to-end lag in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumLagMilliseconds { get; }

    /// <summary>Optional maximum desired unsettled age in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MaximumUnsettledMilliseconds { get; }
}

/// <summary>
/// Exact persisted Relations compilation input, plan fence, and selected output from which a materialization derives.
/// </summary>
/// <remarks>
/// The retained compilation request is resolvable durable content. The compiled-plan reference and fingerprint fence
/// that content. The plan's own requirement graph, dependency manifest, and lineage remain the only dependency
/// authorities; this type intentionally contains no Storage-owned copy of those edges.
/// </remarks>
public sealed record MaterializationRelationReference
{
    /// <summary>Creates an exact materialization relation reference.</summary>
    /// <param name="compilationRequest">Exact persisted Relations definition, semantic snapshots, and output demand.</param>
    /// <param name="compiledPlan">Expected exact demand-scoped compiled-plan reference.</param>
    /// <param name="compiledPlanFingerprint">Fingerprint fencing every component of <paramref name="compiledPlan"/>.</param>
    /// <param name="output">Canonical complete relation output selected for materialization.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationRelationReference(
        RelationQueryCompilationRequest compilationRequest,
        RelationQueryCompiledPlanReference compiledPlan,
        RelationQueryPlanComponentFingerprint compiledPlanFingerprint,
        RelationQueryOutputReference output)
    {
        CompilationRequest = Guard.RequireNotNull(compilationRequest);
        CompiledPlan = Guard.RequireNotNull(compiledPlan);
        CompiledPlanFingerprint = Guard.RequireNotNull(compiledPlanFingerprint);
        Output = Guard.RequireNotNull(output);
    }

    /// <summary>Exact persisted Relations definition, semantic snapshots, and output demand.</summary>
    public RelationQueryCompilationRequest CompilationRequest { get; }

    /// <summary>Expected exact demand-scoped compiled-plan reference.</summary>
    public RelationQueryCompiledPlanReference CompiledPlan { get; }

    /// <summary>Fingerprint fencing every component of <see cref="CompiledPlan"/>.</summary>
    public RelationQueryPlanComponentFingerprint CompiledPlanFingerprint { get; }

    /// <summary>Canonical complete relation output selected for materialization.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>Compiles one exact request and selects a canonical output by identity.</summary>
    /// <param name="request">Exact Relations compilation request to retain.</param>
    /// <param name="output">Stable compiled output identity to select.</param>
    /// <returns>An exact reference retaining the request, compiled-plan fence, and selected output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Compilation fails or <paramref name="output"/> is default or absent.</exception>
    /// <exception cref="InvalidOperationException">A semantic snapshot cannot be represented canonically.</exception>
    /// <exception cref="NotSupportedException">A semantic snapshot contains an unsupported serialization type.</exception>
    public static MaterializationRelationReference From(
        RelationQueryCompilationRequest request,
        RelationQueryOutputId output)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(output.Value))
        {
            throw new ArgumentException("A materialization requires a compiled output identity.", nameof(output));
        }

        var compilation = RelationQueryStaticCompiler.Compile(request);
        if (!compilation.IsSuccessful || compilation.Plan is not { } plan)
        {
            throw new ArgumentException(
                string.Join(" ", compilation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(request));
        }

        var selected = plan.RequirementGraph.Outputs.FirstOrDefault(candidate => candidate.Id == output)
            ?? throw new ArgumentException($"Compiled output '{output.Value}' is absent from the exact Relations plan.", nameof(output));
        var reference = RelationQueryCompiledPlanReference.From(plan);
        return new(
            request,
            reference,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(reference),
            selected);
    }

    /// <summary>Recompiles the exact retained Relations request.</summary>
    /// <returns>A plan or structured Relations compilation diagnostics.</returns>
    /// <exception cref="InvalidOperationException">A semantic snapshot cannot be represented canonically.</exception>
    /// <exception cref="NotSupportedException">A semantic snapshot contains an unsupported serialization type.</exception>
    public RelationQueryCompilationResult Compile() => RelationQueryStaticCompiler.Compile(CompilationRequest);
}

/// <summary>Materialization-specific access requirements for one canonical Relations acquisition source.</summary>
public sealed record MaterializationSourceRequirement
{
    /// <summary>Creates a source requirement.</summary>
    /// <param name="input">Canonical source input from the compiled Relations input contract.</param>
    /// <param name="capabilities">Source capabilities required to acquire or observe the input.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default, a requirement is null or duplicated, capability modes overlap, or a
    /// target capability is supplied.
    /// </exception>
    [JsonConstructor]
    public MaterializationSourceRequirement(
        RelationQueryInputId input,
        ImmutableArray<MaterializationCapabilityRequirement> capabilities)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A materialization source requirement requires a Relations input identity.", nameof(input));
        }

        var normalized = capabilities.IsDefault ? [] : capabilities;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static requirement => requirement is null))
        {
            throw new ArgumentException("A materialization source requires one or more non-null capabilities.", nameof(capabilities));
        }

        if (normalized.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A materialization source cannot repeat a capability requirement identity.", nameof(capabilities));
        }

        if (MaterializationCapabilityOrdering.HasOverlappingModes(normalized))
        {
            throw new ArgumentException("A source capability can have at most one requirement for each synchronization mode.", nameof(capabilities));
        }

        if (normalized.Any(static requirement =>
                MaterializationCapabilityCatalog.RoleOf(requirement.Capability) != MaterializationEndpointRole.Source))
        {
            throw new ArgumentException("A materialization source requirement cannot contain target capabilities.", nameof(capabilities));
        }

        Input = input;
        Capabilities = [.. normalized.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)];
    }

    /// <summary>Canonical source input from the compiled Relations input contract.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Source capabilities in canonical requirement-identity order.</summary>
    public ImmutableArray<MaterializationCapabilityRequirement> Capabilities { get; }
}

/// <summary>Canonical backend-independent definition shared by rebuild and incremental synchronization.</summary>
public sealed record MaterializationDefinition
{
    /// <summary>Creates a materialization definition.</summary>
    /// <param name="id">Stable logical materialization identity.</param>
    /// <param name="relation">Exact Relations request, plan fence, and selected output.</param>
    /// <param name="sources">Materialization-specific requirements keyed by canonical Relations acquisition source.</param>
    /// <param name="targetCapabilities">Capabilities required from a selected generation target.</param>
    /// <param name="updatePolicy">Shared synchronization and convergence policy.</param>
    /// <param name="failurePolicy">Bounded failure and terminal-disposition policy.</param>
    /// <param name="freshnessPolicy">End-to-end freshness requirements.</param>
    /// <param name="controlLoops">Optional bounded Control definitions for source, transform, or target stages.</param>
    /// <param name="provenance">Required producer and source attribution.</param>
    /// <param name="controlWorkloads">Explicit rebuild or realtime workload binding for every Control loop.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or collection invariant is invalid.</exception>
    [JsonConstructor]
    public MaterializationDefinition(
        MaterializationId id,
        MaterializationRelationReference relation,
        ImmutableArray<MaterializationSourceRequirement> sources,
        ImmutableArray<MaterializationCapabilityRequirement> targetCapabilities,
        MaterializationUpdatePolicy updatePolicy,
        MaterializationFailurePolicy failurePolicy,
        MaterializationFreshnessPolicy freshnessPolicy,
        ImmutableArray<ControlLoopDefinition> controlLoops,
        ExecutionProvenance provenance,
        ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> controlWorkloads = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A materialization definition requires a stable identity.", nameof(id));
        }

        Id = id;
        Relation = Guard.RequireNotNull(relation);
        UpdatePolicy = Guard.RequireNotNull(updatePolicy);
        FailurePolicy = Guard.RequireNotNull(failurePolicy);
        FreshnessPolicy = Guard.RequireNotNull(freshnessPolicy);
        Provenance = Guard.RequireNotNull(provenance);

        var normalizedSources = sources.IsDefault ? [] : sources;
        if (normalizedSources.IsDefaultOrEmpty || normalizedSources.Any(static source => source is null))
        {
            throw new ArgumentException("A materialization definition requires one or more non-null source requirements.", nameof(sources));
        }

        if (normalizedSources.GroupBy(static source => source.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A materialization definition cannot repeat a Relations input requirement.", nameof(sources));
        }

        Sources = [.. normalizedSources.OrderBy(static source => source.Input.Value, StringComparer.Ordinal)];

        var normalizedTarget = targetCapabilities.IsDefault ? [] : targetCapabilities;
        if (normalizedTarget.IsDefaultOrEmpty || normalizedTarget.Any(static requirement => requirement is null))
        {
            throw new ArgumentException("A materialization definition requires one or more target capabilities.", nameof(targetCapabilities));
        }

        if (normalizedTarget.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A materialization definition cannot repeat a target requirement identity.", nameof(targetCapabilities));
        }

        if (MaterializationCapabilityOrdering.HasOverlappingModes(normalizedTarget))
        {
            throw new ArgumentException("A target capability can have at most one requirement for each synchronization mode.", nameof(targetCapabilities));
        }

        if (normalizedTarget.Any(static requirement =>
                MaterializationCapabilityCatalog.RoleOf(requirement.Capability) != MaterializationEndpointRole.Target))
        {
            throw new ArgumentException("Target requirements cannot contain source capabilities.", nameof(targetCapabilities));
        }
        TargetCapabilities = [.. normalizedTarget.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)];

        var loops = controlLoops.IsDefault ? [] : controlLoops;
        if (loops.Any(static loop => loop is null))
        {
            throw new ArgumentException("Materialization Control definitions cannot contain null entries.", nameof(controlLoops));
        }

        if (loops.Any(static loop => loop.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion))
        {
            throw new ArgumentException(
                "Every embedded Control loop must use the current portable Control schema version.",
                nameof(controlLoops));
        }

        if (loops.GroupBy(static loop => loop.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A materialization definition cannot repeat a Control loop identity.", nameof(controlLoops));
        }

        if (loops.Any(loop => !string.Equals(loop.Target, id.Value, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Every embedded Control loop must target the materialization identity.", nameof(controlLoops));
        }

        ControlLoops = [.. loops.OrderBy(static loop => loop.Id.Value, StringComparer.Ordinal)];

        var workloads = controlWorkloads.IsDefault ? [] : controlWorkloads;
        if (workloads.Any(static binding => binding is null))
        {
            throw new ArgumentException(
                "Materialization Control workload bindings cannot contain null entries.",
                nameof(controlWorkloads));
        }
        if (workloads.GroupBy(static binding => binding.LoopId).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A materialization definition cannot repeat a Control workload binding.",
                nameof(controlWorkloads));
        }
        if (!ControlLoops.Select(static loop => loop.Id)
                .SequenceEqual(workloads.Select(static binding => binding.LoopId)
                    .OrderBy(static id => id.Value, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Every materialization Control loop requires exactly one explicit rebuild or realtime workload binding.",
                nameof(controlWorkloads));
        }
        var loopsById = ControlLoops.ToDictionary(static loop => loop.Id);
        HashSet<(MaterializationIndexSyncWorkloadKind Workload, ControlStageKind Stage, ControlActuatorKind Actuator)>
            ownedAxes = [];
        foreach (var binding in workloads)
        {
            var loop = loopsById[binding.LoopId];
            foreach (var value in loop.InitialOperatingPoint.Values)
            {
                if (!ownedAxes.Add((binding.Workload, loop.Stage, value.Actuator)))
                {
                    throw new ArgumentException(
                        "One workload cannot bind multiple Control loops to the same stage and actuator.",
                        nameof(controlWorkloads));
                }
            }
        }
        ControlWorkloads = [.. workloads.OrderBy(static binding => binding.LoopId.Value, StringComparer.Ordinal)];
    }

    /// <summary>Stable logical materialization identity.</summary>
    public MaterializationId Id { get; }

    /// <summary>Exact Relations request, plan fence, and selected output.</summary>
    public MaterializationRelationReference Relation { get; }

    /// <summary>Source requirements in canonical Relations acquisition-input order.</summary>
    public ImmutableArray<MaterializationSourceRequirement> Sources { get; }

    /// <summary>Target requirements in canonical requirement-identity order.</summary>
    public ImmutableArray<MaterializationCapabilityRequirement> TargetCapabilities { get; }

    /// <summary>Shared synchronization and convergence policy.</summary>
    public MaterializationUpdatePolicy UpdatePolicy { get; }

    /// <summary>Bounded failure and terminal-disposition policy.</summary>
    public MaterializationFailurePolicy FailurePolicy { get; }

    /// <summary>End-to-end freshness requirements.</summary>
    public MaterializationFreshnessPolicy FreshnessPolicy { get; }

    /// <summary>Bounded Control definitions in canonical loop-identity order.</summary>
    public ImmutableArray<ControlLoopDefinition> ControlLoops { get; }

    /// <summary>Explicit workload bindings in canonical loop-identity order.</summary>
    public ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> ControlWorkloads { get; }

    /// <summary>Required producer and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Gets every source capability required for one concrete run mode.</summary>
    /// <param name="mode">Rebuild or incremental synchronization mode.</param>
    /// <returns>Applicable requirements in canonical identity order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is combined, unsupported, or absent from <see cref="UpdatePolicy"/>.</exception>
    public ImmutableArray<MaterializationCapabilityRequirement> GetSourceCapabilities(
        MaterializationSynchronizationMode mode)
    {
        MaterializationSynchronizationModes.RequireValid(mode, nameof(mode), allowCombined: false);
        if ((UpdatePolicy.SupportedModes & mode) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The materialization definition does not support this mode.");
        }

        return
        [
            .. Sources
                .SelectMany(static source => source.Capabilities)
                .Where(requirement => (requirement.Modes & mode) != 0)
                .OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Gets every target capability required for one concrete run mode.</summary>
    /// <param name="mode">Rebuild or incremental synchronization mode.</param>
    /// <returns>Applicable requirements in canonical identity order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is combined, unsupported, or absent from <see cref="UpdatePolicy"/>.</exception>
    public ImmutableArray<MaterializationCapabilityRequirement> GetTargetCapabilities(
        MaterializationSynchronizationMode mode)
    {
        MaterializationSynchronizationModes.RequireValid(mode, nameof(mode), allowCombined: false);
        if ((UpdatePolicy.SupportedModes & mode) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The materialization definition does not support this mode.");
        }

        return [.. TargetCapabilities.Where(requirement => (requirement.Modes & mode) != 0)];
    }
}

/// <summary>Portable envelope that fences one canonical materialization definition with exact content integrity.</summary>
public sealed record MaterializationDocument
{
    /// <summary>Current portable materialization document schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization/v2";

    /// <summary>Creates a materialization document.</summary>
    /// <param name="schemaVersion">Exact portable materialization schema version.</param>
    /// <param name="definition">Canonical backend-independent materialization definition.</param>
    /// <param name="definitionFingerprint">Fingerprint of the complete canonical definition content.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="schemaVersion"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationDocument(
        string schemaVersion,
        MaterializationDefinition definition,
        ExecutionDefinitionFingerprint definitionFingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Materialization schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }
        Definition = Guard.RequireNotNull(definition);
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
    }

    /// <summary>Exact portable materialization schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical backend-independent materialization definition.</summary>
    public MaterializationDefinition Definition { get; }

    /// <summary>Fingerprint of the complete canonical definition content.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Validates and fences a canonical materialization definition.</summary>
    /// <param name="definition">Definition to persist.</param>
    /// <returns>A current-version definition document with computed content fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> fails semantic validation.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported serialization type.</exception>
    public static MaterializationDocument FromDefinition(MaterializationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validation = MaterializationDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(definition));
        }

        return new(
            CurrentSchemaVersion,
            definition,
            MaterializationDefinitionFingerprinter.Compute(definition));
    }
}
