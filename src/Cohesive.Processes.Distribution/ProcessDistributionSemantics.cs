using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Distribution;

/// <summary>Closed canonical kinds of distributable Process work.</summary>
public enum ProcessWorkKind
{
    /// <summary>No work kind was declared; invalid for persisted work.</summary>
    Unspecified = 0,

    /// <summary>One finite activation of an exact Process continuation.</summary>
    Activation = 1,

    /// <summary>One canonical child-Process start request emitted by a parent Process.</summary>
    ChildProcess = 2,

    /// <summary>One exact durable operation request emitted by a Process.</summary>
    DurableOperation = 3
}

/// <summary>Effect guarantee required when potentially duplicated work is executed.</summary>
/// <remarks>
/// No value claims exactly-once physical execution. Stronger values describe the evidence that must make repeated
/// execution semantically safe.
/// </remarks>
public enum ProcessWorkEffectGuarantee
{
    /// <summary>No guarantee was declared; invalid for persisted work.</summary>
    Unspecified = 0,

    /// <summary>At-least-once delivery is accepted and duplicate effects are workload-visible.</summary>
    AtLeastOnce = 1,

    /// <summary>The canonical workload supplies a stable idempotency boundary for effects.</summary>
    Idempotent = 2,

    /// <summary>Effects and authoritative completion share an atomic commit boundary.</summary>
    Atomic = 3,

    /// <summary>Ambiguous effects are resolved through explicit reconciliation evidence.</summary>
    Reconciled = 4
}

/// <summary>Recovery action required after execution ownership expires without completion evidence.</summary>
public enum ProcessWorkRecoveryMode
{
    /// <summary>No recovery rule was declared; invalid for persisted work.</summary>
    Unspecified = 0,

    /// <summary>Expired work may be delivered again under a greater fence.</summary>
    Redispatch = 1,

    /// <summary>Expired work must enter reconciliation before another delivery can be admitted.</summary>
    ReconcileBeforeRedispatch = 2
}

/// <summary>Policy for work whose resource request exceeds the pool's hard capacity.</summary>
public enum ProcessOversizedWorkBehavior
{
    /// <summary>No behavior was selected; invalid in an effective pool policy.</summary>
    Unspecified = 0,

    /// <summary>Retain the work as queued so later policy or worker changes may make it eligible.</summary>
    RetainQueued = 1,

    /// <summary>Move provably oversized work to a terminal poison state with attributable evidence.</summary>
    Poison = 2
}

/// <summary>Origin of one effective distribution configuration decision.</summary>
public enum ProcessDistributionConfigurationSource
{
    /// <summary>No source was declared; invalid for effective configuration.</summary>
    Unspecified = 0,

    /// <summary>The author declared the value locally.</summary>
    Explicit = 1,

    /// <summary>A scoped application or subsystem profile supplied the value.</summary>
    ScopedProfile = 2,

    /// <summary>A deterministic convention supplied the value.</summary>
    Convention = 3,

    /// <summary>An adapter capability or adapter policy supplied the value.</summary>
    Adapter = 4,

    /// <summary>A narrowly scoped local override replaced a lower-precedence value.</summary>
    LocalOverride = 5
}

/// <summary>Attribution for one effective distribution configuration value or policy.</summary>
public sealed record ProcessDistributionConfigurationEvidence
{
    /// <summary>Creates effective-configuration provenance.</summary>
    /// <param name="source">Configuration precedence source.</param>
    /// <param name="authority">Stable identity and version of the supplying authority.</param>
    /// <param name="reference">Stable reference to the exact declaration, profile, convention, or override.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is unsupported.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="reference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> or <paramref name="reference"/> is empty or white space.
    /// </exception>
    [JsonConstructor]
    public ProcessDistributionConfigurationEvidence(
        ProcessDistributionConfigurationSource source,
        string authority,
        string reference)
    {
        if (!Enum.IsDefined(source) || source == ProcessDistributionConfigurationSource.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source,
                "Effective distribution configuration requires an explicit source.");
        }

