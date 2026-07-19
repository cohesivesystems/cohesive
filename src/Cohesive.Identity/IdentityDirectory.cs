using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Identity;

/// <summary>
/// Directory lookup keys extracted from an authenticated request principal.
/// </summary>
/// <param name="PrincipalId">Optional local principal id.</param>
/// <param name="Email">Optional email claim.</param>
/// <param name="Subject">Optional identity-provider subject claim.</param>
/// <param name="ClientId">Optional OAuth client id claim.</param>
public sealed record IdentityPrincipalLookup(
    string? PrincipalId = null,
    string? Email = null,
    string? Subject = null,
    string? ClientId = null
    );

/// <summary>
/// Directory-backed scope grant materialized from an active membership and active scope.
/// </summary>
/// <param name="Scope">Scope covered by the grant.</param>
/// <param name="Membership">Membership that grants capabilities over the scope.</param>
public sealed record IdentityScopeGrantRecord(
    IdentityScopeRecord Scope,
    ScopeMembershipRecord Membership
    );

/// <summary>
/// Identity directory used by request identity resolution.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>
    /// Finds an active principal account by one of its stable identity keys.
    /// </summary>
    /// <param name="lookup">Lookup keys extracted from transport claims or host policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matched principal account, or <see langword="null"/> when no active account matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lookup"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is canceled.</exception>
    /// <exception cref="IdentityDirectoryEvaluationException">
    /// Canonical evaluation is inconclusive, fails, returns a foreign outcome, violates the expected result contract,
    /// cannot be mapped, or finds more than one matching principal.
    /// </exception>
    ValueTask<PrincipalAccountRecord?> FindPrincipalAsync(
        IdentityPrincipalLookup lookup,
        CancellationToken ct = default
        );

    /// <summary>
    /// Lists active scope grants for a principal.
    /// </summary>
    /// <param name="principalId">Principal id whose grants should be listed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Active membership/scope grant records for the principal.</returns>
    /// <exception cref="ArgumentException"><paramref name="principalId"/> is empty or white space.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is canceled.</exception>
    /// <exception cref="IdentityDirectoryEvaluationException">
    /// Canonical evaluation is inconclusive, fails, returns a foreign outcome, violates an expected result contract,
    /// cannot be mapped, or finds an ambiguous principal or scope.
    /// </exception>
    ValueTask<ImmutableArray<IdentityScopeGrantRecord>> ListScopeGrantsAsync(
        string principalId,
        CancellationToken ct = default
        );

    /// <summary>
    /// Finds the principal's default active scope for a scope kind among visible candidate scopes.
    /// </summary>
    /// <param name="principalId">Principal id whose default scope should be resolved.</param>
    /// <param name="scopeKind">Scope kind being selected.</param>
    /// <param name="candidateScopeIds">Visible candidate scope ids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The default scope id, or <see langword="null"/> when no visible default exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidateScopeIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="principalId"/> or <paramref name="scopeKind"/> is empty or white space.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is canceled.</exception>
    /// <exception cref="IdentityDirectoryEvaluationException">
    /// Canonical evaluation is inconclusive, fails, returns a foreign outcome, violates an expected result contract,
    /// cannot be mapped, or finds an ambiguous principal, default membership, or scope.
    /// </exception>
    ValueTask<string?> FindDefaultScopeIdAsync(
        string principalId,
        string scopeKind,
        IReadOnlySet<string> candidateScopeIds,
        CancellationToken ct = default
        );
}

/// <summary>
/// Failure to obtain an authoritative, contract-valid Identity directory result from canonical query evaluation.
/// </summary>
public sealed class IdentityDirectoryEvaluationException : InvalidOperationException
{
    internal IdentityDirectoryEvaluationException(
        string message,
        RelationQueryEvaluation evaluation,
        RelationQueryEvaluationOutcome? outcome = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        Outcome = outcome;
    }

