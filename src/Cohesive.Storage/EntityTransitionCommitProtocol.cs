using Cohesive.Execution;

namespace Cohesive.Storage;

/// <summary>Shared replay, creation-intent and conditional-conflict protocol around a native atomic commit.</summary>
/// <remarks>The native callback must atomically publish state and receipt or return a conditional conflict without changes.
/// Exceptions, including ambiguous commits, propagate without retry. Retained evidence is never replaced.</remarks>
public static class EntityTransitionCommitProtocol
{
    /// <summary>Attempts one native commit, looking up retained evidence only when its conditional fence fails.</summary>
    /// <param name="repository">Authority for exact occurrence and creation-intent lookup.</param>
    /// <param name="context">Invocation cancellation and attribution.</param>
    /// <param name="commit">Validated canonical commit.</param>
    /// <param name="tryCommit">Native atomic operation; returns Committed or a conditional conflict without changes.</param>
    /// <returns>Committed or replayed evidence, or a consistently located conditional conflict.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The native operation returns another commit's evidence.</exception>
    public static async Task<EntityTransitionOperationResult> CommitAsync(
        IEntityTransitionOperationRepository repository, OperationContext context, EntityTransitionOperationCommit commit,
        Func<OperationContext, EntityTransitionOperationCommit, Task<EntityTransitionOperationResult>> tryCommit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(tryCommit);
        context.ThrowIfCancellationRequested();
        var attempt = await tryCommit(context, commit).ConfigureAwait(false);
        if (attempt.Disposition == EntityTransitionOperationDisposition.Committed)
        {
            if (attempt.Receipt?.Commit.Fingerprint != commit.Fingerprint)
                throw new InvalidOperationException("Native commit returned different canonical evidence.");
            return attempt;
        }
        if (attempt.Disposition is not (EntityTransitionOperationDisposition.ConcurrencyConflict or EntityTransitionOperationDisposition.SubjectStateConflict))
            throw new InvalidOperationException("Native commit must return committed evidence or an unchanged conditional conflict.");
        return await Lookup(repository, context, commit).ConfigureAwait(false) ?? attempt;
    }

    /// <summary>Creates the shared conditional-conflict diagnostic, preserving native failure detail.</summary>
    /// <param name="commit">Attempt whose subject and concurrency evidence identify the failed fence.</param>
    /// <param name="providerDetail">Optional native status detail, retained for operator diagnostics.</param>
    /// <returns>A consistently coded and located conflict with subject and provider context.</returns>
    /// <remarks>Native operator diagnostics may contain private identities and concurrency evidence.
    /// The process adapter projects a safe failure message; direct repository callers must not publish raw diagnostics.</remarks>
    public static EntityTransitionOperationResult Conflict(EntityTransitionOperationCommit commit, string? providerDetail = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var subject = $"Entity '{commit.Request.Subject.EntityType.Value}:{commit.Request.Subject.EntityId.Value}'";
        var detail = string.IsNullOrWhiteSpace(providerDetail) ? "" : " " + providerDetail;
        return commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent
            ? EntityTransitionOperationRepositoryExtensions.SubjectStateConflict(subject + " must be absent." + detail)
            : EntityTransitionOperationRepositoryExtensions.ConcurrencyConflict(subject
                + $" no longer matches concurrency fence '{commit.Write.ExpectedConcurrencyToken?.Value}'." + detail);
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