        Source = source;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Reference = Guard.RequireNotNullOrWhiteSpace(reference);
    }

    /// <summary>Configuration precedence source.</summary>
    public ProcessDistributionConfigurationSource Source { get; }

    /// <summary>Stable identity and version of the supplying authority.</summary>
    public string Authority { get; }

    /// <summary>Stable reference to the exact source evidence.</summary>
    public string Reference { get; }
}

/// <summary>One portable resource quantity used for placement and capacity reservation.</summary>
/// <remarks>
/// Names and units are semantic catalog identities such as <c>cpu</c>/<c>millicores</c> or
/// <c>memory</c>/<c>bytes</c>; they are not provider request objects.
/// </remarks>
public sealed record ProcessResourceQuantity
{
    /// <summary>Creates a resource capacity or requirement.</summary>
    /// <param name="resource">Stable portable resource-kind identity.</param>
    /// <param name="units">Strictly positive quantity.</param>
    /// <param name="unit">Stable portable unit identity.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resource"/> or <paramref name="unit"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resource"/> or <paramref name="unit"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="units"/> is not positive.</exception>
    [JsonConstructor]
    public ProcessResourceQuantity(string resource, long units, string unit)
    {
        if (units <= 0)
            throw new ArgumentOutOfRangeException(nameof(units), units, "Resource capacity must be positive.");

        Resource = Guard.RequireNotNullOrWhiteSpace(resource);
        Units = units;
        Unit = Guard.RequireNotNullOrWhiteSpace(unit);
    }

    /// <summary>Stable portable resource-kind identity.</summary>
    public string Resource { get; }

    /// <summary>Strictly positive quantity.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long Units { get; }

    /// <summary>Stable portable unit identity.</summary>
    public string Unit { get; }
}

