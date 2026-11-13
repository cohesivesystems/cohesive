using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Integrations;

/// <summary>Explicit host ownership boundary for local consumer controllers and admission groups.</summary>
/// <remarks>One consumer has one controller in this scope, regardless of standalone or grouped creation.
/// Use one scope for a host's control namespace. Independent scopes deliberately represent independent hosts;
/// this is not a process-global registry or distributed ownership fence. Ownership is retained for this scope's lifetime.</remarks>
public sealed class InMemoryIntegrationLifecycleScope
{
    readonly object sync = new();
    readonly Dictionary<IngestionLedgerAddress, InMemoryIntegrationLifecycle> consumers = new();
    readonly Dictionary<string, InMemoryIntegrationAdmissionGroup> groups = new(StringComparer.Ordinal);

    /// <summary>Creates one exclusively owned standalone consumer.</summary>
    /// <param name="consumer">Stable consumer identity.</param>
    /// <param name="authorize">Host authorization policy for this consumer.</param>
    /// <param name="verifyFailureResolution">Optional trusted verifier with the controller's effect-accounting obligations.</param>
    /// <returns>Applied with the newly owned paused controller, or AlreadyOwned without an existing controller handle.</returns>
    /// <exception cref="ArgumentNullException">Consumer or authorization policy is null.</exception>
    /// <exception cref="ArgumentException">Consumer identity is invalid.</exception>
    public IntegrationLifecycleResult<IntegrationChangeOutcome, InMemoryIntegrationLifecycle> CreateConsumer(IngestionLedgerAddress consumer,
        Func<IntegrationLifecycleAction, ProcessControlAuthorizationContext, bool> authorize,
        Func<IntegrationFailureResolutionRequest, ProcessActivationDecision?>? verifyFailureResolution = null)
    {
        ArgumentNullException.ThrowIfNull(consumer); ArgumentNullException.ThrowIfNull(authorize);
        consumer.Validate();
        lock (sync)
        {
            if (consumers.ContainsKey(consumer)) return new(IntegrationChangeOutcome.AlreadyOwned);
            var controller = new InMemoryIntegrationLifecycle(consumer, authorize, verifyFailureResolution);
            consumers.Add(consumer, controller);
            return new(IntegrationChangeOutcome.Applied, controller);
        }
    }

    /// <summary>Creates one exclusively named admission group in this ownership scope.</summary>
    /// <param name="id">Stable group identity.</param>
    /// <param name="authorize">Policy for group commands (null consumer) and member actions (identified consumer). Registration requires both decisions.</param>
    /// <param name="verifyFailureResolution">Optional trusted per-consumer failure verifier.</param>
    /// <returns>The newly owned group, initially paused.</returns>
    /// <exception cref="ArgumentNullException">A required policy is null.</exception>
    /// <exception cref="ArgumentException">Group identity is invalid.</exception>
    /// <exception cref="InvalidOperationException">The group identity already exists in this scope.</exception>
    public InMemoryIntegrationAdmissionGroup CreateGroup(string id,
        Func<IngestionLedgerAddress?, IntegrationLifecycleAction, ProcessControlAuthorizationContext, bool> authorize,
        Func<IngestionLedgerAddress, IntegrationFailureResolutionRequest, ProcessActivationDecision?>? verifyFailureResolution = null)
    {
        IngestionLedgerAddress.Require(id);
        ArgumentNullException.ThrowIfNull(authorize);
        lock (sync)
        {
            if (groups.ContainsKey(id)) throw new InvalidOperationException("This scope already owns the group identity.");
            var group = new InMemoryIntegrationAdmissionGroup(this, id, authorize, verifyFailureResolution);
            groups.Add(id, group);
            return group;
        }
    }

    // Factories here are internal construction only, never host callbacks. Registration holds the group lock
    // before this scope lock; no scope operation acquires a group/member lock in the other direction.
    internal bool TryClaim(IngestionLedgerAddress consumer, Func<InMemoryIntegrationLifecycle> create,
        out InMemoryIntegrationLifecycle? controller)
    {
        lock (sync)
        {
            if (consumers.ContainsKey(consumer)) { controller = null; return false; }
            controller = create(); consumers.Add(consumer, controller); return true;
        }
    }
}
