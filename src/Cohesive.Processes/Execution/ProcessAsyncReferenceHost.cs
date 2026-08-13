using Cohesive.Prelude;

namespace Cohesive.Processes.Execution;

/// <summary>Asynchronous physical execution port for exact canonical Process host operations.</summary>
/// <remarks>
/// <para>
/// This port does not replace <see cref="IProcessReferenceHost"/> or define another Process interpreter.
/// <see cref="ProcessReferenceInterpreter.ActivateAsync"/> materializes results from this port into the existing
/// synchronous evidence seam and re-enters the same pure reducer.
/// </para>
/// <para>
/// Implementations may perform naturally asynchronous infrastructure work. They must preserve the complete
/// invocation value and return deterministic evidence for its attempt-, activation-, token-, node-, and
/// occurrence-scoped identity. Caller cancellation is a physical execution signal; it does not itself author
/// semantic Process cancellation.
/// </para>
/// </remarks>
public interface IAsyncProcessReferenceHost
{
    /// <summary>Invokes one exact canonical Transition asynchronously.</summary>
    /// <param name="context">Physical operation context carrying cancellation, tracing, identity, and time.</param>
    /// <param name="invocation">Complete semantic Transition invocation context.</param>
    /// <returns>Typed outcome, produced interactions, or structured failure evidence.</returns>
    /// <exception cref="OperationCanceledException">
    /// Physical execution is cancelled, including when <paramref name="context"/> is cancelled.
    /// </exception>
    ValueTask<ProcessOperationResult> InvokeTransitionAsync(
        OperationContext context,
        ProcessTransitionInvocation invocation);

    /// <summary>Evaluates one exact canonical Relation or Query asynchronously.</summary>
    /// <param name="context">Physical operation context carrying cancellation, tracing, identity, and time.</param>
    /// <param name="evaluation">Complete semantic Relation or Query evaluation context.</param>
    /// <returns>Typed result, produced interactions, or structured failure evidence.</returns>
    /// <exception cref="OperationCanceledException">
    /// Physical execution is cancelled, including when <paramref name="context"/> is cancelled.
    /// </exception>
    ValueTask<ProcessOperationResult> EvaluateRelationAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation);

    /// <summary>Resolves a portable Signal-target value asynchronously.</summary>
    /// <param name="context">Physical operation context carrying cancellation, tracing, identity, and time.</param>
    /// <param name="resolution">Complete semantic target-resolution context.</param>
    /// <returns>A canonical target or structured failure evidence.</returns>
    /// <exception cref="OperationCanceledException">
    /// Physical execution is cancelled, including when <paramref name="context"/> is cancelled.
    /// </exception>
    ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
        OperationContext context,
        ProcessSignalTargetResolution resolution);
}

/// <summary>Explicit compatibility projection from a synchronous reference host to the asynchronous port.</summary>
/// <remarks>
/// The adapter observes cancellation before entering the synchronous host, but cannot interrupt work after that
/// call begins. It is suitable only for bounded synchronous implementations. Naturally asynchronous or
/// cancellation-sensitive infrastructure should implement <see cref="IAsyncProcessReferenceHost"/> directly.
/// </remarks>
public sealed class SynchronousProcessReferenceHostAdapter : IAsyncProcessReferenceHost
{
    readonly IProcessReferenceHost host;

    /// <summary>Creates an explicit asynchronous projection over one synchronous host.</summary>
    /// <param name="host">Bounded synchronous host to invoke.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    public SynchronousProcessReferenceHostAdapter(IProcessReferenceHost host) =>
        this.host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc />
    public ValueTask<ProcessOperationResult> InvokeTransitionAsync(
        OperationContext context,
        ProcessTransitionInvocation invocation)
    {
        Require(context, invocation);
        return ValueTask.FromResult(RequireResult(host.InvokeTransition(invocation), "Transition"));
    }

    /// <inheritdoc />
    public ValueTask<ProcessOperationResult> EvaluateRelationAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation)
    {
        Require(context, evaluation);
        return ValueTask.FromResult(RequireResult(host.EvaluateRelation(evaluation), "Relation/Query"));
    }

    /// <inheritdoc />
    public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
        OperationContext context,
        ProcessSignalTargetResolution resolution)
    {
        Require(context, resolution);
        return ValueTask.FromResult(
            host.ResolveSignalTarget(resolution)
            ?? throw new InvalidOperationException("The synchronous Process host returned null Signal-target evidence."));
    }

    static void Require<TValue>(OperationContext context, TValue value)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);
        context.ThrowIfCancellationRequested();
    }

    static ProcessOperationResult RequireResult(ProcessOperationResult? result, string operation)
    {
        if (result is null)
        {
            throw new InvalidOperationException(
                $"The synchronous Process host returned null {operation} evidence.");
        }
        if (!result.IsValidOutcome())
        {
            throw new InvalidOperationException(
                $"The synchronous Process host returned invalid {operation} evidence.");
        }
        return result;
    }
}
