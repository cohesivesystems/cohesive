using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Explain;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Tests.Relations;
using Microsoft.Azure.Cosmos;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Tests.Model;

public sealed class CosmosRelationQueryCompilerTests
{
    sealed class CosmosRelationQueryArtifactFactAttribute : FactAttribute
    {
        public CosmosRelationQueryArtifactFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COSMOS_RELATION_QUERY_CONNECTION_STRING")))
            {
                Skip = "Set COSMOS_RELATION_QUERY_CONNECTION_STRING to run the Cosmos keyset integration test.";
            }
        }
    }

    [Fact]
    public void Compile_RowQuery_ProducesExactReusableArtifact()
    {
        var fixture = Fixture.Row();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(RelationQueryNativeCompilationStatus.Exact, result.Status);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + "WHERE (c[\"Status\"] = @p0) ORDER BY c[\"Id\"] ASC OFFSET 5 LIMIT 25",
            artifact.Statement.Text);
        Assert.Equal(["Id", "Status"], artifact.SelectedFields.Select(FieldPathText));
        Assert.Equal(["f0", "f1"], artifact.ResultFields.Select(static field => field.Alias));
        Assert.Equal(
            [CosmosRelationQueryResultValueEncoding.JsonString, CosmosRelationQueryResultValueEncoding.JsonString],
            artifact.ResultFields.Select(static field => field.Encoding));
        Assert.All(artifact.ResultFields, static field =>
            Assert.Equal(
                new ScalarTypeRef(ScalarTypeKind.String),
                field.ValueContract.GetEffectiveType()));
        Assert.Equal(new QueryParameterId("status"), Assert.Single(artifact.Parameters).Parameter);
        Assert.Equal(new CosmosRelationQueryPagingContract(5, 25, Fixture.IdPath), artifact.Paging);
        Assert.Equal(fixture.PlanReference, artifact.Provenance.Plan);
        Assert.Equal(fixture.Realization.Fingerprint, artifact.Provenance.Realization);
        Assert.Equal(fixture.Placement.Fingerprint, artifact.Provenance.Placement);
        Assert.NotEmpty(artifact.Provenance.RealizationDecisions);
        Assert.NotEmpty(artifact.Provenance.CapabilityEvidence);
        Assert.NotEmpty(artifact.Provenance.CoveredNodes);
        Assert.Equal(
            artifact.SelectedFields.Select(static field => field.Input).OrderBy(static input => input.Value),
            artifact.Provenance.InputFields.OrderBy(static input => input.Value));
    }

    [Fact]
    public void Compile_NestedNamedTypeSourceField_UsesResolvedRequirementContract()
    {
        var result = Fixture.NestedNamedField().Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + "WHERE (c[\"Payload\"][\"Status\"] = @p0) ORDER BY c[\"Id\"] ASC OFFSET 0 LIMIT 25",
            Assert.Single(result.Artifacts).Statement.Text);
    }

    [Fact]
    public void Compile_OptionalNestedNamedTypeSourceField_RemainsFailClosed()
    {
        var result = Fixture.NestedNamedField(optionalPredicate: true).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("may be missing or null", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_NestedNamedTypeProjection_UsesResolvedResultContractAndEncoding()
    {
        var result = Fixture.NestedNamedProjection().Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains(
            "c[\"Payload\"][\"Status\"] AS f1",
            artifact.Statement.Text,
            StringComparison.Ordinal);
        var projected = Assert.Single(artifact.ResultFields, static field =>
            field.Field.Path == Fixture.StatusPath);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.String), projected.ValueContract.GetEffectiveType());
        Assert.Equal(CosmosRelationQueryResultValueEncoding.JsonString, projected.Encoding);
    }

    [Fact]
    public void Compile_OptionalNestedNamedTypeProjection_RetainsWeakGuarantees()
    {
        var result = Fixture.NestedNamedProjection(optional: true).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var projected = Assert.Single(Assert.Single(result.Artifacts).ResultFields, static field =>
            field.Field.Path == Fixture.ValuePath);
        Assert.Equal(FieldPresence.Optional, projected.ValueContract.Presence);
        Assert.Equal(FieldNullability.Nullable, projected.ValueContract.Nullability);
    }

    [Fact]
    public void Compile_UngroupedRowCount_ProducesDeterministicSingleRowArtifact()
    {
        var fixture = Fixture.UngroupedRowCount();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.QueryAggregation, artifact.Branch.Kind);
        Assert.Equal("SELECT COUNT(1) AS f0 FROM c", artifact.Statement.Text);
        Assert.Empty(artifact.SelectedFields);
        Assert.Equal("Count", Assert.Single(artifact.ResultFields).Field.Path.ToString());
        Assert.Equal(
            CosmosRelationQueryResultValueEncoding.ExactCountInteger,
            Assert.Single(artifact.ResultFields).Encoding);
        Assert.Equal(
            new ScalarTypeRef(ScalarTypeKind.Int64),
            Assert.Single(artifact.ResultFields).ValueContract.GetEffectiveType());
        Assert.Single(artifact.Provenance.CoveredAssignments);
        Assert.Null(artifact.Paging);
    }

    [Fact]
    public void Compile_GroupedAggregation_FailsWithoutDeterministicResultOrdering()
    {
        var result = Fixture.Aggregation(AggregateOperator.Max).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "deterministic order");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RowCountWithoutExactInputBound_FailsClosed()
    {
        var fixture = Fixture.UngroupedRowCount();

        var result = fixture.Compile(fixture.StorageBindingWithMaximumInputRows(null));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "maximumInputRows proof");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_Sum_FailsClosedForInexactNumericAccumulation()
    {
        var result = Fixture.Aggregation(AggregateOperator.Sum).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "decimal SUM");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RelationRows_FailsClosedUntilRootSemanticsAreRepresented()
    {
        var fixture = Fixture.Relation();

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "root correlation");
        Assert.NotNull(diagnostic.Branch);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RepeatedAndReorderedEquivalentBindings_AreDeterministic()
    {
        var fixture = Fixture.Row();
        var reversedBinding = fixture.StorageBindingWithFields(
            [.. fixture.StorageBinding.Fields.Reverse()]);

        var first = Assert.Single(fixture.Compile().Artifacts);
        var second = Assert.Single(fixture.Compile(reversedBinding).Artifacts);

        Assert.Equal(first.Statement.Text, second.Statement.Text);
        Assert.Equal(first.Statement.Parameters.ToArray(), second.Statement.Parameters.ToArray());
        Assert.Equal(first.StorageBinding.Fingerprint, second.StorageBinding.Fingerprint);
        Assert.Equal(first.Provenance.Plan, second.Provenance.Plan);
        Assert.Equal(first.Provenance.Realization, second.Provenance.Realization);
        Assert.Equal(first.Provenance.Placement, second.Provenance.Placement);
        Assert.Equal(first.Provenance.CoveredNodes.ToArray(), second.Provenance.CoveredNodes.ToArray());
        Assert.Equal(first.Provenance.CoveredAssignments.ToArray(), second.Provenance.CoveredAssignments.ToArray());
        Assert.Equal(first.Provenance.InputFields.ToArray(), second.Provenance.InputFields.ToArray());
        Assert.Equal(
            first.Provenance.RealizationDecisions.Select(DecisionKey),
            second.Provenance.RealizationDecisions.Select(DecisionKey));
        Assert.Equal(first.Provenance.CapabilityEvidence.ToArray(), second.Provenance.CapabilityEvidence.ToArray());
        Assert.Equal(first.Provenance.OperatingBoundaries.ToArray(), second.Provenance.OperatingBoundaries.ToArray());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsUnknownCoveredNode()
    {
        var fixture = Fixture.Row();
        var request = fixture.CreateNativeCompilationRequest();
        var branch = Assert.Single(request.Branches);

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                branch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [new QueryNodeId("unknown-node")],
                [],
                []));

        Assert.Equal("coveredNodes", exception.ParamName);
        Assert.Contains("reachable by the selected branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsCoveredNodeFromAnotherBranch()
    {
        var fixture = Fixture.IndependentBranches();
        var request = fixture.CreateNativeCompilationRequest();
        var selectedBranch = Assert.Single(request.Branches, static branch => branch.QueryResult == new QueryResultId("rows"));
        var unrelatedBranch = Assert.Single(request.Branches, branch => branch.Id != selectedBranch.Id);

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                selectedBranch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [unrelatedBranch.Node],
                [],
                []));

        Assert.Equal("coveredNodes", exception.ParamName);
        Assert.Contains("reachable by the selected branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsAssignmentOutsideCoveredNodes()
    {
        var fixture = Fixture.Row();
        var request = fixture.CreateNativeCompilationRequest();
        var branch = Assert.Single(request.Branches);
        var projectionNode = Assert.Single(
            fixture.Plan.ExecutionSlice.Nodes,
            static node => !node.ProjectionAssignments.IsDefaultOrEmpty);
        var assignment = projectionNode.ProjectionAssignments[0].Definition.Id;
        var coveredNode = Assert.Single(
            fixture.Plan.ExecutionSlice.Nodes,
            static node => node.CanonicalNode is SourceQueryNode);

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                branch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [coveredNode.Id],
                [assignment],
                []));

        Assert.Equal("coveredAssignments", exception.ParamName);
        Assert.Contains("belonging to covered branch nodes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsPlanInputReadOnlyByAnotherBranch()
    {
        var fixture = Fixture.IndependentBranches();
        var request = fixture.CreateNativeCompilationRequest();
        var selectedBranch = Assert.Single(request.Branches, static branch => branch.QueryResult == new QueryResultId("rows"));
        var selectedOutputs = selectedBranch.Outputs.Select(static output => output.Id).ToHashSet();
        var unrelatedInput = Assert.Single(
            fixture.Plan.InputContract.Sources.SelectMany(static source => source.Fields),
            field => field.Uses.All(use => !selectedOutputs.Contains(use.Output.Id)));

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                selectedBranch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [selectedBranch.Node],
                [],
                [unrelatedInput.Input.Id]));

        Assert.Equal("inputFields", exception.ParamName);
        Assert.Contains("read by the selected branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_RuntimeParameter_ReusesTemplateAndRejectsInexactInvocations()
    {
        var artifact = Assert.Single(Fixture.Row().Compile().Artifacts);

        var first = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("ready")
        });
        var second = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("closed")
        });

        Assert.Equal(artifact.Statement.Text, first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal("ready", Assert.Single(first.Parameters).Value);
        Assert.Equal("closed", Assert.Single(second.Parameters).Value);
        Assert.NotNull(first.ToQueryDefinition());
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>()));
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromInt64(42)
            }));
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.Undefined
            }));
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready"),
                [new("unknown")] = ObservationValue.FromString("value")
            }));
    }

    [Fact]
    public void Compile_StaleRealizationOrPlacement_IsInvalidBeforeLowering()
    {
        var current = Fixture.Row(offset: 5);
        var changed = Fixture.Row(offset: 6);

        var staleRealization = current.Compile(
            request: new(changed.Plan, current.Realization, changed.Placement));
        var stalePlacement = current.Compile(
            request: new(current.Plan, current.Realization, changed.Placement));

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, staleRealization.Status);
        Assert.Contains(staleRealization.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid
            && diagnostic.Message.Contains("realization report differs from the compiled plan", StringComparison.Ordinal));
        Assert.Empty(staleRealization.Artifacts);
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, stalePlacement.Status);
        Assert.Contains(stalePlacement.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid
            && diagnostic.Message.Contains("source placement differs from the compiled plan", StringComparison.Ordinal));
        Assert.Empty(stalePlacement.Artifacts);
    }

    [Fact]
    public void Compile_ExplicitIdBindingAffinityRejectsReuseAcrossAlignedPlanAndPlacementSnapshots()
    {
        var current = Fixture.Row(offset: 5);
        var changedPlan = Fixture.Row(offset: 6);
        var verified = current.StorageBindingWithAffinity();
        var changedPlacement = new RelationQuerySourcePlacement(
            current.Placement.SchemaVersion,
            current.Placement.Plan,
            current.Placement.ConventionSetVersion + "/changed",
            current.Placement.SourceInstances,
            current.Placement.Bindings);

        var planReuse = current.Compile(
            verified,
            new(changedPlan.Plan, changedPlan.Realization, changedPlan.Placement));
        var placementReuse = current.Compile(
            verified,
            new(current.Plan, current.Realization, changedPlacement));

        Assert.Equal(current.StorageBinding.Id, verified.Id);
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, planReuse.Status);
        Assert.Contains(planReuse.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch
            && diagnostic.Message.Contains("compiled-plan affinity", StringComparison.Ordinal));
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, placementReuse.Status);
        Assert.Contains(placementReuse.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch
            && diagnostic.Message.Contains("source-placement affinity", StringComparison.Ordinal));
        Assert.Empty(planReuse.Artifacts);
        Assert.Empty(placementReuse.Artifacts);
    }

    [Fact]
    public void Compile_KeysetInputs_AreRealizableAndCompileExactly()
    {
        var fixture = Fixture.Row(keyset: true);
        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(
            realization.IsRealizable,
            string.Join(Environment.NewLine, realization.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var result = fixture.Compile(
            request: new(fixture.Plan, realization, fixture.Placement));

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("(c[\"Id\"] > @p1)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(CosmosRelationQueryPagingKind.Keyset, artifact.Paging!.Kind);
        Assert.Equal(0, artifact.Paging.Offset);
        Assert.Equal(25, artifact.Paging.Limit);
    }

    [Fact]
    public void Compile_MismatchedStorageBinding_IsInvalidAndAttributable()
    {
        var fixture = Fixture.Row();
        var mismatched = fixture.StorageBindingWithTarget(new("different-target"));

        var result = fixture.Compile(mismatched);

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch
            && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_CrossSourceJoin_IsRejectedByTheSingleContainerBoundary()
    {
        var fixture = Fixture.CrossSourceJoin();

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid
            && diagnostic.Message.Contains("exactly the one source contract", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_SelectedSingleSourceBranch_IgnoresIndependentUnselectedSource()
    {
        var fixture = Fixture.IndependentSources();
        var allBranches = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var selectedPlacement = fixture.Placement.Bindings.Single(binding =>
            binding.Id == fixture.StorageBinding.PlacementBinding);
        var selected = Assert.Single(allBranches.Branches, branch => branch.Node == selectedPlacement.Node);
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement,
            [selected.Id]);

        var result = fixture.Compile(request: request);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(selected.Id, Assert.Single(result.Artifacts).Branch.Id);
    }

    [Fact]
    public void Compile_InitialKeysetPage_AppliesOnlyTheDeterministicLimit()
    {
        var fixture = Fixture.Row(keyset: true, initialKeyset: true);

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + "WHERE (c[\"Status\"] = @p0) ORDER BY c[\"Id\"] ASC OFFSET 0 LIMIT 25",
            artifact.Statement.Text);
        Assert.Equal(CosmosRelationQueryPagingKind.Keyset, artifact.Paging!.Kind);
        Assert.Single(artifact.Parameters);
    }

    [CosmosRelationQueryArtifactFact]
    public async Task CosmosArtifact_KeysetPagesMatchCanonicalOrderingWithoutDuplicatesOrOmissions()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOS_RELATION_QUERY_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The Cosmos connection string disappeared after discovery.");
        var databaseId = $"cohesive-relation-query-tests-{Guid.NewGuid():N}";
        using var client = CreateCosmosClient(connectionString);
        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        try
        {
            const string containerId = "loads";
            var container = (await database.CreateContainerAsync(
                new ContainerProperties(containerId, "/pk"))).Container;
            foreach (var id in new[] { "load-4", "load-2", "load-1", "load-3" })
            {
                await container.UpsertItemAsync(
                    new { id, pk = "tenant-a", Id = id, Status = "ready" },
                    new PartitionKey("tenant-a"));
            }

            var initial = Fixture.Row(keyset: true, initialKeyset: true, pageSize: 2);
            var continued = Fixture.Row(keyset: true, pageSize: 2);
            var initialBinding = EmulatorBinding(initial, client.Endpoint, databaseId, containerId);
            var continuedBinding = EmulatorBinding(continued, client.Endpoint, databaseId, containerId);
            var initialArtifact = Assert.Single(initial.Compile(initialBinding).Artifacts);
            var continuedArtifact = Assert.Single(continued.Compile(continuedBinding).Artifacts);
            var executor = new CosmosRelationQueryArtifactExecutor(container);

            var first = await executor.ExecuteAsync(ExecutionRequest(
                initial,
                initialBinding,
                initialArtifact,
                new Dictionary<QueryParameterId, ObservationValue>
                {
                    [new("status")] = ObservationValue.FromString("ready")
                }));
            var secondRequest = ExecutionRequest(
                continued,
                continuedBinding,
                continuedArtifact,
                new Dictionary<QueryParameterId, ObservationValue>
                {
                    [new("status")] = ObservationValue.FromString("ready"),
                    [new("cursor")] = ObservationValue.FromString("load-2")
                });
            var second = await executor.ExecuteAsync(secondRequest);
            var replayedSecond = await executor.ExecuteAsync(secondRequest);
            var exhausted = await executor.ExecuteAsync(ExecutionRequest(
                continued,
                continuedBinding,
                continuedArtifact,
                new Dictionary<QueryParameterId, ObservationValue>
                {
                    [new("status")] = ObservationValue.FromString("ready"),
                    [new("cursor")] = ObservationValue.FromString("load-4")
                }));

            Assert.True(first.IsSuccessful, string.Join(Environment.NewLine, first.Diagnostics.Select(static x => x.Message)));
            Assert.True(second.IsSuccessful, string.Join(Environment.NewLine, second.Diagnostics.Select(static x => x.Message)));
            Assert.Equal(
                ["load-1", "load-2", "load-3", "load-4"],
                first.Rows.Concat(second.Rows).Select(static row => row.Value.GetProperty("Id").String));
            Assert.Equal(
                second.Rows.Select(static row => row.Value),
                replayedSecond.Rows.Select(static row => row.Value));
            Assert.Empty(exhausted.Rows);
        }
        finally
        {
            await database.DeleteAsync();
        }
    }

    [CosmosRelationQueryArtifactFact]
    public async Task CosmosArtifact_PhysicalEntityScopeExcludesOutboxDocumentsFromSharedContainer()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOS_RELATION_QUERY_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The Cosmos connection string disappeared after discovery.");
        var databaseId = $"cohesive-relation-query-tests-{Guid.NewGuid():N}";
        using var client = CreateCosmosClient(connectionString);
        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        try
        {
            const string containerId = "shared";
            var container = (await database.CreateContainerAsync(
                new ContainerProperties(containerId, "/pk"))).Container;
            await container.UpsertItemAsync(
                new { id = "entity-1", pk = "tenant-a", documentKind = "entity-v2", Id = "load-1", Status = "ready" },
                new PartitionKey("tenant-a"));
            await container.UpsertItemAsync(
                new { id = "outbox-1", pk = "tenant-a", documentKind = "outbox-v2", Id = "outbox-1", Status = "ready" },
                new PartitionKey("tenant-a"));

            var fixture = Fixture.Row(offset: 0);
            var unscoped = EmulatorBinding(fixture, client.Endpoint, databaseId, containerId);
            var binding = WithEntitySourceScope(unscoped, "entity-v2");
            var artifact = Assert.Single(fixture.Compile(binding).Artifacts);
            var executor = new CosmosRelationQueryArtifactExecutor(container);

            var result = await executor.ExecuteAsync(ExecutionRequest(
                fixture,
                binding,
                artifact,
                new Dictionary<QueryParameterId, ObservationValue>
                {
                    [new("status")] = ObservationValue.FromString("ready")
                }));

            Assert.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics.Select(static x => x.Message)));
            Assert.Equal(
                ["load-1"],
                result.Rows.Select(static row => row.Value.GetProperty("Id").String));
        }
        finally
        {
            await database.DeleteAsync();
        }
    }

    [CosmosRelationQueryArtifactFact]
    public async Task CosmosArtifact_ProofGatedUtcInstantKeysetMatchesCanonicalPage()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOS_RELATION_QUERY_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The Cosmos connection string disappeared after discovery.");
        var databaseId = $"cohesive-relation-query-tests-{Guid.NewGuid():N}";
        using var client = CreateCosmosClient(connectionString);
        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        try
        {
            const string containerId = "temporal-loads";
            var container = (await database.CreateContainerAsync(
                new ContainerProperties(containerId, "/pk"))).Container;
            var atTen = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
            var atEleven = atTen.AddHours(1);
            await container.UpsertItemAsync(
                new { id = "load-1", pk = "tenant-a", Id = "load-1", ObservedInstant = atTen.ToString("O") },
                new PartitionKey("tenant-a"));
            await container.UpsertItemAsync(
                new { id = "load-2", pk = "tenant-a", Id = "load-2", ObservedInstant = atTen.ToString("O") },
                new PartitionKey("tenant-a"));
            await container.UpsertItemAsync(
                new { id = "load-3", pk = "tenant-a", Id = "load-3", ObservedInstant = atEleven.ToString("O") },
                new PartitionKey("tenant-a"));

            var fixture = Fixture.TemporalKeyset(ScalarTypeKind.Instant);
            var binding = EmulatorBinding(
                fixture,
                client.Endpoint,
                databaseId,
                containerId,
                stableUniqueOrderingPaths: [Fixture.IdPath],
                exactOrderingPaths: [Fixture.TemporalSourcePath(ScalarTypeKind.Instant), Fixture.IdPath]);
            var artifact = Assert.Single(fixture.Compile(binding).Artifacts);
            var executor = new CosmosRelationQueryArtifactExecutor(container);

            var result = await executor.ExecuteAsync(ExecutionRequest(
                fixture,
                binding,
                artifact,
                new Dictionary<QueryParameterId, ObservationValue>
                {
                    [new("temporal-cursor")] = ObservationValue.FromDateTimeOffset(atTen),
                    [new("id-cursor")] = ObservationValue.FromString("load-1")
                }));

            Assert.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics.Select(static x => x.Message)));
            Assert.Equal(
                ["load-2", "load-3"],
                result.Rows.Select(static row => row.Value.GetProperty("Id").String));
        }
        finally
        {
            await database.DeleteAsync();
        }
    }

    [Fact]
    public void Compile_MixedDirectionKeysetPage_ProducesLexicographicStrictAfterPredicate()
    {
        var fixture = Fixture.MixedDirectionKeyset();
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: [Fixture.StatusPath, Fixture.IdPath]);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("(c[\"Status\"] > @p1)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("(c[\"Status\"] = @p1)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("(c[\"Id\"] < @p2)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(
            "ORDER BY c[\"Status\"] ASC, c[\"Id\"] DESC OFFSET 0 LIMIT 25",
            artifact.Statement.Text,
            StringComparison.Ordinal);
        Assert.Equal(CosmosRelationQueryPagingKind.Keyset, artifact.Paging!.Kind);
        Assert.Equal(
            ["status", "cursor-status", "cursor-id"],
            artifact.Parameters.Select(static parameter => parameter.Parameter.Value));
    }

    [Fact]
    public void PagingContract_KeysetPageRejectsAnOffset()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CosmosRelationQueryPagingContract(
            CosmosRelationQueryPagingKind.Keyset,
            offset: 1,
            limit: 25,
            stableUniquePath: Fixture.IdPath));

        Assert.Equal("offset", exception.ParamName);
    }

    [Fact]
    public void PagingContract_KeysetKindSurvivesPortableJsonRoundTrip()
    {
        CosmosRelationQueryPagingContract contract = new(
            CosmosRelationQueryPagingKind.Keyset,
            offset: 0,
            limit: 25,
            stableUniquePath: Fixture.IdPath);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var rehydrated = JsonSerializer.Deserialize<CosmosRelationQueryPagingContract>(
            JsonSerializer.Serialize(contract, options),
            options);

        Assert.Equal(contract, rehydrated);
    }

    [Fact]
    public void ParameterBinding_TemporalValueDomainSurvivesPortableJsonRoundTrip()
    {
        QueryParameterDefinition definition = new(
            new("cursor"),
            new ScalarTypeRef(ScalarTypeKind.Instant));
        CosmosRelationQueryParameterBinding binding = new(
            "@p0",
            definition,
            definition.EffectiveValueContract,
            CosmosRelationQueryParameterValueDomain.UtcRoundTripInstant);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var rehydrated = JsonSerializer.Deserialize<CosmosRelationQueryParameterBinding>(
            JsonSerializer.Serialize(binding, options),
            options);

        Assert.Equal(binding, rehydrated);
    }

    [Fact]
    public void Compile_OversizedKeysetPage_FailsAtTheDeclaredPageBoundary()
    {
        var fixture = Fixture.Row(
            keyset: true,
            initialKeyset: true,
            pageSize: CosmosRelationQueryTargetProfile.MaximumPageSize + 1,
            overrideUnavailableRequirements: true);

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("Page size", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_OptionalKeysetContinuation_FailsTheNonNullBoundary()
    {
        var fixture = Fixture.Row(
            keyset: true,
            cursorPresence: FieldPresence.Optional,
            overrideUnavailableRequirements: true);

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("keyset continuation", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("missing or null", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Author_NonScalarKeysetContinuation_FailsTheComparableCategoryBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() => Fixture.Row(
            keyset: true,
            cursorType: new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String))));

        Assert.Contains("relationQuery.expression.resultCategoryMismatch", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Comparable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OptionalNullablePredicate_IsRejectedWithoutClaimingExactMissingNullSemantics()
    {
        var fixture = Fixture.Row(optionalPredicate: true);

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("may be missing or null", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_DefaultExactContributorObservability_IsRejectedByNativeCompiler()
    {
        var fixture = Fixture.Row();
        var realization = fixture.RealizeExactContributors();

        var result = fixture.Compile(
            request: new(fixture.Plan, realization, fixture.Placement));

        Assert.True(realization.IsRealizable);
        Assert.Equal(
            RelationQueryOccurrenceProvenanceMode.ExactContributors,
            realization.Observability.OccurrenceProvenance);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "contributor-occurrence lineage");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_WholeRowDistinct_RetainsUndemandedProjectionFieldsInSqlShape()
    {
        var result = Fixture.DistinctSelectedId().Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT DISTINCT c[\"Id\"] AS f0, c[\"Status\"] AS __distinct0 FROM c "
            + "ORDER BY c[\"Id\"] ASC",
            artifact.Statement.Text);
        Assert.Equal(["Id", "Status"], artifact.SelectedFields.Select(FieldPathText));
        Assert.Equal("Id", Assert.Single(artifact.ResultFields).Field.Path.ToString());
    }

    [Fact]
    public void Compile_UnorderedWholeRowDistinct_FailsClosed()
    {
        var result = Fixture.DistinctSelectedId(ordered: false).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("first-seen row order", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ImplicitIdentityOrdering_RequiresExactOrderingEvidence()
    {
        var fixture = Fixture.Int32LiteralEquality(ObservationValue.FromInt64(42));
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: []);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "exact physical ordering evidence");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ExpansionFieldWithoutElementOrderingEvidence_FailsClosed()
    {
        var result = Fixture.ExpandedItemField().Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "collection-element ordering evidence");
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant)]
    [InlineData(ScalarTypeKind.DateTime)]
    public void Compile_TemporalOrdering_FailsClosedWhenPhysicalOrderIsNotProven(
        ScalarTypeKind temporalKind)
    {
        var result = Fixture.TemporalOrdering(temporalKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("ordering", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("temporal ordering is not exact", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant)]
    [InlineData(ScalarTypeKind.DateTime)]
    public void Compile_TemporalOrdering_UsesExplicitUtcPhysicalOrderingProof(
        ScalarTypeKind temporalKind)
    {
        var fixture = Fixture.TemporalOrdering(temporalKind);
        var sourcePath = Fixture.TemporalSourcePath(temporalKind);
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [sourcePath],
            exactOrderingPaths: [sourcePath]);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Contains(
            $"ORDER BY c[\"{sourcePath}\"] ASC",
            Assert.Single(result.Artifacts).Statement.Text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant)]
    [InlineData(ScalarTypeKind.DateTime)]
    public void Compile_TemporalEquality_FailsClosedWithoutCanonicalStorageEncoding(
        ScalarTypeKind temporalKind)
    {
        var result = Fixture.TemporalEquality(temporalKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("proven exact Cosmos JSON value domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant, CosmosRelationQueryParameterValueDomain.UtcRoundTripInstant)]
    [InlineData(ScalarTypeKind.DateTime, CosmosRelationQueryParameterValueDomain.UtcRoundTripDateTime)]
    public void Compile_ProofGatedTemporalRange_RetainsCanonicalUtcParameterDomain(
        ScalarTypeKind temporalKind,
        CosmosRelationQueryParameterValueDomain expectedDomain)
    {
        var fixture = Fixture.TemporalComparison(temporalKind, BinaryOperator.Ge);
        var sourcePath = Fixture.TemporalSourcePath(temporalKind);
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: [sourcePath, Fixture.IdPath]);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains($"(c[\"{sourcePath}\"] >= @p0)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(expectedDomain, Assert.Single(artifact.Parameters).ValueDomain);
        var canonical = temporalKind == ScalarTypeKind.DateTime
            ? ObservationValue.FromString("2026-08-23T12:34:56.0000000Z")
            : ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 8, 23, 12, 34, 56, TimeSpan.Zero));
        Assert.NotNull(artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("temporal-value")] = canonical
        }));
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant)]
    [InlineData(ScalarTypeKind.DateTime)]
    public void Bind_ProofGatedTemporalRange_RejectsNonCanonicalOrNonUtcBoundary(
        ScalarTypeKind temporalKind)
    {
        var fixture = Fixture.TemporalComparison(temporalKind, BinaryOperator.Ge);
        var sourcePath = Fixture.TemporalSourcePath(temporalKind);
        var compilation = fixture.Compile(fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: [sourcePath, Fixture.IdPath]));
        Assert.True(compilation.IsSuccessful, Diagnostics(compilation));
        var artifact = Assert.Single(compilation.Artifacts);
        var invalid = temporalKind == ScalarTypeKind.DateTime
            ? ObservationValue.FromString("2026-08-23T12:34:56Z")
            : ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 8, 23, 12, 34, 56, TimeSpan.FromHours(2)));

        Assert.Throws<ArgumentException>(() => artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("temporal-value")] = invalid
        }));
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant, CosmosRelationQueryParameterValueDomain.UtcRoundTripInstant)]
    [InlineData(ScalarTypeKind.DateTime, CosmosRelationQueryParameterValueDomain.UtcRoundTripDateTime)]
    public void Compile_TemporalKeyset_UsesProofGatedLexicographicContinuation(
        ScalarTypeKind temporalKind,
        CosmosRelationQueryParameterValueDomain expectedDomain)
    {
        var fixture = Fixture.TemporalKeyset(temporalKind);
        var sourcePath = Fixture.TemporalSourcePath(temporalKind);
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: [sourcePath, Fixture.IdPath]);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains($"(c[\"{sourcePath}\"] > @p0)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains($"(c[\"{sourcePath}\"] = @p0)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("(c[\"Id\"] > @p1)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(expectedDomain, artifact.Parameters[0].ValueDomain);
        Assert.Equal(CosmosRelationQueryParameterValueDomain.Canonical, artifact.Parameters[1].ValueDomain);
        Assert.Equal(CosmosRelationQueryPagingKind.Keyset, artifact.Paging!.Kind);
    }

    [Theory]
    [InlineData(AggregateOperator.Min)]
    [InlineData(AggregateOperator.Max)]
    public void Compile_NonNumericMinimumOrMaximum_FailsClosed(AggregateOperator operation)
    {
        var result = Fixture.NonNumericAggregation(operation).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "known Int32 value");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_DeterministicSqlAndFingerprint_AreCultureIndependent()
    {
        var fixture = Fixture.Row();
        CosmosRelationQueryCompiledArtifact first;
        CosmosRelationQueryCompiledArtifact second;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            first = Assert.Single(fixture.Compile().Artifacts);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            second = Assert.Single(fixture.Compile().Artifacts);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        Assert.Equal(first.Statement.Text, second.Statement.Text);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Compile_ArtifactFingerprint_ChangesWithCompilerProfileOrStorageBinding()
    {
        var fixture = Fixture.Row();
        var baseline = Assert.Single(fixture.Compile().Artifacts);
        var changedCompiler = Assert.Single(fixture.Compile(
            options: new(
                compilerProfile: "tests/cosmos/compiler-v2",
                conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet)).Artifacts);
        var changedBinding = Assert.Single(fixture.Compile(
            fixture.StorageBindingWithContainer("loads-v2")).Artifacts);

        Assert.Equal(baseline.Statement.Text, changedCompiler.Statement.Text);
        Assert.NotEqual(baseline.Fingerprint, changedCompiler.Fingerprint);
        Assert.Equal(baseline.Statement.Text, changedBinding.Statement.Text);
        Assert.NotEqual(baseline.StorageBinding.Fingerprint, changedBinding.StorageBinding.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedBinding.Fingerprint);
    }

    [Fact]
    public void NativeCompile_RejectsBoundEvidenceAuthoredUnderDifferentCompilerPolicy()
    {
        var fixture = Fixture.Row();
        var boundRequest = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var bound = new CosmosRelationQueryCompiler().Realize(boundRequest, fixture.StorageBinding);
        Assert.True(bound.IsRealizable);
        var nativeRequest = new RelationQueryNativeCompilationRequest(
            fixture.Plan,
            bound,
            fixture.Placement);
        CosmosRelationQueryCompilerOptions[] changedPolicies =
        [
            new(
                compilerProfile: "tests/cosmos/compiler-v3",
                conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet),
            new(
                compilerProfile: CosmosRelationQueryCompilerOptions.CurrentCompilerProfile,
                conventionSetVersion: "tests/cosmos/lowering-conventions/v2")
        ];

        foreach (var changedPolicy in changedPolicies)
        {
            var result = new CosmosRelationQueryCompiler(changedPolicy).Compile(
                nativeRequest,
                fixture.StorageBinding);

            Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
                && diagnostic.Message.Contains("compiler-policy evidence", StringComparison.Ordinal));
            Assert.Empty(result.Artifacts);
            var explain = CosmosRelationQueryExplainProjector.Project(nativeRequest, result);
            Assert.Equal(bound.Fingerprint, explain.Attempt.BoundRealization);
            Assert.Equal(RelationQueryExplainStageStatus.Invalid, explain.Status);
        }
    }

    [Fact]
    public void Realize_BindingReferenceRetainsCompilerConfigurationAndItsOrigin()
    {
        var fixture = Fixture.Row();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var conventional = new CosmosRelationQueryCompiler().Realize(request, fixture.StorageBinding);
        CosmosRelationQueryCompilerOptions explicitOptions = new(
            compilerProfile: CosmosRelationQueryCompilerOptions.CurrentCompilerProfile,
            conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet);
        var explicitlyConfigured = new CosmosRelationQueryCompiler(explicitOptions)
            .Realize(request, fixture.StorageBinding);

        var conventionalConfiguration = conventional.Evidence.Binding.ConfigurationDecisions
            .ToDictionary(static decision => decision.Setting, StringComparer.Ordinal);
        var explicitConfiguration = explicitlyConfigured.Evidence.Binding.ConfigurationDecisions
            .ToDictionary(static decision => decision.Setting, StringComparer.Ordinal);
        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            conventionalConfiguration[CosmosRelationQueryCompiler.CompilerProfileSetting].Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.Explicit,
            explicitConfiguration[CosmosRelationQueryCompiler.CompilerProfileSetting].Origin);
        Assert.Equal(
            explicitOptions.CompilerProfile,
            explicitConfiguration[CosmosRelationQueryCompiler.CompilerProfileSetting].Authority);
        Assert.Equal(
            explicitOptions.ConventionSetVersion,
            explicitConfiguration[CosmosRelationQueryCompiler.CompilerConventionSetting].Authority);
        Assert.NotEqual(conventional.Fingerprint, explicitlyConfigured.Fingerprint);
    }

    [Fact]
    public void Realize_ProfileInfeasibilityDoesNotInvokeContextualSuccessProjection()
    {
        var fixture = Fixture.Row();
        var planReference = RelationQueryCompiledPlanReference.From(fixture.Plan);
        var unavailableProfile = new RelationQueryTargetCapabilityProfile(
            CosmosRelationQueryTargetProfile.Target,
            CosmosRelationQueryTargetProfile.ProfileId,
            [planReference.DefinitionSchemaVersion],
            [planReference.CompilerProfile]);
        var infeasible = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            unavailableProfile,
            CosmosRelationQueryTargetProfile.Policy);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, infeasible.Status);
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            infeasible,
            fixture.Placement);
        CosmosRelationQueryCompiler compiler = new();

        var bound = compiler.Realize(request, fixture.StorageBinding);
        var compilation = compiler.Compile(request, fixture.StorageBinding);

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, bound.Status);
        Assert.Empty(bound.Evidence.Assessments);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
        Assert.Empty(compilation.Artifacts);
    }

    [Fact]
    public void Realize_MissingFieldRecordsOnePrimaryFailureAndBlocksUnexaminedRequirements()
    {
        var fixture = Fixture.Row();
        var statusInput = fixture.InputFor(Fixture.StatusPath);
        var incomplete = fixture.StorageBindingWithFields(
        [
            .. fixture.StorageBinding.Fields.Where(field => field.Input != statusInput)
        ]);
        var compiler = new CosmosRelationQueryCompiler();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);

        var bound = compiler.Realize(request, incomplete);

        Assert.False(bound.IsRealizable);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, bound.Status);
        var branch = Assert.Single(request.Branches);
        var assessments = bound.Evidence.Assessments
            .Where(assessment => assessment.Branch == branch.Id)
            .ToArray();
        Assert.True(assessments.Length > 1);
        var primary = Assert.Single(assessments, static assessment =>
            assessment.Status is RelationQueryBoundAssessmentStatus.Unavailable
                or RelationQueryBoundAssessmentStatus.Invalid);
        Assert.Equal(RelationQueryBoundAssessmentStatus.Unavailable, primary.Status);
        Assert.Equal(
            new RelationQueryAdapterDecisionCode(CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing),
            primary.AdapterDecisionCode);
        Assert.Equal(statusInput, primary.Input);
        Assert.Equal(Fixture.StatusPath, primary.Field);
        Assert.Equal($"field/{statusInput.Value}", primary.FailedConfigurationSetting);
        Assert.Empty(primary.CapabilityEvidence);
        Assert.Empty(primary.OperatingBoundaries);
        Assert.Empty(primary.PreservedGuarantees);
        var contextualDiagnostic = Assert.Single(bound.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable
            && diagnostic.AdapterDecisionCode == primary.AdapterDecisionCode);
        Assert.Equal(statusInput, contextualDiagnostic.Input);
        Assert.Equal(Fixture.StatusPath, contextualDiagnostic.Field);
        Assert.Equal($"field/{statusInput.Value}", contextualDiagnostic.BindingSetting);

        var configuration = bound.Evidence.Binding.ConfigurationDecisions.ToDictionary(
            static decision => decision.Setting,
            StringComparer.Ordinal);
        var defaultOrigin = incomplete.Origin == CosmosRelationQueryBindingOrigin.Convention
            ? EffectiveConfigurationOrigin.AdapterConvention
            : EffectiveConfigurationOrigin.Explicit;
        var defaultAuthority = incomplete.Origin == CosmosRelationQueryBindingOrigin.Convention
            ? incomplete.ConventionSetVersion!
            : incomplete.Id.Value;

        var blocked = assessments.Where(assessment => assessment.Id != primary.Id).ToArray();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, assessment =>
        {
            Assert.Equal(RelationQueryBoundAssessmentStatus.Blocked, assessment.Status);
            Assert.Equal(RelationQueryUnavailableReason.PrerequisiteBlocked, assessment.UnavailableReason);
            Assert.Equal(primary.AdapterDecisionCode, assessment.AdapterDecisionCode);
            Assert.Equal(primary.Id, assessment.BlockedBy);
            Assert.Empty(assessment.CapabilityEvidence);
            Assert.Empty(assessment.OperatingBoundaries);
            Assert.Empty(assessment.PreservedGuarantees);
            Assert.Empty(assessment.MissingCapabilityEvidence);
            Assert.Null(assessment.FailedOperatingBoundary);
            Assert.Null(assessment.FailedConfigurationSetting);
            if (assessment.ConfigurationSetting is { } setting)
            {
                Assert.Equal(configuration[setting].Origin, assessment.Origin);
                Assert.Equal(configuration[setting].Authority, assessment.Authority);
            }
            else
            {
                Assert.Equal(defaultOrigin, assessment.Origin);
                Assert.Equal(defaultAuthority, assessment.Authority);
            }
        });

        var compilation = compiler.Compile(request, incomplete);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
        Assert.Empty(compilation.Artifacts);
        var nativeDiagnostic = Assert.Single(compilation.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable
            && diagnostic.AdapterDecisionCode == primary.AdapterDecisionCode);
        Assert.Equal(statusInput, nativeDiagnostic.Input);
        Assert.Equal(Fixture.StatusPath, nativeDiagnostic.Field);
        Assert.Equal($"field/{statusInput.Value}", nativeDiagnostic.BindingSetting);
    }

    [Fact]
    public void NativeCompile_BindingFingerprintMismatchIsInvalid()
    {
        var fixture = Fixture.Row();
        var request = fixture.CreateNativeCompilationRequest();

        var result = new CosmosRelationQueryCompiler().Compile(
            request,
            fixture.StorageBindingWithContainer("loads-v2"));

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("storage-binding fingerprint", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_UnpagedOrderBy_RejectsNonUniqueFinalKey()
    {
        var fixture = Fixture.OrderingByStatus();
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: [Fixture.IdPath, Fixture.StatusPath]);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("final stable unique source path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_UnpagedOrderBy_AcceptsExplicitlyProvenUniqueFinalKey()
    {
        var fixture = Fixture.OrderingByStatus();
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath, Fixture.StatusPath],
            exactOrderingPaths: [Fixture.IdPath, Fixture.StatusPath]);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c ORDER BY c[\"Status\"] ASC",
            artifact.Statement.Text);
        Assert.Null(artifact.Paging);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_PrecisionUnsafeNumericComparison_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.PrecisionUnsafeComparison(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("proven exact Cosmos JSON value domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_PrecisionUnsafeNumericOrdering_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.PrecisionUnsafeOrdering(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("wider numeric", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_PrecisionUnsafeNumericResult_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.PrecisionUnsafeProjection(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("physical result encoding", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_Int64DirectProjection_WithExactPhysicalDomain_UsesExactEncoding()
    {
        var fixture = Fixture.PrecisionUnsafeProjection(ScalarTypeKind.Int64);
        var binding = fixture.StorageBindingWithExactIntegerDomain(
            Fixture.Int64ValuePath,
            CosmosRelationQueryExactIntegerDomain.NonNegative);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var value = Assert.Single(Assert.Single(result.Artifacts).ResultFields, static field =>
            field.Field.Path == Fixture.ValuePath);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Int64), value.ValueContract.GetEffectiveType());
        Assert.Equal(CosmosRelationQueryResultValueEncoding.JsonExactInt64, value.Encoding);
    }

    [Fact]
    public void Compile_BytesRuntimeParameter_IsRejectedBeforeArtifactConstruction()
    {
        var result = Fixture.BytesParameterProjection().Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("does not have a Cosmos SQL v4 parameter encoding", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_TypedInt32LiteralWithCanonicalRepresentation_Succeeds()
    {
        var result = Fixture.Int32LiteralEquality(ObservationValue.FromInt64(42)).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("c[\"Amount\"] = @p0", artifact.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_TypedInt32LiteralWithNoncanonicalRepresentation_FailsDeterministically()
    {
        var fixture = Fixture.Int32LiteralEquality(ObservationValue.FromDouble(42d));

        var first = fixture.Compile();
        var second = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        var diagnostic = Assert.Single(first.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("exact canonical representation", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(first.Artifacts);
        Assert.Empty(second.Artifacts);
    }

    [Fact]
    public void Compile_Int32ParameterDefaultWithCanonicalRepresentation_Succeeds()
    {
        var result = Fixture.Int32ParameterDefaultEquality(ObservationValue.FromInt64(42)).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("c[\"Amount\"] = @p0", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(Fixture.NumericParameter, Assert.Single(artifact.Parameters).Parameter);
    }

    [Fact]
    public void Compile_Int32ParameterDefaultWithNoncanonicalRepresentation_FailsDeterministically()
    {
        var fixture = Fixture.Int32ParameterDefaultEquality(ObservationValue.FromDouble(42d));

        var first = fixture.Compile();
        var second = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        var diagnostic = Assert.Single(first.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("default outside its exact Cosmos representation", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(first.Artifacts);
        Assert.Empty(second.Artifacts);
    }

    [Fact]
    public void CanonicalDocument_UndefinedConstant_IsRejectedBeforeNativeArtifactConstruction()
    {
        var exception = Assert.Throws<ArgumentException>(Fixture.UndefinedConstantProjection);

        Assert.Contains("relationQuery.value.kindUnsupported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Undefined", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalDocument_BytesConstant_IsRejectedBeforeNativeArtifactConstruction()
    {
        var exception = Assert.Throws<ArgumentException>(Fixture.BytesConstantProjection);

        Assert.Contains("relationQuery.value.kindUnsupported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bytes", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_KeyedDistinct_FailsClosedAsUnsupportedLogicalOperator()
    {
        var result = Fixture.DistinctSelectedId(keyed: true).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("explicit distinct keys are unsupported", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(UnsafeDistinctDomain.NullableString)]
    [InlineData(UnsafeDistinctDomain.NestedArray)]
    [InlineData(UnsafeDistinctDomain.Int64)]
    [InlineData(UnsafeDistinctDomain.Decimal)]
    public void Compile_WholeRowDistinct_RejectsInexactEqualityDomains(UnsafeDistinctDomain domain)
    {
        var result = Fixture.UnsafeDistinct(domain).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("DISTINCT assignment", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("exact scalar equality domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ContainsExactStringArray_EmitsArrayContains()
    {
        var result = Fixture.ContainsFilter(ScalarTypeKind.String).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + "WHERE ARRAY_CONTAINS(@p0, c[\"Status\"]) ORDER BY c[\"Id\"] ASC",
            artifact.Statement.Text);
        Assert.Equal(new QueryParameterId("contains-values"), Assert.Single(artifact.Parameters).Parameter);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_ContainsPrecisionUnsafeDomain_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.ContainsFilter(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("proven exact scalar equality domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ContainsNestedNamedTypeCandidate_UsesResolvedRequirementContract()
    {
        var result = Fixture.ContainsFilter(ScalarTypeKind.String, nestedCandidate: true).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Contains(
            "ARRAY_CONTAINS(@p0, c[\"Payload\"][\"Status\"])",
            Assert.Single(result.Artifacts).Statement.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ContainsOptionalNestedNamedTypeCandidate_RemainsFailClosed()
    {
        var result = Fixture.ContainsFilter(ScalarTypeKind.String, optionalCandidate: true).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("may be missing or null", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_EmitsOneCorrelatedExistsWithoutMultiplyingRoots()
    {
        var fixture = Fixture.StructuredCollectionAny(includeCount: true);

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(2, result.Artifacts.Length);
        var rows = Assert.Single(result.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
        var count = Assert.Single(result.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);
        const string predicate =
            "EXISTS (SELECT VALUE e0 FROM e0 IN c[\"Stops\"] WHERE ((e0[\"Location\"] = @p0) AND (e0[\"Type\"] = @p1)))";
        Assert.Equal(
            $"SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c WHERE {predicate} "
            + "ORDER BY c[\"Id\"] ASC OFFSET 0 LIMIT 25",
            rows.Statement.Text);
        Assert.Equal($"SELECT COUNT(1) AS f0 FROM c WHERE {predicate}", count.Statement.Text);
        Assert.DoesNotContain(" JOIN ", rows.Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ARRAY_CONTAINS", rows.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(
            new string?[] { "location", null },
            rows.Statement.Parameters.Select(static parameter => parameter.Binding).ToArray());
        Assert.Equal(
            [Fixture.IdPath, Fixture.StatusPath, Fixture.StopsPath],
            rows.SelectedFields.Select(static field => field.Field.Path).OrderBy(static path => path.ToString()).ToArray());
        Assert.Equal(
            [Fixture.StopsPath],
            count.SelectedFields.Select(static field => field.Field.Path).ToArray());
        var stopsInput = fixture.InputFor(Fixture.StopsPath);
        Assert.All([rows, count], artifact =>
        {
            Assert.Contains(Fixture.Filter, artifact.Provenance.CoveredNodes);
            Assert.Contains(stopsInput, artifact.Provenance.InputFields);
            Assert.Equal(
                CosmosRelationQueryCompilerOptions.CurrentCompilerProfile,
                artifact.Provenance.CompilerProfile);
        });
    }

    [Fact]
    public void Compile_StructuredCollectionAny_IsDeterministicAcrossReorderedChildEvidence()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var first = Assert.Single(fixture.Compile().Artifacts);
        var reordered = fixture.StorageBindingWithCollectionScope(new(
            fixture.StopsCollectionScope.SemanticProfile,
            fixture.StopsCollectionScope.ElementScope,
            fixture.StopsCollectionScope.CorrelationGuarantee,
            fixture.StopsCollectionScope.CollectionMissingValueBehavior,
            fixture.StopsCollectionScope.CollectionNullValueBehavior,
            fixture.StopsCollectionScope.NullElementBehavior,
            fixture.StopsCollectionScope.EmptyCollectionBehavior,
            [.. fixture.StopsCollectionScope.ChildFields.Reverse()]));

        var second = Assert.Single(fixture.Compile(reordered).Artifacts);

        Assert.Equal(first.Statement.Text, second.Statement.Text);
        Assert.Equal(first.Statement.Parameters.ToArray(), second.Statement.Parameters.ToArray());
        Assert.Equal(first.StorageBinding.Fingerprint, second.StorageBinding.Fingerprint);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutCollectionEvidence()
    {
        var fixture = Fixture.StructuredCollectionAny();

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(collectionScope: null));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "does not provide explicit");
        Assert.NotNull(diagnostic.Node);
        Assert.Contains(result.Diagnostics, static candidate =>
            candidate.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable
            && candidate.Message.Contains("does not provide explicit", StringComparison.Ordinal)
            && candidate.Input is not null);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutSameElementGuarantee()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsCollectionScope;
        var weak = new CosmosRelationQueryCollectionScopeEvidence(
            current.SemanticProfile,
            current.ElementScope,
            CosmosRelationQueryCollectionCorrelationGuarantee.Unproven,
            current.CollectionMissingValueBehavior,
            current.CollectionNullValueBehavior,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            current.ChildFields);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "same-array-element");
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWhenChildAbsenceIsUnproven()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsCollectionScope;
        var location = current.ResolveChild(Fixture.StopLocationPath);
        var weakLocation = new CosmosRelationQueryCollectionElementFieldBinding(
            location.ElementPath,
            location.DocumentPath,
            location.ValueDomain,
            location.SemanticCapabilities,
            location.SemanticProfile,
            CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven,
            location.NullValueBehavior);
        var weak = new CosmosRelationQueryCollectionScopeEvidence(
            current.SemanticProfile,
            current.ElementScope,
            current.CorrelationGuarantee,
            current.CollectionMissingValueBehavior,
            current.CollectionNullValueBehavior,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            [
                .. current.ChildFields.Select(child =>
                    child.ElementPath == Fixture.StopLocationPath ? weakLocation : child)
            ]);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("prohibits missing and null", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(StructuredAnyPredicate.Inequality, " != @p0")]
    [InlineData(StructuredAnyPredicate.NegatedEquality, "(NOT (e0[\"Location\"] = @p0))")]
    [InlineData(StructuredAnyPredicate.Disjunction, " OR ")]
    public void Compile_StructuredCollectionAny_SupportsTheDeclaredPredicateClosure(
        StructuredAnyPredicate predicate,
        string expectedSql)
    {
        var result = Fixture.StructuredCollectionAny(predicate: predicate).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains(expectedSql, artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("EXISTS (SELECT VALUE e0", artifact.Statement.Text, StringComparison.Ordinal);
    }

    public static TheoryData<StructuredAnyPredicate, string, object> StructuredCollectionConstantCases => new()
    {
        { StructuredAnyPredicate.BoolConstant, "IsRequired", true },
        { StructuredAnyPredicate.Int32Constant, "Sequence", 7L },
        { StructuredAnyPredicate.StringConstant, "Location", "SEA" },
        {
            StructuredAnyPredicate.GuidConstant,
            "ExternalId",
            "01234567-89ab-cdef-0123-456789abcdef"
        },
        { StructuredAnyPredicate.DateConstant, "ServiceDate", "2026-07-19" }
    };

    [Theory]
    [MemberData(nameof(StructuredCollectionConstantCases))]
    public void Compile_StructuredCollectionAny_ConstantEqualitySupportsAdvertisedScalarDomains(
        StructuredAnyPredicate predicate,
        string childField,
        object expectedConstant)
    {
        var result = Fixture.StructuredCollectionAny(predicate: predicate).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + $"WHERE EXISTS (SELECT VALUE e0 FROM e0 IN c[\"Stops\"] WHERE (e0[\"{childField}\"] = @p0)) "
            + "ORDER BY c[\"Id\"] ASC OFFSET 0 LIMIT 25",
            artifact.Statement.Text);
        var parameter = Assert.Single(artifact.Statement.Parameters);
        Assert.Equal("@p0", parameter.Name);
        Assert.Equal(CosmosSqlParameterBindingKind.Constant, parameter.Kind);
        Assert.Null(parameter.Binding);
        Assert.Equal(expectedConstant, parameter.ConstantValue);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedForOutOfRangeInt32Constant()
    {
        var result = Fixture.StructuredCollectionAny(
            predicate: StructuredAnyPredicate.OutOfRangeInt32Constant).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("does not satisfy", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionInequality_FailsClosedWithoutExactInequalityEvidence()
    {
        var fixture = Fixture.StructuredCollectionAny(predicate: StructuredAnyPredicate.Inequality);
        var current = fixture.StopsCollectionScope;
        var location = current.ResolveChild(Fixture.StopLocationPath);
        var weakLocation = new CosmosRelationQueryCollectionElementFieldBinding(
            location.ElementPath,
            location.DocumentPath,
            location.ValueDomain,
            CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality,
            location.SemanticProfile,
            location.MissingValueBehavior,
            location.NullValueBehavior);
        var weak = new CosmosRelationQueryCollectionScopeEvidence(
            current.SemanticProfile,
            current.ElementScope,
            current.CorrelationGuarantee,
            current.CollectionMissingValueBehavior,
            current.CollectionNullValueBehavior,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            [
                .. current.ChildFields.Select(child =>
                    child.ElementPath == Fixture.StopLocationPath ? weakLocation : child)
            ]);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("ExactInequality", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWhenNullElementsAreNotProhibited()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsCollectionScope;
        var weak = new CosmosRelationQueryCollectionScopeEvidence(
            current.SemanticProfile,
            current.ElementScope,
            current.CorrelationGuarantee,
            current.CollectionMissingValueBehavior,
            current.CollectionNullValueBehavior,
            CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven,
            current.EmptyCollectionBehavior,
            current.ChildFields);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            "explicit-null collection elements");
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(StructuredCollectionScopeGap.ElementScope, "JSON-array element scope")]
    [InlineData(StructuredCollectionScopeGap.CollectionMissing, "missing and null collections")]
    [InlineData(StructuredCollectionScopeGap.CollectionNull, "missing and null collections")]
    [InlineData(StructuredCollectionScopeGap.EmptyCollection, "empty JSON array")]
    public void Compile_StructuredCollectionAny_FailsClosedForUnprovenCollectionScopeEvidence(
        StructuredCollectionScopeGap gap,
        string expectedDiagnostic)
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsCollectionScope;
        var weak = CopyCollectionScope(
            current,
            elementScope: gap == StructuredCollectionScopeGap.ElementScope
                ? CosmosRelationQueryCollectionElementScope.Unproven
                : current.ElementScope,
            collectionMissing: gap == StructuredCollectionScopeGap.CollectionMissing
                ? CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven
                : current.CollectionMissingValueBehavior,
            collectionNull: gap == StructuredCollectionScopeGap.CollectionNull
                ? CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven
                : current.CollectionNullValueBehavior,
            empty: gap == StructuredCollectionScopeGap.EmptyCollection
                ? CosmosRelationQueryEmptyCollectionBehavior.Unproven
                : current.EmptyCollectionBehavior);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        _ = AssertContextDiagnostic(
            result,
            RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
            expectedDiagnostic);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutReferencedChildBinding()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsCollectionScope;
        var weak = CopyCollectionScope(
            current,
            children:
            [
                .. current.ChildFields.Where(static child =>
                    child.ElementPath != Fixture.StopLocationPath)
            ]);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("no direct child mapping", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedForWrongChildValueDomain()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsCollectionScope;
        var location = current.ResolveChild(Fixture.StopLocationPath);
        var wrongLocation = new CosmosRelationQueryCollectionElementFieldBinding(
            location.ElementPath,
            location.DocumentPath,
            CosmosRelationQueryCollectionElementValueDomain.Bool,
            location.SemanticCapabilities,
            location.SemanticProfile,
            location.MissingValueBehavior,
            location.NullValueBehavior);
        var weak = CopyCollectionScope(
            current,
            children:
            [
                .. current.ChildFields.Select(child =>
                    child.ElementPath == Fixture.StopLocationPath ? wrongLocation : child)
            ]);

        var result = fixture.Compile(fixture.StorageBindingWithCollectionScope(weak));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("rather than required canonical domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedForNestedCurrentItemPath()
    {
        var result = Fixture.StructuredCollectionAny(predicate: StructuredAnyPredicate.NestedChild).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Contains("one direct current-element child field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void TargetProfile_AdvertisesOnlyDirectCurrentItemCollectionElementReads()
    {
        var capabilities = CosmosRelationQueryTargetProfile.Default.Capabilities
            .Select(static evidence => evidence.Capability)
            .ToArray();
        var structural = capabilities.OfType<StructuralRelationQueryCapability>().ToArray();

        Assert.Contains(capabilities, static capability => capability is ExpressionRelationQueryCapability expression
            && expression.Capability == ExprCapabilities.ForFunction(ExprFunctionNames.Any));
        Assert.Contains(capabilities, static capability => capability is GuaranteeRelationQueryCapability guarantee
            && guarantee.Kind == RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation);
        Assert.Contains(structural, static capability =>
            capability.Role == RelationQueryStructuralCapabilityRole.CurrentItemRead
            && capability.PathKind == RelationQueryStructuralPathKind.CollectionElement);
        Assert.DoesNotContain(structural, static capability =>
            capability.PathKind == RelationQueryStructuralPathKind.CollectionElement
            && capability.Role != RelationQueryStructuralCapabilityRole.CurrentItemRead);
        Assert.DoesNotContain(structural, static capability =>
            capability.Role == RelationQueryStructuralCapabilityRole.CurrentItemRead
            && capability.PathKind != RelationQueryStructuralPathKind.CollectionElement);
        Assert.DoesNotContain(structural, static capability =>
            capability.PathKind == RelationQueryStructuralPathKind.NestedCollectionElement);
    }

    static string FieldPathText(CosmosRelationQuerySelectedField field) => field.DocumentPath.ToString();

    static CosmosRelationQueryCollectionScopeEvidence CopyCollectionScope(
        CosmosRelationQueryCollectionScopeEvidence source,
        CosmosRelationQueryCollectionElementScope? elementScope = null,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior? collectionMissing = null,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior? collectionNull = null,
        CosmosRelationQueryEmptyCollectionBehavior? empty = null,
        ImmutableArray<CosmosRelationQueryCollectionElementFieldBinding> children = default) => new(
        source.SemanticProfile,
        elementScope ?? source.ElementScope,
        source.CorrelationGuarantee,
        collectionMissing ?? source.CollectionMissingValueBehavior,
        collectionNull ?? source.CollectionNullValueBehavior,
        source.NullElementBehavior,
        empty ?? source.EmptyCollectionBehavior,
        children.IsDefault ? source.ChildFields : children);

    static string DecisionKey(RelationQueryNativeCompilationDecisionReference decision) => string.Join(
        "|",
        decision.Requirement.Value,
        decision.Kind,
        string.Join(",", decision.CapabilityEvidence.Select(static evidence => evidence.Value)),
        string.Join(",", decision.CompositionRules.Select(static rule => rule.Value)),
        decision.Override?.Value ?? string.Empty,
        string.Join(",", decision.OperatingBoundaries.Select(static boundary => boundary.Value)),
        string.Join(",", decision.PreservedGuarantees));

    static string Diagnostics(CosmosRelationQueryCompilationResult result) => string.Join(
        Environment.NewLine,
        result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    static CosmosRelationQueryStorageBinding EmulatorBinding(
        Fixture fixture,
        Uri accountEndpoint,
        string databaseName,
        string containerName,
        ImmutableArray<FieldPath> stableUniqueOrderingPaths = default,
        ImmutableArray<FieldPath> exactOrderingPaths = default)
    {
        var placement = fixture.Placement.Bindings.Single(binding =>
            binding.Id == fixture.StorageBinding.PlacementBinding);
        return CosmosRelationQueryStorageBinding.FromSemanticPathConvention(
            new("tests/cosmos-keyset-emulator/v1"),
            placement,
            CosmosRelationQueryTargetProfile.Target,
            CosmosRelationQueryTargetProfile.ProfileId,
            accountEndpoint,
            databaseName,
            containerName,
            Fixture.IdPath,
            stableUniqueOrderingPaths: stableUniqueOrderingPaths.IsDefault
                ? [Fixture.IdPath]
                : stableUniqueOrderingPaths,
            exactOrderingPaths: exactOrderingPaths.IsDefault ? [Fixture.IdPath] : exactOrderingPaths,
            maximumInputRows: 100);
    }

    static CosmosClient CreateCosmosClient(string connectionString)
    {
        var options = new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway };
        var endpointValue = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static part => part.StartsWith("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))?
            ["AccountEndpoint=".Length..];
        if (Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback)
        {
            options.HttpClientFactory = static () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        }

        return new CosmosClient(connectionString, options);
    }

    static CosmosRelationQueryStorageBinding WithEntitySourceScope(
        CosmosRelationQueryStorageBinding binding,
        string entityDocumentKind) => new(
        binding.Id,
        binding.Source,
        binding.PlacementBinding,
        binding.Target,
        binding.TargetProfile,
        binding.AccountEndpoint,
        binding.DatabaseName,
        binding.ContainerName,
        binding.RootAlias,
        binding.IdentityPath,
        binding.Fields,
        binding.DocumentRoot,
        binding.PartitionPath,
        binding.StableUniqueOrderingPaths,
        binding.ExactOrderingPaths,
        binding.MaximumInputRows,
        binding.MissingValueEncoding,
        binding.NullValueEncoding,
        binding.Origin,
        binding.ConventionSetVersion,
        binding.ConfigurationDecisions,
        binding.CompiledPlanFingerprint,
        binding.PlacementFingerprint,
        [new(FieldPath.FromField(CosmosRelationQuerySourceReader.DocumentKindSourceSelector), entityDocumentKind)]);

    static CosmosRelationQueryArtifactExecutionRequest ExecutionRequest(
        Fixture fixture,
        CosmosRelationQueryStorageBinding binding,
        CosmosRelationQueryCompiledArtifact artifact,
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters) => new(
        fixture.PlanReference,
        artifact.Provenance.Realization,
        artifact.Provenance.Placement,
        binding.Fingerprint,
        artifact,
        maximumRows: 10,
        parameters);

    static RelationQueryNativeCompilationDiagnostic AssertContextDiagnostic(
        CosmosRelationQueryCompilationResult result,
        string code,
        string messageFragment) => Assert.Single(
        result.Diagnostics
            .Where(diagnostic => diagnostic.Code == code
                                 && diagnostic.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static diagnostic => diagnostic.Message));

    internal static RelationQueryAdapterConformanceCase CreateBoundRealizationConformanceCase() => new(
        "Cosmos",
        CosmosRelationQueryTelemetry.InstrumentationName,
        static () =>
        {
            var fixture = Fixture.Row();
            var compiler = new CosmosRelationQueryCompiler();
            var request = new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                fixture.Realization,
                fixture.Placement);
            var bound = compiler.Realize(request, fixture.StorageBinding);
            var repeated = compiler.Realize(request, fixture.StorageBinding);
            var compilation = compiler.Compile(request, fixture.StorageBinding);

            return new(
                bound,
                repeated,
                compilation.Status,
                [.. compilation.Artifacts.Select(static artifact => artifact.Provenance.BoundRealization)],
                CosmosRelationQueryExplainProjector.Project(compilation));
        },
        static () =>
        {
            var fixture = Fixture.Row();
            var statusInput = fixture.InputFor(Fixture.StatusPath);
            var incomplete = fixture.StorageBindingWithFields(
            [
                .. fixture.StorageBinding.Fields.Where(field => field.Input != statusInput)
            ]);
            var compiler = new CosmosRelationQueryCompiler();
            var request = new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                fixture.Realization,
                fixture.Placement);
            var bound = compiler.Realize(request, incomplete);
            var compilation = compiler.Compile(request, incomplete);

            return new(
                bound,
                compilation.Status,
                compilation.Artifacts.Length,
                CosmosRelationQueryExplainProjector.Project(compilation));
        });

    public enum UnsafeDistinctDomain
    {
        NullableString,
        NestedArray,
        Int64,
        Decimal
    }

    public enum StructuredAnyPredicate
    {
        CompoundEquality,
        Inequality,
        NegatedEquality,
        Disjunction,
        NestedChild,
        BoolConstant,
        Int32Constant,
        StringConstant,
        GuidConstant,
        DateConstant,
        OutOfRangeInt32Constant
    }

    public enum StructuredCollectionScopeGap
    {
        ElementScope,
        CollectionMissing,
        CollectionNull,
        EmptyCollection
    }

    internal sealed class Fixture
    {
        static readonly GraphId Graph = new("cosmos-compiler-tests/v1");
        static readonly QualifiedShapeId LoadShape = new(Graph, new ShapeId("Load"));
        static readonly QualifiedShapeId CustomerShape = new(Graph, new ShapeId("Customer"));
        static readonly QualifiedShapeId RowShape = new(Graph, new ShapeId("LoadRow"));
        static readonly QualifiedShapeId InstantRowShape = new(Graph, new ShapeId("LoadInstantRow"));
        static readonly QualifiedShapeId DateTimeRowShape = new(Graph, new ShapeId("LoadDateTimeRow"));
        static readonly QualifiedShapeId Int64RowShape = new(Graph, new ShapeId("LoadInt64Row"));
        static readonly QualifiedShapeId DecimalRowShape = new(Graph, new ShapeId("LoadDecimalRow"));
        static readonly QualifiedShapeId NullableRowShape = new(Graph, new ShapeId("LoadNullableRow"));
        static readonly QualifiedShapeId NestedRowShape = new(Graph, new ShapeId("LoadNestedRow"));
        static readonly QualifiedShapeId BytesRowShape = new(Graph, new ShapeId("LoadBytesRow"));
        static readonly QualifiedShapeId UndefinedRowShape = new(Graph, new ShapeId("LoadUndefinedRow"));
        static readonly QualifiedShapeId AggregateShape = new(Graph, new ShapeId("LoadAggregate"));
        static readonly QualifiedShapeId CountAggregateShape = new(Graph, new ShapeId("LoadCountAggregate"));
        static readonly QualifiedShapeId StringAggregateShape = new(Graph, new ShapeId("LoadStringAggregate"));
        static readonly ValueBindingId Load = new("load");
        static readonly ValueBindingId Customer = new("customer");
        static readonly ValueBindingId RowBinding = new("row");
        static readonly ValueBindingId AggregateBinding = new("aggregate");
        static readonly ValueBindingId ExpandedItemBinding = new("expanded-item");
        static readonly QueryNodeId LoadSource = new("loads");
        static readonly QueryNodeId CustomerSource = new("customers");
        public static readonly QueryNodeId Filter = new("status-filter");
        static readonly QueryNodeId Project = new("project-row");
        static readonly QueryNodeId Order = new("order-row");
        static readonly QueryNodeId Page = new("page-row");
        static readonly QueryNodeId Aggregate = new("aggregate-loads");
        static readonly QueryResultId Rows = new("rows");
        static readonly QueryResultId CustomerRows = new("customer-rows");
        static readonly QueryResultId Aggregations = new("aggregations");
        static readonly QueryParameterId StatusParameter = new("status");
        public static readonly QueryParameterId NumericParameter = new("numeric-value");
        static readonly QueryParameterId BytesParameter = new("bytes-value");
        static readonly QueryParameterId ContainsValuesParameter = new("contains-values");
        static readonly QueryParameterId LocationParameter = new("location");
        static readonly TypeId PayloadType = new("type.load.payload");

        public static readonly FieldPath IdPath = FieldPath.FromField("Id");
        static readonly FieldPath CustomerIdPath = FieldPath.FromField("CustomerId");
        public static readonly FieldPath StatusPath = FieldPath.FromField("Status");
        static readonly FieldPath AmountPath = FieldPath.FromField("Amount");
        static readonly FieldPath NotesPath = FieldPath.FromField("Notes");
        public static readonly FieldPath Int64ValuePath = FieldPath.FromField("Int64Value");
        static readonly FieldPath DecimalValuePath = FieldPath.FromField("DecimalValue");
        static readonly FieldPath TagsPath = FieldPath.FromField("Tags");
        static readonly FieldPath ItemsPath = FieldPath.FromField("Items");
        static readonly FieldPath NamePath = FieldPath.FromField("Name");
        public static readonly FieldPath StopsPath = FieldPath.FromField("Stops");
        public static readonly FieldPath StopLocationPath = FieldPath.FromField("Location");
        static readonly FieldPath StopTypePath = FieldPath.FromField("Type");
        static readonly FieldPath StopIsRequiredPath = FieldPath.FromField("IsRequired");
        static readonly FieldPath StopSequencePath = FieldPath.FromField("Sequence");
        static readonly FieldPath StopExternalIdPath = FieldPath.FromField("ExternalId");
        static readonly FieldPath StopServiceDatePath = FieldPath.FromField("ServiceDate");
        public static readonly FieldPath ValuePath = FieldPath.FromField("Value");
        static readonly FieldPath PayloadPath = FieldPath.FromField("Payload");
        static readonly FieldPath ObservedInstantPath = FieldPath.FromField("ObservedInstant");
        static readonly FieldPath ObservedDateTimePath = FieldPath.FromField("ObservedDateTime");
        public static readonly FieldPath OccurredAtPath = FieldPath.FromField("OccurredAt");
        static readonly FieldPath CountPath = FieldPath.FromField("Count");
        static readonly FieldPath TotalPath = FieldPath.FromField("Total");
        static readonly FieldPath MinimumStatusPath = FieldPath.FromField("MinimumStatus");
        static readonly FieldPath PayloadStatusPath = FieldPath.Parse("Payload.Status");
        static readonly FieldPath PayloadNotesPath = FieldPath.Parse("Payload.Notes");

        Fixture(
            CompiledRelationQueryPlan plan,
            RelationQueryRealizationReport realization,
            RelationQuerySourcePlacement placement,
            CosmosRelationQueryStorageBinding storageBinding)
        {
            Plan = plan;
            Realization = realization;
            Placement = placement;
            StorageBinding = storageBinding;
        }

        public CompiledRelationQueryPlan Plan { get; }

        public RelationQueryCompiledPlanReference PlanReference => RelationQueryCompiledPlanReference.From(Plan);

        public RelationQueryRealizationReport Realization { get; }

        public RelationQuerySourcePlacement Placement { get; }

        public CosmosRelationQueryStorageBinding StorageBinding { get; }

        public CosmosRelationQueryCompilationResult Compile(
            CosmosRelationQueryStorageBinding? storageBinding = null,
            RelationQueryBoundRealizationRequest? request = null,
            CosmosRelationQueryCompilerOptions? options = null) =>
            new CosmosRelationQueryCompiler(options).Compile(
                request ?? new RelationQueryBoundRealizationRequest(Plan, Realization, Placement),
                storageBinding ?? StorageBinding);

        public RelationQueryNativeCompilationRequest CreateNativeCompilationRequest()
        {
            var compiler = new CosmosRelationQueryCompiler();
            var bound = compiler.Realize(
                new(Plan, Realization, Placement),
                StorageBinding);
            Assert.True(
                bound.IsRealizable,
                string.Join(Environment.NewLine, bound.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            return new(Plan, bound, Placement);
        }

        public CosmosRelationQueryStorageBinding StorageBindingWithAffinity() => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.AccountEndpoint,
            StorageBinding.DatabaseName,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion,
            StorageBinding.ConfigurationDecisions,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(PlanReference),
            Placement.Fingerprint,
            StorageBinding.SourceScopeEqualities);

        public RelationQueryRealizationReport RealizeExactContributors() => Realize(
            Plan,
            overrideUnavailableRequirements: true,
            observability: RelationQueryResultObservability.ExactContributors);

        public CosmosRelationQueryStorageBinding StorageBindingWithFields(
            ImmutableArray<CosmosRelationQueryFieldBinding> fields) => new(
                StorageBinding.Id,
                StorageBinding.Source,
                StorageBinding.PlacementBinding,
                StorageBinding.Target,
                StorageBinding.TargetProfile,
                StorageBinding.AccountEndpoint,
                StorageBinding.DatabaseName,
                StorageBinding.ContainerName,
                StorageBinding.RootAlias,
                StorageBinding.IdentityPath,
                fields,
                StorageBinding.DocumentRoot,
                StorageBinding.PartitionPath,
                StorageBinding.StableUniqueOrderingPaths,
                StorageBinding.ExactOrderingPaths,
                StorageBinding.MaximumInputRows,
                StorageBinding.MissingValueEncoding,
                StorageBinding.NullValueEncoding,
                StorageBinding.Origin,
                StorageBinding.ConventionSetVersion,
                StorageBinding.ConfigurationDecisions,
                StorageBinding.CompiledPlanFingerprint,
                StorageBinding.PlacementFingerprint,
                StorageBinding.SourceScopeEqualities);

        public CosmosRelationQueryStorageBinding StorageBindingWithTarget(RelationQueryTargetId target) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            target,
            StorageBinding.TargetProfile,
            StorageBinding.AccountEndpoint,
            StorageBinding.DatabaseName,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion,
            StorageBinding.ConfigurationDecisions,
            StorageBinding.CompiledPlanFingerprint,
            StorageBinding.PlacementFingerprint,
            StorageBinding.SourceScopeEqualities);

        public CosmosRelationQueryCollectionScopeEvidence StopsCollectionScope =>
            StorageBinding.ResolveFieldBinding(InputFor(StopsPath)).CollectionScope!;

        public CosmosRelationQueryStorageBinding StorageBindingWithCollectionScope(
            CosmosRelationQueryCollectionScopeEvidence? collectionScope)
        {
            var input = InputFor(StopsPath);
            return StorageBindingWithFields(
            [
                .. StorageBinding.Fields.Select(field => field.Input == input
                    ? new CosmosRelationQueryFieldBinding(field.Input, field.DocumentPath, collectionScope)
                    : field)
            ]);
        }

        public CosmosRelationQueryStorageBinding StorageBindingWithExactIntegerDomain(
            FieldPath path,
            CosmosRelationQueryExactIntegerDomain domain)
        {
            var input = InputFor(path);
            return StorageBindingWithFields(
            [
                .. StorageBinding.Fields.Select(field => field.Input == input
                    ? new CosmosRelationQueryFieldBinding(
                        field.Input,
                        field.DocumentPath,
                        field.CollectionScope,
                        domain)
                    : field)
            ]);
        }

        public RelationQueryInputId InputFor(FieldPath path) => Plan.InputContract.Sources
            .SelectMany(static source => source.Fields)
            .Single(field => field.Input.Field.Path == path)
            .Input.Id;

        public CosmosRelationQueryStorageBinding StorageBindingWithContainer(string containerName) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.AccountEndpoint,
            StorageBinding.DatabaseName,
            containerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion,
            StorageBinding.ConfigurationDecisions,
            StorageBinding.CompiledPlanFingerprint,
            StorageBinding.PlacementFingerprint,
            StorageBinding.SourceScopeEqualities);

        public CosmosRelationQueryStorageBinding StorageBindingWithOrderingProofs(
            ImmutableArray<FieldPath> stableUniqueOrderingPaths,
            ImmutableArray<FieldPath> exactOrderingPaths) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.AccountEndpoint,
            StorageBinding.DatabaseName,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            stableUniqueOrderingPaths,
            exactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion,
            StorageBinding.ConfigurationDecisions,
            StorageBinding.CompiledPlanFingerprint,
            StorageBinding.PlacementFingerprint,
            StorageBinding.SourceScopeEqualities);

        public CosmosRelationQueryStorageBinding StorageBindingWithMaximumInputRows(
            long? maximumInputRows) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.AccountEndpoint,
            StorageBinding.DatabaseName,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            maximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion,
            StorageBinding.ConfigurationDecisions,
            StorageBinding.CompiledPlanFingerprint,
            StorageBinding.PlacementFingerprint,
            StorageBinding.SourceScopeEqualities);

        public static Fixture Row(
            int offset = 5,
            bool keyset = false,
            bool initialKeyset = false,
            int pageSize = 25,
            TypeRef? cursorType = null,
            FieldPresence cursorPresence = FieldPresence.Required,
            bool optionalPredicate = false,
            bool overrideUnavailableRequirements = false)
        {
            QueryPageDefinition page = keyset
                ? new KeysetPageDefinition(pageSize, initialKeyset ? [] : [Expr.Param("cursor")])
                : new OffsetPageDefinition(pageSize, offset);
            List<QueryParameterDefinition> parameters =
            [
                new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String))
            ];
            if (keyset && !initialKeyset)
            {
                parameters.Add(new(
                    new("cursor"),
                    cursorType ?? new ScalarTypeRef(ScalarTypeKind.String),
                    cursorPresence));
            }

            IRQueryDefinition definition = new(
                new("row-query"),
                new("RowQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(
                                Expr.Field(Load, optionalPredicate ? NotesPath : StatusPath),
                                Expr.Param(StatusParameter.Value))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ]),
                        new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, IdPath))]),
                        new PageQueryNode(Page, Order, page)
                    ],
                    parameters: [.. parameters]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: overrideUnavailableRequirements);
        }

        public static Fixture NestedNamedField(bool optionalPredicate = false)
        {
            var predicatePath = optionalPredicate ? PayloadNotesPath : PayloadStatusPath;
            IRQueryDefinition definition = new(
                new("nested-named-field-query"),
                new("NestedNamedFieldQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(Expr.Field(Load, predicatePath), Expr.Param(StatusParameter.Value))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ]),
                        new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, IdPath))]),
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                    ],
                    parameters:
                    [
                        new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture NestedNamedProjection(bool optional = false)
        {
            var rowShape = optional ? NullableRowShape : RowShape;
            var resultPath = optional ? ValuePath : StatusPath;
            var sourcePath = optional ? PayloadNotesPath : PayloadStatusPath;
            IRQueryDefinition definition = new(
                new(optional ? "optional-nested-named-projection" : "nested-named-projection"),
                new(optional ? "OptionalNestedNamedProjection" : "NestedNamedProjection"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), resultPath, Expr.Field(Load, sourcePath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture MixedDirectionKeyset()
        {
            IRQueryDefinition definition = new(
                new("mixed-keyset-row-query"),
                new("MixedKeysetRowQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(Expr.Field(Load, StatusPath), Expr.Param(StatusParameter.Value))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ]),
                        new OrderQueryNode(
                            Order,
                            Project,
                            [
                                new(Expr.Field(RowBinding, StatusPath), QuerySortDirection.Ascending),
                                new(Expr.Field(RowBinding, IdPath), QuerySortDirection.Descending)
                            ]),
                        new PageQueryNode(
                            Page,
                            Order,
                            new KeysetPageDefinition(
                                25,
                                [Expr.Param("cursor-status"), Expr.Param("cursor-id")]))
                    ],
                    parameters:
                    [
                        new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("cursor-status"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("cursor-id"), new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture IndependentBranches()
        {
            IRQueryDefinition definition = new(
                new("independent-branches-query"),
                new("IndependentBranchesQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ]),
                    new ProjectQueryNode(
                        CustomerSource,
                        LoadSource,
                        Customer,
                        CustomerShape,
                        [new(new("customer-id"), IdPath, Expr.Field(Load, CustomerIdPath))])
                ]),
                [
                    new RowsQueryResultDefinition(Rows, Project),
                    new RowsQueryResultDefinition(CustomerRows, CustomerSource)
                ]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture OrderingByStatus()
        {
            IRQueryDefinition definition = new(
                new("status-ordering-query"),
                new("StatusOrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ]),
                    new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, StatusPath))])
                ]),
                [new RowsQueryResultDefinition(Rows, Order)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture PrecisionUnsafeComparison(ScalarTypeKind numericKind)
        {
            var (sourcePath, scalarType) = PrecisionUnsafeNumeric(numericKind);
            IRQueryDefinition definition = new(
                new($"{numericKind}-comparison-query"),
                new($"{numericKind}ComparisonQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(
                                Expr.Field(Load, sourcePath),
                                Expr.Param(NumericParameter.Value))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ])
                    ],
                    parameters: [new(NumericParameter, scalarType)]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture PrecisionUnsafeOrdering(ScalarTypeKind numericKind)
        {
            var (sourcePath, scalarType) = PrecisionUnsafeNumeric(numericKind);
            var rowShape = numericKind == ScalarTypeKind.Int64 ? Int64RowShape : DecimalRowShape;
            IRQueryDefinition definition = new(
                new($"{numericKind}-ordering-query"),
                new($"{numericKind}OrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), ValuePath, Expr.Field(Load, sourcePath))
                        ]),
                    new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, ValuePath))])
                ]),
                [new RowsQueryResultDefinition(Rows, Order)]);
            var demand = RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    Rows,
                    [new RelationQueryFieldReference(rowShape, IdPath)])
            ]);
            return Create(RelationQueryDocument.FromDefinition(definition), demand: demand);
        }

        public static Fixture PrecisionUnsafeProjection(ScalarTypeKind numericKind)
        {
            var (sourcePath, _) = PrecisionUnsafeNumeric(numericKind);
            var rowShape = numericKind == ScalarTypeKind.Int64 ? Int64RowShape : DecimalRowShape;
            IRQueryDefinition definition = new(
                new($"{numericKind}-result-query"),
                new($"{numericKind}ResultQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), ValuePath, Expr.Field(Load, sourcePath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture BytesParameterProjection()
        {
            IRQueryDefinition definition = new(
                new("bytes-parameter-query"),
                new("BytesParameterQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new ProjectQueryNode(
                            Project,
                            LoadSource,
                            RowBinding,
                            BytesRowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-payload"), PayloadPath, Expr.Param(BytesParameter.Value))
                            ])
                    ],
                    parameters:
                    [
                        new(BytesParameter, new ScalarTypeRef(ScalarTypeKind.Bytes))
                    ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture Int32LiteralEquality(ObservationValue value) => Int32Equality(
            "typed-int32-literal-query",
            new LiteralExpr(new ScalarTypeRef(ScalarTypeKind.Int32), value));

        public static Fixture Int32ParameterDefaultEquality(ObservationValue defaultValue) => Int32Equality(
            "int32-parameter-default-query",
            Expr.Param(NumericParameter.Value),
            [
                new(
                    NumericParameter,
                    new ScalarTypeRef(ScalarTypeKind.Int32),
                    FieldPresence.Optional,
                    defaultValue)
            ]);

        static Fixture Int32Equality(
            string queryId,
            Expr right,
            ImmutableArray<QueryParameterDefinition> parameters = default)
        {
            IRQueryDefinition definition = new(
                new(queryId),
                new(queryId),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(Expr.Field(Load, AmountPath), right)),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ])
                    ],
                    parameters: parameters),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture UndefinedConstantProjection() => ConstantProjection(
            "undefined-constant-query",
            UndefinedRowShape,
            ValuePath,
            Expr.Const(ObservationValue.Undefined));

        public static Fixture BytesConstantProjection() => ConstantProjection(
            "bytes-constant-query",
            BytesRowShape,
            PayloadPath,
            Expr.Const(ObservationValue.FromBytes(new byte[] { 1, 2, 3 })));

        static Fixture ConstantProjection(
            string queryId,
            QualifiedShapeId rowShape,
            FieldPath target,
            Expr value)
        {
            IRQueryDefinition definition = new(
                new(queryId),
                new(queryId),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [new(new("constant-value"), target, value)])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture DistinctSelectedId(bool keyed = false, bool ordered = true)
        {
            var distinct = new QueryNodeId("distinct-row");
            ImmutableArray<Expr> keys = keyed ? [Expr.Field(RowBinding, IdPath)] : [];
            List<LogicalQueryNode> nodes =
            [
                new SourceQueryNode(LoadSource, Load, LoadShape),
                new ProjectQueryNode(
                    Project,
                    LoadSource,
                    RowBinding,
                    RowShape,
                    [
                        new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                        new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                    ]),
                new DistinctQueryNode(distinct, Project, keys)
            ];
            if (ordered)
                nodes.Add(new OrderQueryNode(Order, distinct, [new(Expr.Field(RowBinding, IdPath))]));
            IRQueryDefinition definition = new(
                new("distinct-row-query"),
                new("DistinctRowQuery"),
                new([.. nodes]),
                [new RowsQueryResultDefinition(Rows, ordered ? Order : distinct)]);
            var demand = RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    Rows,
                    [new RelationQueryFieldReference(RowShape, IdPath)])
            ]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: keyed,
                demand: demand);
        }

        public static Fixture ExpandedItemField()
        {
            var expand = new QueryNodeId("expand-items");
            var itemType = new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("Name", new ScalarTypeRef(ScalarTypeKind.String))
            ]);
            IRQueryDefinition definition = new(
                new("expanded-item-query"),
                new("ExpandedItemQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ExpandCollectionQueryNode(
                        expand,
                        LoadSource,
                        Expr.Field(Load, ItemsPath),
                        ExpandedItemBinding,
                        itemType),
                    new ProjectQueryNode(
                        Project,
                        expand,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(ExpandedItemBinding, NamePath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture UnsafeDistinct(UnsafeDistinctDomain domain)
        {
            var (rowShape, value) = domain switch
            {
                UnsafeDistinctDomain.NullableString =>
                    (NullableRowShape, Expr.Field(Load, NotesPath)),
                UnsafeDistinctDomain.NestedArray =>
                    (NestedRowShape, Expr.Field(Load, TagsPath)),
                UnsafeDistinctDomain.Int64 =>
                    (Int64RowShape, Expr.Field(Load, Int64ValuePath)),
                UnsafeDistinctDomain.Decimal =>
                    (DecimalRowShape, Expr.Field(Load, DecimalValuePath)),
                _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unsupported DISTINCT fixture domain.")
            };
            var distinct = new QueryNodeId("unsafe-distinct-row");
            IRQueryDefinition definition = new(
                new($"{domain}-distinct-query"),
                new($"{domain}DistinctQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), ValuePath, value)
                        ]),
                    new DistinctQueryNode(distinct, Project)
                ]),
                [new RowsQueryResultDefinition(Rows, distinct)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture ContainsFilter(
            ScalarTypeKind elementKind,
            bool nestedCandidate = false,
            bool optionalCandidate = false)
        {
            var (sourcePath, scalarType) = elementKind switch
            {
                ScalarTypeKind.String => (
                    optionalCandidate
                        ? PayloadNotesPath
                        : nestedCandidate
                            ? PayloadStatusPath
                            : StatusPath,
                    (TypeRef)new ScalarTypeRef(ScalarTypeKind.String)),
                ScalarTypeKind.Int64 or ScalarTypeKind.Decimal => PrecisionUnsafeNumeric(elementKind),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(elementKind),
                    elementKind,
                    "The contains fixture supports string, Int64, and Decimal values.")
            };
            IRQueryDefinition definition = new(
                new($"{elementKind}-contains-query"),
                new($"{elementKind}ContainsQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Call(
                                ExprFunctionNames.Contains,
                                Expr.Param(ContainsValuesParameter.Value),
                                Expr.Field(Load, sourcePath))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ])
                    ],
                    parameters:
                    [
                        new(ContainsValuesParameter, new ArrayTypeRef(scalarType))
                    ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture StructuredCollectionAny(
            bool includeCount = false,
            StructuredAnyPredicate predicate = StructuredAnyPredicate.CompoundEquality)
        {
            Expr elementPredicate = predicate switch
            {
                StructuredAnyPredicate.CompoundEquality => Expr.And(
                    Expr.Eq(
                        Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
                        Expr.Param(LocationParameter.Value)),
                    Expr.Eq(
                        Expr.Field($"{ExprFieldRoots.CurrentItem}.Type"),
                        Expr.Const("Pickup"))),
                StructuredAnyPredicate.Inequality => Expr.Ne(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
                    Expr.Param(LocationParameter.Value)),
                StructuredAnyPredicate.NegatedEquality => Expr.Not(Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
                    Expr.Param(LocationParameter.Value))),
                StructuredAnyPredicate.Disjunction => Expr.Or(
                    Expr.Eq(
                        Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
                        Expr.Param(LocationParameter.Value)),
                    Expr.Eq(
                        Expr.Field($"{ExprFieldRoots.CurrentItem}.Type"),
                        Expr.Const("Pickup"))),
                StructuredAnyPredicate.NestedChild => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.Address.City"),
                    Expr.Param(LocationParameter.Value)),
                StructuredAnyPredicate.BoolConstant => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.IsRequired"),
                    Expr.Const(true)),
                StructuredAnyPredicate.Int32Constant => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.Sequence"),
                    Expr.Const(7)),
                StructuredAnyPredicate.StringConstant => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
                    Expr.Const("SEA")),
                StructuredAnyPredicate.GuidConstant => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.ExternalId"),
                    Expr.Const(new Guid("01234567-89ab-cdef-0123-456789abcdef"))),
                StructuredAnyPredicate.DateConstant => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.ServiceDate"),
                    Expr.Const("2026-07-19")),
                StructuredAnyPredicate.OutOfRangeInt32Constant => Expr.Eq(
                    Expr.Field($"{ExprFieldRoots.CurrentItem}.Sequence"),
                    Expr.Const((long)int.MaxValue + 1L)),
                _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "Unsupported structured-any predicate.")
            };
            List<LogicalQueryNode> nodes =
            [
                new SourceQueryNode(LoadSource, Load, LoadShape),
                new FilterQueryNode(
                    Filter,
                    LoadSource,
                    Expr.Any(
                        Expr.Field(Load, StopsPath),
                        elementPredicate)),
                new ProjectQueryNode(
                    Project,
                    Filter,
                    RowBinding,
                    RowShape,
                    [
                        new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                        new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                    ]),
                new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, IdPath))]),
                new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
            ];
            List<QueryResultDefinition> results = [new RowsQueryResultDefinition(Rows, Page)];
            if (includeCount)
            {
                nodes.Add(new AggregateQueryNode(
                    Aggregate,
                    Filter,
                    AggregateBinding,
                    CountAggregateShape,
                    aggregates:
                    [
                        new(new("count-loads"), CountPath, AggregateOperator.Count)
                    ]));
                results.Add(new AggregationQueryResultDefinition(Aggregations, Aggregate));
            }

            IRQueryDefinition definition = new(
                new("structured-collection-any-query"),
                new("StructuredCollectionAnyQuery"),
                new(
                    nodes: [.. nodes],
                    parameters:
                    [
                        new(LocationParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [.. results]);
            var fixture = Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: predicate == StructuredAnyPredicate.NestedChild);
            CosmosRelationQueryCollectionScopeEvidence scope = new(
                "tests/cosmos-json-array/v1",
                CosmosRelationQueryCollectionElementScope.JsonArrayElement,
                CosmosRelationQueryCollectionCorrelationGuarantee.SameArrayElement,
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                CosmosRelationQueryEmptyCollectionBehavior.NoElements,
                childFields:
                [
                    new CosmosRelationQueryCollectionElementFieldBinding(
                        StopLocationPath,
                        StopLocationPath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
                        "tests/cosmos-json-string/v1",
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion),
                    new CosmosRelationQueryCollectionElementFieldBinding(
                        StopTypePath,
                        StopTypePath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
                        "tests/cosmos-json-string/v1",
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion),
                    new CosmosRelationQueryCollectionElementFieldBinding(
                        StopIsRequiredPath,
                        StopIsRequiredPath,
                        CosmosRelationQueryCollectionElementValueDomain.Bool,
                        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
                        "tests/cosmos-json-bool/v1",
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion),
                    new CosmosRelationQueryCollectionElementFieldBinding(
                        StopSequencePath,
                        StopSequencePath,
                        CosmosRelationQueryCollectionElementValueDomain.Int32,
                        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
                        "tests/cosmos-json-int32/v1",
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion),
                    new CosmosRelationQueryCollectionElementFieldBinding(
                        StopExternalIdPath,
                        StopExternalIdPath,
                        CosmosRelationQueryCollectionElementValueDomain.Guid,
                        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
                        "tests/cosmos-json-guid/v1",
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion),
                    new CosmosRelationQueryCollectionElementFieldBinding(
                        StopServiceDatePath,
                        StopServiceDatePath,
                        CosmosRelationQueryCollectionElementValueDomain.Date,
                        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
                        "tests/cosmos-json-date/v1",
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
                        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion)
                ]);
            return new(
                fixture.Plan,
                fixture.Realization,
                fixture.Placement,
                fixture.StorageBindingWithCollectionScope(scope));
        }

        static (FieldPath Path, ScalarTypeRef Type) PrecisionUnsafeNumeric(ScalarTypeKind numericKind) =>
            numericKind switch
            {
                ScalarTypeKind.Int64 => (Int64ValuePath, new(ScalarTypeKind.Int64)),
                ScalarTypeKind.Decimal => (DecimalValuePath, new(ScalarTypeKind.Decimal)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(numericKind),
                    numericKind,
                    "The precision-unsafe fixture supports Int64 and Decimal values only.")
            };

        public static Fixture TemporalOrdering(ScalarTypeKind temporalKind)
        {
            var (sourcePath, rowShape) = temporalKind switch
            {
                ScalarTypeKind.Instant => (ObservedInstantPath, InstantRowShape),
                ScalarTypeKind.DateTime => (ObservedDateTimePath, DateTimeRowShape),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(temporalKind),
                    temporalKind,
                    "The fixture supports instant and date-time ordering only.")
            };
            IRQueryDefinition definition = new(
                new($"{temporalKind}-ordering-query"),
                new($"{temporalKind}OrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-occurred-at"), OccurredAtPath, Expr.Field(Load, sourcePath))
                        ]),
                    new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, OccurredAtPath))])
                ]),
                [new RowsQueryResultDefinition(Rows, Order)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture TemporalEquality(ScalarTypeKind temporalKind) =>
            TemporalComparison(temporalKind, BinaryOperator.Eq);

        public static Fixture TemporalComparison(
            ScalarTypeKind temporalKind,
            BinaryOperator comparison)
        {
            var sourcePath = TemporalSourcePath(temporalKind);
            QueryParameterId parameter = new("temporal-value");
            var predicate = comparison switch
            {
                BinaryOperator.Eq => Expr.Eq(Expr.Field(Load, sourcePath), Expr.Param(parameter.Value)),
                BinaryOperator.Ge => Expr.Ge(Expr.Field(Load, sourcePath), Expr.Param(parameter.Value)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(comparison),
                    comparison,
                    "The fixture supports equality and greater-than-or-equal temporal comparison only.")
            };
            IRQueryDefinition definition = new(
                new($"{temporalKind}-{comparison}-query"),
                new($"{temporalKind}{comparison}Query"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            predicate),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ])
                    ],
                    parameters: [new(parameter, new ScalarTypeRef(temporalKind))]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture TemporalKeyset(ScalarTypeKind temporalKind)
        {
            var sourcePath = TemporalSourcePath(temporalKind);
            var rowShape = temporalKind == ScalarTypeKind.Instant ? InstantRowShape : DateTimeRowShape;
            QueryParameterId temporalCursor = new("temporal-cursor");
            QueryParameterId idCursor = new("id-cursor");
            IRQueryDefinition definition = new(
                new($"{temporalKind}-keyset-query"),
                new($"{temporalKind}KeysetQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new ProjectQueryNode(
                            Project,
                            LoadSource,
                            RowBinding,
                            rowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-occurred-at"), OccurredAtPath, Expr.Field(Load, sourcePath))
                            ]),
                        new OrderQueryNode(
                            Order,
                            Project,
                            [
                                new(Expr.Field(RowBinding, OccurredAtPath), QuerySortDirection.Ascending),
                                new(Expr.Field(RowBinding, IdPath), QuerySortDirection.Ascending)
                            ]),
                        new PageQueryNode(
                            Page,
                            Order,
                            new KeysetPageDefinition(
                                25,
                                [Expr.Param(temporalCursor.Value), Expr.Param(idCursor.Value)]))
                    ],
                    parameters:
                    [
                        new(temporalCursor, new ScalarTypeRef(temporalKind)),
                        new(idCursor, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static FieldPath TemporalSourcePath(ScalarTypeKind temporalKind) => temporalKind switch
        {
            ScalarTypeKind.Instant => ObservedInstantPath,
            ScalarTypeKind.DateTime => ObservedDateTimePath,
            _ => throw new ArgumentOutOfRangeException(
                nameof(temporalKind),
                temporalKind,
                "The fixture supports instant and date-time semantics only.")
        };

        public static Fixture Aggregation(AggregateOperator operation = AggregateOperator.Min)
        {
            IRQueryDefinition definition = new(
                new("aggregate-query"),
                new("AggregateQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new AggregateQueryNode(
                        Aggregate,
                        LoadSource,
                        AggregateBinding,
                        AggregateShape,
                        groupings:
                        [
                            new(new("group-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ],
                        aggregates:
                        [
                            new(new("count-loads"), CountPath, AggregateOperator.Count),
                            new(new("numeric-amount"), TotalPath, operation, Expr.Field(Load, AmountPath))
                        ])
                ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: operation == AggregateOperator.Sum);
        }

        public static Fixture UngroupedRowCount()
        {
            IRQueryDefinition definition = new(
                new("row-count-query"),
                new("RowCountQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new AggregateQueryNode(
                        Aggregate,
                        LoadSource,
                        AggregateBinding,
                        CountAggregateShape,
                        aggregates:
                        [
                            new(new("count-loads"), CountPath, AggregateOperator.Count)
                        ])
                ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture NonNumericAggregation(AggregateOperator operation)
        {
            IRQueryDefinition definition = new(
                new($"string-{operation}-query"),
                new($"String{operation}Query"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new AggregateQueryNode(
                        Aggregate,
                        LoadSource,
                        AggregateBinding,
                        StringAggregateShape,
                        groupings:
                        [
                            new(new("group-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ],
                        aggregates:
                        [
                            new(
                                new("string-extreme"),
                                MinimumStatusPath,
                                operation,
                                Expr.Field(Load, StatusPath))
                        ])
                ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture Relation()
        {
            IRRelationDefinition definition = new(
                new("load-relation"),
                new("LoadRelation"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ])
                ]),
                Load,
                new(Project, RowShape, RelationOutputMode.OnePerRoot, Expr.Field(RowBinding, IdPath)));
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture CrossSourceJoin()
        {
            IRQueryDefinition definition = new(
                new("cross-source-query"),
                new("CrossSourceQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new SourceQueryNode(CustomerSource, Customer, CustomerShape),
                    new JoinQueryNode(
                        new("join-customer"),
                        LoadSource,
                        CustomerSource,
                        JoinKind.Inner,
                        Expr.Eq(Expr.Field(Load, CustomerIdPath), Expr.Field(Customer, IdPath))),
                    new ProjectQueryNode(
                        Project,
                        new("join-customer"),
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture IndependentSources()
        {
            IRQueryDefinition definition = new(
                new("independent-source-query"),
                new("IndependentSourceQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new SourceQueryNode(CustomerSource, Customer, CustomerShape)
                ]),
                [
                    new RowsQueryResultDefinition(Rows, LoadSource),
                    new RowsQueryResultDefinition(CustomerRows, CustomerSource)
                ]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        static Fixture Create(
            RelationQueryDocument document,
            bool overrideUnavailableRequirements = false,
            RelationQueryCompilationDemand? demand = null)
        {
            var compilation = RelationQueryStaticCompiler.Compile(new(
                document,
                [ShapeDocument()],
                demand: demand));
            Assert.True(
                compilation.IsSuccessful,
                string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
            var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
            var realization = Realize(plan, overrideUnavailableRequirements);
            Assert.True(realization.IsRealizable, string.Join(
                Environment.NewLine,
                realization.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var placement = CreatePlacement(plan);
            var sourcePlacement = placement.Bindings.First(static binding =>
                binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet);
            var storage = CosmosRelationQueryStorageBinding.FromSemanticPathConvention(
                new("tests/cosmos-binding/v1"),
                sourcePlacement,
                CosmosRelationQueryTargetProfile.Target,
                CosmosRelationQueryTargetProfile.ProfileId,
                new Uri("https://tests.invalid"),
                "operations",
                "loads",
                IdPath,
                stableUniqueOrderingPaths: [IdPath],
                exactOrderingPaths: [IdPath],
                maximumInputRows: 10_000);
            var fixture = new Fixture(plan, realization, placement, storage);
            return new(plan, realization, placement, fixture.StorageBindingWithAffinity());
        }

        static RelationQueryRealizationReport Realize(
            CompiledRelationQueryPlan plan,
            bool overrideUnavailableRequirements,
            RelationQueryResultObservability? observability = null)
        {
            var effectiveObservability = observability ?? RelationQueryResultObservability.NotRequested;
            var baseline = RelationQueryRealizationCompiler.Compile(
                plan,
                CosmosRelationQueryTargetProfile.Default,
                CosmosRelationQueryTargetProfile.Policy,
                effectiveObservability);
            if (!overrideUnavailableRequirements || baseline.IsRealizable)
            {
                return baseline;
            }

            var requirements = baseline.Requirements.ToDictionary(static requirement => requirement.Id);
            ImmutableArray<RelationQueryRealizationOverride> overrides =
            [
                .. baseline.Decisions
                    .OfType<UnavailableRelationQueryRealizationDecision>()
                    .Select((decision, index) => new RelationQueryRealizationOverride(
                        new($"tests/cosmos-unsupported-override/{index:D4}"),
                        decision.Requirement,
                        requirements[decision.Requirement].Capability,
                        preservedGuarantees: requirements[decision.Requirement].RequiredGuarantees,
                        justification: "Exercise the Cosmos compiler's fail-closed unsupported diagnostic."))
            ];
            var policy = new RelationQueryRealizationPolicy(
                new("tests/cosmos-unsupported-policy/v1"),
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated,
                overrides: overrides);
            return RelationQueryRealizationCompiler.Compile(
                plan,
                CosmosRelationQueryTargetProfile.Default,
                policy,
                effectiveObservability);
        }

        static RelationQuerySourcePlacement CreatePlacement(CompiledRelationQueryPlan plan)
        {
            ImmutableArray<RelationQuerySourcePlacementBinding> bindings =
            [
                .. plan.InputContract.Sources.Select(source => new RelationQuerySourcePlacementBinding(
                    new($"placement/{source.Binding.Value}"),
                    source.Input.Id,
                    source.Node,
                    source.Binding,
                    source.Shape,
                    new($"source/{source.Binding.Value}"),
                    RelationQuerySourcePlacementBindingKind.SourceSet,
                    RelationQuerySourceAcquisitionKind.BoundedEnumeration,
                    RelationQuerySourcePlacementOrigin.Explicit,
                    new(source.Shape, "Id"),
                    [
                        .. source.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                            field.Input.Id,
                            field.Input.Field.Path,
                            field.Input.Field.Path.ToString()))
                    ]))
            ];
            ImmutableArray<RelationQuerySourceInstance> sources =
            [
                .. bindings.Select(static binding => binding.Source)
                    .Distinct()
                    .Select(source => new RelationQuerySourceInstance(
                        source,
                        new("tests/cosmos"),
                        CosmosRelationQueryTargetProfile.Default,
                        new(100, 10_000, 100, 4)))
            ];
            return new(
                RelationQuerySourcePlacement.CurrentSchemaVersion,
                RelationQueryCompiledPlanReference.From(plan),
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                sources,
                bindings);
        }

        static ShapeGraphDocument ShapeDocument()
        {
            var stringType = new ScalarTypeRef(ScalarTypeKind.String);
            var itemType = new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("Name", stringType)
            ]);
            var stopAddressType = new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("City", stringType)
            ]);
            var stopType = new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("Location", stringType),
                new ObjectFieldTypeDef("Type", stringType),
                new ObjectFieldTypeDef("IsRequired", new ScalarTypeRef(ScalarTypeKind.Bool)),
                new ObjectFieldTypeDef("Sequence", new ScalarTypeRef(ScalarTypeKind.Int32)),
                new ObjectFieldTypeDef("ExternalId", new ScalarTypeRef(ScalarTypeKind.Guid)),
                new ObjectFieldTypeDef("ServiceDate", new ScalarTypeRef(ScalarTypeKind.Date)),
                new ObjectFieldTypeDef("Address", stopAddressType)
            ]);
            var payloadType = new TypeDefinition.Structural(
                PayloadType,
                [
                    new(new("Status"), stringType),
                    new(
                        new("Notes"),
                        stringType,
                        presence: FieldPresence.Optional,
                        nullability: FieldNullability.Nullable)
                ]);
            var load = new Shape(
                LoadShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("CustomerId"), stringType),
                    new(new("Status"), stringType),
                    new(new("Amount"), new ScalarTypeRef(ScalarTypeKind.Int32)),
                    new(new("Int64Value"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                    new(new("DecimalValue"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
                    new(new("Tags"), new ArrayTypeRef(stringType)),
                    new(new("Items"), new ArrayTypeRef(itemType)),
                    new(new("Stops"), new ArrayTypeRef(stopType)),
                    new(new("ObservedInstant"), new ScalarTypeRef(ScalarTypeKind.Instant)),
                    new(new("ObservedDateTime"), new ScalarTypeRef(ScalarTypeKind.DateTime)),
                    new(new("Payload"), new NamedTypeRef(PayloadType)),
                    new(
                        new("Notes"),
                        stringType,
                        presence: FieldPresence.Optional,
                        nullability: FieldNullability.Nullable)
                ],
                role: ShapeRoles.Entity);
            var customer = new Shape(
                CustomerShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity)
                ],
                role: ShapeRoles.Entity);
            var row = new Shape(
                RowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Status"), stringType)
                ],
                role: ShapeRoles.Projection);
            var instantRow = new Shape(
                InstantRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("OccurredAt"), new ScalarTypeRef(ScalarTypeKind.Instant))
                ],
                role: ShapeRoles.Projection);
            var dateTimeRow = new Shape(
                DateTimeRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("OccurredAt"), new ScalarTypeRef(ScalarTypeKind.DateTime))
                ],
                role: ShapeRoles.Projection);
            var int64Row = new Shape(
                Int64RowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Int64))
                ],
                role: ShapeRoles.Projection);
            var decimalRow = new Shape(
                DecimalRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Decimal))
                ],
                role: ShapeRoles.Projection);
            var nullableRow = new Shape(
                NullableRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(
                        new("Value"),
                        stringType,
                        presence: FieldPresence.Optional,
                        nullability: FieldNullability.Nullable)
                ],
                role: ShapeRoles.Projection);
            var nestedRow = new Shape(
                NestedRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Value"), new ArrayTypeRef(stringType))
                ],
                role: ShapeRoles.Projection);
            var bytesRow = new Shape(
                BytesRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Payload"), new ScalarTypeRef(ScalarTypeKind.Bytes))
                ],
                role: ShapeRoles.Projection);
            var undefinedRow = new Shape(
                UndefinedRowShape.ShapeId,
                [
                    new(new("Value"), stringType, presence: FieldPresence.Optional)
                ],
                role: ShapeRoles.Projection);
            var aggregate = new Shape(
                AggregateShape.ShapeId,
                [
                    new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                    new(new("Status"), stringType),
                    new(new("Total"), new ScalarTypeRef(ScalarTypeKind.Int32))
                ],
                role: ShapeRoles.Projection);
            var countAggregate = new Shape(
                CountAggregateShape.ShapeId,
                [
                    new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64))
                ],
                role: ShapeRoles.Projection);
            var stringAggregate = new Shape(
                StringAggregateShape.ShapeId,
                [
                    new(new("Status"), stringType),
                    new(new("MinimumStatus"), stringType)
                ],
                role: ShapeRoles.Projection);
            return ShapeGraphDocument.FromGraph(new(
                Graph,
                [
                    load,
                    customer,
                    row,
                    instantRow,
                    dateTimeRow,
                    int64Row,
                    decimalRow,
                    nullableRow,
                    nestedRow,
                    bytesRow,
                    undefinedRow,
                    aggregate,
                    countAggregate,
                    stringAggregate
                ],
                namedTypes: [payloadType]));
        }
    }
}
