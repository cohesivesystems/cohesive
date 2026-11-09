using Cohesive.Configuration;
using Cohesive.Execution;
using Cohesive.Integrations;
using Xunit;
using Layer = Cohesive.Configuration.SelectionLayer<Cohesive.Integrations.IntegrationChoice, Cohesive.Integrations.IntegrationSelectionPolicy>;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static readonly SelectionScope Tenant = new("research");
    static readonly SelectionScope User = new("research", "analyst");
    static readonly SelectionGrant[] Grants = [new("admin", new()), new("tenant-admin", Tenant), new("analyst", User, true)];
    static Layer SystemLayer() => Layer.Define("config/system", "v1", "admin", new())
        .Prefer(IntegrationChoice.Select(Profile(), Connection()))
        .Enforce(new(AllowedProviders: ["tiingo"], MaximumBatchSize: 5));
    static IntegrationSelectionResult ResolveScoped(params ExecutionDefinitionDocument[] layers) =>
        IntegrationSelection.Resolve(MarketData, User, layers, Grants, [Profile()], [Connection(), Connection("premium")]);

    [Fact]
    public void HierarchyPinsWinnerAndEveryConstraintAndIsOrderIndependent()
    {
        var system = SystemLayer().Document;
        var tenant = Layer.Define("config/tenant", "v1", "tenant-admin", Tenant)
            .Prefer(IntegrationChoice.Select(Profile(), Connection("premium"), 3))
            .Enforce(new(RequiredEntitlements: ["daily"], MaximumBatchSize: 4)).Document;
        var user = Layer.Define("config/user", "v1", "analyst", User)
            .Prefer(IntegrationChoice.Select(Profile(), Connection("premium"), 4)).Document;
        var selected = ResolveScoped(system, tenant, user);
        Assert.True(selected.Resolution.Validation.IsValid);
        Assert.Equal(selected.Resolution.Snapshot!.Reference, ResolveScoped(user, system, tenant).Resolution.Snapshot!.Reference);
        var snapshot = selected.Resolution.Snapshot.GetDefinition<ScopedSelectionSnapshot<IntegrationChoice>>();
        Assert.Equal(user.Reference, snapshot.Winner);
        Assert.Equal(3, snapshot.Contributions.Length);
        var compiled = Connected(selected.Profile!).Compile(selected.Bindings!);
        Valid(compiled);
        Assert.Equal(4, Resolution(compiled).EffectiveBatchSize);
        Assert.Equal(selected.Resolution.Snapshot.Reference, Resolution(compiled).Configuration);
        Assert.False(Connected(selected.Profile!).WithBatchSize(5).Compile(selected.Bindings!).IsSuccessful);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents, out var catalog).IsValid);
        Assert.True(IntegrationSelection.CheckDispatch(selected.Resolution.Snapshot, [user, system, tenant], Grants, catalog!).IsValid);
    }

    [Fact]
    public void DescendantAndExplicitChoiceCannotRelaxAncestorPolicy()
    {
        var user = Layer.Define("config/local", "v1", "analyst", User, SelectionPrecedence.Local)
            .Prefer(IntegrationChoice.Select(Profile(), Connection(), 6))
            .Enforce(new(MaximumBatchSize: 100)).Document;
        var result = ResolveScoped(SystemLayer().Document, user);
        Assert.Null(result.Resolution.Snapshot);
        Assert.Contains(result.Resolution.Validation.Diagnostics, d => d.Code == "integrations.policy.batch");
    }

    [Theory]
    [InlineData("providers")]
    [InlineData("resources")]
    [InlineData("entitlements")]
    [InlineData("bounds")]
    public void AllPolicyConstraintsAccumulate(string scenario)
    {
        var policy = scenario switch
        {
            "providers" => new IntegrationSelectionPolicy(AllowedProviders: []),
            "resources" => new IntegrationSelectionPolicy(AllowedResources: ["other-account"]),
            "entitlements" => new IntegrationSelectionPolicy(RequiredEntitlements: ["not-granted"]),
            _ => new IntegrationSelectionPolicy(MinimumBatchSize: 6)
        };
        var tenant = Layer.Define("config/policy", "v1", "tenant-admin", Tenant).Enforce(policy).Document;
        Assert.False(ResolveScoped(SystemLayer().Document, tenant).Resolution.Validation.IsValid);
    }

    [Fact]
    public void EqualTierChoicesAndUnauthorizedOverridesFail()
    {
        var a = Layer.Define("config/a", "v1", "analyst", User).Prefer(IntegrationChoice.Select(Profile(), Connection())).Document;
        var b = Layer.Define("config/b", "v1", "analyst", User).Prefer(IntegrationChoice.Select(Profile(), Connection())).Document;
        Assert.Contains(ResolveScoped(a, b).Resolution.Validation.Diagnostics, d => d.Code == "configuration.selection.ambiguous");
        var unauthorized = Layer.Define("config/local", "v1", "tenant-admin", Tenant, SelectionPrecedence.Local)
            .Prefer(IntegrationChoice.Select(Profile(), Connection())).Document;
        Assert.Contains(ResolveScoped(SystemLayer().Document, unauthorized).Resolution.Validation.Diagnostics,
            d => d.Code == "configuration.selection.unauthorized");
    }

    [Fact]
    public void TenantAndUserScopesAreIsolatedAndPublicationAuthorityIsExact()
    {
        var unrelated = Layer.Define("config/other", "v1", "someone", new("other", "analyst"))
            .Prefer(IntegrationChoice.Select(Profile(), Connection(), 99)).Enforce(new(AllowedProviders: [])).Document;
        Assert.Equal(ResolveScoped(SystemLayer().Document).Resolution.Snapshot!.Reference,
            ResolveScoped(unrelated, SystemLayer().Document).Resolution.Snapshot!.Reference);
        var forged = Layer.Define("config/forged", "v1", "analyst", new()).Enforce(new(MaximumBatchSize: 1)).Document;
        Assert.False(ResolveScoped(SystemLayer().Document, forged).Resolution.Validation.IsValid);
    }

    [Fact]
    public void CurrentRestrictionsBlockNewEffectsWithoutRebindingOrLosingOriginalRuntime()
    {
        var original = ResolveScoped(SystemLayer().Document);
        var compiled = Connected(original.Profile!).Compile(original.Bindings!);
        Valid(compiled);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents, out var catalog).IsValid);
        var changedDefault = Layer.Define("config/system", "v2", "admin", new())
            .Prefer(IntegrationChoice.Select(Profile(), Connection("premium"), 3)).Document;
        Assert.True(IntegrationSelection.CheckDispatch(original.Resolution.Snapshot!, [changedDefault], Grants, catalog!).IsValid);
        var revoked = Layer.Define("config/system", "v3", "admin", new()).Enforce(new(AllowedResources: [])).Document;
        Assert.False(IntegrationSelection.CheckDispatch(original.Resolution.Snapshot!, [revoked], Grants, catalog!).IsValid);
        var originalHandler = new object();
        var runtime = new IntegrationRuntime().Register(Profile(), Connection(), originalHandler);
        Assert.Same(originalHandler, runtime.ResolveForDispatch<object>(compiled, [changedDefault], Grants));
        Assert.Throws<InvalidOperationException>(() => runtime.ResolveForDispatch<object>(compiled, [revoked], Grants));
        Assert.Same(originalHandler, runtime.Resolve<object>(compiled)); // Original effect reconciliation; not permission for a new dispatch.
        Assert.Equal(Connection().Document.Reference, Resolution(compiled).Connection);
    }

    [Fact]
    public void FrameworkPrecedenceIsIndependentOfScopeSpecificity()
    {
        var scoped = Layer.Define("config/user", "v1", "analyst", User, SelectionPrecedence.Framework)
            .Prefer(IntegrationChoice.Select(Profile(), Connection("premium"))).Document;
        var result = ResolveScoped(SystemLayer().Document, scoped);
        Assert.Equal(SystemLayer().Document.Reference, result.Resolution.Snapshot!.GetDefinition<ScopedSelectionSnapshot<IntegrationChoice>>().Winner);
    }
    [Fact]
    public void PolicySetsNormalizeAndSourceValuesAreSnapshots()
    {
        var a = Layer.Define("config/policy", "v1", "admin", new()).Enforce(new(AllowedProviders: ["b", "a", "b"]));
        var b = Layer.Define("config/policy", "v1", "admin", new()).Enforce(new(AllowedProviders: ["a", "b"]));
        Assert.Equal(a.Document.Reference, b.Document.Reference);
        var choice = new PredicateSource();
        var source = SelectionLayer<PredicateSource, PredicateSource>.Define("config/snapshot", "v1", "admin", new()).Prefer(choice);
        var before = source.Document.Reference;
        choice.Required = false;
        Assert.Equal(before, source.Document.Reference);
    }

    [Fact]
    public void ForgedSnapshotWinnerAndMissingLineageCannotDispatch()
    {
        var selected = ResolveScoped(SystemLayer().Document);
        var compiled = Connected(selected.Profile!).Compile(selected.Bindings!);
        Valid(compiled);
        var snapshot = selected.Resolution.Snapshot!;
        var value = snapshot.GetDefinition<ScopedSelectionSnapshot<IntegrationChoice>>();
        var forged = ExecutionDefinitionDocument.Create(snapshot.Kind, snapshot.Metadata.DefinitionId, snapshot.Metadata.RevisionId,
            value with { Winner = Connection().Document.Reference }, snapshot.Metadata.Provenance);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents, out var catalog).IsValid);
        Assert.False(IntegrationSelection.CheckDispatch(forged, [SystemLayer().Document], Grants, catalog!).IsValid);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents.Where(d => d.Reference != SystemLayer().Document.Reference), out var incomplete).IsValid);
        Assert.False(IntegrationSelection.CheckDispatch(snapshot, [SystemLayer().Document], Grants, incomplete!).IsValid);
    }
}
