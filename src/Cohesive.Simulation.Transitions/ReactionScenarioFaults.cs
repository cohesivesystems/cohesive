using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;

namespace Cohesive.Simulation.Transitions;

/// <summary>Physical target boundary to interrupt during one scheduled recovery action.</summary>
public enum ReactionScenarioFault
{
    /// <summary>Execute normally.</summary>
    None,
    /// <summary>Fail before invoking the target; no new target effect is produced.</summary>
    BeforeExecution,
    /// <summary>Let the target return, then lose its acknowledgement; retained receipts remain available.</summary>
    LoseAcknowledgement
}

/// <summary>Single-run target instrumentation driven exclusively by canonical scenario action inputs.</summary>
/// <remarks>Bind targets before constructing their hosts. Pass this same instance to the scenario interpreter.
/// Only sequential isolated simulations are supported. Faults never carry into another action, and a declared
/// fault that did not reach its target boundary fails the run rather than silently passing.</remarks>
public sealed class ReactionScenarioFaults
{
    readonly Dictionary<string, MachineReactionTarget> targets = new(StringComparer.Ordinal);
    bool attached;
    string? activeHost;
    ReactionScenarioFault activeFault;
    int hits;

    /// <summary>Wraps a target without changing its transition, authority, receipt resolution or real failure classification.</summary>
    /// <param name="host">Unique host alias used by scenario inputs.</param>
    /// <param name="target">Isolated deterministic target to instrument.</param>
    /// <returns>A target to supply to the corresponding durable host.</returns>
    /// <exception cref="ArgumentException">The alias is blank or already bound.</exception>
    /// <exception cref="ArgumentNullException">The target is null.</exception>
    /// <exception cref="InvalidOperationException">The session is already attached to an interpreter.</exception>
    public MachineReactionTarget Bind(string host, MachineReactionTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(target);
        if (attached) throw new InvalidOperationException("Bind targets before attaching the fault session to an interpreter.");
        if (!targets.TryAdd(host, target)) throw new ArgumentException("A fault target alias is already bound.", nameof(host));
        return new Target(this, host, target);
    }

    internal bool Contains(string host) => targets.ContainsKey(host);
    internal IEnumerable<string> Aliases => targets.Keys.Order(StringComparer.Ordinal);

    internal void Attach(IReadOnlyDictionary<string, MachineReactionHost> hosts)
    {
        if (attached) throw new InvalidOperationException("Use fresh fault instrumentation for each interpreter.");
        foreach (var (alias, target) in targets)
            if (!hosts.TryGetValue(alias, out var host) || host.Authority != target.Authority || host.Policy.Payload.Target.Reference != target.Transition)
                throw new ArgumentException("An instrumented target must match its registered host's exact transition and authority.");
        attached = true;
    }

    internal void Begin(string host, ReactionScenarioFault fault)
    {
        if (activeHost is not null) throw new InvalidOperationException("Reaction fault sessions cannot overlap.");
        activeHost = host;
        activeFault = fault;
        hits = 0;
    }

    internal void End(bool requireHit)
    {
        var missed = activeFault != ReactionScenarioFault.None && hits != 1;
        activeHost = null;
        activeFault = ReactionScenarioFault.None;
        if (requireHit && missed)
            throw new InvalidOperationException("The scheduled reaction fault did not reach exactly one target execution boundary.");
    }

    sealed class InjectedFailure(ReactionScenarioFault fault) : TimeoutException($"Injected reaction fault: {fault}")
    {
        internal ReactionScenarioFault Fault { get; } = fault;
    }

    sealed class Target(ReactionScenarioFaults owner, string alias, MachineReactionTarget inner)
        : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var fault = owner.activeHost == alias ? owner.activeFault : ReactionScenarioFault.None;
            if (fault != ReactionScenarioFault.None)
            {
                if (owner.hits != 0) throw new InvalidOperationException("A fault action executed its target more than once.");
                if (fault == ReactionScenarioFault.BeforeExecution)
                {
                    owner.hits++;
                    throw new InjectedFailure(fault);
                }
            }
            var result = await inner.ExecuteAsync(context, invocation).ConfigureAwait(false);
            if (fault == ReactionScenarioFault.LoseAcknowledgement)
            {
                owner.hits++;
                throw new InjectedFailure(fault);
            }
            return result;
        }

        public override DurableOperationFailure DescribeFailure(Exception exception) => exception is InjectedFailure injected
            ? new(injected.Fault == ReactionScenarioFault.BeforeExecution ? DurableOperationFailurePhase.PreCall : DurableOperationFailurePhase.PostCommitPreAcknowledgement,
                injected.Fault == ReactionScenarioFault.BeforeExecution ? DurableOperationEffectEvidence.NotExecuted : DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable, "simulation.reaction.fault." + injected.Fault)
            : inner.DescribeFailure(exception);

        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }
}
