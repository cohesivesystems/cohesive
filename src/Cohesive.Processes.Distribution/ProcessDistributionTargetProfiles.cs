using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Distribution;

/// <summary>Portable deployment target families with distinct placement and activation constraints.</summary>
public enum ProcessDistributionTargetKind
{
    /// <summary>No target was declared; invalid in a profile.</summary>
    Unspecified = 0,

    /// <summary>Horizontally replicated Azure App Service runtimes.</summary>
    AzureAppService = 1,

    /// <summary>Orleans silo and grain activation runtimes.</summary>
    Orleans = 2,

    /// <summary>Akka.NET cluster, sharding, actor, or router runtimes.</summary>
    AkkaNet = 3,

    /// <summary>Kubernetes Deployment, consumer, Job, or pod-placement runtimes.</summary>
    Kubernetes = 4,

    /// <summary>Azure Functions queue or event activation runtimes.</summary>
    AzureFunctions = 5,

    /// <summary>Custom adapter target with explicitly supplied capability evidence.</summary>
    Custom = 6
}

/// <summary>Physical lowering strategy selected without changing the canonical job model.</summary>
public enum ProcessDistributionStrategyKind
{
    /// <summary>No strategy was selected; invalid in a profile.</summary>
    Unspecified = 0,

    /// <summary>Activated runtimes compete for eligible work from a shared durable ledger.</summary>
    CompetingConsumerLedger = 1,

    /// <summary>A durable ledger assigns work to an addressable activation such as a grain.</summary>
    ActivationTargeting = 2,

    /// <summary>A durable ledger routes work through actor, shard, or router identities.</summary>
    ActorRouting = 3,

    /// <summary>A durable ledger selects a queue consumer, Deployment, Job, or pod realization.</summary>
    KubernetesWorkload = 4,

    /// <summary>A durable ledger emits bounded queue or event activation.</summary>
    FunctionActivation = 5
}

/// <summary>Stable diagnostics for target-specific constraint mismatches.</summary>
public static class ProcessDistributionTargetDiagnosticCodes
{
    /// <summary>The selected target cannot preserve a requested affinity constraint.</summary>
    public const string AffinityUnavailable = "processes.distribution.target.affinityUnavailable";

    /// <summary>The selected target requires an explicit bounded execution duration.</summary>
    public const string ExecutionBoundRequired = "processes.distribution.target.executionBoundRequired";

    /// <summary>The requested execution duration exceeds the selected target boundary.</summary>
    public const string ExecutionDurationExceeded = "processes.distribution.target.executionDurationExceeded";
}

