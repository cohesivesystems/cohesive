using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Infrastructure realization of one exact target transition with replayable atomic receipts.</summary>
/// <remarks>Implementations must authorize fresh execution and receipt access, preserve exact operation identity,
/// and never translate transport acceptance into successful execution. Ambiguous failures propagate.</remarks>
public abstract class MachineReactionTarget : IProcessTransitionOperationAdapter
{
    /// <summary>Declares the exact operation and logical scope supplied by this realization.</summary>
    /// <param name="transition">Exact target transition reference.</param>
    /// <param name="authority">Supported authority and tenant.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    protected MachineReactionTarget(ExecutionDefinitionReference transition, InteractionAuthorityScope authority)
    { Transition = transition ?? throw new ArgumentNullException(nameof(transition)); Authority = authority ?? throw new ArgumentNullException(nameof(authority)); }

    /// <summary>Exact realized transition revision.</summary>
    public ExecutionDefinitionReference Transition { get; }
    /// <summary>Supported authority scope, including tenant.</summary>
    public InteractionAuthorityScope Authority { get; }

    /// <summary>Executes or replays an exact operation, with fresh target authorization.</summary>
    /// <param name="context">Trusted caller identity, clock and cancellation.</param>
    /// <param name="invocation">Canonical operation intent.</param>
    /// <returns>Domain result with an exact receipt locator, or explicit operation failure.</returns>
    /// <exception cref="UnauthorizedAccessException">The caller or scope is denied.</exception>
    public abstract ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation);

    /// <summary>Resolves retained commitment evidence without consulting later entity state.</summary>
    /// <param name="context">Trusted caller identity, clock and cancellation.</param>
    /// <param name="reference">Exact locator, not an authorization grant.</param>
    /// <returns>The exact receipt, or null when no receipt is available; absence is not non-commit evidence.</returns>
    /// <exception cref="UnauthorizedAccessException">The caller or scope is denied.</exception>
    public abstract ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference);

    /// <summary>Binds the existing native entity adapter and atomic receipt repository.</summary>
    /// <param name="binding">Exact compiled transition, repository and interaction contracts.</param>
    /// <param name="authority">Host-bound scope; no scope is inferred from untrusted input.</param>
    /// <param name="authorizeEntity">Fresh operation/resource authorization, also required before receipt reads.</param>
    /// <returns>A native dispatch and receipt-resolution binding.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ProcessTransitionBindingException">The repository cannot supply the exact atomic receipt contract.</exception>
    public static MachineReactionTarget Local(ProcessTransitionOperationBinding binding, InteractionAuthorityScope authority,
        Func<OperationContext, InteractionEntityReference, ValueTask<bool>> authorizeEntity) => new LocalTarget(binding, authority, authorizeEntity);

    sealed class LocalTarget : MachineReactionTarget
    {
        readonly ProcessTransitionOperationBinding binding;
        readonly Func<OperationContext, InteractionEntityReference, ValueTask<bool>> authorize;
        readonly EntityTransitionProcessOperationAdapter adapter;
        internal LocalTarget(ProcessTransitionOperationBinding binding, InteractionAuthorityScope authority,
            Func<OperationContext, InteractionEntityReference, ValueTask<bool>> authorize)
            : base((binding ?? throw new ArgumentNullException(nameof(binding))).Plan.DefinitionReference, authority)
        {
            this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
            _ = binding.CreateProcessDefinitionLink();
            this.binding = binding;
            adapter = new(call => call.Definition == Transition ? binding : null);
        }

        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(invocation);
            if (invocation.Definition != Transition) throw new InvalidOperationException("Target revision does not match this binding.");
            await Authorize(context, invocation.Context.AuthorityScope, binding.ResolveSubject(invocation)).ConfigureAwait(false);
            return await adapter.ExecuteAsync(context, invocation).ConfigureAwait(false);
        }

        public override async ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(reference);
            await Authorize(context, reference.AuthorityScope, reference.Subject).ConfigureAwait(false);
            var result = await binding.Repository.ResolveTransitionOperation(context, reference).ConfigureAwait(false);
            if (result.Receipt is { } receipt && (receipt.Request.Reference != reference || receipt.Request.Transition != Transition))
                throw new InvalidOperationException("Resolved receipt belongs to another operation or transition.");
            return result.Receipt;
        }

        async ValueTask Authorize(OperationContext context, InteractionAuthorityScope authority, InteractionEntityReference subject)
        {
            if (authority != Authority || subject.EntityType.Value != binding.Repository.EntityType
                || !await authorize(context, subject).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Reaction target access is denied.");
        }
    }
}
