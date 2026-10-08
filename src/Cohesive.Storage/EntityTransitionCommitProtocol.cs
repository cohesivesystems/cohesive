using Cohesive.Execution;

namespace Cohesive.Storage;

/// <summary>Shared replay, creation-intent and conditional-conflict protocol around a native atomic commit.</summary>
/// <remarks>The native callback must atomically publish state and receipt or return null without changes.
/// Exceptions, including ambiguous commits, propagate without retry. Retained evidence is never replaced.</remarks>
public static class EntityTransitionCommitProtocol
{
    /// <summary>Resolves retained evidence, attempts one native commit, and resolves a conditional race once.</summary>
    /// <param name="repository">Authority for exact occurrence and creation-intent lookup.</param>
    /// <param name="context">Invocation cancellation and attribution.</param>
    /// <param name="commit">Validated canonical commit.</param>
    /// <param name="tryCommit">Native atomic operation; null means a failed presence, identity or concurrency fence.</param>
    /// <returns>Committed or replayed evidence, or a consistently located conditional conflict.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The native operation returns another commit's evidence.</exception>
    public static async Task<EntityTransitionOperationResult> CommitAsync(
        IEntityTransitionOperationRepository repository, OperationContext context, EntityTransitionOperationCommit commit,
        Func<OperationContext, EntityTransitionOperationCommit, Task<EntityTransitionOperationReceipt?>> tryCommit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(tryCommit);
        context.ThrowIfCancellationRequested();
        var retained = await Lookup(repository, context, commit).ConfigureAwait(false);
        if (retained is not null) return retained;
        var receipt = await tryCommit(context, commit).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (receipt.Commit.Fingerprint != commit.Fingerprint)
                throw new InvalidOperationException("Native commit returned different canonical evidence.");
            return EntityTransitionOperationResult.Committed(receipt);
        }
        retained = await Lookup(repository, context, commit).ConfigureAwait(false);
        return retained ?? (commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent
            ? EntityTransitionOperationRepositoryExtensions.SubjectStateConflict("The creation subject already exists.")
            : EntityTransitionOperationRepositoryExtensions.ConcurrencyConflict("The subject no longer matches its storage concurrency fence."));
    }

    static async Task<EntityTransitionOperationResult?> Lookup(IEntityTransitionOperationRepository repository,
        OperationContext context, EntityTransitionOperationCommit commit)
    {
        var exact = await repository.TryGetTransitionOperation(context, commit.Request).ConfigureAwait(false);
        if (exact.Receipt is { } receipt) return receipt.Replay(commit);
        if (exact.Disposition != EntityTransitionOperationDisposition.NotFound) return exact;
        if (commit.SubjectCondition != EntityTransitionSubjectCondition.MustBeAbsent) return null;
        var creation = await repository.TryGetCreationTransitionOperation(context, commit.Request).ConfigureAwait(false);
        if (creation.Disposition == EntityTransitionOperationDisposition.NotFound) return null;
        if (creation.Receipt is { } original && (original.Entity.Entity != commit.Write.Entity
            || original.Commit.DecisionKind != commit.DecisionKind || original.Result.Value != commit.Result.Value))
            return EntityTransitionOperationRepositoryExtensions.IdentityConflict(
                "Creation intent has a different candidate state or typed result.", "/commit");
        return creation;
    }
}