/// <summary>Attributable target capability profile and physical lowering plan.</summary>
public sealed record ProcessDistributionTargetProfile
{
    /// <summary>Creates a target profile.</summary>
    /// <param name="id">Stable target-profile identity and version.</param>
    /// <param name="target">Deployment target family.</param>
    /// <param name="strategy">Selected physical lowering strategy.</param>
    /// <param name="storeCapabilities">Durable ledger guarantee evidence.</param>
    /// <param name="supportsAffinity">Whether portable affinity can be preserved.</param>
    /// <param name="requiresBoundedExecution">Whether every work unit must declare an execution timeout.</param>
    /// <param name="maximumExecutionDuration">Optional hard target execution-duration boundary.</param>
    /// <param name="evidence">Attribution for the target and strategy selection.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="storeCapabilities"/> or <paramref name="evidence"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum value is unsupported or <paramref name="maximumExecutionDuration"/> is not positive.
    /// </exception>
    public ProcessDistributionTargetProfile(
        string id,
        ProcessDistributionTargetKind target,
        ProcessDistributionStrategyKind strategy,
        ProcessDistributionStoreCapabilities storeCapabilities,
        bool supportsAffinity,
        bool requiresBoundedExecution,
        TimeSpan? maximumExecutionDuration,
        ProcessDistributionConfigurationEvidence evidence)
    {
        if (!Enum.IsDefined(target) || target == ProcessDistributionTargetKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(target), target, "A distribution target is required.");
        if (!Enum.IsDefined(strategy) || strategy == ProcessDistributionStrategyKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "A distribution strategy is required.");
        if (maximumExecutionDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumExecutionDuration),
                maximumExecutionDuration,
                "A present target duration boundary must be positive.");
        }

        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Target = target;
        Strategy = strategy;
        StoreCapabilities = storeCapabilities ?? throw new ArgumentNullException(nameof(storeCapabilities));
        SupportsAffinity = supportsAffinity;
        RequiresBoundedExecution = requiresBoundedExecution;
        MaximumExecutionDuration = maximumExecutionDuration;
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    /// <summary>Stable target-profile identity and version.</summary>
    public string Id { get; }

    /// <summary>Deployment target family.</summary>
    public ProcessDistributionTargetKind Target { get; }

    /// <summary>Selected physical lowering strategy.</summary>
    public ProcessDistributionStrategyKind Strategy { get; }

    /// <summary>Durable ledger guarantee evidence.</summary>
    public ProcessDistributionStoreCapabilities StoreCapabilities { get; }

    /// <summary>Whether portable affinity can be preserved.</summary>
    public bool SupportsAffinity { get; }

    /// <summary>Whether every work unit must declare an execution timeout.</summary>
    public bool RequiresBoundedExecution { get; }

    /// <summary>Optional hard target execution-duration boundary.</summary>
    public TimeSpan? MaximumExecutionDuration { get; }

    /// <summary>Attribution for the target and strategy selection.</summary>
    public ProcessDistributionConfigurationEvidence Evidence { get; }

    /// <summary>Validates one work intent against store guarantees and target constraints.</summary>
    /// <param name="submission">Exact canonical work intent.</param>
    /// <param name="requireAtomicProcessCommit">
    /// Whether this placement must share a provider boundary with canonical Process state.
    /// </param>
    /// <returns>Structured fail-closed diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="submission"/> is <see langword="null"/>.</exception>
    public DocumentValidationResult Validate(
        ProcessWorkSubmission submission,
        bool requireAtomicProcessCommit = true)
    {
        ArgumentNullException.ThrowIfNull(submission);
        List<DocumentValidationDiagnostic> diagnostics = [..
            ProcessDistributionCapabilityValidator.ValidateProduction(
                StoreCapabilities,
                requireAtomicProcessCommit).Diagnostics];
        var requirements = submission.Requirements;
        if (requirements.Affinity is not null && !SupportsAffinity)
        {
            diagnostics.Add(new(
                ProcessDistributionTargetDiagnosticCodes.AffinityUnavailable,
                DiagnosticSeverity.Error,
                $"Target profile '{Id}' cannot preserve the requested portable affinity.",
                "/requirements/affinity"));
        }
        if (RequiresBoundedExecution && requirements.ExecutionTimeout is null)
        {
            diagnostics.Add(new(
                ProcessDistributionTargetDiagnosticCodes.ExecutionBoundRequired,
                DiagnosticSeverity.Error,
                $"Target profile '{Id}' requires an explicit bounded execution timeout.",
                "/requirements/executionTimeout"));
        }
        if (requirements.ExecutionTimeout is { } timeout
            && MaximumExecutionDuration is { } maximum
            && timeout > maximum)
        {
            diagnostics.Add(new(
                ProcessDistributionTargetDiagnosticCodes.ExecutionDurationExceeded,
                DiagnosticSeverity.Error,
                $"Requested execution timeout '{timeout}' exceeds target profile '{Id}' maximum '{maximum}'.",
                "/requirements/executionTimeout"));
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }
}

/// <summary>Conventional target plans that retain one canonical Process distribution model.</summary>
public static class ProcessDistributionTargetProfiles
{
    /// <summary>Creates the App Service competing-consumer reference plan.</summary>
    /// <param name="storeCapabilities">Shared durable queue or ledger capabilities.</param>
    /// <param name="evidence">Attribution for adapter and policy selection.</param>
    /// <returns>An App Service plan that assumes no direct runtime address.</returns>
    public static ProcessDistributionTargetProfile AzureAppService(
        ProcessDistributionStoreCapabilities storeCapabilities,
        ProcessDistributionConfigurationEvidence evidence) => new(
        "cohesive.processes.distribution/azure-app-service/v1",
        ProcessDistributionTargetKind.AzureAppService,
        ProcessDistributionStrategyKind.CompetingConsumerLedger,
        storeCapabilities,
        supportsAffinity: true,
        requiresBoundedExecution: false,
        maximumExecutionDuration: null,
        evidence);