    /// <summary>Exact canonical evaluation whose directory projection failed.</summary>
    public RelationQueryEvaluation Evaluation { get; }

    /// <summary>
    /// Canonical outcome that could not be consumed, or <see langword="null"/> when the evaluator returned no outcome.
    /// </summary>
    public RelationQueryEvaluationOutcome? Outcome { get; }
}

/// <summary>
/// Identity directory backed by canonical relation/query evaluation over registered entity sources.
/// </summary>
/// <param name="evaluator">Canonical evaluator configured with the Identity entity sources.</param>
/// <param name="mappingContext">Optional mapping context used to materialize identity records.</param>
/// <exception cref="ArgumentNullException"><paramref name="evaluator"/> is <see langword="null"/>.</exception>
public sealed class EntityRepositoryIdentityDirectory(
    IRelationQueryEvaluator evaluator,
    ShapeMappingContext? mappingContext = null
    ) : IIdentityDirectory
{
    readonly IRelationQueryEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    readonly ShapeMappingContext mappingContext = mappingContext ?? ShapeMappingContext.Default;

    /// <inheritdoc />
    public async ValueTask<PrincipalAccountRecord?> FindPrincipalAsync(
        IdentityPrincipalLookup lookup,
        CancellationToken ct = default
        )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ct.ThrowIfCancellationRequested();

        var principalId = IdentityBootstrap.NormalizeOptional(lookup.PrincipalId);
        if (principalId is not null)
        {
            var evaluation = IdentityDirectoryQueries.EvaluatePrincipalById(
                CreateEvaluationId("principal-by-id"),
                principalId);
            return await EvaluateSingleAsync<PrincipalAccountRecord>(
                "find principal by id",
                evaluation,
                IdentityDomainModel.PrincipalAccountShape,
                ct);
        }

        var email = IdentityBootstrap.NormalizeEmail(lookup.Email);
        if (email is not null)
        {
            var evaluation = IdentityDirectoryQueries.EvaluatePrincipalByEmail(
                CreateEvaluationId("principal-by-email"),
                email);
            return await EvaluateSingleAsync<PrincipalAccountRecord>(
                "find principal by email",
                evaluation,
                IdentityDomainModel.PrincipalAccountShape,
                ct);
        }

        var subject = IdentityBootstrap.NormalizeOptional(lookup.Subject);
        if (subject is not null)
        {
            var evaluation = IdentityDirectoryQueries.EvaluatePrincipalBySubject(
                CreateEvaluationId("principal-by-subject"),
                subject);
            return await EvaluateSingleAsync<PrincipalAccountRecord>(
                "find principal by subject",
                evaluation,
                IdentityDomainModel.PrincipalAccountShape,
                ct);
        }

        var clientId = IdentityBootstrap.NormalizeOptional(lookup.ClientId);
        if (clientId is not null)
        {
            var evaluation = IdentityDirectoryQueries.EvaluatePrincipalByClientId(
                CreateEvaluationId("principal-by-client-id"),
                clientId);
            return await EvaluateSingleAsync<PrincipalAccountRecord>(
                "find principal by client id",
                evaluation,
                IdentityDomainModel.PrincipalAccountShape,
                ct);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<ImmutableArray<IdentityScopeGrantRecord>> ListScopeGrantsAsync(
        string principalId,
        CancellationToken ct = default
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ct.ThrowIfCancellationRequested();

        if (await FindPrincipalAsync(new(PrincipalId: principalId), ct) is null)
            return ImmutableArray<IdentityScopeGrantRecord>.Empty;

        var membershipEvaluation = IdentityDirectoryQueries.EvaluateActiveMembershipsByPrincipal(
            CreateEvaluationId("active-memberships-by-principal"),
            principalId);
        var (memberships, _) = await EvaluateRowsAsync<ScopeMembershipRecord>(
            "list active memberships by principal",
            membershipEvaluation,
            IdentityDomainModel.ScopeMembershipShape,
            ct);
        var grants = ImmutableArray.CreateBuilder<IdentityScopeGrantRecord>();
        foreach (var membership in memberships)
        {
            var scope = await FindActiveScopeAsync(membership.ScopeKind, membership.ScopeId, ct);
            if (scope is not null)
                grants.Add(new(scope, membership));
        }

        return grants.ToImmutable();
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindDefaultScopeIdAsync(
        string principalId,
        string scopeKind,
        IReadOnlySet<string> candidateScopeIds,
        CancellationToken ct = default
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        ArgumentNullException.ThrowIfNull(candidateScopeIds);
        ct.ThrowIfCancellationRequested();

        if (candidateScopeIds.Count == 0
            || await FindPrincipalAsync(new(PrincipalId: principalId), ct) is null)
        {
            return null;
        }

        var membershipEvaluation =
            IdentityDirectoryQueries.EvaluateActiveDefaultMembershipsByPrincipalAndKind(
                CreateEvaluationId("active-default-membership-by-principal-and-kind"),
                principalId,
                scopeKind,
                [.. candidateScopeIds.Order(StringComparer.Ordinal)]);
        var membership = await EvaluateSingleAsync<ScopeMembershipRecord>(
            "find active default membership by principal and scope kind",
            membershipEvaluation,
            IdentityDomainModel.ScopeMembershipShape,
            ct);
        if (membership is null)
            return null;

        var scope = await FindActiveScopeAsync(membership.ScopeKind, membership.ScopeId, ct);
        return scope is null ? null : membership.ScopeId;
    }

    async ValueTask<IdentityScopeRecord?> FindActiveScopeAsync(
        string scopeKind,
        string scopeId,
        CancellationToken ct)
    {
        var evaluation = IdentityDirectoryQueries.EvaluateActiveScopeByKindAndId(
            CreateEvaluationId("active-scope-by-kind-and-id"),
            scopeKind,
            scopeId);
        return await EvaluateSingleAsync<IdentityScopeRecord>(
            "find active scope by kind and id",
            evaluation,
            IdentityDomainModel.ScopeShape,
            ct);
    }

    async ValueTask<TRecord?> EvaluateSingleAsync<TRecord>(
        string operation,
        RelationQueryEvaluation evaluation,
        QualifiedShapeId expectedShape,
        CancellationToken ct)
        where TRecord : notnull
    {
        var (rows, outcome) = await EvaluateRowsAsync<TRecord>(
            operation,
            evaluation,
            expectedShape,
            ct);
        if (rows.Length <= 1)
            return rows.IsDefaultOrEmpty ? default : rows[0];

        throw Failure(
            operation,
            evaluation,
            outcome,
            $"expected at most one authoritative row but received {rows.Length}");
    }

    async ValueTask<(ImmutableArray<TRecord> Rows, RelationQueryEvaluationOutcome Outcome)>
        EvaluateRowsAsync<TRecord>(
            string operation,
            RelationQueryEvaluation evaluation,
            QualifiedShapeId expectedShape,
            CancellationToken ct)
        where TRecord : notnull
    {
        ct.ThrowIfCancellationRequested();
        RelationQueryEvaluationOutcome? outcome;
        try
        {
            outcome = await evaluator.EvaluateAsync(evaluation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            ct.ThrowIfCancellationRequested();
            throw Failure(
                operation,
                evaluation,
                outcome: null,
                "the configured evaluator threw before producing an authoritative outcome",
                exception);
        }

        ct.ThrowIfCancellationRequested();
        if (outcome is null)
        {
            throw Failure(
                operation,
                evaluation,
                outcome: null,
                "the configured evaluator returned no outcome");
        }

        if (!evaluation.HasSameSemantics(outcome.Evaluation))
        {
            throw Failure(
                operation,
                evaluation,
                outcome,
                "the configured evaluator returned an outcome for a different evaluation");
        }

        if (outcome.Status != RelationQueryExecutionStatus.Succeeded || outcome.Result is null)
            throw Failure(operation, evaluation, outcome, "canonical evaluation was not conclusive");

        var result = outcome.Result;
        if (result.Status != RelationQueryExecutionStatus.Succeeded
            || result.Relation is not null
            || result.QueryResults.Length != 1)
        {
            throw Failure(
                operation,
                evaluation,
                outcome,
                "canonical evaluation did not produce exactly one successful named query result");
        }

        var branch = result.QueryResults[0];
        if (branch.Result != IdentityDirectoryQueries.RowsResultId
            || branch.Kind != RelationQueryExecutionResultKind.Rows
            || branch.State != RelationQueryExecutionOutputState.Complete
            || branch.Shape != expectedShape
            || branch.Rows.Any(static row => !row.IsComplete))
        {
            throw Failure(
                operation,
                evaluation,
                outcome,
                "canonical evaluation returned an incomplete or incompatible named row result");
        }

        var records = ImmutableArray.CreateBuilder<TRecord>(branch.Rows.Length);
        try
        {
            foreach (var row in branch.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row.Identity is not { Kind: ObservationValueKind.String, String: { } identity }
                    || string.IsNullOrWhiteSpace(identity)
                    || row.Value.Fields is null)
                {
                    throw new InvalidOperationException(
                        "An Identity entity result row requires a string identity and an object field payload.");
                }

                var observation = new Observation(
                    row.Shape.ShapeId,
                    identity,
                    row.Value.Fields);
                records.Add(observation.Map<TRecord>(mappingContext));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException
                                          and not StackOverflowException)
        {
            throw Failure(
                operation,
                evaluation,
                outcome,
                "canonical rows could not be materialized as the expected Identity record",
                exception);
        }

        return (records.ToImmutable(), outcome);
    }

    static RelationQueryEvaluationId CreateEvaluationId(string operation) =>
        new($"cohesive.identity/{operation}/{Guid.NewGuid():N}");

    static IdentityDirectoryEvaluationException Failure(
        string operation,
        RelationQueryEvaluation evaluation,
        RelationQueryEvaluationOutcome? outcome,
        string reason,
        Exception? innerException = null)
    {
        var diagnosticSummary = outcome is null ? string.Empty : FormatDiagnostics(outcome);
        var message =
            $"Identity directory operation '{operation}' failed for evaluation '{evaluation.Evaluation.Value}': {reason}.";
        if (!string.IsNullOrWhiteSpace(diagnosticSummary))
            message += " " + diagnosticSummary;
        return new(message, evaluation, outcome, innerException);
    }

    static string FormatDiagnostics(RelationQueryEvaluationOutcome outcome)
    {
        IEnumerable<string> diagnostics = outcome.Diagnostics.Select(static diagnostic =>
            $"evaluation {diagnostic.Code}: {diagnostic.Message}");
        diagnostics = diagnostics.Concat(outcome.Compilation.Diagnostics.Select(static diagnostic =>
            $"compilation {diagnostic.Code}: {diagnostic.Message}"));
        if (outcome.Realization is { } realization)
        {
            diagnostics = diagnostics.Concat(realization.Diagnostics.Select(static diagnostic =>
                $"realization {diagnostic.Code}: {diagnostic.Message}"));
        }
        if (outcome.PhysicalPlanning is { } planning)
        {
            diagnostics = diagnostics.Concat(planning.Diagnostics.Select(static diagnostic =>
                $"planning {diagnostic.Code}: {diagnostic.Message}"));
        }
        if (outcome.PhysicalExecution is { } execution)
        {
            diagnostics = diagnostics.Concat(execution.Diagnostics.Select(static diagnostic =>
                $"execution {diagnostic.Code}: {diagnostic.Message}"));
        }

        var summary = string.Join("; ", diagnostics.Take(8));
        return string.IsNullOrWhiteSpace(summary) ? string.Empty : "Diagnostics: " + summary;
    }
}
