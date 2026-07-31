using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationInverseImpactTests
{
    const long MaximumAffectedRoots = 100;
    const long MaximumGlobalRoots = 500;
    const long ReadBytes = 64_000;
    const long WriteItems = 100;
    const long WriteBytes = 1_000_000;

    static readonly QueryNodeId CustomerSourceNode = new("customers");
    static readonly QueryNodeId LoadTraversalNode = new("customer-loads");
    static readonly QueryNodeId ProjectionNode = new("project-customer-load");
    static readonly ValueBindingId ResultBinding = new("customerLoad");

    [Fact]
    public void InverseRootedRelation_WithBeforeImages_UsesExactReferenceExtraction()
    {
        var fixture = CreateFixture(TestFacilities.BeforeImage);

        var compilation = MaterializationImpactPlanCompiler.Compile(fixture.Document, ExactPolicy());

        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        var route = Assert.Single(plan.Routes, candidate => candidate.ChangeInput == fixture.LoadInput);
        var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
        var step = Assert.Single(strategy.Steps);
        Assert.Equal(MaterializationImpactPrecision.Exact, route.Precision);
        Assert.Equal(MaterializationImpactLineageKind.BeforeAndAfterRelationshipReferences, strategy.Lineage);
        Assert.Equal(MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction, step.Operation);
        Assert.Equal(fixture.LoadInput, step.RelationshipInput);
        Assert.Equal(fixture.LoadInput, step.ReferenceSourceInput);
        var capability = Assert.Single(route.Capabilities);
        Assert.Equal($"{fixture.LoadInput.Value}/changes", capability.Requirement.Value);
    }

    [Fact]
    public void InverseRootedRelation_WithoutBeforeImages_UsesExactContributorLedgerWhenDeclared()
    {
        var fixture = CreateFixture(TestFacilities.ContributorLedger);

        var compilation = MaterializationImpactPlanCompiler.Compile(fixture.Document, ExactPolicy());

        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        var route = Assert.Single(plan.Routes, candidate => candidate.ChangeInput == fixture.LoadInput);
        var strategy = Assert.IsType<MaterializationContributorLedgerImpactStrategy>(route.Strategy);
        Assert.Equal(fixture.LoadInput, strategy.ContributorInput);
        Assert.Equal(MaterializationImpactPrecision.Exact, route.Precision);
        Assert.Equal(MaterializationImpactLineageKind.PriorLedgerAndCurrentRelationshipState, strategy.Lineage);
        Assert.Contains(
            route.Capabilities,
            static capability => capability.Role == MaterializationEndpointRole.Target
                && capability.Requirement.Value == "target/contributor-ledger");
    }

    [Fact]
    public void InverseRootedRelation_WithoutExactLineage_UsesOnlyExplicitBoundedGlobalFallback()
    {
        var fixture = CreateFixture(TestFacilities.GlobalEnumeration);

        var compilation = MaterializationImpactPlanCompiler.Compile(fixture.Document, GlobalPolicy());

        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        var route = Assert.Single(plan.Routes, candidate => candidate.ChangeInput == fixture.LoadInput);
        var strategy = Assert.IsType<MaterializationBoundedGlobalImpactStrategy>(route.Strategy);
        Assert.Equal(fixture.CustomerInput, strategy.RootInput);
        Assert.Equal(MaterializationImpactPrecision.Conservative, route.Precision);
        Assert.Equal(MaximumGlobalRoots, route.MaximumAffectedRoots);
    }

    [Fact]
    public void InverseRootedRelation_WithoutLineageOrAdmittedFallback_FailsClosed()
    {
        var fixture = CreateFixture(TestFacilities.None);

        var compilation = MaterializationImpactPlanCompiler.Compile(fixture.Document, ExactPolicy());

        Assert.False(compilation.IsSuccessful);
        Assert.Null(compilation.Plan);
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationImpactDiagnosticCodes.StrategyUnavailable
                && diagnostic.Message.Contains(fixture.LoadInput.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void ContributorLedger_RetainsManyRootsAndItemsInCanonicalFencedScopes()
    {
        var fixture = CreateFixture(TestFacilities.ContributorLedger);
        var plan = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(fixture.Document, ExactPolicy()).Plan);
        MaterializationContributorLedgerScope scope = new(
            materialization: fixture.Document.Definition.Id,
            generation: new("generation-a"),
            definitionFingerprint: fixture.Document.DefinitionFingerprint,
            impactPlanFingerprint: plan.Fingerprint);
        MaterializationContributorLedgerKey key = new(
            scope,
            input: fixture.LoadInput,
            shape: FederatedLoadRelationFixture.LoadShapeId,
            contributorIdentity: "load-42");

        MaterializationContributorLedgerEntry entry = new(
            key,
            roots:
            [
                new(
                    rootIdentity: "customer-b",
                    materializedItems: [new("item-b2"), new("item-b1")]),
                new(
                    rootIdentity: "customer-a",
                    materializedItems: [new("item-a")])
            ]);

        Assert.Equal(["customer-a", "customer-b"], entry.Roots.Select(static root => root.RootIdentity));
        Assert.Equal(
            ["item-b1", "item-b2"],
            entry.Roots[1].MaterializedItems.Select(static item => item.Value));

        MaterializationContributorLedgerKey nextGenerationKey = new(
            new(
                materialization: scope.Materialization,
                generation: new("generation-b"),
                definitionFingerprint: scope.DefinitionFingerprint,
                impactPlanFingerprint: scope.ImpactPlanFingerprint),
            input: fixture.LoadInput,
            shape: FederatedLoadRelationFixture.LoadShapeId,
            contributorIdentity: "load-42");
        MaterializationContributorLedgerKey nextPlanKey = new(
            new(
                materialization: scope.Materialization,
                generation: scope.Generation,
                definitionFingerprint: scope.DefinitionFingerprint,
                impactPlanFingerprint: new(
                    algorithm: plan.Fingerprint.Algorithm,
                    canonicalization: plan.Fingerprint.Canonicalization,
                    value: "0")),
            input: fixture.LoadInput,
            shape: FederatedLoadRelationFixture.LoadShapeId,
            contributorIdentity: "load-42");

        Assert.NotEqual(key, nextGenerationKey);
        Assert.NotEqual(key, nextPlanKey);
        Assert.NotEqual(entry, new MaterializationContributorLedgerEntry(nextGenerationKey, entry.Roots));
        Assert.NotEqual(entry, new MaterializationContributorLedgerEntry(nextPlanKey, entry.Roots));
        Assert.Equal(key.ContributorIdentity, nextGenerationKey.ContributorIdentity);
        Assert.Equal(key.ContributorIdentity, nextPlanKey.ContributorIdentity);
    }

    [Fact]
    public void InverseDirectionRoutes_MatchFullRecomputationForCreateUpdateDeleteAndReferenceMoves()
    {
        const int Seed = 71_905;
        const int Cases = 256;
        var beforeImageFixture = CreateFixture(TestFacilities.BeforeImage);
        var ledgerFixture = CreateFixture(TestFacilities.ContributorLedger);
        var beforeImagePlan = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(beforeImageFixture.Document, ExactPolicy()).Plan);
        var ledgerPlan = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(ledgerFixture.Document, ExactPolicy()).Plan);
        var beforeImageRoute = Assert.Single(
            beforeImagePlan.Routes,
            route => route.ChangeInput == beforeImageFixture.LoadInput);
        var ledgerRoute = Assert.Single(
            ledgerPlan.Routes,
            route => route.ChangeInput == ledgerFixture.LoadInput);
        var beforeImageStrategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(
            beforeImageRoute.Strategy);
        var ledgerStrategy = Assert.IsType<MaterializationContributorLedgerImpactStrategy>(ledgerRoute.Strategy);
        var beforeImageStep = Assert.Single(beforeImageStrategy.Steps);
        var currentRootStep = Assert.Single(ledgerStrategy.CurrentRootSteps);
        Assert.Equal(
            MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction,
            beforeImageStep.Operation);
        Assert.Equal(
            MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction,
            currentRootStep.Operation);
        Assert.Equal(beforeImageFixture.LoadInput, beforeImageStep.ReferenceSourceInput);
        Assert.Equal(ledgerFixture.LoadInput, currentRootStep.ReferenceSourceInput);
        var random = new Random(Seed);

        for (var caseIndex = 0; caseIndex < Cases; caseIndex++)
        {
            List<string> customers = ["customer-a", "customer-b", "customer-c", "customer-d"];
            Dictionary<string, InverseLoadState> before = Enumerable.Range(start: 0, count: 12)
                .Select(index => new InverseLoadState(
                    Id: $"load-{index}",
                    CustomerId: customers[random.Next(customers.Count)],
                    Status: $"status-{random.Next(minValue: 0, maxValue: 5)}"))
                .ToDictionary(static load => load.Id, StringComparer.Ordinal);
            Dictionary<string, InverseLoadState> after = new(before, StringComparer.Ordinal);
            InverseLoadState? changedBefore;
            InverseLoadState? changedAfter;

            switch (caseIndex % 4)
            {
                case 0:
                    changedBefore = null;
                    changedAfter = new(
                        Id: $"created-{caseIndex}",
                        CustomerId: customers[random.Next(customers.Count)],
                        Status: $"created-status-{caseIndex}");
                    after.Add(changedAfter.Id, changedAfter);
                    break;
                case 1:
                    changedBefore = PickLoad(before, random);
                    changedAfter = changedBefore with { Status = $"updated-status-{caseIndex}" };
                    after[changedAfter.Id] = changedAfter;
                    break;
                case 2:
                    changedBefore = PickLoad(before, random);
                    changedAfter = null;
                    after.Remove(changedBefore.Id);
                    break;
                default:
                    changedBefore = PickLoad(before, random);
                    var destinations = customers.Where(customer => customer != changedBefore.CustomerId).ToArray();
                    changedAfter = changedBefore with
                    {
                        CustomerId = destinations[random.Next(destinations.Length)]
                    };
                    after[changedAfter.Id] = changedAfter;
                    break;
            }

            var expected = ChangedRoots(Recompute(before.Values), Recompute(after.Values));
            var beforeAndAfterRoots = ResolveBeforeAndAfterRoute(
                beforeImageRoute,
                changedBefore,
                changedAfter);
            var priorLedgerRoots = changedBefore is null
                ? ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)
                : ImmutableHashSet.Create(StringComparer.Ordinal, changedBefore.CustomerId);
            var ledgerAndCurrentRoots = ResolveLedgerAndCurrentRoute(
                ledgerRoute,
                priorLedgerRoots,
                changedAfter);

            Assert.Equal(expected.Order(StringComparer.Ordinal), beforeAndAfterRoots.Order(StringComparer.Ordinal));
            Assert.Equal(expected.Order(StringComparer.Ordinal), ledgerAndCurrentRoots.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void SetOutput_NeverUsesContributorLedgerAndRequiresGlobalInvalidation()
    {
        var globalFixture = CreateFixture(
            TestFacilities.ContributorLedger | TestFacilities.GlobalEnumeration,
            outputMode: RelationOutputMode.Set);

        var compilation = MaterializationImpactPlanCompiler.Compile(globalFixture.Document, GlobalPolicy());

        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        Assert.All(
            plan.Routes,
            static route => Assert.IsType<MaterializationBoundedGlobalImpactStrategy>(route.Strategy));

        var exactOnlyFixture = CreateFixture(
            TestFacilities.ContributorLedger,
            outputMode: RelationOutputMode.Set);
        var exactOnly = MaterializationImpactPlanCompiler.Compile(exactOnlyFixture.Document, ExactPolicy());
        Assert.False(exactOnly.IsSuccessful);
        Assert.Null(exactOnly.Plan);
        Assert.Contains(
            exactOnly.Diagnostics,
            static diagnostic => diagnostic.Code is MaterializationImpactDiagnosticCodes.StrategyUnavailable
                or MaterializationImpactDiagnosticCodes.RelationshipPathUnavailable);
    }

    static MaterializationImpactPlanningPolicy ExactPolicy() => new(
        id: new("tests/inverse-impact/exact/v1"),
        strategyPreference:
        [
            MaterializationImpactStrategyKind.InverseTraversal,
            MaterializationImpactStrategyKind.ContributorLedger
        ],
        maximumAffectedRoots: MaximumAffectedRoots,
        maximumReadBytes: ReadBytes,
        maximumLedgerWriteBytes: WriteBytes);

    static MaterializationImpactPlanningPolicy GlobalPolicy() => new(
        id: new("tests/inverse-impact/global/v1"),
        strategyPreference:
        [
            MaterializationImpactStrategyKind.InverseTraversal,
            MaterializationImpactStrategyKind.ContributorLedger,
            MaterializationImpactStrategyKind.BoundedGlobalInvalidation
        ],
        maximumAffectedRoots: MaximumAffectedRoots,
        maximumReadBytes: ReadBytes,
        maximumLedgerWriteBytes: WriteBytes,
        maximumGlobalRoots: MaximumGlobalRoots);

    static InverseFixture CreateFixture(
        TestFacilities facilities,
        RelationOutputMode outputMode = RelationOutputMode.ManyPerRoot)
    {
        RelationDefinition definition = new(
            id: new("customer-loads"),
            name: new("CustomerLoads"),
            body: new(
            [
                new SourceQueryNode(
                    CustomerSourceNode,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.CustomerShapeId),
                new TraverseRelationshipQueryNode(
                    LoadTraversalNode,
                    CustomerSourceNode,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.LoadCustomerRelationshipId,
                    RelationshipTraversalDirection.Inverse,
                    FederatedLoadRelationFixture.LoadBinding,
                    JoinKind.Inner,
                    QueryInputRequirement.Required),
                new ProjectQueryNode(
                    ProjectionNode,
                    LoadTraversalNode,
                    ResultBinding,
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-load-id"),
                            FederatedLoadRelationFixture.SearchIdPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadIdPath)),
                        new(
                            new("assign-customer-name"),
                            FederatedLoadRelationFixture.SearchCustomerNamePath,
                            Expr.Field(
                                FederatedLoadRelationFixture.CustomerBinding,
                                FederatedLoadRelationFixture.CustomerNamePath)),
                        new(
                            new("assign-load-status"),
                            FederatedLoadRelationFixture.SearchEquipmentNumberPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadStatusPath))
                    ])
            ]),
            rootBinding: FederatedLoadRelationFixture.CustomerBinding,
            output: new(
                node: ProjectionNode,
                shape: FederatedLoadRelationFixture.LoadSearchShapeId,
                mode: outputMode,
                key: Expr.Field(
                    ResultBinding,
                    FederatedLoadRelationFixture.SearchIdPath)));
        var relationDocument = RelationQueryDocument.FromDefinition(definition);
        RelationQueryCompilationRequest request = new(
            definitionDocument: relationDocument,
            shapeDocuments: FederatedLoadRelationFixture.ShapeGraphDocuments,
            relationshipCatalogDocument: FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var relationCompilation = RelationQueryStaticCompiler.Compile(request);
        Assert.True(
            relationCompilation.IsSuccessful,
            string.Join(Environment.NewLine, relationCompilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var relationPlan = Assert.IsType<CompiledRelationQueryPlan>(relationCompilation.Plan);
        var output = Assert.Single(
            relationPlan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var customerInput = Assert.Single(
            relationPlan.InputContract.Sources,
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot).Input.Id;
        var loadInput = Assert.Single(relationPlan.InputContract.Traversals).Input.Id;
        var relation = MaterializationRelationReference.From(request, output.Id);

        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            SourceRequirement(
                customerInput,
                rebuildRead: MaterializationCapabilityKind.SourceBoundedEnumeration,
                changeGuarantees: ChangeGuarantees(beforeImage: false),
                enumerationModes: facilities.HasFlag(TestFacilities.GlobalEnumeration)
                    ? MaterializationSynchronizationMode.All
                    : MaterializationSynchronizationMode.Rebuild),
            SourceRequirement(
                loadInput,
                rebuildRead: MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                changeGuarantees: ChangeGuarantees(facilities.HasFlag(TestFacilities.BeforeImage)),
                enumerationModes: MaterializationSynchronizationMode.Rebuild)
        ];
        ImmutableArray<MaterializationCapabilityRequirement> targetCapabilities =
        [
            TargetRequirement("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation, MaterializationSynchronizationMode.Rebuild),
            TargetRequirement("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert, MaterializationSynchronizationMode.All),
            TargetRequirement("target/delete", MaterializationCapabilityKind.TargetBulkDelete, MaterializationSynchronizationMode.All),
            TargetRequirement("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationSynchronizationMode.All),
            TargetRequirement("target/seal", MaterializationCapabilityKind.TargetSeal, MaterializationSynchronizationMode.Rebuild),
            TargetRequirement("target/validation", MaterializationCapabilityKind.TargetValidation, MaterializationSynchronizationMode.Rebuild),
            TargetRequirement("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion, MaterializationSynchronizationMode.Rebuild),
            TargetRequirement("target/retirement", MaterializationCapabilityKind.TargetRetirement, MaterializationSynchronizationMode.Rebuild),
            TargetRequirement("target/cleanup", MaterializationCapabilityKind.TargetCleanup, MaterializationSynchronizationMode.Rebuild)
        ];
        if (facilities.HasFlag(TestFacilities.ContributorLedger))
        {
            targetCapabilities = targetCapabilities.Add(TargetRequirement(
                id: "target/contributor-ledger",
                capability: MaterializationCapabilityKind.TargetContributorLedger,
                modes: MaterializationSynchronizationMode.Incremental));
        }

        MaterializationDefinition materialization = new(
            id: new("customer-loads/search"),
            relation,
            sources,
            targetCapabilities,
            updatePolicy: new(
                supportedModes: MaterializationSynchronizationMode.All,
                consistency: MaterializationConsistencyKind.BaselinePlusCatchUp,
                idempotency: MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(
                maximumAttempts: 5,
                exhaustedDisposition: MaterializationFailureDisposition.Stop),
            freshnessPolicy: new(
                maximumLagMilliseconds: 30_000,
                maximumUnsettledMilliseconds: 10_000),
            controlLoops: [],
            provenance: new(
                producer: new("tests/inverse-impact"),
                source: new("tests/inverse-impact"),
                origin: DocumentOrigin.User));
        return new(MaterializationDocument.FromDefinition(materialization), customerInput, loadInput);
    }

    static InverseLoadState PickLoad(
        IReadOnlyDictionary<string, InverseLoadState> loads,
        Random random) => loads.Values.ElementAt(random.Next(loads.Count));

    static ImmutableHashSet<string> ResolveBeforeAndAfterRoute(
        MaterializationImpactRoute route,
        InverseLoadState? before,
        InverseLoadState? after)
    {
        var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
        var step = Assert.Single(strategy.Steps);
        Assert.Equal(
            MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction,
            step.Operation);
        ImmutableHashSet<string>.Builder roots = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (before is not null)
        {
            roots.Add(before.CustomerId);
        }

        if (after is not null)
        {
            roots.Add(after.CustomerId);
        }

        return roots.ToImmutable();
    }

    static ImmutableHashSet<string> ResolveLedgerAndCurrentRoute(
        MaterializationImpactRoute route,
        ImmutableHashSet<string> priorLedgerRoots,
        InverseLoadState? after)
    {
        var strategy = Assert.IsType<MaterializationContributorLedgerImpactStrategy>(route.Strategy);
        var step = Assert.Single(strategy.CurrentRootSteps);
        Assert.Equal(
            MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction,
            step.Operation);
        ImmutableHashSet<string>.Builder roots = priorLedgerRoots.ToBuilder();
        if (after is not null)
        {
            roots.Add(after.CustomerId);
        }

        return roots.ToImmutable();
    }

    static Dictionary<string, string> Recompute(IEnumerable<InverseLoadState> loads) => loads
        .GroupBy(static load => load.CustomerId, StringComparer.Ordinal)
        .ToDictionary(
            static group => group.Key,
            static group => string.Join(
                "|",
                group.OrderBy(static load => load.Id, StringComparer.Ordinal)
                    .Select(static load => $"{load.Id}:{load.Status}")),
            StringComparer.Ordinal);

    static ImmutableHashSet<string> ChangedRoots(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after) => before.Keys
            .Concat(after.Keys)
            .Where(root => !before.TryGetValue(root, out var beforeValue)
                || !after.TryGetValue(root, out var afterValue)
                || !string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            .ToImmutableHashSet(StringComparer.Ordinal);

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind rebuildRead,
        ImmutableArray<MaterializationGuaranteeKind> changeGuarantees,
        MaterializationSynchronizationMode enumerationModes) => new(
            input,
            capabilities:
            [
                new(
                    id: new($"{input.Value}/read"),
                    capability: rebuildRead,
                    guarantees:
                    [
                        MaterializationGuaranteeKind.StableOrdering,
                        MaterializationGuaranteeKind.RequestLocalCompleteness
                    ],
                    operatingLimits: ReadLimits,
                    modes: enumerationModes),
                new(
                    id: new($"{input.Value}/continuation"),
                    capability: MaterializationCapabilityKind.SourceContinuation,
                    modes: MaterializationSynchronizationMode.Rebuild),
                new(
                    id: new($"{input.Value}/changes"),
                    capability: MaterializationCapabilityKind.SourceChangeDelivery,
                    guarantees: changeGuarantees,
                    operatingLimits:
                    [
                        new(MaterializationLimitKind.ChangeItems, MaximumAffectedRoots),
                        new(MaterializationLimitKind.ReadBytes, ReadBytes)
                    ],
                    modes: MaterializationSynchronizationMode.All),
                new(
                    id: new($"{input.Value}/settlement"),
                    capability: MaterializationCapabilityKind.SourceSettlement,
                    guarantees: [MaterializationGuaranteeKind.ExplicitSettlement],
                    modes: MaterializationSynchronizationMode.All)
            ]);

    static MaterializationCapabilityRequirement TargetRequirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) => new(
            id: new(id),
            capability,
            guarantees: capability switch
            {
                MaterializationCapabilityKind.TargetGenerationIsolation =>
                    [MaterializationGuaranteeKind.GenerationIsolation, MaterializationGuaranteeKind.FencedMutation],
                MaterializationCapabilityKind.TargetBulkUpsert
                    or MaterializationCapabilityKind.TargetBulkDelete =>
                    [
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                MaterializationCapabilityKind.TargetPerItemOutcomes =>
                    [MaterializationGuaranteeKind.ExactPerItemOutcome],
                MaterializationCapabilityKind.TargetFencedPromotion =>
                    [MaterializationGuaranteeKind.AtomicPromotion, MaterializationGuaranteeKind.FencedPromotion],
                MaterializationCapabilityKind.TargetSeal
                    or MaterializationCapabilityKind.TargetValidation
                    or MaterializationCapabilityKind.TargetRetirement
                    or MaterializationCapabilityKind.TargetCleanup =>
                    [MaterializationGuaranteeKind.FencedMutation],
                MaterializationCapabilityKind.TargetContributorLedger =>
                    [
                        MaterializationGuaranteeKind.RequestLocalCompleteness,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite,
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.AtomicWithMaterializationMutation
                    ],
                _ => []
            },
            operatingLimits: capability switch
            {
                MaterializationCapabilityKind.TargetBulkUpsert
                    or MaterializationCapabilityKind.TargetBulkDelete
                    or MaterializationCapabilityKind.TargetPerItemOutcomes => WriteLimits,
                MaterializationCapabilityKind.TargetContributorLedger =>
                    [
                        new(MaterializationLimitKind.ReadItems, MaximumAffectedRoots),
                        new(MaterializationLimitKind.ReadBytes, ReadBytes),
                        new(MaterializationLimitKind.WriteItems, WriteItems),
                        new(MaterializationLimitKind.WriteBytes, WriteBytes)
                    ],
                _ => []
            },
            modes);

    static ImmutableArray<MaterializationGuaranteeKind> ChangeGuarantees(bool beforeImage) => beforeImage
        ?
        [
            MaterializationGuaranteeKind.StableOrdering,
            MaterializationGuaranteeKind.AtLeastOnceDelivery,
            MaterializationGuaranteeKind.BaselinePlusCatchUp,
            MaterializationGuaranteeKind.BeforeImage
        ]
        :
        [
            MaterializationGuaranteeKind.StableOrdering,
            MaterializationGuaranteeKind.AtLeastOnceDelivery,
            MaterializationGuaranteeKind.BaselinePlusCatchUp
        ];

    static ImmutableArray<MaterializationOperatingLimit> ReadLimits =>
    [
        new(MaterializationLimitKind.ReadItems, MaximumGlobalRoots),
        new(MaterializationLimitKind.ReadBytes, ReadBytes)
    ];

    static ImmutableArray<MaterializationOperatingLimit> WriteLimits =>
    [
        new(MaterializationLimitKind.WriteItems, WriteItems),
        new(MaterializationLimitKind.WriteBytes, WriteBytes)
    ];

    [Flags]
    enum TestFacilities
    {
        None = 0,
        BeforeImage = 1,
        ContributorLedger = 2,
        GlobalEnumeration = 4
    }

    sealed record InverseFixture(
        MaterializationDocument Document,
        RelationQueryInputId CustomerInput,
        RelationQueryInputId LoadInput);

    sealed record InverseLoadState(string Id, string CustomerId, string Status);
}
