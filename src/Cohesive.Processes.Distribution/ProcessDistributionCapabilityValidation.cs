using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Distribution;

/// <summary>Stable structured diagnostic codes for distribution capability mismatches.</summary>
public static class ProcessDistributionDiagnosticCodes
{
    /// <summary>The target cannot durably retain admitted work and completion evidence.</summary>
    public const string DurabilityUnavailable = "processes.distribution.capability.durabilityUnavailable";

    /// <summary>The target cannot create one exclusive atomic work claim.</summary>
    public const string AtomicClaimUnavailable = "processes.distribution.capability.atomicClaimUnavailable";

    /// <summary>The target cannot reject stale physical state mutations.</summary>
    public const string CompareAndSwapUnavailable = "processes.distribution.capability.compareAndSwapUnavailable";

    /// <summary>The target cannot persist and expire worker incarnation leases.</summary>
    public const string WorkerLeaseUnavailable = "processes.distribution.capability.workerLeaseUnavailable";

    /// <summary>The target cannot renew execution ownership.</summary>
    public const string ClaimRenewalUnavailable = "processes.distribution.capability.claimRenewalUnavailable";

    /// <summary>The target cannot fence stale work owners monotonically.</summary>
    public const string FenceUnavailable = "processes.distribution.capability.fenceUnavailable";

    /// <summary>The target cannot discover runnable work through a shared authority.</summary>
    public const string RunnableDiscoveryUnavailable = "processes.distribution.capability.runnableDiscoveryUnavailable";

    /// <summary>The target cannot atomically reserve pool, domain, and worker capacity.</summary>
    public const string CapacityReservationUnavailable = "processes.distribution.capability.capacityReservationUnavailable";

    /// <summary>The target cannot retain poison-work evidence.</summary>
    public const string PoisonWorkUnavailable = "processes.distribution.capability.poisonWorkUnavailable";

    /// <summary>The target cannot atomically compose Process state and newly runnable work.</summary>
    public const string AtomicProcessCommitUnavailable = "processes.distribution.capability.atomicProcessCommitUnavailable";
}

/// <summary>Fail-closed validation of physical distribution guarantees.</summary>
public static class ProcessDistributionCapabilityValidator
{
    /// <summary>Validates the capabilities required by a production competing-consumer realization.</summary>
    /// <param name="capabilities">Physical target capability evidence.</param>
    /// <param name="requireAtomicProcessCommit">
    /// Whether the selected composition must atomically persist canonical Process state and newly runnable work.
    /// </param>
    /// <returns>Canonical structured diagnostics; no error means the target preserves every required guarantee.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult ValidateProduction(
        ProcessDistributionStoreCapabilities capabilities,
        bool requireAtomicProcessCommit = true)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        Require(
            capabilities.IsDurable,
            ProcessDistributionDiagnosticCodes.DurabilityUnavailable,
            "The selected distribution ledger does not durably retain admitted work and completion evidence.",
            "/capabilities/isDurable");
        Require(
            capabilities.SupportsAtomicClaim,
            ProcessDistributionDiagnosticCodes.AtomicClaimUnavailable,
            "The selected distribution ledger cannot guarantee one exclusive atomic work claim.",
            "/capabilities/supportsAtomicClaim");
        Require(
            capabilities.SupportsCompareAndSwap,
            ProcessDistributionDiagnosticCodes.CompareAndSwapUnavailable,
            "The selected distribution ledger cannot reject stale physical state mutations.",
            "/capabilities/supportsCompareAndSwap");
        Require(
            capabilities.SupportsWorkerLeases,
            ProcessDistributionDiagnosticCodes.WorkerLeaseUnavailable,
            "The selected distribution ledger cannot persist and expire worker incarnation leases.",
            "/capabilities/supportsWorkerLeases");
        Require(
            capabilities.SupportsClaimRenewal,
            ProcessDistributionDiagnosticCodes.ClaimRenewalUnavailable,
            "The selected distribution ledger cannot renew execution ownership.",
            "/capabilities/supportsClaimRenewal");
        Require(
            capabilities.SupportsMonotonicFencing,
            ProcessDistributionDiagnosticCodes.FenceUnavailable,
            "The selected distribution ledger cannot create monotonic fences when work is reclaimed.",
            "/capabilities/supportsMonotonicFencing");
        Require(
            capabilities.SupportsRunnableDiscovery,
            ProcessDistributionDiagnosticCodes.RunnableDiscoveryUnavailable,
            "The selected distribution ledger cannot discover eligible work through a shared authority.",
            "/capabilities/supportsRunnableDiscovery");
        Require(
            capabilities.SupportsCapacityReservations,
            ProcessDistributionDiagnosticCodes.CapacityReservationUnavailable,
            "The selected distribution ledger cannot atomically count claims against capacity.",
            "/capabilities/supportsCapacityReservations");
        Require(
            capabilities.SupportsPoisonWork,
            ProcessDistributionDiagnosticCodes.PoisonWorkUnavailable,
            "The selected distribution ledger cannot durably retain poison-work evidence.",
            "/capabilities/supportsPoisonWork");
        if (requireAtomicProcessCommit)
        {
            Require(
                capabilities.SupportsAtomicProcessCommit,
                ProcessDistributionDiagnosticCodes.AtomicProcessCommitUnavailable,
                "The selected composition cannot atomically persist Process state and newly runnable work; use a shared aggregate boundary or attributable inbox/outbox composition.",
                "/capabilities/supportsAtomicProcessCommit");
        }

        return diagnostics.Count == 0
            ? DocumentValidationResult.Valid
            : new(diagnostics.ToImmutable());

        void Require(bool available, string code, string message, string location)
        {
            if (!available)
                diagnostics.Add(new(code, DiagnosticSeverity.Error, message, location));
        }
    }
}