/// <summary>Exact canonical Process work addressed by a distributable job.</summary>
/// <remarks>
/// <see cref="SemanticPath"/> identifies the activation, child request occurrence, or durable operation within
/// the exact pinned definition and Process attempt. The reference carries no serialized delegate or credential.
/// </remarks>
public sealed record ProcessWorkReference
{
    /// <summary>Creates an exact canonical Process work reference.</summary>
    /// <param name="definition">Exact pinned Process definition revision and fingerprint.</param>
    /// <param name="processIrVersion">Exact canonical Process IR schema version required by the worker.</param>
    /// <param name="continuation">Exact logical Process instance and attempt.</param>
    /// <param name="kind">Closed canonical work kind.</param>
    /// <param name="semanticPath">Exact semantic path of the activation or emitted request occurrence.</param>
    /// <param name="provenance">Producer and source attribution for the referenced canonical work.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="continuation"/>, or <paramref name="provenance"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="processIrVersion"/> or <paramref name="semanticPath"/> is default.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkReference(
        ExecutionDefinitionReference definition,
        ExecutionIrSchemaVersion processIrVersion,
        ProcessContinuationIdentity continuation,
        ProcessWorkKind kind,
        ExecutionSemanticPath semanticPath,
        ExecutionProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(processIrVersion.Value))
        {
            throw new ArgumentException(
                "A distributed work reference requires an exact Process IR version.",
                nameof(processIrVersion));
        }
        if (!Enum.IsDefined(kind) || kind == ProcessWorkKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Distributed Process work requires an explicit kind.");
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A distributed work reference requires a semantic path.", nameof(semanticPath));

        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ProcessIrVersion = processIrVersion;
        Continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        Kind = kind;
        SemanticPath = semanticPath;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    /// <summary>Exact pinned Process definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Exact canonical Process IR schema version required by the worker.</summary>
    public ExecutionIrSchemaVersion ProcessIrVersion { get; }

    /// <summary>Exact logical Process instance and attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Closed canonical work kind.</summary>
    public ProcessWorkKind Kind { get; }

    /// <summary>Exact semantic path of the activation or emitted request occurrence.</summary>
    public ExecutionSemanticPath SemanticPath { get; }

    /// <summary>Producer and source attribution for the referenced canonical work.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>Portable placement, capacity, fairness, and recovery requirements for one work unit.</summary>
public sealed record ProcessWorkRequirements
{
    /// <summary>Creates portable work requirements.</summary>
    /// <param name="pool">Required logical worker pool.</param>
    /// <param name="capabilities">Required worker capability identities.</param>
    /// <param name="capacity">Portable resource quantities reserved while claimed.</param>
    /// <param name="effectGuarantee">Evidence required to preserve effects under duplicate delivery.</param>
    /// <param name="recoveryMode">Behavior after ownership expires without completion.</param>
    /// <param name="capacityDomain">Optional bounded-work capacity-domain identity.</param>
    /// <param name="fairnessKey">Optional tenant or workload fairness identity.</param>
    /// <param name="affinity">Optional opaque portable affinity identity required from a worker offer.</param>
    /// <param name="priority">Signed priority; greater values are admitted first within fairness policy.</param>
    /// <param name="deadlineUtc">Optional latest UTC time at which execution may start.</param>
    /// <param name="executionTimeout">Optional strictly positive maximum execution duration.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="effectGuarantee"/> or <paramref name="recoveryMode"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The pool is default; a collection contains null, malformed, duplicate, or conflicting entries; the deadline
    /// is not UTC; or <paramref name="executionTimeout"/> is not positive.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkRequirements(
        ProcessWorkerPoolId pool,
        ImmutableArray<string> capabilities,
        ImmutableArray<ProcessResourceQuantity> capacity,
        ProcessWorkEffectGuarantee effectGuarantee,
        ProcessWorkRecoveryMode recoveryMode,
        string? capacityDomain = null,
        string? fairnessKey = null,
        string? affinity = null,
        int priority = 0,
        DateTimeOffset? deadlineUtc = null,
        TimeSpan? executionTimeout = null)
    {
        ProcessDistributionRequirements.Require(pool.Value, nameof(pool));
        if (!Enum.IsDefined(effectGuarantee) || effectGuarantee == ProcessWorkEffectGuarantee.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(effectGuarantee), effectGuarantee, "An effect guarantee is required.");
        if (!Enum.IsDefined(recoveryMode) || recoveryMode == ProcessWorkRecoveryMode.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(recoveryMode), recoveryMode, "A recovery mode is required.");
        if (recoveryMode == ProcessWorkRecoveryMode.Redispatch
            && effectGuarantee == ProcessWorkEffectGuarantee.Reconciled)
        {
            throw new ArgumentException(
                "Reconciled effects require reconciliation before redispatch.",
                nameof(recoveryMode));
        }
        if (deadlineUtc is { Offset: var deadlineOffset } && deadlineOffset != TimeSpan.Zero)
            throw new ArgumentException("A work deadline must be UTC.", nameof(deadlineUtc));
        if (executionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(executionTimeout), executionTimeout, "Execution timeout must be positive.");

        Pool = pool;
        Capabilities = ProcessDistributionRequirements.NormalizeStrings(capabilities, nameof(capabilities));
        Capacity = ProcessDistributionRequirements.NormalizeCapacity(capacity, nameof(capacity));
        EffectGuarantee = effectGuarantee;
        RecoveryMode = recoveryMode;
        CapacityDomain = capacityDomain.TrimmedEmptyOrWhiteSpaceAs();
        FairnessKey = fairnessKey.TrimmedEmptyOrWhiteSpaceAs();
        Affinity = affinity.TrimmedEmptyOrWhiteSpaceAs();
        Priority = priority;
        DeadlineUtc = deadlineUtc;
        ExecutionTimeout = executionTimeout;
    }

    /// <summary>Required logical worker pool.</summary>
    public ProcessWorkerPoolId Pool { get; }

    /// <summary>Required worker capability identities in canonical order.</summary>
    public ImmutableArray<string> Capabilities { get; }

    /// <summary>Portable resource quantities reserved while claimed, in canonical order.</summary>
    public ImmutableArray<ProcessResourceQuantity> Capacity { get; }

    /// <summary>Evidence required to preserve effects under duplicate delivery.</summary>
    public ProcessWorkEffectGuarantee EffectGuarantee { get; }

    /// <summary>Behavior after ownership expires without completion.</summary>
    public ProcessWorkRecoveryMode RecoveryMode { get; }

    /// <summary>Optional bounded-work capacity-domain identity.</summary>
    public string? CapacityDomain { get; }

    /// <summary>Optional tenant or workload fairness identity.</summary>
    public string? FairnessKey { get; }

    /// <summary>Optional opaque portable affinity identity required from a worker offer.</summary>
    public string? Affinity { get; }

    /// <summary>Signed priority; greater values are admitted first within fairness policy.</summary>
    public int Priority { get; }

    /// <summary>Optional latest UTC time at which execution may start.</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>Optional strictly positive maximum execution duration.</summary>
    public TimeSpan? ExecutionTimeout { get; }
}