    /// <summary>Creates an Orleans activation-targeting plan over canonical durable admission.</summary>
    /// <param name="storeCapabilities">Durable admission and completion ledger capabilities.</param>
    /// <param name="evidence">Attribution for adapter and policy selection.</param>
    /// <returns>An Orleans plan in which grains are a physical realization, not job authority.</returns>
    public static ProcessDistributionTargetProfile Orleans(
        ProcessDistributionStoreCapabilities storeCapabilities,
        ProcessDistributionConfigurationEvidence evidence) => new(
        "cohesive.processes.distribution/orleans/v1",
        ProcessDistributionTargetKind.Orleans,
        ProcessDistributionStrategyKind.ActivationTargeting,
        storeCapabilities,
        supportsAffinity: true,
        requiresBoundedExecution: false,
        maximumExecutionDuration: null,
        evidence);

    /// <summary>Creates an Akka.NET actor-routing plan over canonical durable admission.</summary>
    /// <param name="storeCapabilities">Durable admission and completion ledger capabilities.</param>
    /// <param name="evidence">Attribution for adapter and policy selection.</param>
    /// <returns>An Akka.NET plan in which actors and sharding are physical strategies.</returns>
    public static ProcessDistributionTargetProfile AkkaNet(
        ProcessDistributionStoreCapabilities storeCapabilities,
        ProcessDistributionConfigurationEvidence evidence) => new(
        "cohesive.processes.distribution/akka-net/v1",
        ProcessDistributionTargetKind.AkkaNet,
        ProcessDistributionStrategyKind.ActorRouting,
        storeCapabilities,
        supportsAffinity: true,
        requiresBoundedExecution: false,
        maximumExecutionDuration: null,
        evidence);

    /// <summary>Creates a Kubernetes consumer, Job, or pod-placement plan.</summary>
    /// <param name="storeCapabilities">Durable admission and completion ledger capabilities.</param>
    /// <param name="evidence">Attribution for adapter and policy selection.</param>
    /// <returns>A Kubernetes physical workload plan.</returns>
    public static ProcessDistributionTargetProfile Kubernetes(
        ProcessDistributionStoreCapabilities storeCapabilities,
        ProcessDistributionConfigurationEvidence evidence) => new(
        "cohesive.processes.distribution/kubernetes/v1",
        ProcessDistributionTargetKind.Kubernetes,
        ProcessDistributionStrategyKind.KubernetesWorkload,
        storeCapabilities,
        supportsAffinity: true,
        requiresBoundedExecution: false,
        maximumExecutionDuration: null,
        evidence);

    /// <summary>Creates a bounded Azure Functions queue-activation plan.</summary>
    /// <param name="storeCapabilities">Durable admission and completion ledger capabilities.</param>
    /// <param name="maximumExecutionDuration">Caller-attested hard duration boundary of the selected hosting plan.</param>
    /// <param name="evidence">Attribution for hosting-plan and adapter selection.</param>
    /// <returns>An Azure Functions plan that requires explicit bounded work and rejects affinity.</returns>
    public static ProcessDistributionTargetProfile AzureFunctions(
        ProcessDistributionStoreCapabilities storeCapabilities,
        TimeSpan maximumExecutionDuration,
        ProcessDistributionConfigurationEvidence evidence) => new(
        "cohesive.processes.distribution/azure-functions/v1",
        ProcessDistributionTargetKind.AzureFunctions,
        ProcessDistributionStrategyKind.FunctionActivation,
        storeCapabilities,
        supportsAffinity: false,
        requiresBoundedExecution: true,
        maximumExecutionDuration,
        evidence);
}
