using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Storage;

/// <summary>One committed machine movement, projected from an authoritative entity receipt.</summary>
/// <remarks>This is observation evidence, not a publication request or an authorization grant.
/// An adapter must obtain the receipt from a trusted atomic state/receipt repository. A successful
/// reference decision alone cannot produce an occurrence. Replay retains the receipt and node identity;
/// consumers must deduplicate by that pair, never by delivery time or current entity state.</remarks>
public sealed class EntityMachineOccurrence
{
    EntityMachineOccurrence(EntityTransitionOperationReceipt receipt, TransitionTraceEvent movement)
    {
        Receipt = receipt;
        Movement = movement;
    }

    /// <summary>Authoritative committed state, causation, command, outcome and exact definition evidence.</summary>
    public EntityTransitionOperationReceipt Receipt { get; }

    /// <summary>Exact movement evidence retained by the receipt, including its node, exact machine reference and edge identity.</summary>
    public TransitionTraceEvent Movement { get; }

    /// <summary>Input-free receipt identity; together with Movement.Node identifies this occurrence.</summary>
    public EntityTransitionOperationReference Source => Receipt.Request.Reference;

    /// <summary>Matches an owner-scoped movement against the exact committed machine revision.</summary>
    /// <param name="movement">Typed authoring handle; resolving it freezes its owning declaration.</param>
    /// <returns>True only when both the exact machine reference and edge identity match.</returns>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    /// <exception cref="InvalidOperationException">The owning machine cannot be built.</exception>
    public bool Matches(MachineMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        var resolved = movement.Resolve();
        return Movement.Contract == resolved.Machine.Document.Reference && Movement.Edge?.Value == movement.Id;
    }

    /// <summary>Projects implicit occurrences without mutating the receipt or declaring extra domain events.</summary>
    /// <param name="receipt">Trusted atomic entity commit receipt, including replayed receipts.</param>
    /// <returns>Movements in execution order; empty for rejection or successful preservation.</returns>
    /// <exception cref="ArgumentNullException">The receipt is null.</exception>
    /// <exception cref="InvalidOperationException">Retained movement evidence is incomplete or nodes are duplicated.</exception>
    /// <remarks>Applied and NoChange are successful: a named self-loop is still an occurrence even
    /// when no fields change. Rejected decisions never induce a reaction.</remarks>
    public static ImmutableArray<EntityMachineOccurrence> FromReceipt(EntityTransitionOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Commit.DecisionKind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange))
            return [];
        var result = ImmutableArray.CreateBuilder<EntityMachineOccurrence>();
        HashSet<ExecutionNodeId> nodes = [];
        foreach (var movement in receipt.Commit.Evidence.Trace)
        {
            if (movement.Kind != TransitionTraceEventKind.MachineMoved) continue;
            if (movement.Contract is null || movement.Edge is null
                || string.IsNullOrWhiteSpace(movement.Edge.Value.Value)
                || string.IsNullOrWhiteSpace(movement.Node.Value))
                throw new InvalidOperationException("Committed machine movement evidence is incomplete.");
            if (!nodes.Add(movement.Node))
                throw new InvalidOperationException("Committed machine movements require unique execution node identities.");
            result.Add(new(receipt, movement));
        }
        return result.ToImmutable();
    }
}
