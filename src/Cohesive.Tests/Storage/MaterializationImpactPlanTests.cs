using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationImpactPlanTests
{
    const long ReadBytes = 4_096;
    const long MaximumAffectedRoots = 100;
    const long WriteItems = 100;
    const long WriteBytes = 1_000_000;

    [Fact]
    public void Compiler_UsesDirectRootAndCanonicalInverseTraversalRoutes()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var policy = Policy(
            MaterializationImpactStrategyKind.InverseTraversal,
            MaterializationImpactStrategyKind.ContributorLedger);

        var compilation = MaterializationImpactPlanCompiler.Compile(document, policy);

        var impactPlan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        Assert.True(compilation.IsSuccessful);
        var relationPlan = Assert.IsType<CompiledRelationQueryPlan>(document.Definition.Relation.Compile().Plan);
        var root = Assert.Single(
            relationPlan.InputContract.Sources,
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var directRoute = Assert.Single(impactPlan.Routes, route => route.ChangeInput == root.Input.Id);
        var direct = Assert.IsType<MaterializationDirectRootImpactStrategy>(directRoute.Strategy);
        Assert.Equal(root.Input.Id, direct.RootInput);
        Assert.Equal(MaterializationImpactPrecision.Exact, directRoute.Precision);
        Assert.Equal(1, directRoute.MaximumAffectedRoots);
        Assert.Equal(ReadBytes, directRoute.MaximumReadBytes);

        AssertCanonicalInverseRoute(
            impactPlan,
            relationPlan,
            FederatedLoadRelationFixture.CustomerShapeId,
            root.Input.Id);
        AssertCanonicalInverseRoute(
            impactPlan,
            relationPlan,
            FederatedLoadRelationFixture.EquipmentShapeId,
            root.Input.Id);
    }

    [Fact]
    public void Compiler_UsesExactContributorLedgerUnionWithCurrentInverseRootsWhenPreferred()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: true,
            globalEnumeration: false,
            reverseDeclarations: false);

        var compilation = MaterializationImpactPlanCompiler.Compile(
            document,
            Policy(
                MaterializationImpactStrategyKind.ContributorLedger,
                MaterializationImpactStrategyKind.InverseTraversal));

        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        Assert.True(compilation.IsSuccessful);
        foreach (var route in ContributorRoutes(plan))
        {
            var strategy = Assert.IsType<MaterializationContributorLedgerImpactStrategy>(route.Strategy);
            Assert.Equal(route.ChangeInput, strategy.ContributorInput);
            Assert.Equal(
                MaterializationImpactLineageKind.PriorLedgerAndCurrentRelationshipState,
                strategy.Lineage);
            var currentRootStep = Assert.Single(strategy.CurrentRootSteps);
            Assert.Equal(route.ChangeInput, currentRootStep.RelationshipInput);
            Assert.Equal(
                MaterializationInverseImpactOperationKind.PredicateLookup,
                currentRootStep.Operation);
            Assert.Equal(MaterializationImpactPrecision.Exact, route.Precision);
            Assert.Equal(ReadBytes, route.MaximumReadBytes);
            Assert.Contains(
                route.Capabilities,
                capability => capability.Role == MaterializationEndpointRole.Source
                    && capability.SourceInput == route.ChangeInput);
            Assert.Contains(
                route.Capabilities,
                capability => capability.Role == MaterializationEndpointRole.Source
                    && capability.SourceInput == currentRootStep.ReferenceSourceInput
                    && capability.Requirement.Value == $"{currentRootStep.ReferenceSourceInput.Value}/inverse");
            var capability = Assert.Single(
                route.Capabilities,
                static candidate => candidate.Role == MaterializationEndpointRole.Target);
            Assert.Equal(MaterializationEndpointRole.Target, capability.Role);
            Assert.Equal("target/contributor-ledger", capability.Requirement.Value);
        }
    }

    [Fact]
    public void Compiler_LabelsBoundedGlobalFallbackAsConservativeAndBounded()
    {
        const long MaximumGlobalRoots = 64;
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: false,
            contributorLedger: false,
            globalEnumeration: true,
            reverseDeclarations: false);
        MaterializationImpactPlanningPolicy policy = new(
            id: new("tests/impact/global/v1"),
            strategyPreference: [MaterializationImpactStrategyKind.BoundedGlobalInvalidation],
            maximumAffectedRoots: MaximumAffectedRoots,
            maximumReadBytes: ReadBytes,
            maximumGlobalRoots: MaximumGlobalRoots);

        var compilation = MaterializationImpactPlanCompiler.Compile(document, policy);

        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        Assert.True(compilation.IsSuccessful);
        var relationPlan = Assert.IsType<CompiledRelationQueryPlan>(document.Definition.Relation.Compile().Plan);
        var root = Assert.Single(
            relationPlan.InputContract.Sources,
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        foreach (var route in ContributorRoutes(plan))
        {
            var strategy = Assert.IsType<MaterializationBoundedGlobalImpactStrategy>(route.Strategy);
            Assert.Equal(root.Input.Id, strategy.RootInput);
            Assert.Equal(MaterializationImpactPrecision.Conservative, route.Precision);
            Assert.Equal(MaximumGlobalRoots, route.MaximumAffectedRoots);
            Assert.Equal(ReadBytes, route.MaximumReadBytes);
            Assert.Contains(
                route.Capabilities,
                capability => capability.Role == MaterializationEndpointRole.Source
                    && capability.SourceInput == route.ChangeInput);
            Assert.Contains(
                route.Capabilities,
                capability => capability.Role == MaterializationEndpointRole.Source
                    && capability.SourceInput == root.Input.Id
                    && capability.Requirement.Value == $"{root.Input.Id.Value}/read");
        }
    }

    [Fact]
    public void Compiler_FailsClosedWhenNoContributorStrategyIsAvailable()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: false,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);

        var compilation = MaterializationImpactPlanCompiler.Compile(
            document,
            Policy(MaterializationImpactStrategyKind.InverseTraversal));

        Assert.False(compilation.IsSuccessful);
        Assert.Null(compilation.Plan);
        Assert.Contains(
            compilation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationImpactDiagnosticCodes.StrategyUnavailable
                && diagnostic.Evidence is not null);
    }

    [Fact]
    public void Compiler_RejectsUnsupportedMaterializationDocumentSchema()
    {
        var current = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        MaterializationDocument unsupported = new(
            schemaVersion: "cohesive-materialization/unsupported",
            definition: current.Definition,
            definitionFingerprint: current.DefinitionFingerprint);

        var compilation = MaterializationImpactPlanCompiler.Compile(
            unsupported,
            Policy(MaterializationImpactStrategyKind.InverseTraversal));

        Assert.False(compilation.IsSuccessful);
        Assert.Null(compilation.Plan);
        Assert.Contains(
            compilation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationImpactDiagnosticCodes.DefinitionInvalid
                && diagnostic.Location == "/schemaVersion");
    }

    [Fact]
    public void ImpactStrategies_RejectLineageAndOperationSequenceContradictions()
    {
        MaterializationInverseImpactStep predicate = new(
            relationshipInput: new("relationship/predicate"),
            referenceSourceInput: new("source/predicate"),
            operation: MaterializationInverseImpactOperationKind.PredicateLookup);
        MaterializationInverseImpactStep beforeAndAfter = new(
            relationshipInput: new("relationship/extraction"),
            referenceSourceInput: new("source/extraction"),
            operation: MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction);
        MaterializationInverseImpactStep after = new(
            relationshipInput: new("relationship/after"),
            referenceSourceInput: new("source/after"),
            operation: MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction);
        MaterializationInverseImpactStep current = new(
            relationshipInput: new("relationship/current"),
            referenceSourceInput: new("source/current"),
            operation: MaterializationInverseImpactOperationKind.CurrentRelationshipReferenceExtraction);

        Assert.Throws<ArgumentException>(() => new MaterializationInverseTraversalImpactStrategy(
            steps: [predicate],
            lineage: MaterializationImpactLineageKind.BeforeAndAfterRelationshipReferences));
        Assert.Throws<ArgumentException>(() => new MaterializationInverseTraversalImpactStrategy(
            steps: [beforeAndAfter],
            lineage: MaterializationImpactLineageKind.ContributorIdentity));
        Assert.Throws<ArgumentException>(() => new MaterializationInverseTraversalImpactStrategy(
            steps: [after],
            lineage: MaterializationImpactLineageKind.ContributorIdentity));
        Assert.Throws<ArgumentException>(() => new MaterializationContributorLedgerImpactStrategy(
            contributorInput: new("contributor"),
            currentRootSteps: [current]));
    }

    [Fact]
    public void Fingerprint_IsDeterministicAcrossReversedDefinitionDeclarationOrder()
    {
        var forward = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: true,
            globalEnumeration: false,
            reverseDeclarations: false);
        var reverse = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: true,
            globalEnumeration: false,
            reverseDeclarations: true);
        var policy = Policy(
            MaterializationImpactStrategyKind.InverseTraversal,
            MaterializationImpactStrategyKind.ContributorLedger);

        var forwardPlan = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(forward, policy).Plan);
        var reversePlan = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(reverse, policy).Plan);

        Assert.Equal(forward.DefinitionFingerprint, reverse.DefinitionFingerprint);
        Assert.Equal(forwardPlan.Fingerprint, reversePlan.Fingerprint);
    }

    [Fact]
    public void SameContributorShapeAcrossMaterializations_ProducesIndependentlyFencedPlans()
    {
        var firstDocument = CreateDocument(
            materializationId: "loads/search-a",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var secondDocument = CreateDocument(
            materializationId: "loads/search-b",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var policy = Policy(MaterializationImpactStrategyKind.InverseTraversal);

        var first = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(firstDocument, policy).Plan);
        var second = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(secondDocument, policy).Plan);
        var firstCustomer = Assert.Single(
            first.Routes,
            static route => route.ChangeShape == FederatedLoadRelationFixture.CustomerShapeId);
        var secondCustomer = Assert.Single(
            second.Routes,
            static route => route.ChangeShape == FederatedLoadRelationFixture.CustomerShapeId);

        Assert.Equal(firstCustomer.ChangeInput, secondCustomer.ChangeInput);
        Assert.Equal(firstCustomer.ChangeShape, secondCustomer.ChangeShape);
        Assert.NotEqual(first.Materialization, second.Materialization);
        Assert.NotEqual(first.DefinitionFingerprint, second.DefinitionFingerprint);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void CompiledRoutes_MatchFullRecomputationForSharedContributorChangesDeletesAndReferenceMoves()
    {
        const int Seed = 17_042;
        const int Cases = 192;
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var impactPlan = Assert.IsType<MaterializationImpactPlan>(
            MaterializationImpactPlanCompiler.Compile(
                document,
                Policy(MaterializationImpactStrategyKind.InverseTraversal)).Plan);
        var customerRoute = Assert.Single(
            impactPlan.Routes,
            static route => route.ChangeShape == FederatedLoadRelationFixture.CustomerShapeId);
        var rootRoute = Assert.Single(
            impactPlan.Routes,
            static route => route.ChangeShape == FederatedLoadRelationFixture.LoadShapeId);
        Assert.IsType<MaterializationInverseTraversalImpactStrategy>(customerRoute.Strategy);
        Assert.IsType<MaterializationDirectRootImpactStrategy>(rootRoute.Strategy);
        var random = new Random(Seed);

        for (var caseIndex = 0; caseIndex < Cases; caseIndex++)
        {
            var customers = CreateCustomers(random);
            var loads = CreateLoads(random, customers);
            var beforeCustomers = customers.ToDictionary(static customer => customer.Id, StringComparer.Ordinal);
            var afterCustomers = new Dictionary<string, CustomerState>(beforeCustomers, StringComparer.Ordinal);
            var beforeLoads = loads.ToDictionary(static load => load.Id, StringComparer.Ordinal);
            var afterLoads = new Dictionary<string, LoadState>(beforeLoads, StringComparer.Ordinal);
            ImmutableHashSet<string> routedRoots;

            switch (random.Next(minValue: 0, maxValue: 3))
            {
                case 0:
                    {
                        var changed = customers[random.Next(customers.Count)];
                        afterCustomers[changed.Id] = changed with { Name = $"{changed.Name}-updated-{caseIndex}" };
                        routedRoots = ResolveInverseCustomerRoute(customerRoute, loads, changed.Id);
                        break;
                    }
                case 1:
                    {
                        var deleted = customers[random.Next(customers.Count)];
                        afterCustomers.Remove(deleted.Id);
                        routedRoots = ResolveInverseCustomerRoute(customerRoute, loads, deleted.Id);
                        break;
                    }
                default:
                    {
                        var moved = loads[random.Next(loads.Count)];
                        var candidates = customers.Where(customer => customer.Id != moved.CustomerId).ToArray();
                        var replacement = candidates[random.Next(candidates.Length)];
                        afterLoads[moved.Id] = moved with { CustomerId = replacement.Id };
                        routedRoots = ResolveDirectRootRoute(rootRoute, moved.Id);
                        break;
                    }
            }

            var expectedRoots = ChangedRoots(
                Recompute(beforeLoads.Values, beforeCustomers),
                Recompute(afterLoads.Values, afterCustomers));
            Assert.Equal(
                expectedRoots.Order(StringComparer.Ordinal),
                routedRoots.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Json_RoundTripsCanonicalPlanThroughDefinitionBoundLinking()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: true,
            globalEnumeration: false,
            reverseDeclarations: false);
        var plan = CompileExactPlan(document);

        var json = MaterializationImpactJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var restored = MaterializationImpactJsonSerializer.Deserialize(json, document.Definition);

        Assert.Equal(plan.Fingerprint, restored.Fingerprint);
        Assert.Equal(
            MaterializationImpactJsonSerializer.GetCanonicalBytes(plan),
            MaterializationImpactJsonSerializer.GetCanonicalBytes(restored));
    }

    [Fact]
    public void Json_RejectsUnknownAndDuplicatePropertiesThroughDefinitionBoundLinking()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var plan = CompileExactPlan(document);
        var json = MaterializationImpactJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var unknown = json.Insert(startIndex: 1, "\"unknown\":true,");
        var duplicate = json.Replace(
            "\"schemaVersion\":\"cohesive-materialization-impact-plan/v1\"",
            "\"schemaVersion\":\"cohesive-materialization-impact-plan/v1\","
            + "\"schemaVersion\":\"cohesive-materialization-impact-plan/v1\"",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            MaterializationImpactJsonSerializer.Deserialize(unknown, document.Definition));
        Assert.Throws<JsonException>(() =>
            MaterializationImpactJsonSerializer.Deserialize(duplicate, document.Definition));
    }

    [Fact]
    public void Json_RejectsFingerprintTamperingThroughDefinitionBoundLinking()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var plan = CompileExactPlan(document);
        var json = MaterializationImpactJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var tampered = json.Replace(
            plan.Fingerprint.Value,
            new string('0', plan.Fingerprint.Value.Length),
            StringComparison.Ordinal);
        Assert.NotEqual(json, tampered);

        Assert.Throws<JsonException>(() =>
            MaterializationImpactJsonSerializer.Deserialize(tampered, document.Definition));
    }

    [Fact]
    public void Linker_RejectsPlanFromDifferentDefinition()
    {
        var owner = CreateDocument(
            materializationId: "loads/search-a",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var foreign = CreateDocument(
            materializationId: "loads/search-b",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var plan = CompileExactPlan(owner);

        MaterializationImpactPlanLinker.Link(plan, owner.Definition);
        Assert.Throws<ArgumentException>(() =>
            MaterializationImpactPlanLinker.Link(plan, foreign.Definition));
    }

    [Fact]
    public void Linker_RejectsFreshlyFingerprintedSemanticTampering()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var plan = CompileExactPlan(document);
        var route = Assert.Single(
            plan.Routes,
            static candidate => candidate.ChangeShape == FederatedLoadRelationFixture.CustomerShapeId);
        MaterializationImpactRoute narrowed = new(
            changeInput: route.ChangeInput,
            changeShape: route.ChangeShape,
            dependencyInputs: route.DependencyInputs,
            strategy: route.Strategy,
            precision: route.Precision,
            capabilities: route.Capabilities,
            maximumAffectedRoots: route.MaximumAffectedRoots - 1,
            maximumReadBytes: route.MaximumReadBytes);
        MaterializationImpactPlan tampered = new(
            schemaVersion: plan.SchemaVersion,
            materialization: plan.Materialization,
            definitionFingerprint: plan.DefinitionFingerprint,
            relationPlan: plan.RelationPlan,
            output: plan.Output,
            policy: plan.Policy,
            routes:
            [
                .. plan.Routes.Select(candidate => ReferenceEquals(candidate, route) ? narrowed : candidate)
            ]);

        Assert.NotEqual(plan.Fingerprint, tampered.Fingerprint);
        Assert.Throws<ArgumentException>(() =>
            MaterializationImpactPlanLinker.Link(tampered, document.Definition));
    }

    [Fact]
    public void Explain_RetainsCanonicalRelationsAndCapabilityObjectsAndRejectsStaleAffinity()
    {
        var owner = CreateDocument(
            materializationId: "loads/search-a",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var foreign = CreateDocument(
            materializationId: "loads/search-b",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false);
        var plan = CompileExactPlan(owner);
        var artifact = MaterializationImpactExplainProjector.Project(plan, owner.Definition);
        var route = Assert.Single(
            plan.Routes,
            static candidate => candidate.ChangeShape == FederatedLoadRelationFixture.CustomerShapeId);

        var explained = Assert.Single(artifact.Routes, candidate => candidate.Route.ChangeInput == route.ChangeInput);
        Assert.Same(route, explained.Route);
        Assert.NotEmpty(explained.Dependencies);
        Assert.All(
            explained.Dependencies,
            dependency => Assert.Contains(dependency.Input.Id, route.DependencyInputs));
        var relationship = Assert.Single(explained.Relationships);
        Assert.Equal(route.ChangeInput, relationship.Id);
        Assert.Equal(FederatedLoadRelationFixture.LoadCustomerRelationship, relationship.Definition);
        foreach (var capabilityReference in route.Capabilities)
        {
            var expected = capabilityReference.Role == MaterializationEndpointRole.Source
                ? Assert.Single(
                    owner.Definition.Sources,
                    source => source.Input == capabilityReference.SourceInput).Capabilities
                    .Single(requirement => requirement.Id == capabilityReference.Requirement)
                : owner.Definition.TargetCapabilities.Single(
                    requirement => requirement.Id == capabilityReference.Requirement);
            Assert.Contains(explained.Capabilities, actual => ReferenceEquals(actual, expected));
        }

        Assert.Equal(plan.Materialization, artifact.Materialization);
        Assert.Equal(plan.Fingerprint, artifact.PlanFingerprint);
        Assert.Throws<ArgumentException>(() =>
            MaterializationImpactExplainProjector.Project(plan, foreign.Definition));
    }

    [Fact]
    public void Explain_IncludesCurrentRelationshipPathForContributorLedger()
    {
        var document = CreateDocument(
            materializationId: "loads/search",
            inverseLookup: true,
            contributorLedger: true,
            globalEnumeration: false,
            reverseDeclarations: false);
        var compilation = MaterializationImpactPlanCompiler.Compile(
            document,
            Policy(
                MaterializationImpactStrategyKind.ContributorLedger,
                MaterializationImpactStrategyKind.InverseTraversal));
        var plan = Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
        var route = Assert.Single(
            plan.Routes,
            static candidate => candidate.ChangeShape == FederatedLoadRelationFixture.CustomerShapeId);
        var strategy = Assert.IsType<MaterializationContributorLedgerImpactStrategy>(route.Strategy);

        var artifact = MaterializationImpactExplainProjector.Project(plan, document.Definition);

        var explained = Assert.Single(
            artifact.Routes,
            candidate => candidate.Route.ChangeInput == route.ChangeInput);
        var relationship = Assert.Single(explained.Relationships);
        Assert.Equal(Assert.Single(strategy.CurrentRootSteps).RelationshipInput, relationship.Id);
        Assert.Equal(FederatedLoadRelationFixture.LoadCustomerRelationship, relationship.Definition);
    }

    [Fact]
    public void Catalog_FansOneSemanticShapeToIndependentlyFencedMaterializationsAndRoles()
    {
        var first = CompileExactPlan(CreateDocument(
            materializationId: "loads/search-a",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false));
        var second = CompileExactPlan(CreateDocument(
            materializationId: "loads/search-b",
            inverseLookup: true,
            contributorLedger: false,
            globalEnumeration: false,
            reverseDeclarations: false));
        MaterializationImpactPlanCatalog catalog = new([second, first]);

        var matches = catalog.GetRoutes(FederatedLoadRelationFixture.CustomerShapeId);

        Assert.Equal(
            ["loads/search-a", "loads/search-b"],
            matches.Select(static match => match.Plan.Materialization.Value));
        Assert.All(matches, static match =>
        {
            Assert.Equal(FederatedLoadRelationFixture.CustomerShapeId, match.Route.ChangeShape);
            Assert.IsType<MaterializationInverseTraversalImpactStrategy>(match.Route.Strategy);
            Assert.Contains(match.Route.ChangeInput, match.Plan.RelationPlan.Inputs);
            Assert.Same(
                match.Route,
                Assert.Single(match.Plan.Routes, candidate => ReferenceEquals(candidate, match.Route)));
        });
        Assert.NotEqual(matches[0].Plan.Fingerprint, matches[1].Plan.Fingerprint);
    }

    static MaterializationImpactPlan CompileExactPlan(MaterializationDocument document)
    {
        var compilation = MaterializationImpactPlanCompiler.Compile(
            document,
            Policy(
                MaterializationImpactStrategyKind.InverseTraversal,
                MaterializationImpactStrategyKind.ContributorLedger));
        Assert.True(
            compilation.IsSuccessful,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<MaterializationImpactPlan>(compilation.Plan);
    }

    static void AssertCanonicalInverseRoute(
        MaterializationImpactPlan impactPlan,
        CompiledRelationQueryPlan relationPlan,
        QualifiedShapeId contributorShape,
        RelationQueryInputId rootInput)
    {
        var traversal = Assert.Single(
            relationPlan.InputContract.Traversals,
            candidate => candidate.ResultShape == contributorShape);
        var route = Assert.Single(impactPlan.Routes, candidate => candidate.ChangeInput == traversal.Input.Id);
        var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
        var step = Assert.Single(strategy.Steps);
        Assert.Equal(traversal.Input.Id, step.RelationshipInput);
        Assert.Equal(rootInput, step.ReferenceSourceInput);
        Assert.Equal(MaterializationInverseImpactOperationKind.PredicateLookup, step.Operation);
        Assert.Equal(MaterializationImpactLineageKind.ContributorIdentity, strategy.Lineage);
        Assert.Equal(MaterializationImpactPrecision.Exact, route.Precision);
        Assert.Equal(MaximumAffectedRoots, route.MaximumAffectedRoots);
        Assert.Equal(ReadBytes, route.MaximumReadBytes);
        Assert.Contains(traversal.Input.Id, route.DependencyInputs);
        Assert.Contains(
            route.Capabilities,
            capability => capability.Role == MaterializationEndpointRole.Source
                && capability.SourceInput == route.ChangeInput
                && capability.Requirement.Value == $"{route.ChangeInput.Value}/changes");
        var capability = Assert.Single(
            route.Capabilities,
            candidate => candidate.SourceInput == rootInput
                && candidate.Requirement.Value == $"{rootInput.Value}/inverse");
        Assert.Equal(MaterializationEndpointRole.Source, capability.Role);
        Assert.Equal(rootInput, capability.SourceInput);
        Assert.Equal($"{rootInput.Value}/inverse", capability.Requirement.Value);
    }

    static IEnumerable<MaterializationImpactRoute> ContributorRoutes(MaterializationImpactPlan plan) =>
        plan.Routes.Where(static route => route.ChangeShape != FederatedLoadRelationFixture.LoadShapeId);

    static MaterializationImpactPlanningPolicy Policy(
        params MaterializationImpactStrategyKind[] strategies)
    {
        var maximumLedgerWriteBytes = strategies.Contains(MaterializationImpactStrategyKind.ContributorLedger)
            ? WriteBytes
            : (long?)null;
        return new(
            id: new("tests/impact/exact/v1"),
            strategyPreference: [.. strategies],
            maximumAffectedRoots: MaximumAffectedRoots,
            maximumReadBytes: ReadBytes,
            maximumLedgerWriteBytes);
    }

    static MaterializationDocument CreateDocument(
        string materializationId,
        bool inverseLookup,
        bool contributorLedger,
        bool globalEnumeration,
        bool reverseDeclarations)
    {
        RelationQueryCompilationRequest request = new(
            definitionDocument: FederatedLoadRelationFixture.RelationDocument,
            shapeDocuments: FederatedLoadRelationFixture.ShapeGraphDocuments,
            relationshipCatalogDocument: FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var relationCompilation = RelationQueryStaticCompiler.Compile(request);
        var relationPlan = Assert.IsType<CompiledRelationQueryPlan>(relationCompilation.Plan);
        var output = Assert.Single(
            relationPlan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(request, output.Id);
        var rootInput = Assert.Single(
            relationPlan.InputContract.Sources,
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot).Input.Id;
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. relationPlan.InputContract.Sources.Select(source => SourceRequirement(
                input: source.Input.Id,
                rebuildRead: MaterializationCapabilityKind.SourceBoundedEnumeration,
                isRoot: source.Input.Id == rootInput,
                inverseLookup,
                globalEnumeration,
                reverseDeclarations)),
            .. relationPlan.InputContract.Traversals.Select(traversal => SourceRequirement(
                input: traversal.Input.Id,
                rebuildRead: traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                isRoot: false,
                inverseLookup,
                globalEnumeration,
                reverseDeclarations))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> targets =
        [
            Requirement("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert, MaterializationSynchronizationMode.All),
            Requirement("target/delete", MaterializationCapabilityKind.TargetBulkDelete, MaterializationSynchronizationMode.All),
            Requirement("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationSynchronizationMode.All),
            Requirement("target/seal", MaterializationCapabilityKind.TargetSeal, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/validation", MaterializationCapabilityKind.TargetValidation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/retirement", MaterializationCapabilityKind.TargetRetirement, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/cleanup", MaterializationCapabilityKind.TargetCleanup, MaterializationSynchronizationMode.Rebuild)
        ];
        if (contributorLedger)
        {
            targets = targets.Add(Requirement(
                id: "target/contributor-ledger",
                capability: MaterializationCapabilityKind.TargetContributorLedger,
                modes: MaterializationSynchronizationMode.Incremental));
        }

        if (reverseDeclarations)
        {
            sources = [.. sources.Reverse()];
            targets = [.. targets.Reverse()];
        }

        MaterializationDefinition definition = new(
            id: new(materializationId),
            relation,
            sources,
            targetCapabilities: targets,
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
                producer: new("tests/materialization-impact"),
                source: new("tests/materialization-impact-plan"),
                origin: DocumentOrigin.User));
        return MaterializationDocument.FromDefinition(definition);
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind rebuildRead,
        bool isRoot,
        bool inverseLookup,
        bool globalEnumeration,
        bool reverseDeclarations)
    {
        var readModes = isRoot && globalEnumeration
            ? MaterializationSynchronizationMode.All
            : MaterializationSynchronizationMode.Rebuild;
        ImmutableArray<MaterializationCapabilityRequirement> capabilities =
        [
            Requirement($"{input.Value}/read", rebuildRead, readModes),
            Requirement($"{input.Value}/continuation", MaterializationCapabilityKind.SourceContinuation, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/changes", MaterializationCapabilityKind.SourceChangeDelivery, MaterializationSynchronizationMode.All),
            Requirement($"{input.Value}/settlement", MaterializationCapabilityKind.SourceSettlement, MaterializationSynchronizationMode.All)
        ];
        if (isRoot && inverseLookup)
        {
            capabilities = capabilities.Add(Requirement(
                id: $"{input.Value}/inverse",
                capability: MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                modes: MaterializationSynchronizationMode.Incremental));
        }

        if (reverseDeclarations)
        {
            capabilities = [.. capabilities.Reverse()];
        }

        return new(input, capabilities);
    }

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) => new(
            id: new(id),
            capability,
            guarantees: Guarantees(capability),
            operatingLimits: OperatingLimits(capability),
            modes);

    static ImmutableArray<MaterializationGuaranteeKind> Guarantees(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.AtLeastOnceDelivery,
                    MaterializationGuaranteeKind.BaselinePlusCatchUp
                ],
            MaterializationCapabilityKind.SourceSettlement => [MaterializationGuaranteeKind.ExplicitSettlement],
            MaterializationCapabilityKind.TargetGenerationIsolation =>
                [
                    MaterializationGuaranteeKind.GenerationIsolation,
                    MaterializationGuaranteeKind.FencedMutation
                ],
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
                [
                    MaterializationGuaranteeKind.AtomicPromotion,
                    MaterializationGuaranteeKind.FencedPromotion
                ],
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
        };

    static ImmutableArray<MaterializationOperatingLimit> OperatingLimits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, MaximumAffectedRoots),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, MaximumAffectedRoots),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, WriteItems),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            MaterializationCapabilityKind.TargetContributorLedger =>
                [
                    new(MaterializationLimitKind.ReadItems, MaximumAffectedRoots),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes),
                    new(MaterializationLimitKind.WriteItems, WriteItems),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            _ => []
        };

    static List<CustomerState> CreateCustomers(Random random)
    {
        var count = random.Next(minValue: 3, maxValue: 9);
        return Enumerable.Range(start: 0, count)
            .Select(index => new CustomerState($"customer-{index}", $"Customer {index}"))
            .ToList();
    }

    static List<LoadState> CreateLoads(Random random, IReadOnlyList<CustomerState> customers)
    {
        var count = random.Next(minValue: 12, maxValue: 41);
        return Enumerable.Range(start: 0, count)
            .Select(index => new LoadState(
                Id: $"load-{index}",
                CustomerId: customers[random.Next(customers.Count)].Id))
            .ToList();
    }

    static ImmutableHashSet<string> ResolveInverseCustomerRoute(
        MaterializationImpactRoute route,
        IEnumerable<LoadState> loads,
        string contributorId)
    {
        var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
        var step = Assert.Single(strategy.Steps);
        Assert.Equal(MaterializationInverseImpactOperationKind.PredicateLookup, step.Operation);
        return loads
            .Where(load => string.Equals(load.CustomerId, contributorId, StringComparison.Ordinal))
            .Select(static load => load.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    static ImmutableHashSet<string> ResolveDirectRootRoute(MaterializationImpactRoute route, string rootId)
    {
        Assert.IsType<MaterializationDirectRootImpactStrategy>(route.Strategy);
        return ImmutableHashSet.Create(StringComparer.Ordinal, rootId);
    }

    static Dictionary<string, string?> Recompute(
        IEnumerable<LoadState> loads,
        IReadOnlyDictionary<string, CustomerState> customers) => loads.ToDictionary(
            static load => load.Id,
            load => customers.TryGetValue(load.CustomerId, out var customer) ? customer.Name : null,
            StringComparer.Ordinal);

    static ImmutableHashSet<string> ChangedRoots(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after) => before.Keys
            .Concat(after.Keys)
            .Where(root => !before.TryGetValue(root, out var beforeValue)
                || !after.TryGetValue(root, out var afterValue)
                || !string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            .ToImmutableHashSet(StringComparer.Ordinal);

    sealed record CustomerState(string Id, string Name);

    sealed record LoadState(string Id, string CustomerId);
}