/// <summary>Effective policy of one logical worker pool.</summary>
public sealed record ProcessWorkerPoolPolicy
{
    /// <summary>Stable authority for framework convention defaults.</summary>
    public const string ConventionalAuthority = "cohesive.processes.distribution/conventions/v1";

    /// <summary>Creates an effective worker-pool policy.</summary>
    /// <param name="maximumConcurrentClaims">Hard maximum live claims across the pool.</param>
    /// <param name="maximumAttempts">Maximum physical dispatch attempts before poison handling.</param>
    /// <param name="workerLeaseDuration">Strictly positive worker-incarnation lease lifetime.</param>
    /// <param name="claimLeaseDuration">Strictly positive work-claim lease lifetime.</param>
    /// <param name="capacity">Optional hard aggregate resource capacity of the pool.</param>
    /// <param name="capacityDomains">Optional named simultaneous-work limits.</param>
    /// <param name="oversizedWorkBehavior">Behavior for requests proven to exceed hard pool capacity.</param>
    /// <param name="evidence">Attribution for the effective policy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A maximum or duration is not positive, or <paramref name="oversizedWorkBehavior"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="evidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Capacity or capacity-domain declarations are malformed or duplicated.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkerPoolPolicy(
        int maximumConcurrentClaims,
        int maximumAttempts,
        TimeSpan workerLeaseDuration,
        TimeSpan claimLeaseDuration,
        ImmutableArray<ProcessResourceQuantity> capacity,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains,
        ProcessOversizedWorkBehavior oversizedWorkBehavior,
        ProcessDistributionConfigurationEvidence evidence)
    {
        if (maximumConcurrentClaims <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentClaims), maximumConcurrentClaims, "Pool concurrency must be positive.");
        if (maximumAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts), maximumAttempts, "Maximum attempts must be positive.");
        if (workerLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(workerLeaseDuration), workerLeaseDuration, "Worker lease must be positive.");
        if (claimLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(claimLeaseDuration), claimLeaseDuration, "Claim lease must be positive.");
        if (!Enum.IsDefined(oversizedWorkBehavior) || oversizedWorkBehavior == ProcessOversizedWorkBehavior.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(oversizedWorkBehavior),
                oversizedWorkBehavior,
                "Oversized-work behavior must be explicit.");
        }

        MaximumConcurrentClaims = maximumConcurrentClaims;
        MaximumAttempts = maximumAttempts;
        WorkerLeaseDuration = workerLeaseDuration;
        ClaimLeaseDuration = claimLeaseDuration;
        Capacity = ProcessDistributionRequirements.NormalizeCapacity(capacity, nameof(capacity));
        CapacityDomains = ProcessDistributionRequirements.NormalizeCapacityDomains(capacityDomains);
        OversizedWorkBehavior = oversizedWorkBehavior;
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    /// <summary>Hard maximum live claims across the pool.</summary>
    public int MaximumConcurrentClaims { get; }

    /// <summary>Maximum physical dispatch attempts before poison handling.</summary>
    public int MaximumAttempts { get; }

    /// <summary>Strictly positive worker-incarnation lease lifetime.</summary>
    public TimeSpan WorkerLeaseDuration { get; }

    /// <summary>Strictly positive work-claim lease lifetime.</summary>
    public TimeSpan ClaimLeaseDuration { get; }

    /// <summary>Hard aggregate resource capacity of the pool, in canonical order.</summary>
    public ImmutableArray<ProcessResourceQuantity> Capacity { get; }

    /// <summary>Named simultaneous-work limits in canonical order.</summary>
    public ImmutableArray<ProcessCapacityDomainLimit> CapacityDomains { get; }

    /// <summary>Behavior for requests proven to exceed hard pool capacity.</summary>
    public ProcessOversizedWorkBehavior OversizedWorkBehavior { get; }

    /// <summary>Attribution for the effective policy.</summary>
    public ProcessDistributionConfigurationEvidence Evidence { get; }

    /// <summary>Creates deterministic framework convention defaults.</summary>
    /// <param name="reference">Stable source reference explaining where defaults were applied.</param>
    /// <returns>A conservative bounded policy attributable to the framework convention set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="reference"/> is empty or white space.</exception>
    public static ProcessWorkerPoolPolicy Conventional(string reference) => new(
        maximumConcurrentClaims: 1,
        maximumAttempts: 5,
        workerLeaseDuration: TimeSpan.FromSeconds(30),
        claimLeaseDuration: TimeSpan.FromSeconds(30),
        capacity: [],
        capacityDomains: [],
        oversizedWorkBehavior: ProcessOversizedWorkBehavior.RetainQueued,
        evidence: new(
            ProcessDistributionConfigurationSource.Convention,
            ConventionalAuthority,
            Guard.RequireNotNullOrWhiteSpace(reference)));
}

