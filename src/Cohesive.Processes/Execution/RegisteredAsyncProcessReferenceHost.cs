using Cohesive.Execution;

namespace Cohesive.Processes.Execution;

/// <summary>Executes one Process Transition occurrence through a deployed physical adapter.</summary>
/// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
/// <param name="invocation">Complete exact Transition occurrence.</param>
/// <returns>Typed Transition evidence or a structured failure.</returns>
public delegate ValueTask<ProcessOperationResult> ProcessTransitionOperationHandler(
    OperationContext context,
    ProcessTransitionInvocation invocation);

/// <summary>Resolves one Process Signal target through deployed application policy.</summary>
/// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
/// <param name="resolution">Complete exact Signal-target resolution occurrence.</param>
/// <returns>A closed canonical target or structured failure.</returns>
public delegate ValueTask<ProcessSignalTargetResult> ProcessSignalTargetHandler(
    OperationContext context,
    ProcessSignalTargetResolution resolution);

/// <summary>Stable diagnostics emitted by the registered asynchronous Process reference host.</summary>
public static class RegisteredAsyncProcessReferenceHostDiagnosticCodes
{
    /// <summary>No Signal-target handler is deployed.</summary>
    public const string SignalTargetNotRegistered = "processes.asyncHost.signalTarget.notRegistered";
}

/// <summary>
/// Immutable composition of exact Relation/Query registrations with one Transition adapter and optional Signal
/// target policy.
/// </summary>
/// <remarks>
/// This class is physical deployment state, not another Process interpreter or definition catalog. Exact canonical
/// definition references remain the sole Relation/Query dispatch authority. The supplied Transition handler is
/// expected to perform its own exact binding resolution because Transition persistence policy belongs to the
/// selected adapter. Missing Signal policy produces structured failure evidence rather than ambient fallback.
/// The host is safe for concurrent dispatch; handler thread safety remains the application's responsibility.
/// </remarks>
public sealed class RegisteredAsyncProcessReferenceHost : IAsyncProcessReferenceHost
{
    readonly ProcessRelationHandlerCatalog relations;
    readonly ProcessTransitionOperationHandler transitions;
    readonly ProcessSignalTargetHandler? signalTargets;

    /// <summary>Creates one exact physical Process host composition.</summary>
    /// <param name="relations">Immutable exact Relation/Query handler catalog.</param>
    /// <param name="transitions">Physical adapter for exact Transition invocations.</param>
    /// <param name="signalTargets">Optional explicit Signal-target resolver.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relations"/> or <paramref name="transitions"/> is <see langword="null"/>.
    /// </exception>
    public RegisteredAsyncProcessReferenceHost(
        ProcessRelationHandlerCatalog relations,
        ProcessTransitionOperationHandler transitions,
        ProcessSignalTargetHandler? signalTargets = null)
    {
        this.relations = relations ?? throw new ArgumentNullException(nameof(relations));
        this.transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        this.signalTargets = signalTargets;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The registered handler returns null or invalid operation evidence.
    /// </exception>
    public async ValueTask<ProcessOperationResult> InvokeTransitionAsync(
        OperationContext context,
        ProcessTransitionInvocation invocation)
    {
        Require(context, invocation);
        var result = await transitions(context, invocation).ConfigureAwait(false);
        return result is not null && result.IsValidOutcome()
            ? result
            : throw new InvalidOperationException(
                "The registered Transition handler returned invalid operation evidence.");
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The registered handler returns null resolution evidence.
    /// </exception>
    public ValueTask<ProcessOperationResult> EvaluateRelationAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation) =>
        relations.EvaluateAsync(context, evaluation);

    /// <inheritdoc />
    public async ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
        OperationContext context,
        ProcessSignalTargetResolution resolution)
    {
        Require(context, resolution);
        var result = signalTargets is null
            ? ProcessSignalTargetResult.Failed(new(
                RegisteredAsyncProcessReferenceHostDiagnosticCodes.SignalTargetNotRegistered,
                DiagnosticSeverity.Error,
                "No Signal-target handler is deployed for this Process runtime.",
                $"process/{resolution.Node.Value}/target"))
            : await signalTargets(context, resolution).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException(
            "The registered Signal-target handler returned null resolution evidence.");
    }

    static void Require<TValue>(OperationContext context, TValue value)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);
        context.ThrowIfCancellationRequested();
    }
}
