using Cohesive.Model;
using Cohesive.Identity;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Tests.Identity;

public sealed class IdentityDirectoryTests
{
    const string ScopeKind = "test.scope";

    [Fact]
    public void IdentityDirectoryQueries_AreStableTypedCanonicalDefinitions()
    {
        Assert.Same(IdentityDirectoryQueries.PrincipalById, IdentityDirectoryQueries.PrincipalById);
        Assert.Same(
            IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind,
            IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind);

        var documents = new[]
        {
            IdentityDirectoryQueries.PrincipalById,
            IdentityDirectoryQueries.PrincipalByEmail,
            IdentityDirectoryQueries.PrincipalBySubject,
            IdentityDirectoryQueries.PrincipalByClientId,
            IdentityDirectoryQueries.ActiveMembershipsByPrincipal,
            IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind,
            IdentityDirectoryQueries.ActiveScopeByKindAndId
        };
        var definitions = documents
            .Select(static document => Assert.IsType<QueryDefinition>(document.Definition))
            .ToArray();

        Assert.Equal(documents.Length, definitions.Select(static definition => definition.Id).Distinct().Count());
        Assert.Equal(
            documents.Length,
            documents.Select(static document => document.DefinitionFingerprint.Value).Distinct().Count());
        Assert.All(documents, static document =>
            Assert.True(RelationQueryDefinitionValidator.Validate(document.Definition).IsValid));
        Assert.All(definitions, static definition =>
            Assert.All(definition.Body.Parameters, static parameter =>
            {
                Assert.Equal(FieldPresence.Required, parameter.Presence);
                Assert.Equal(QueryParameterDefaultKind.None, parameter.DefaultKind);
                Assert.Null(parameter.DefaultValue);
            }));

        var defaultMembershipDefinition = Assert.IsType<QueryDefinition>(
            IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind.Definition);
        var candidateScopeIds = Assert.Single(
            defaultMembershipDefinition.Body.Parameters,
            static parameter => parameter.Id.Value == "candidateScopeIds");
        var candidateArray = Assert.IsType<ArrayTypeRef>(candidateScopeIds.Type);
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(candidateArray.ElementType).Kind);
    }

    [Fact]
    public async Task InMemoryIdentityDomainRepositories_ResolveActivePrincipalScopeGrants()
    {
        var directory = InMemoryIdentityDomainRepositoryFactory
            .Create(new InMemoryIdentityDirectoryBuilder()
                .AddScope(new(
                    Id: "scope-active",
                    Kind: ScopeKind,
                    Name: "Active Scope",
                    PartitionKey: "scope-active-partition"
                    ))
                .AddScope(new(
                    Id: "scope-archived",
                    Kind: ScopeKind,
                    Name: "Archived Scope",
                    Status: IdentityScopeStatus.Archived
                    ))
                .AddScope(new(
                    Id: "scope-revoked",
                    Kind: ScopeKind,
                    Name: "Revoked Scope"
                    ))
                .AddPrincipal(new(
                    Id: "user:active",
                    Kind: PrincipalKind.User,
                    Email: "active@example.com"
                    ))
                .AddPrincipal(new(
                    Id: "user:inactive",
                    Kind: PrincipalKind.User,
                    Status: PrincipalAccountStatus.Deactivated,
                    Email: "inactive@example.com"
                    ))
                .AddScopeGrant(
                    "user:active",
                    "scope-active",
                    ScopeKind,
                    ["identity.read"],
                    isDefaultScope: true
                    )
                .AddScopeGrant(
                    "user:active",
                    "scope-archived",
                    ScopeKind,
                    ["identity.read"]
                    )
                .AddMembership(new(
                    Id: "user:active:test.scope:scope-revoked",
                    PrincipalId: "user:active",
                    ScopeId: "scope-revoked",
                    ScopeKind: ScopeKind,
                    Capabilities: ["identity.read"],
                    Status: ScopeMembershipStatus.Revoked
                    ))
                .AddScopeGrant(
                    "user:inactive",
                    "scope-active",
                    ScopeKind,
                    ["identity.read"],
                    membershipId: "user:inactive:test.scope:scope-active"
                    )
                .Build())
            .CreateDirectory();

        var activePrincipal = await directory.FindPrincipalAsync(new(Email: " ACTIVE@EXAMPLE.COM "));
        var precedencePrincipal = await directory.FindPrincipalAsync(new(
            PrincipalId: "user:active",
            Email: "inactive@example.com"));
        var inactivePrincipal = await directory.FindPrincipalAsync(new(Email: "inactive@example.com"));
        var activeGrants = await directory.ListScopeGrantsAsync("user:active");
        var inactiveGrants = await directory.ListScopeGrantsAsync("user:inactive");
        var defaultScopeId = await directory.FindDefaultScopeIdAsync(
            "user:active",
            ScopeKind,
            new HashSet<string>(["scope-active", "scope-archived"], StringComparer.Ordinal)
            );

        Assert.NotNull(activePrincipal);
        Assert.Equal("user:active", precedencePrincipal?.Id);
        Assert.Null(inactivePrincipal);
        var grant = Assert.Single(activeGrants);
        Assert.Equal("scope-active", grant.Scope.Id);
        Assert.Equal("scope-active-partition", grant.Scope.PartitionKey);
        Assert.Empty(inactiveGrants);
        Assert.Equal("scope-active", defaultScopeId);
    }

    [Fact]
    public async Task InMemoryIdentityDomainRepositories_FilterDefaultMembershipsByVisibleCandidates()
    {
        var directory = InMemoryIdentityDomainRepositoryFactory.Create(
            scopes:
            [
                new("scope-hidden", ScopeKind, "Hidden Scope"),
                new("scope-visible", ScopeKind, "Visible Scope")
            ],
            principals:
            [
                new("user:active", PrincipalKind.User)
            ],
            memberships:
            [
                new(
                    "membership-a-hidden",
                    "user:active",
                    "scope-hidden",
                    ScopeKind,
                    [],
                    IsDefaultScope: true),
                new(
                    "membership-b-visible",
                    "user:active",
                    "scope-visible",
                    ScopeKind,
                    [],
                    IsDefaultScope: true)
            ]).CreateDirectory();

        var defaultScopeId = await directory.FindDefaultScopeIdAsync(
            "user:active",
            ScopeKind,
            new HashSet<string>(["scope-visible"], StringComparer.Ordinal));

        Assert.Equal("scope-visible", defaultScopeId);
    }

    [Fact]
    public async Task EntityRepositoryIdentityDirectory_FailsClosedWhenCanonicalEvaluationFails()
    {
        FailedCanonicalEvaluator evaluator = new();
        EntityRepositoryIdentityDirectory directory = new(evaluator);

        var exception = await Assert.ThrowsAsync<IdentityDirectoryEvaluationException>(async () =>
            await directory.FindPrincipalAsync(new(PrincipalId: "user:active")));

        Assert.NotNull(evaluator.Outcome);
        Assert.Same(evaluator.Outcome, exception.Outcome);
        Assert.True(exception.Evaluation.HasSameSemantics(evaluator.Outcome.Evaluation));
        Assert.Contains("canonical evaluation was not conclusive", exception.Message, StringComparison.Ordinal);
        Assert.Contains(FailedCanonicalEvaluator.DiagnosticCode, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityRepositoryIdentityDirectory_FailsClosedWhenCanonicalEvidenceIsIncomplete()
    {
        var repositories = InMemoryIdentityDomainRepositoryFactory.Create(
            scopes:
            [
                new("scope-a", ScopeKind, "Scope A"),
                new("scope-b", ScopeKind, "Scope B")
            ],
            principals:
            [
                new("user:active", PrincipalKind.User)
            ],
            memberships:
            [
                new("membership-a", "user:active", "scope-a", ScopeKind, []),
                new("membership-b", "user:active", "scope-b", ScopeKind, [])
            ]);
        RelationQueryPhysicalPlanningPolicy constrainedPolicy = new(
            new("tests/identity/incomplete/v1"),
            conventionSetVersion: "tests/identity/incomplete-conventions/v1",
            maximumBatchSize: 1,
            maximumBufferedRows: 1,
            maximumLocalRows: 1,
            maximumFanOut: 1,
            maximumReferenceKeysPerObservation: 1,
            maximumConcurrency: 1);
        var directory = repositories.CreateDirectory(physicalPlanningPolicy: constrainedPolicy);

        var exception = await Assert.ThrowsAsync<IdentityDirectoryEvaluationException>(async () =>
            await directory.ListScopeGrantsAsync("user:active"));

        Assert.NotNull(exception.Outcome);
        Assert.Equal(RelationQueryExecutionStatus.Incomplete, exception.Outcome.Status);
        Assert.Contains("canonical evaluation was not conclusive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityRepositoryIdentityDirectory_RejectsAmbiguousPrincipalLookup()
    {
        var directory = InMemoryIdentityDomainRepositoryFactory.Create(
            scopes: [],
            principals:
            [
                new("user:a", PrincipalKind.User, Email: "shared@example.com"),
                new("user:b", PrincipalKind.User, Email: "shared@example.com")
            ],
            memberships: []).CreateDirectory();

        var exception = await Assert.ThrowsAsync<IdentityDirectoryEvaluationException>(async () =>
            await directory.FindPrincipalAsync(new(Email: "shared@example.com")));

        Assert.Contains("expected at most one authoritative row", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityRepositoryIdentityDirectory_ObservesCancellationBeforeEvaluation()
    {
        UnexpectedEvaluator evaluator = new();
        EntityRepositoryIdentityDirectory directory = new(evaluator);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await directory.FindPrincipalAsync(new(PrincipalId: "user:active"), cancellation.Token));

        Assert.False(evaluator.WasCalled);
    }

    [Fact]
    public async Task EntityRepositoryIdentityDirectory_ObservesCancellationRaisedDuringEvaluation()
    {
        using CancellationTokenSource cancellation = new();
        CancelingEvaluator evaluator = new(cancellation);
        EntityRepositoryIdentityDirectory directory = new(evaluator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await directory.FindPrincipalAsync(
                new(PrincipalId: "user:active"),
                cancellation.Token));

        Assert.True(evaluator.WasCalled);
    }

    sealed class FailedCanonicalEvaluator : IRelationQueryEvaluator
    {
        public const string DiagnosticCode = "IDENTITY-TEST-EVALUATION-FAILED";

        public RelationQueryEvaluationOutcome? Outcome { get; private set; }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
            RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
            if (!compilation.IsSuccessful)
                throw new InvalidOperationException("The Identity fixture requires a statically valid query.");

            Outcome = new(
                evaluation,
                compilation,
                diagnostics:
                [
                    new(
                        DiagnosticCode,
                        DiagnosticSeverity.Error,
                        "The Identity test evaluator intentionally failed before realization.")
                ]);
            return ValueTask.FromResult(Outcome);
        }
    }

    sealed class UnexpectedEvaluator : IRelationQueryEvaluator
    {
        public bool WasCalled { get; private set; }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
            RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Evaluation should not be invoked after host cancellation.");
        }
    }

    sealed class CancelingEvaluator(CancellationTokenSource cancellation) : IRelationQueryEvaluator
    {
        public bool WasCalled { get; private set; }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
            RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            cancellation.Cancel();
            throw new InvalidOperationException("The evaluator was interrupted after cancellation.");
        }
    }
}