/// <summary>Persisted logical worker-pool definition and effective policy.</summary>
public sealed record ProcessWorkerPoolDefinition
{
    /// <summary>Creates a logical worker-pool definition.</summary>
    /// <param name="schemaVersion">Exact portable distribution schema version.</param>
    /// <param name="id">Stable logical pool identity.</param>
    /// <param name="policy">Effective attributable placement and recovery policy.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ProcessWorkerPoolDefinition(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessWorkerPoolId id,
        ProcessWorkerPoolPolicy policy)
    {
        if (schemaVersion != ProcessDistributionWireNames.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported Process distribution schema version.", nameof(schemaVersion));
        ProcessDistributionRequirements.Require(id.Value, nameof(id));
        SchemaVersion = schemaVersion;
        Id = id;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>Exact portable distribution schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable logical pool identity.</summary>
    public ProcessWorkerPoolId Id { get; }

    /// <summary>Effective attributable placement and recovery policy.</summary>
    public ProcessWorkerPoolPolicy Policy { get; }
}

/// <summary>Capability, version, affinity, and capacity offer of one worker incarnation.</summary>
public sealed record ProcessWorkerOffer
{
    /// <summary>Creates one worker offer.</summary>
    /// <param name="schemaVersion">Exact portable distribution schema version.</param>
    /// <param name="worker">Unique worker-incarnation identity.</param>
    /// <param name="pools">Pools from which the worker may claim.</param>
    /// <param name="supportedProcessIrVersions">Exact Process IR schema versions supported by the worker.</param>
    /// <param name="supportedWorkKinds">Canonical work kinds interpreted by the worker.</param>
    /// <param name="supportedEffectGuarantees">Effect guarantees preservable by the worker integration.</param>
    /// <param name="capabilities">Portable capability identities implemented by the worker.</param>
    /// <param name="capacity">Resource capacity reservable across live claims.</param>
    /// <param name="affinities">Opaque portable affinity identities available on the worker.</param>
    /// <param name="maximumConcurrentClaims">Hard maximum live claims owned by this worker.</param>
    /// <exception cref="ArgumentException">
    /// The worker is default; a collection is empty, malformed, or duplicated; or a capacity is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumConcurrentClaims"/> is not positive.</exception>
    [JsonConstructor]
    public ProcessWorkerOffer(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessWorkerIncarnationId worker,
        ImmutableArray<ProcessWorkerPoolId> pools,
        ImmutableArray<ExecutionIrSchemaVersion> supportedProcessIrVersions,
        ImmutableArray<ProcessWorkKind> supportedWorkKinds,
        ImmutableArray<ProcessWorkEffectGuarantee> supportedEffectGuarantees,
        ImmutableArray<string> capabilities,
        ImmutableArray<ProcessResourceQuantity> capacity,
        ImmutableArray<string> affinities,
        int maximumConcurrentClaims)
    {
        if (schemaVersion != ProcessDistributionWireNames.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported Process distribution schema version.", nameof(schemaVersion));
        ProcessDistributionRequirements.Require(worker.Value, nameof(worker));
        if (maximumConcurrentClaims <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentClaims),
                maximumConcurrentClaims,
                "Worker concurrency must be positive.");
        }

        SchemaVersion = schemaVersion;
        Worker = worker;
        Pools = ProcessDistributionRequirements.NormalizePools(pools);
        SupportedProcessIrVersions = ProcessDistributionRequirements.NormalizeVersions(supportedProcessIrVersions);
        SupportedWorkKinds = ProcessDistributionRequirements.NormalizeWorkKinds(supportedWorkKinds);
        SupportedEffectGuarantees = ProcessDistributionRequirements.NormalizeEffectGuarantees(supportedEffectGuarantees);
        Capabilities = ProcessDistributionRequirements.NormalizeStrings(capabilities, nameof(capabilities));
        Capacity = ProcessDistributionRequirements.NormalizeCapacity(capacity, nameof(capacity));
        Affinities = ProcessDistributionRequirements.NormalizeStrings(affinities, nameof(affinities));
        MaximumConcurrentClaims = maximumConcurrentClaims;
    }

    /// <summary>Exact portable distribution schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Unique worker-incarnation identity.</summary>
    public ProcessWorkerIncarnationId Worker { get; }

    /// <summary>Pools from which the worker may claim, in canonical order.</summary>
    public ImmutableArray<ProcessWorkerPoolId> Pools { get; }

    /// <summary>Exact Process IR schema versions supported by the worker, in canonical order.</summary>
    public ImmutableArray<ExecutionIrSchemaVersion> SupportedProcessIrVersions { get; }

    /// <summary>Canonical work kinds interpreted by the worker, in numeric order.</summary>
    public ImmutableArray<ProcessWorkKind> SupportedWorkKinds { get; }

    /// <summary>Effect guarantees preservable by the worker integration, in numeric order.</summary>
    public ImmutableArray<ProcessWorkEffectGuarantee> SupportedEffectGuarantees { get; }

    /// <summary>Portable capability identities implemented by the worker, in canonical order.</summary>
    public ImmutableArray<string> Capabilities { get; }

    /// <summary>Resource capacity reservable across live claims, in canonical order.</summary>
    public ImmutableArray<ProcessResourceQuantity> Capacity { get; }

    /// <summary>Opaque portable affinity identities available on the worker, in canonical order.</summary>
    public ImmutableArray<string> Affinities { get; }

    /// <summary>Hard maximum live claims owned by this worker.</summary>
    public int MaximumConcurrentClaims { get; }
}

