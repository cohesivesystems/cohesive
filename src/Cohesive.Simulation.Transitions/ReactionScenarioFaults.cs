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

// Owned by the interpreter: targets are wrapped from their actual registered hosts.
internal sealed class ReactionScenarioFaults
{
    string? activeHost;
    ReactionScenarioFault activeFault;
    int hits;
    int calls;

    internal MachineReactionTarget Bind(string host, MachineReactionTarget target) => new Target(this, host, target);

    internal static ReactionScenarioFault Parse(string? name)
    {
        if (!Enum.TryParse<ReactionScenarioFault>(name, out var fault) || !Enum.IsDefined(fault)
            || !string.Equals(name, fault.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Unknown or noncanonical reaction scenario fault.");
        return fault;
    }

    internal void Begin(string host, ReactionScenarioFault fault)
    {
        if (activeHost is not null) throw new InvalidOperationException("Reaction fault sessions cannot overlap.");
        activeHost = host;
        activeFault = fault;
        hits = 0;
        calls = 0;
    }

    internal void End(bool requireHit)
    {
        var missed = calls > 1 || activeFault != ReactionScenarioFault.None && hits != 1;
        activeHost = null;
        activeFault = ReactionScenarioFault.None;
        if (calls > 1 || requireHit && missed)
            throw new InvalidOperationException("The scheduled reaction fault did not reach exactly one target execution boundary.");
    }

    sealed class InjectedFailure(ReactionScenarioFault fault) : Exception($"Injected reaction fault: {fault}")
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
            if (owner.activeHost == alias && ++owner.calls > 1)
                throw new InvalidOperationException("A recovery action executed its target more than once.");
            if (fault != ReactionScenarioFault.None)
            {
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
