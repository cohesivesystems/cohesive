using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Projects committed Process activation evidence from the existing durable checkpoint authority.</summary>
public static class ProcessDurableExecutionTraceProjector
{
    /// <summary>Projects every committed activation in durable attempt and commit-sequence order.</summary>
    /// <param name="checkpoint">Complete validated Process checkpoint.</param>
    /// <returns>
    /// One projection result per committed activation. Successful traces retain the durable commit sequence while
    /// their semantic fingerprint remains equal to the corresponding reference-interpreter trace.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<ExecutionTraceProjectionResult> Project(
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Activations.IsDefaultOrEmpty)
            return [];

        var envelopeCount = checkpoint.Emissions.Length + checkpoint.Inbox.Length;
        var envelopes = ImmutableArray.CreateBuilder<InteractionEnvelope>(envelopeCount);
        foreach (var emission in checkpoint.Emissions)
            envelopes.Add(emission.Envelope);
        foreach (var input in checkpoint.Inbox)
            envelopes.Add(input.Input.Envelope);
        var canonicalEnvelopes = envelopes.MoveToImmutable();

        var results = ImmutableArray.CreateBuilder<ExecutionTraceProjectionResult>(checkpoint.Activations.Length);
        foreach (var activation in checkpoint.Activations)
        {
            results.Add(ProcessExecutionTraceProjector.ProjectCommitted(
                evidence: activation.Evidence,
                disposition: activation.Disposition,
                durableCommitSequence: activation.Sequence,
                expectedDefinition: checkpoint.Definition,
                expectedContinuation: activation.Continuation,
                envelopes: canonicalEnvelopes));
        }

        return results.MoveToImmutable();
    }
}