static class ProcessDistributionRequirements
{
    internal static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A distribution identity cannot be default.", parameterName);
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A distribution observation must be UTC.", parameterName);
    }

    internal static ImmutableArray<string> NormalizeStrings(
        ImmutableArray<string> values,
        string parameterName)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        var normalized = values.ToArray();
        for (var index = 0; index < normalized.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(normalized[index]))
                throw new ArgumentException("A string set cannot contain null, empty, or white-space entries.", parameterName);
        }
        Array.Sort(normalized, StringComparer.Ordinal);
        for (var index = 1; index < normalized.Length; index++)
        {
            if (string.Equals(normalized[index - 1], normalized[index], StringComparison.Ordinal))
                throw new ArgumentException($"String identity '{normalized[index]}' is duplicated.", parameterName);
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }

    internal static ImmutableArray<ProcessResourceQuantity> NormalizeCapacity(
        ImmutableArray<ProcessResourceQuantity> capacity,
        string parameterName)
    {
        if (capacity.IsDefaultOrEmpty)
            return [];
        if (capacity.Any(static item => item is null))
            throw new ArgumentException("Capacity cannot contain null entries.", parameterName);

        var normalized = capacity.ToArray();
        Array.Sort(normalized, static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Resource, right.Resource);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Unit, right.Unit);
        });
        for (var index = 1; index < normalized.Length; index++)
        {
            if (string.Equals(normalized[index - 1].Resource, normalized[index].Resource, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Resource capacity '{normalized[index].Resource}' is duplicated.",
                    parameterName);
            }
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }

    internal static ImmutableArray<ProcessCapacityDomainLimit> NormalizeCapacityDomains(
        ImmutableArray<ProcessCapacityDomainLimit> domains)
    {
        if (domains.IsDefaultOrEmpty)
            return [];
        if (domains.Any(static domain => domain is null))
            throw new ArgumentException("Capacity-domain limits cannot contain null entries.", nameof(domains));

        var normalized = domains.ToArray();
        foreach (var domain in normalized)
        {
            if (string.IsNullOrWhiteSpace(domain.Identity) || domain.MaximumParallelism <= 0)
                throw new ArgumentException("Capacity-domain limits require an identity and positive parallelism.", nameof(domains));
        }
        Array.Sort(normalized, static (left, right) => StringComparer.Ordinal.Compare(left.Identity, right.Identity));
        for (var index = 1; index < normalized.Length; index++)
        {
            if (string.Equals(normalized[index - 1].Identity, normalized[index].Identity, StringComparison.Ordinal))
                throw new ArgumentException($"Capacity domain '{normalized[index].Identity}' is duplicated.", nameof(domains));
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }

    internal static ImmutableArray<ProcessWorkerPoolId> NormalizePools(ImmutableArray<ProcessWorkerPoolId> pools)
    {
        if (pools.IsDefaultOrEmpty)
            throw new ArgumentException("A worker offer requires at least one pool.", nameof(pools));
        var normalized = pools.ToArray();
        foreach (var pool in normalized)
            Require(pool.Value, nameof(pools));
        Array.Sort(normalized, static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        for (var index = 1; index < normalized.Length; index++)
        {
            if (normalized[index - 1] == normalized[index])
                throw new ArgumentException($"Worker pool '{normalized[index].Value}' is duplicated.", nameof(pools));
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }

    internal static ImmutableArray<ExecutionIrSchemaVersion> NormalizeVersions(
        ImmutableArray<ExecutionIrSchemaVersion> versions)
    {
        if (versions.IsDefaultOrEmpty)
            throw new ArgumentException("A worker offer requires at least one Process IR version.", nameof(versions));
        var normalized = versions.ToArray();
        foreach (var version in normalized)
        {
            if (string.IsNullOrWhiteSpace(version.Value))
                throw new ArgumentException("Process IR versions cannot contain default values.", nameof(versions));
        }
        Array.Sort(normalized, static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        for (var index = 1; index < normalized.Length; index++)
        {
            if (normalized[index - 1] == normalized[index])
                throw new ArgumentException($"Process IR version '{normalized[index].Value}' is duplicated.", nameof(versions));
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }

    internal static ImmutableArray<ProcessWorkKind> NormalizeWorkKinds(ImmutableArray<ProcessWorkKind> kinds)
    {
        if (kinds.IsDefaultOrEmpty)
            throw new ArgumentException("A worker offer requires at least one supported work kind.", nameof(kinds));
        var normalized = kinds.ToArray();
        foreach (var kind in normalized)
        {
            if (!Enum.IsDefined(kind) || kind == ProcessWorkKind.Unspecified)
                throw new ArgumentException("A worker offer contains an unsupported work kind.", nameof(kinds));
        }
        Array.Sort(normalized);
        for (var index = 1; index < normalized.Length; index++)
        {
            if (normalized[index - 1] == normalized[index])
                throw new ArgumentException($"Work kind '{normalized[index]}' is duplicated.", nameof(kinds));
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }

    internal static ImmutableArray<ProcessWorkEffectGuarantee> NormalizeEffectGuarantees(
        ImmutableArray<ProcessWorkEffectGuarantee> guarantees)
    {
        if (guarantees.IsDefaultOrEmpty)
            throw new ArgumentException("A worker offer requires at least one effect guarantee.", nameof(guarantees));
        var normalized = guarantees.ToArray();
        foreach (var guarantee in normalized)
        {
            if (!Enum.IsDefined(guarantee) || guarantee == ProcessWorkEffectGuarantee.Unspecified)
                throw new ArgumentException("A worker offer contains an unsupported effect guarantee.", nameof(guarantees));
        }
        Array.Sort(normalized);
        for (var index = 1; index < normalized.Length; index++)
        {
            if (normalized[index - 1] == normalized[index])
                throw new ArgumentException($"Effect guarantee '{normalized[index]}' is duplicated.", nameof(guarantees));
        }
        return ImmutableCollectionsMarshal.AsImmutableArray(normalized);
    }
}
