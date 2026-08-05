using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Execution;

namespace Cohesive.Processes.Execution;

/// <summary>Lowers Transition intents under the exact Process operation occurrence that invoked them.</summary>
public static class ProcessTransitionEmissionEnvelopeLowerer
{
    /// <summary>Attempts to create canonical Process-owned envelopes for every Transition emission intent.</summary>
    /// <param name="invocation">Exact Process Transition invocation that owns the emissions.</param>
    /// <param name="subject">Resolved authoritative aggregate subject.</param>
    /// <param name="decision">Complete deterministic Transition decision.</param>
    /// <param name="contracts">Exact interaction-contract catalog used for linking and validation.</param>
    /// <param name="createRequestTarget">
    /// Optional policy that creates an explicit response target for Request intents. It is not used for Domain Events.
    /// </param>
    /// <param name="envelopes">Receives all canonical envelopes on success, otherwise an empty sequence.</param>
    /// <returns>Structured exact-link, identity, provenance, target, and payload validation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="invocation"/>, <paramref name="subject"/>, <paramref name="decision"/>, or
    /// <paramref name="contracts"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult TryLower(
        ProcessTransitionInvocation invocation,
        InteractionEntityReference subject,
        TransitionDecision decision,
        InteractionContractCatalog contracts,
        Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget,
        out ImmutableArray<InteractionEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(contracts);

        var outcome = decision.Evidence.Trace.LastOrDefault(static trace =>
            trace.Kind == TransitionTraceEventKind.OutcomeReturned)?.Node;
        if (outcome is null)
        {
            envelopes = [];
            return DocumentValidationResult.FromDiagnostics(
            [
                new(
                    TransitionExecutionDiagnosticCodes.OutcomeUnavailable,
                    DiagnosticSeverity.Error,
                    "A Process-hosted Transition requires exact terminal outcome evidence before lowering emissions.",
                    "/decision/evidence/trace")
            ]);
        }

        return TransitionEmissionEnvelopeLowerer.TryLower(
            decision,
            contracts,
            new(
                (intent, _) => Context(invocation, subject, intent, outcome.Value),
                createRequestTarget),
            out envelopes);
    }

    static InteractionEnvelopeContext Context(
        ProcessTransitionInvocation invocation,
        InteractionEntityReference subject,
        TransitionEmissionIntent intent,
        ExecutionNodeId outcome)
    {
        var emission = ProcessReferenceIdentities.TransitionEmission(invocation, intent.Node);
        return new(
            emission,
            new ProcessInteractionOrigin(
                invocation.Process,
                invocation.Node,
                invocation.Continuation,
                invocation.Activation,
                invocation.Token,
                subject,
                invocation.Definition,
                outcome,
                intent.Node),
            invocation.Context.CorrelationId,
            invocation.Context.CausationId,
            invocation.Context.AuthorityScope,
            ProcessReferenceIdentities.Idempotency(emission),
            invocation.Context.Ordering,
            invocation.Context.Delivery,
            invocation.Context.Provenance);
    }
}
