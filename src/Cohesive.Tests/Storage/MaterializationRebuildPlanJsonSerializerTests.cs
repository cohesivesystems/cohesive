using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanJsonSerializerTests
{
    const long ReadBytes = 4_096;
    const long WriteItems = 100;
    const long WriteBytes = 1_000_000;

    [Fact]
    public void Plan_RoundTripsThroughStrictCanonicalJsonWithTheSameFingerprint()
    {
        var plan = CreatePlan();

        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var restored = MaterializationRebuildPlanJsonSerializer.Deserialize(json);

        Assert.Equal(plan.Fingerprint, restored.Fingerprint);
        Assert.Equal(
            MaterializationRebuildPlanFingerprinter.Compute(restored),
            restored.Fingerprint);
        Assert.Equal(
            MaterializationRebuildPlanJsonSerializer.GetCanonicalBytes(plan),
            MaterializationRebuildPlanJsonSerializer.GetCanonicalBytes(restored));
        Assert.Equal("cohesive-materialization-rebuild-plan/v5", restored.SchemaVersion);
        Assert.Equal("cohesive-materialization-rebuild-plan/v5-c14n/v1", restored.Fingerprint.Canonicalization);
        Assert.Equal(plan.PlacementSlice.Fingerprint, restored.PlacementSlice.Fingerprint);
        Assert.Equal(
            plan.ChangeFeedCatalogs.Select(static catalog => catalog.EvidenceReference),
            restored.ChangeFeedCatalogs.Select(static catalog => catalog.EvidenceReference));
        Assert.Equal(
            plan.ChangeFeedCatalogs.SelectMany(static catalog => catalog.Scopes)
                .Select(scope => MaterializationChannelSemantics.ToChannelScopeId(scope).Value),
            restored.ChangeFeedCatalogs.SelectMany(static catalog => catalog.Scopes)
                .Select(scope => MaterializationChannelSemantics.ToChannelScopeId(scope).Value));
        Assert.Equal(
            plan.Limits.MaximumChangeFeedsPerConvergenceActivation,
            restored.Limits.MaximumChangeFeedsPerConvergenceActivation);
    }

    [Fact]
    public void PlanJson_RejectsMissingControlRealizationsEvenWhenTheCanonicalCatalogIsEmpty()
    {
        var plan = CreatePlan();
        Assert.Empty(plan.ControlRealizations);
        var root = JsonNode.Parse(MaterializationRebuildPlanJsonSerializer.Serialize(plan))!.AsObject();
        Assert.True(root.Remove("controlRealizations"));

        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void Plan_RequiresRootFeedScopesToExactlyEqualBaselineShardsAndEveryCatalog()
    {
        var plan = CreatePlan();
        var (expandedCatalogs, expandedFeeds) = ExpandRootFeedCatalog(plan);

        var baselineMismatch = Assert.Throws<ArgumentException>(() =>
            RebuildPlan(plan, expandedCatalogs, expandedFeeds, plan.Limits));
        var omitted = Assert.Throws<ArgumentException>(() =>
            RebuildPlan(plan, expandedCatalogs, plan.ChangeFeeds, plan.Limits));
        var unreported = Assert.Throws<ArgumentException>(() =>
            RebuildPlan(plan, plan.ChangeFeedCatalogs, expandedFeeds, plan.Limits));
        Assert.Contains("Baseline shard scopes", baselineMismatch.Message, StringComparison.Ordinal);
        Assert.Contains("exactly equal", baselineMismatch.Message, StringComparison.Ordinal);
        Assert.Contains("exactly equal", omitted.Message, StringComparison.Ordinal);
        Assert.Contains("exactly equal", unreported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanJson_RejectsARootFeedScopeWithoutAnExactBaselineShard()
    {
        var plan = CreatePlan();
        var (expandedCatalogs, expandedFeeds) = ExpandRootFeedCatalog(plan);
        var options = MaterializationRebuildPlanJsonSerializer.CreateOptions(
            formatting: PortableDocumentJsonFormatting.Compact);
        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        using var document = JsonDocument.Parse(json);
        var originalCatalogsJson = document.RootElement
            .GetProperty("changeFeedCatalogs")
            .GetRawText();
        var expandedCatalogsJson = JsonSerializer.Serialize(expandedCatalogs, options);
        var originalFeedsJson = document.RootElement
            .GetProperty("changeFeeds")
            .GetRawText();
        var expandedFeedsJson = JsonSerializer.Serialize(expandedFeeds, options);
        var unsupported = json
            .Replace(originalCatalogsJson, expandedCatalogsJson, StringComparison.Ordinal)
            .Replace(originalFeedsJson, expandedFeedsJson, StringComparison.Ordinal);

        Assert.NotEqual(json, unsupported);
        var exception = Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(unsupported));
        Assert.Contains("Baseline shard scopes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exactly equal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogEvidence_RejectsScopesFromAnotherSourceAtTheEvidenceBoundary()
    {
        var plan = CreatePlan();
        var catalog = plan.ChangeFeedCatalogs[0];

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationChangeFeedCatalogEvidence(
            input: catalog.Input,
            source: new("source/forged"),
            scopes: catalog.Scopes,
            evidenceReference: "catalog/forged"));

        Assert.Contains("attributed input and source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_RequireAPositiveConvergenceFeedBoundAndRejectAnOversizedPlan()
    {
        var plan = CreatePlan();
        var invalid = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CopyLimits(plan.Limits, maximumChangeFeedsPerConvergenceActivation: 0));
        var tooSmall = CopyLimits(
            plan.Limits,
            maximumChangeFeedsPerConvergenceActivation: plan.ChangeFeeds.Length - 1);

        var oversized = Assert.Throws<ArgumentException>(() =>
            RebuildPlan(plan, plan.ChangeFeedCatalogs, plan.ChangeFeeds, tooSmall));

        Assert.Equal("maximumChangeFeedsPerConvergenceActivation", invalid.ParamName);
        Assert.Contains("per-convergence-activation feed bound", oversized.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsABulkLimitAboveDeleteCapabilityEvidence()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            CreatePlan(targetDeleteWriteItems: WriteItems - 1));

        Assert.Contains(nameof(MaterializationCapabilityKind.TargetBulkDelete), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(MaterializationLimitKind.WriteItems), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_FingerprintIncludesCatalogEvidenceAndTheConvergenceFeedBound()
    {
        var plan = CreatePlan();
        var firstCatalog = plan.ChangeFeedCatalogs[0];
        MaterializationChangeFeedCatalogEvidence changedEvidence = new(
            input: firstCatalog.Input,
            source: firstCatalog.Source,
            scopes: firstCatalog.Scopes,
            evidenceReference: firstCatalog.EvidenceReference + "/new-evidence");
        var changedCatalogs = plan.ChangeFeedCatalogs.SetItem(index: 0, changedEvidence);
        var changedLimit = CopyLimits(
            plan.Limits,
            maximumChangeFeedsPerConvergenceActivation:
                plan.Limits.MaximumChangeFeedsPerConvergenceActivation + 1);

        var evidencePlan = RebuildPlan(plan, changedCatalogs, plan.ChangeFeeds, plan.Limits);
        var limitPlan = RebuildPlan(plan, plan.ChangeFeedCatalogs, plan.ChangeFeeds, changedLimit);

        Assert.NotEqual(plan.Fingerprint, evidencePlan.Fingerprint);
        Assert.NotEqual(plan.Fingerprint, limitPlan.Fingerprint);
    }

    [Fact]
    public void Plan_PlacementSliceIsMandatoryFingerprintAndGenerationAuthority()
    {
        var plan = CreatePlan();
        MaterializationRebuildMembershipFingerprint changedMembership = new(
            algorithm: "sha256",
            canonicalization: "tests/materialization-membership/changed/v1",
            value: new string('e', 64));
        var changedSlice = MaterializationPlacementSliceReference.Create(
            plan.PlacementSlice.Materialization,
            changedMembership,
            plan.PlacementSlice.Pool,
            plan.Target.Id,
            plan.PlacementSlice.Subjects);
        var changed = RebuildPlan(
            plan,
            plan.ChangeFeedCatalogs,
            plan.ChangeFeeds,
            plan.Limits,
            changedSlice);
        MaterializationRebuildAttempt attempt = new(
            continuation: new(
                processInstanceId: new("process/placement-fingerprint"),
                processAttemptId: new("attempt/1")),
            startedAtUtc: new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

        Assert.NotEqual(plan.PlacementSlice.Fingerprint, changed.PlacementSlice.Fingerprint);
        Assert.NotEqual(plan.Fingerprint, changed.Fingerprint);
        Assert.NotEqual(
            MaterializationRebuildIdentities.Generation(plan, attempt),
            MaterializationRebuildIdentities.Generation(changed, attempt));

        var root = JsonNode.Parse(MaterializationRebuildPlanJsonSerializer.Serialize(plan))!.AsObject();
        Assert.True(root.Remove("placementSlice"));
        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void Plan_RejectsAPlacementSliceForAnotherTarget()
    {
        var plan = CreatePlan();
        var mismatched = new MaterializationPlacementSliceReference(
            schemaVersion: MaterializationPlacementSliceReference.CurrentSchemaVersion,
            id: new("placement-slice/mismatched-target"),
            materialization: plan.PlacementSlice.Materialization,
            membership: plan.PlacementSlice.Membership,
            pool: plan.PlacementSlice.Pool,
            target: new("target/other"),
            subjects: plan.PlacementSlice.Subjects);

        var exception = Assert.Throws<ArgumentException>(() => RebuildPlan(
            plan,
            plan.ChangeFeedCatalogs,
            plan.ChangeFeeds,
            plan.Limits,
            mismatched));

        Assert.Equal("placementSlice", exception.ParamName);
    }

    [Fact]
    public void Plan_RejectsAForgedPersistedFingerprint()
    {
        var plan = CreatePlan();
        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var tampered = json.Replace(
            plan.Fingerprint.Value,
            new string('0', plan.Fingerprint.Value.Length),
            StringComparison.Ordinal);

        Assert.NotEqual(json, tampered);
        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(tampered));
    }

    [Fact]
    public void Plan_RejectsUnknownPropertiesAtTheClosedWireBoundary()
    {
        var plan = CreatePlan();
        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var unknown = json.Insert(startIndex: 1, "\"unknown\":true,");

        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(unknown));
    }

    [Fact]
    public void Plan_RejectsQuarantineUntilTheReferenceInterpreterCanDurablyRealizeIt()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreatePlan(MaterializationFailureDisposition.QuarantineAndContinue));

        Assert.Contains("stop-on-exhaustion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsContributorLedgerRoutesUntilExecutionPopulatesTheLedgerAtomically()
    {
        var plan = CreatePlan();
        var ledgerImpactPlan = CreateContributorLedgerImpactPlan(plan.ImpactPlan);

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlan(
            materialization: plan.Materialization,
            placementSlice: plan.PlacementSlice,
            impactPlan: ledgerImpactPlan,
            sources: plan.Sources,
            target: plan.Target,
            targetCapabilityMatch: plan.TargetCapabilityMatch,
            shards: plan.Shards,
            changeFeedCatalogs: plan.ChangeFeedCatalogs,
            changeFeeds: plan.ChangeFeeds,
            limits: plan.Limits,
            provenance: plan.Provenance));

        Assert.Equal("impactPlan", exception.ParamName);
        Assert.Contains("contributor-ledger", exception.Message, StringComparison.Ordinal);
        Assert.Contains("atomic baseline and incremental", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanJson_RejectsContributorLedgerRoutesAtTheExecutablePlanBoundary()
    {
        var plan = CreatePlan();
        var ledgerImpactPlan = CreateContributorLedgerImpactPlan(plan.ImpactPlan);
        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var originalImpactJson = MaterializationImpactJsonSerializer.Serialize(
            plan.ImpactPlan,
            PortableDocumentJsonFormatting.Compact);
        var ledgerImpactJson = MaterializationImpactJsonSerializer.Serialize(
            ledgerImpactPlan,
            PortableDocumentJsonFormatting.Compact);
        var unsupported = json.Replace(originalImpactJson, ledgerImpactJson, StringComparison.Ordinal);

        Assert.NotEqual(json, unsupported);
        var exception = Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(unsupported));
        Assert.Contains("contributor-ledger", exception.Message, StringComparison.Ordinal);
        Assert.Contains("atomic baseline and incremental", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsWholeSetOutputBecauseBoundedPagesCannotProveCompleteInput()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreatePlan(
                exhaustedDisposition: MaterializationFailureDisposition.Stop,
                outputMode: RelationOutputMode.Set,
                maximumPageItems: 1));

        Assert.Contains("whole-set", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bounded pages", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsManyPerRootOutputWithoutAFiniteHydrationExpansionBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreatePlan(
                exhaustedDisposition: MaterializationFailureDisposition.Stop,
                outputMode: RelationOutputMode.ManyPerRoot,
                maximumPageItems: 1));

        Assert.Contains("many-per-root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("finitely bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsFingerprintCorrectDocumentThatBypassesSemanticFactory()
    {
        var valid = CreatePlan();
        var retained = valid.Materialization.Definition;
        var invalidDefinition = new MaterializationDefinition(
            id: retained.Id,
            relation: retained.Relation,
            sources: retained.Sources,
            targetCapabilities:
            [
                .. retained.TargetCapabilities.Where(static requirement =>
                    requirement.Capability != MaterializationCapabilityKind.TargetGenerationAbandonment)
            ],
            updatePolicy: retained.UpdatePolicy,
            failurePolicy: retained.FailurePolicy,
            freshnessPolicy: retained.FreshnessPolicy,
            controlLoops: retained.ControlLoops,
            provenance: retained.Provenance);
        var forgedDocument = new MaterializationDocument(
            MaterializationDocument.CurrentSchemaVersion,
            invalidDefinition,
            MaterializationDefinitionFingerprinter.Compute(invalidDefinition));

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlan(
            forgedDocument,
            valid.PlacementSlice,
            valid.ImpactPlan,
            valid.Sources,
            valid.Target,
            valid.TargetCapabilityMatch,
            valid.Shards,
            valid.ChangeFeedCatalogs,
            valid.ChangeFeeds,
            valid.Limits,
            valid.Provenance));

        Assert.Contains(
            MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("TargetGenerationAbandonment", exception.Message, StringComparison.Ordinal);
    }

    static MaterializationRebuildPlan RebuildPlan(
        MaterializationRebuildPlan plan,
        ImmutableArray<MaterializationChangeFeedCatalogEvidence> changeFeedCatalogs,
        ImmutableArray<MaterializationChangeFeedPlan> changeFeeds,
        MaterializationRebuildLimits limits,
        MaterializationPlacementSliceReference? placementSlice = null) =>
        new(
            materialization: plan.Materialization,
            placementSlice: placementSlice ?? plan.PlacementSlice,
            impactPlan: plan.ImpactPlan,
            sources: plan.Sources,
            target: plan.Target,
            targetCapabilityMatch: plan.TargetCapabilityMatch,
            shards: plan.Shards,
            changeFeedCatalogs: changeFeedCatalogs,
            changeFeeds: changeFeeds,
            limits: limits,
            provenance: plan.Provenance);

    static (
        ImmutableArray<MaterializationChangeFeedCatalogEvidence> Catalogs,
        ImmutableArray<MaterializationChangeFeedPlan> Feeds) ExpandRootFeedCatalog(
            MaterializationRebuildPlan plan)
    {
        var shard = Assert.Single(plan.Shards);
        var rootCatalog = plan.ChangeFeedCatalogs.Single(catalog => catalog.Input == shard.Scope.Input);
        var rootFeed = plan.ChangeFeeds.Single(feed => feed.Scope == shard.Scope);
        MaterializationSourceScope additionalScope = new(
            physicalPlan: shard.Scope.PhysicalPlan,
            placement: shard.Scope.Placement,
            partition: new("partition/additional"),
            orderingScope: new("ordering/additional"));
        MaterializationChangeFeedCatalogEvidence expandedRootCatalog = new(
            input: rootCatalog.Input,
            source: rootCatalog.Source,
            scopes: [.. rootCatalog.Scopes, additionalScope],
            evidenceReference: rootCatalog.EvidenceReference + "/expanded");
        var catalogs = plan.ChangeFeedCatalogs
            .Select(catalog => catalog.Input == rootCatalog.Input ? expandedRootCatalog : catalog)
            .ToImmutableArray();
        MaterializationChangeFeedPlan additionalFeed = new(
            id: new("feed/additional"),
            scope: additionalScope,
            channel: rootFeed.Channel);

        return (catalogs, plan.ChangeFeeds.Add(additionalFeed));
    }

    static MaterializationRebuildLimits CopyLimits(
        MaterializationRebuildLimits limits,
        int maximumChangeFeedsPerConvergenceActivation) =>
        new(
            maximumPageItems: limits.MaximumPageItems,
            maximumPageBytes: limits.MaximumPageBytes,
            maximumBulkItems: limits.MaximumBulkItems,
            maximumBulkBytes: limits.MaximumBulkBytes,
            maximumPagesPerShard: limits.MaximumPagesPerShard,
            maximumStartsPerActivation: limits.MaximumStartsPerActivation,
            maximumParallelism: limits.MaximumParallelism,
            maximumChangeFeedsPerConvergenceActivation: maximumChangeFeedsPerConvergenceActivation);

    static MaterializationImpactPlan CreateContributorLedgerImpactPlan(MaterializationImpactPlan impactPlan)
    {
        MaterializationImpactPlanningPolicy policy = new(
            id: new("tests/materialization-rebuild-json-contributor-ledger/v1"),
            strategyPreference: [MaterializationImpactStrategyKind.ContributorLedger],
            maximumAffectedRoots: impactPlan.Policy.MaximumAffectedRoots,
            maximumReadBytes: impactPlan.Policy.MaximumReadBytes,
            maximumLedgerWriteBytes: ReadBytes);
        var routes = impactPlan.Routes.Select(route => route.Strategy switch
        {
            MaterializationInverseTraversalImpactStrategy inverse => new MaterializationImpactRoute(
                changeInput: route.ChangeInput,
                changeShape: route.ChangeShape,
                dependencyInputs: route.DependencyInputs,
                strategy: new MaterializationContributorLedgerImpactStrategy(
                    contributorInput: route.ChangeInput,
                    currentRootSteps: ToCurrentRootSteps(inverse.Steps)),
                precision: route.Precision,
                capabilities: route.Capabilities,
                maximumAffectedRoots: route.MaximumAffectedRoots,
                maximumReadBytes: route.MaximumReadBytes),
            _ => route
        }).ToImmutableArray();

        Assert.Contains(
            routes,
            static route => route.Strategy is MaterializationContributorLedgerImpactStrategy);
        return new(
            schemaVersion: impactPlan.SchemaVersion,
            materialization: impactPlan.Materialization,
            definitionFingerprint: impactPlan.DefinitionFingerprint,
            relationPlan: impactPlan.RelationPlan,
            output: impactPlan.Output,
            policy: policy,
            routes: routes);
    }

    static ImmutableArray<MaterializationInverseImpactStep> ToCurrentRootSteps(
        ImmutableArray<MaterializationInverseImpactStep> steps) =>
        [
            .. steps.Select((step, index) => index == 0
                && step.Operation == MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction
                    ? new MaterializationInverseImpactStep(
                        relationshipInput: step.RelationshipInput,
                        referenceSourceInput: step.ReferenceSourceInput,
                        operation: MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction)
                    : step)
        ];

    internal static MaterializationRebuildPlan CreateControlledPlan(
        ImmutableArray<ControlLoopDefinition> controlLoops,
        ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> controlWorkloads,
        int maximumPageItems = 100) =>
        CreatePlan(
            maximumPageItems: maximumPageItems,
            controlLoops: controlLoops,
            controlWorkloads: controlWorkloads);

    static MaterializationRebuildPlan CreatePlan(
        MaterializationFailureDisposition exhaustedDisposition = MaterializationFailureDisposition.Stop,
        RelationOutputMode outputMode = RelationOutputMode.OnePerRoot,
        int maximumPageItems = 100,
        long targetDeleteWriteItems = WriteItems,
        ImmutableArray<ControlLoopDefinition> controlLoops = default,
        ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> controlWorkloads = default)
    {
        var materialization = MaterializationDocument.FromDefinition(CreateDefinition(
            exhaustedDisposition,
            outputMode,
            targetDeleteWriteItems,
            controlLoops,
            controlWorkloads));
        var compiled = materialization.Definition.Relation.Compile().Plan
            ?? throw new InvalidOperationException("The test materialization relation did not compile.");
        var sourcePlans = materialization.Definition.Sources.Select(source =>
        {
            RelationQuerySourceInstanceId sourceId = new($"source/{source.Input.Value}");
            var profile = Profile(
                role: MaterializationEndpointRole.Source,
                subject: sourceId.Value,
                source.Capabilities);
            return new MaterializationRebuildSourcePlan(
                source.Input,
                sourceId,
                profile,
                MaterializationCapabilityMatcher.MatchForMode(
                    source.Capabilities,
                    profile,
                    MaterializationSynchronizationMode.Rebuild));
        }).ToImmutableArray();

        MaterializationTargetId targetId = new("target/loads-search");
        var targetProfile = Profile(
            role: MaterializationEndpointRole.Target,
            subject: targetId.Value,
            materialization.Definition.TargetCapabilities);
        var target = new MaterializationTargetDescriptor(
            targetId,
            materialization.Definition.Id,
            targetProfile);
        var targetMatch = MaterializationCapabilityMatcher.MatchForMode(
            materialization.Definition.TargetCapabilities,
            targetProfile,
            MaterializationSynchronizationMode.Rebuild);

        var root = Assert.Single(
            compiled.InputContract.Sources,
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var rootSource = sourcePlans.Single(source => source.Input == root.Input.Id);
        RelationQueryPhysicalPlanFingerprint physicalPlan = new(
            algorithm: "sha256",
            canonicalization: "tests/materialization-rebuild-physical-plan/v1",
            value: new string('a', 64));
        var fieldBindings = root.Fields.Select(field => new RelationQuerySourceFieldBinding(
            field.Input.Id,
            field.Input.Field.Path,
            sourceSelector: field.Input.Field.Path.ToString())).ToImmutableArray();
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/rebuild-root"),
            input: root.Input.Id,
            node: root.Node,
            binding: root.Binding,
            shape: root.Shape,
            source: rootSource.Source,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(root.Shape, sourceSelector: "$identity"),
            fields: fieldBindings);
        MaterializationSourceScope scope = new(
            physicalPlan,
            placement,
            partition: new("partition/a"),
            orderingScope: new("ordering/root"));
        RelationQuerySourceReadRequest read = new(
            physicalPlan,
            stage: new("stage/rebuild-root"),
            placementBinding: placement.Id,
            source: placement.Source,
            shape: placement.Shape,
            identitySelector: placement.Identity!.SourceSelector,
            fields:
            [
                .. fieldBindings.Select(static field => new RelationQuerySourceReadField(
                    field.Input,
                    field.SemanticPath,
                    field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            constraint: new RelationQueryBoundedEnumeration(maximumRows: 100),
            maximumBufferedRows: 100);
        MaterializationRebuildShardPlan shard = new(
            id: new("shard/a"),
            scope,
            read,
            hydrationPhysicalPlan: new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-rebuild-hydration-plan/v1",
                value: new string('e', 64)));
        var impactMaterialization = outputMode is RelationOutputMode.Set or RelationOutputMode.ManyPerRoot
            ? MaterializationDocument.FromDefinition(CreateDefinition(
                exhaustedDisposition,
                RelationOutputMode.OnePerRoot,
                targetDeleteWriteItems,
                controlLoops,
                controlWorkloads))
            : materialization;
        var impactPlan = MaterializationRebuildTestPlan.CompileImpactPlan(
            impactMaterialization,
            policyId: "tests/materialization-rebuild-json-impact/v1",
            maximumAffectedRoots: 100,
            maximumReadBytes: ReadBytes);
        var changeFeedCatalog = MaterializationRebuildTestPlan.CreateChangeFeedCatalog(
            compiled,
            physicalPlan,
            impactPlan,
            sourcePlans,
            shards: [shard],
            contributorPlacement: route =>
            {
                var traversal = compiled.InputContract.Traversals.Single(candidate =>
                    candidate.Input.Id == route.ChangeInput);
                var source = sourcePlans.Single(candidate => candidate.Input == route.ChangeInput);
                return new RelationQuerySourcePlacementBinding(
                    id: new($"placement/change-feed/{route.ChangeInput.Value}"),
                    input: route.ChangeInput,
                    node: traversal.Input.Traversal,
                    binding: traversal.Result,
                    shape: traversal.ResultShape,
                    source: source.Source,
                    kind: RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                    acquisition: RelationQuerySourceAcquisitionKind.BoundedLookup,
                    origin: RelationQuerySourcePlacementOrigin.Explicit,
                    identity: new(traversal.ResultShape, sourceSelector: "$identity"));
            },
            channelCanonicalization: "tests/materialization-rebuild-json-channel/v1");

        return new(
            materialization,
            placementSlice: CreateSinglePlacementSlice(materialization, target),
            impactPlan,
            sourcePlans,
            target,
            targetMatch,
            shards: [shard],
            changeFeedCatalogs: changeFeedCatalog.Evidence,
            changeFeeds: changeFeedCatalog.Feeds,
            limits: new(
                maximumPageItems: maximumPageItems,
                maximumPageBytes: ReadBytes,
                maximumBulkItems: 100,
                maximumBulkBytes: WriteBytes,
                maximumPagesPerShard: 100,
                maximumStartsPerActivation: 2,
                maximumParallelism: 2,
                maximumChangeFeedsPerConvergenceActivation: 16),
            provenance: Provenance());
    }

    internal static MaterializationPlacementSliceReference CreateSinglePlacementSlice(
        MaterializationDocument materialization,
        MaterializationTargetDescriptor target) =>
        CreateSinglePlacementScenario(materialization, target).Placement.Slices.Single();

    internal static MaterializationRebuildPlanSet CreateSinglePlanSet(MaterializationRebuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var scenario = CreateSinglePlacementScenario(plan.Materialization, plan.Target);
        if (!scenario.Placement.Slices.Single().Equals(plan.PlacementSlice))
            throw new ArgumentException("The leaf does not carry the canonical single-target placement slice.", nameof(plan));
        return RequireArtifact(MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            scenario.Placement,
            [plan],
            Provenance()));
    }

    static SinglePlacementScenario CreateSinglePlacementScenario(
        MaterializationDocument materialization,
        MaterializationTargetDescriptor target)
    {
        var pool = MaterializationBackendPoolDocument.FromDefinition(new(
            id: new($"pool/single/{target.Id.Value}"),
            materializationId: materialization.Definition.Id,
            definitionFingerprint: materialization.DefinitionFingerprint,
            members: [target],
            defaultTarget: target.Id,
            provenance: Provenance()));
        MaterializationPlacementSubjectId subject = new("placement/single");
        MaterializationRebuildRequestDocument request = new(
            schemaVersion: MaterializationRebuildRequestDocument.CurrentSchemaVersion,
            materialization,
            selection: new MaterializationExplicitPlacementSubjectSelection([subject]),
            placement: new(MaterializationBackendPoolReference.FromDocument(pool)),
            scheduling: new(maximumStartsPerActivation: 1, maximumParallelism: 1),
            promotion: new(MaterializationRebuildPromotionMode.Independent),
            provenance: Provenance());
        var membership = RequireArtifact(MaterializationRebuildPlanSetCompiler.FreezeMembership(
            request,
            [subject],
            new(
                authority: "tests/materialization-single-placement",
                revision: "revision/1",
                cut: "cut/1",
                completeness: MaterializationRebuildMembershipCompleteness.Complete,
                evidenceReferences: ["tests/materialization-single-placement/membership"]),
            Provenance()));
        MaterializationPhysicalCapacityDomainId capacityDomainId = new("capacity/single");
        var placement = RequireArtifact(MaterializationRebuildPlanSetCompiler.CompilePlacement(
            request,
            membership,
            pool,
            assignments: [new(subject, target.Id)],
            capacityDomains: [new(capacityDomainId, maximumParallelism: 1, ["tests/capacity/single"])],
            capacityAssignments: [new(target.Id, capacityDomainId)],
            provenance: Provenance()));
        return new(request, membership, placement);
    }

    static TArtifact RequireArtifact<TArtifact>(MaterializationRebuildPlanningResult<TArtifact> result)
        where TArtifact : class =>
        result.Artifact ?? throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    sealed record SinglePlacementScenario(
        MaterializationRebuildRequestDocument Request,
        MaterializationRebuildMembershipEvidence Membership,
        MaterializationTargetPlacementPlan Placement);

    static MaterializationDefinition CreateDefinition(
        MaterializationFailureDisposition exhaustedDisposition,
        RelationOutputMode outputMode,
        long targetDeleteWriteItems,
        ImmutableArray<ControlLoopDefinition> controlLoops = default,
        ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> controlWorkloads = default)
    {
        var fixtureDefinition = Assert.IsType<RelationDefinition>(FederatedLoadRelationFixture.RelationDocument.Definition);
        var relationDocument = outputMode == fixtureDefinition.Output.Mode
            ? FederatedLoadRelationFixture.RelationDocument
            : RelationQueryDocument.FromDefinition(
                fixtureDefinition with
                {
                    Output = fixtureDefinition.Output with { Mode = outputMode }
                });
        RelationQueryCompilationRequest request = new(
            relationDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var output = Assert.Single(
            plan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(request, output.Id);
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. plan.InputContract.Sources.Select(source => SourceRequirement(
                source.Input.Id,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                isRoot: source.Role == RelationQuerySourceInputRole.RelationRoot)),
            .. plan.InputContract.Traversals.Select(traversal => SourceRequirement(
                traversal.Input.Id,
                traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                isRoot: false))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> targets =
        [
            Requirement(
                id: "target/isolation",
                capability: MaterializationCapabilityKind.TargetGenerationIsolation,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/upsert",
                capability: MaterializationCapabilityKind.TargetBulkUpsert,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: "target/delete",
                capability: MaterializationCapabilityKind.TargetBulkDelete,
                modes: MaterializationSynchronizationMode.All,
                writeItems: targetDeleteWriteItems),
            Requirement(
                id: "target/outcomes",
                capability: MaterializationCapabilityKind.TargetPerItemOutcomes,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: "target/seal",
                capability: MaterializationCapabilityKind.TargetSeal,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/validation",
                capability: MaterializationCapabilityKind.TargetValidation,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/promotion",
                capability: MaterializationCapabilityKind.TargetFencedPromotion,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/abandonment",
                capability: MaterializationCapabilityKind.TargetGenerationAbandonment,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/retirement",
                capability: MaterializationCapabilityKind.TargetRetirement,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/cleanup",
                capability: MaterializationCapabilityKind.TargetCleanup,
                modes: MaterializationSynchronizationMode.Rebuild)
        ];

        return new(
            id: new("loads/search-json"),
            relation,
            sources,
            targetCapabilities: targets,
            updatePolicy: new(
                supportedModes: MaterializationSynchronizationMode.All,
                consistency: MaterializationConsistencyKind.BaselinePlusCatchUp,
                idempotency: MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(
                maximumAttempts: 5,
                exhaustedDisposition: exhaustedDisposition),
            freshnessPolicy: new(
                maximumLagMilliseconds: 30_000,
                maximumUnsettledMilliseconds: 10_000),
            controlLoops: controlLoops.IsDefault ? [] : controlLoops,
            provenance: Provenance(),
            controlWorkloads: controlWorkloads);
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind rebuildRead,
        bool isRoot)
    {
        ImmutableArray<MaterializationCapabilityRequirement> capabilities =
        [
            Requirement(
                id: $"{input.Value}/read",
                capability: rebuildRead,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: $"{input.Value}/continuation",
                capability: MaterializationCapabilityKind.SourceContinuation,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: $"{input.Value}/changes",
                capability: MaterializationCapabilityKind.SourceChangeDelivery,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: $"{input.Value}/settlement",
                capability: MaterializationCapabilityKind.SourceSettlement,
                modes: MaterializationSynchronizationMode.All)
        ];
        if (isRoot)
        {
            capabilities = capabilities.Add(Requirement(
                id: $"{input.Value}/inverse",
                capability: MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                modes: MaterializationSynchronizationMode.Incremental));
        }

        return new(input, capabilities);
    }

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes,
        long? writeItems = null) => new(
        id: new(id),
        capability,
        guarantees: Guarantees(capability),
        operatingLimits: OperatingLimits(capability, writeItems),
        modes);

    static MaterializationCapabilityProfile Profile(
        MaterializationEndpointRole role,
        string subject,
        ImmutableArray<MaterializationCapabilityRequirement> requirements) => new(
        id: new($"profile/{Uri.EscapeDataString(subject)}/v1"),
        role,
        subject,
        evidence:
        [
            .. requirements.Select(static requirement => new MaterializationCapabilityEvidence(
                id: new($"evidence/{requirement.Id.Value}"),
                capability: requirement.Capability,
                realization: CapabilityRealizationKind.Native,
                guarantees: requirement.Guarantees,
                operatingLimits: requirement.OperatingLimits,
                sourceReferences: ["tests/materialization-rebuild-json/v1"]))
        ]);

    static ImmutableArray<MaterializationOperatingLimit> OperatingLimits(
        MaterializationCapabilityKind capability,
        long? writeItems = null) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, 100),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, 100),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, writeItems ?? WriteItems),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            _ => []
        };

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
                    MaterializationGuaranteeKind.BaselinePlusCatchUp,
                    MaterializationGuaranteeKind.CompleteMutationDelivery
                ],
            MaterializationCapabilityKind.SourceSettlement =>
                [MaterializationGuaranteeKind.ExplicitSettlement],
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
            MaterializationCapabilityKind.TargetGenerationAbandonment =>
                [MaterializationGuaranteeKind.AtomicDurableGenerationExclusion],
            MaterializationCapabilityKind.TargetSeal
                or MaterializationCapabilityKind.TargetValidation
                or MaterializationCapabilityKind.TargetRetirement
                or MaterializationCapabilityKind.TargetCleanup =>
                [MaterializationGuaranteeKind.FencedMutation],
            _ => []
        };

    static ExecutionProvenance Provenance() => new(
        new ExecutionProducerProvenance("tests/materialization-rebuild-json", "1"),
        new ExecutionSourceProvenance("tests/materialization-rebuild-json-plan"),
        DocumentOrigin.Generated);
}
