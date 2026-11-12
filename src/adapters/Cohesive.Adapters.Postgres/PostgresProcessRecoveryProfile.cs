using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Adapters.Postgres;

/// <summary>Recovery evidence for the native PostgreSQL process-store and fenced transition composition.</summary>
public static class PostgresProcessRecoveryProfile
{
    /// <summary>Requires all transition repositories to use the acquired fence and the same native data source.</summary>
    public static ProcessInterpreterOperatingBoundaryId SameDatabaseFencedReceipts { get; } =
        new("postgres/recovery/same-datasource-fenced-receipts/v1");

    /// <summary>Recovery-only evidence, qualified by the PostgreSQL cancellation conformance tests.</summary>
    /// <remarks>This does not advertise support for arbitrary effects or all Process constructs.</remarks>
    public static ProcessInterpreterCapabilityProfile Fenced { get; } = new(
        new("postgres/process/fenced-recovery/v1"), new("postgres/process-durable-store"),
        [.. ProcessRecoveryRequirements.WorkerTakeover.Select(key => new ProcessInterpreterCapabilityEvidence(
            new("postgres/recovery/" + key.Name), key, CapabilityRealizationKind.Constrained,
            auxiliaryEvidence: [new("postgres/process-root-and-transition-receipt-transaction/v1")],
            operatingBoundaries: [SameDatabaseFencedReceipts]))]);
}

public sealed partial class PostgresProcessDurableStore
{
    /// <summary>Creates a runtime with explicit native recovery evidence and an acquired-fence host factory.</summary>
    /// <param name="hostFactory">Binds every transition repository with the supplied fence; caller owns shared resources.</param>
    /// <param name="runtimeOptions">Worker identity and lease policy.</param>
    /// <returns>A runtime that validates explicitly requested recovery guarantees before initialization or activation.</returns>
    /// <exception cref="ArgumentNullException">The factory or runtime options is null.</exception>
    /// <remarks>The factory is an explicit integration boundary: attaching the supplied fence to every transition
    /// repository is mandatory. Native commit checks validate current ownership, receipt setup and data-source identity.
    /// Capability declarations are not a substitute for adapter conformance testing.</remarks>
    public ProcessDurableRuntime CreateFencedRuntime(Func<PostgresProcessWorkerFence, IAsyncProcessReferenceHost> hostFactory,
        ProcessDurableRuntimeOptions runtimeOptions)
    {
        ArgumentNullException.ThrowIfNull(hostFactory);
        return new(this, snapshot => hostFactory(BindWorkerFence(snapshot)), runtimeOptions,
            recoveryProfile: PostgresProcessRecoveryProfile.Fenced);
    }
}
