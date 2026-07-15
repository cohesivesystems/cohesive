using System.Collections.Immutable;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryPhysicalExecutorTests
{
    [Fact]
    public async Task Physical_execution_result_requires_the_exact_interpreted_evidence_snapshot()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(compilation);
        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/result-evidence-snapshot"));
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var interpretation = Assert.IsType<RelationQueryExecutionResult>(result.Interpretation);
        var equivalentEvidence = new RelationQueryRuntimeEvidence(
            evidence.Evaluation,
            compilation.Plan,
            evidence.Completeness,
            evidence.Sources,
            evidence.Fields,
            evidence.Traversals,
            evidence.Parameters,
            evidence.Capabilities,
            evidence.ConversionFailures);

        Assert.Same(evidence, interpretation.Evidence);
        var exception = Assert.Throws<ArgumentException>(() => new RelationQueryPhysicalExecutionResult(
            result.Status,
            equivalentEvidence,
            interpretation,
            result.SourceReads,
            result.Diagnostics));
        Assert.Contains("exact snapshot", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_FederatedQueryBatchesExactReadsAndMatchesCanonicalInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/federated-query-execution"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Evidence);
        Assert.NotNull(result.Interpretation);
        Assert.Single(readers.Loads.Requests);
        Assert.IsType<RelationQueryBoundedEnumeration>(readers.Loads.Requests[0].Constraint);
        AssertRequestFields(
            readers.Loads.Requests,
            FederatedLoadRelationFixture.LoadCustomerIdPath,
            FederatedLoadRelationFixture.LoadEquipmentIdPath,
            FederatedLoadRelationFixture.LoadIdPath);
        AssertRequestFields(readers.Customers.Requests, FederatedLoadRelationFixture.CustomerNamePath);
        AssertRequestFields(readers.Equipment.Requests, FederatedLoadRelationFixture.EquipmentNumberPath);
        AssertIdentityBatches(
            readers.Customers.Requests,
            batchSize: 2,
            "customer-1",
            "customer-2",
            "customer-3",
            "customer-4");
        AssertIdentityBatches(
            readers.Equipment.Requests,
            batchSize: 2,
            "equipment-1",
            "equipment-2",
            "equipment-3",
            "equipment-4");
        Assert.Equal(5, result.SourceReads.Length);

        var direct = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            result.Evidence,
            RelationRequirementGapPolicy.Conventional));
        AssertEquivalent(result.Interpretation, direct);
        AssertProjectedRows(result.Interpretation);

        var customerTraversal = compilation.Plan.InputContract.Traversals.Single(
            traversal => traversal.Input.Traversal == FederatedLoadRelationFixture.CustomerTraversalNodeId);
        var sharedCustomerOccurrences = result.Evidence.Traversals
            .Where(traversal => traversal.Input == customerTraversal.Input.Id)
            .SelectMany(static traversal => traversal.Results.Select(result => (traversal.From, Result: result)))
            .Where(static item => item.Result.ObservationIdentity == "customer-1")
            .ToArray();
        Assert.Equal(2, sharedCustomerOccurrences.Length);
        Assert.Equal(2, sharedCustomerOccurrences.Select(static item => item.From).Distinct().Count());
        Assert.Equal(2, sharedCustomerOccurrences.Select(static item => item.Result.Id).Distinct().Count());
        Assert.All(sharedCustomerOccurrences, static item =>
            Assert.Equal(FederatedLoadRelationFixture.CustomerBinding, item.Result.Binding));

        var sharedOwners = sharedCustomerOccurrences.Select(static item => item.Result.Id).ToHashSet();
        Assert.Equal(
            2,
            result.Evidence.Fields.Count(field => sharedOwners.Contains(field.Owner)
                && field.State == RelationQueryFieldEvidenceState.Value
                && field.Value == ObservationValue.FromString("Customer One")));
    }

    [Fact]
    public async Task ExecuteAsync_AggregationMatchesCanonicalInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.AggregationDocument,
            maximumBatchSize: 2);
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            LoadRows);

        var result = await new RelationQueryPhysicalExecutor([loadReader]).ExecuteAsync(
            Request(compilation, "tests/federated-aggregation"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Single(loadReader.Requests);
        Assert.Empty(loadReader.Requests[0].Fields);
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var interpretation = Assert.IsType<RelationQueryExecutionResult>(result.Interpretation);
        var direct = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            evidence,
            RelationRequirementGapPolicy.Conventional));
        AssertEquivalent(interpretation, direct);
        var aggregation = Assert.Single(interpretation.QueryResults);
        Assert.Equal(RelationQueryExecutionResultKind.Aggregation, aggregation.Kind);
        Assert.Equal(
            5L,
            Assert.Single(aggregation.Rows).Value
                .GetProperty(FederatedLoadRelationFixture.AggregateLoadCountFieldName)
                .Int64);
    }

    [Fact]
    public async Task ExecuteAsync_RequirementGapPolicyMatchesCanonicalInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(
            compilation,
            customerResultFactory: request =>
            {
                var lookup = Assert.IsType<RelationQueryIdentityBatchLookup>(request.Constraint);
                return new(
                    RelationQuerySourceReadState.Complete,
                    [
                        .. lookup.Identities.Select(identity =>
                        {
                            var row = CustomerRows.Single(candidate => candidate.Identity == identity);
                            return new RelationQuerySourceReadObservation(
                                row.Identity,
                                request.Shape,
                                [
                                    .. request.Fields.Select(field =>
                                        field.SemanticPath == FederatedLoadRelationFixture.CustomerNamePath
                                            ? new RelationQuerySourceReadFieldResult(
                                                field,
                                                RelationQuerySourceReadFieldState.Missing,
                                                evidenceReference: "tests/customer-name-missing")
                                            : row.Fields[field.SemanticPath].ToResult(field))
                                ]);
                        })
                    ],
                    "tests/customer-name-missing");
            });
        var fallback = ObservationValue.FromString("Unknown customer");
        var policy = new RelationRequirementGapPolicy(
            new("tests/physical-default-customer-name/v1"),
            RelationRequirementGapPolicySource.Explicit,
            (_, impact) => new(
                impact.Output.Field?.Path == FederatedLoadRelationFixture.SearchCustomerNamePath
                    ? RelationRequirementGapDisposition.UseDefault(fallback)
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(
                compilation,
                "tests/physical-gap-policy",
                requirementGapPolicy: policy));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var interpretation = Assert.IsType<RelationQueryExecutionResult>(result.Interpretation);
        var direct = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            evidence,
            policy));
        AssertEquivalent(interpretation, direct);
        Assert.All(Assert.Single(interpretation.QueryResults).Rows, row => Assert.Equal(
            fallback,
            row.Value.GetProperty(FederatedLoadRelationFixture.SearchCustomerNameFieldName)));
    }

    [Fact]
    public async Task ExecuteAsync_IdOnlySuppliedRelationPerformsNoRelatedOrExternalReads()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    FederatedLoadRelationFixture.SearchIdPath)
            ]),
            maximumBatchSize: 2);
        var source = Assert.Single(compilation.Plan.InputContract.Sources);
        var field = Assert.Single(source.Fields);
        var placement = compilation.Placement.Bindings.Single(binding => binding.Input == source.Input.Id);
        var fieldBinding = Assert.Single(placement.Fields);
        var selection = new RelationQuerySourceReadField(
            field.Input.Id,
            field.Input.Field.Path,
            fieldBinding.SourceSelector,
            RelationQuerySourceReadFieldPurpose.SemanticInput);
        var supplied = new RelationQuerySuppliedSourceInput(
            source.Input.Id,
            RelationQueryEvidenceCompleteness.Complete,
            [
                new(
                    "load-1",
                    FederatedLoadRelationFixture.LoadShapeId,
                    [
                        new(
                            selection,
                            RelationQuerySourceReadFieldState.Value,
                            ObservationValue.FromString("load-1"))
                    ])
            ],
            "tests/supplied-load");
        var unusedReaders = CreateReaders(FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2));

        var result = await new RelationQueryPhysicalExecutor(unusedReaders.All).ExecuteAsync(new(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new("tests/id-only-supplied-relation"),
            suppliedSources: [supplied],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.SourceReads);
        Assert.Empty(result.Diagnostics);
        AssertNoIo(unusedReaders);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Interpretation?.Relation);
        var row = Assert.Single(relation.Rows);
        Assert.Equal("load-1", row.Value.GetProperty(FederatedLoadRelationFixture.SearchIdFieldName).String);
        Assert.Empty(compilation.Plan.InputContract.Traversals);
    }

    [Fact]
    public async Task ExecuteAsync_InverseTraversalUsesPredicateBatchesAndReturnsManyRowsPerOwner()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            CreateInverseManyDocument(),
            maximumBatchSize: 2);
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            LoadRows);
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customers.Id, customers.ExecutionDomain, customers.TargetProfile),
            CustomerRows);

        var result = await new RelationQueryPhysicalExecutor([customerReader, loadReader]).ExecuteAsync(
            Request(compilation, "tests/inverse-many"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Single(customerReader.Requests);
        Assert.Equal(2, loadReader.Requests.Length);
        var constraints = loadReader.Requests
            .Select(static request => Assert.IsType<RelationQueryRelationshipKeyBatchLookup>(request.Constraint))
            .ToArray();
        Assert.All(constraints, static constraint => Assert.InRange(constraint.Keys.Length, 1, 2));
        Assert.Equal(
            ["customer-1", "customer-2", "customer-3", "customer-4"],
            constraints.SelectMany(static constraint => constraint.Keys)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(loadReader.Requests, static request =>
        {
            var semantic = Assert.Single(
                request.Fields,
                field => field.SemanticPath == FederatedLoadRelationFixture.LoadIdPath);
            var correlation = Assert.Single(
                request.Fields,
                field => field.SemanticPath == FederatedLoadRelationFixture.LoadCustomerIdPath
                    && field.Purpose == RelationQuerySourceReadFieldPurpose.Correlation);
            var semanticReference = Assert.Single(
                request.Fields,
                field => field.SemanticPath == FederatedLoadRelationFixture.LoadCustomerIdPath
                    && field.Purpose == RelationQuerySourceReadFieldPurpose.SemanticInput);
            Assert.Equal(RelationQuerySourceReadFieldPurpose.SemanticInput, semantic.Purpose);
            Assert.NotNull(semanticReference.Input);
            Assert.Null(correlation.Input);
        });

        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var inverse = Assert.Single(compilation.Plan.InputContract.Traversals);
        var source = Assert.Single(evidence.Sources);
        var identities = source.Occurrences.ToDictionary(
            static occurrence => occurrence.Id,
            static occurrence => occurrence.ObservationIdentity!);
        var counts = evidence.Traversals
            .Where(traversal => traversal.Input == inverse.Input.Id)
            .ToDictionary(
                traversal => identities[traversal.From],
                static traversal => traversal.Results.Length,
                StringComparer.Ordinal);
        Assert.Equal(2, counts["customer-1"]);
        Assert.Equal(1, counts["customer-2"]);
        Assert.Equal(1, counts["customer-3"]);
        Assert.Equal(1, counts["customer-4"]);
        var interpretation = Assert.IsType<RelationQueryExecutionResult>(result.Interpretation);
        Assert.Equal(5, Assert.Single(interpretation.QueryResults).Rows.Length);
    }

    [Fact]
    public async Task ExecuteAsync_InverseSemanticAndCorrelationReferencesMustAgree()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            CreateInverseManyDocument(),
            maximumBatchSize: 2);
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            LoadRows,
            request =>
            {
                var lookup = Assert.IsType<RelationQueryRelationshipKeyBatchLookup>(request.Constraint);
                var key = lookup.Keys[0];
                var row = LoadRows.First(candidate =>
                    candidate.Fields[FederatedLoadRelationFixture.LoadCustomerIdPath].Value?.String == key);
                var conflictingKey = string.Equals(key, "customer-4", StringComparison.Ordinal)
                    ? "customer-1"
                    : "customer-4";
                return new(
                    RelationQuerySourceReadState.Complete,
                    [
                        new(
                            row.Identity,
                            request.Shape,
                            [
                                .. request.Fields.Select(field =>
                                    field.SemanticPath == FederatedLoadRelationFixture.LoadCustomerIdPath
                                    && field.Purpose == RelationQuerySourceReadFieldPurpose.SemanticInput
                                        ? new RelationQuerySourceReadFieldResult(
                                            field,
                                            RelationQuerySourceReadFieldState.Value,
                                            ObservationValue.FromString(conflictingKey))
                                        : row.Fields[field.SemanticPath].ToResult(field))
                            ])
                    ],
                    "tests/conflicting-inverse-reference");
            });
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customers.Id, customers.ExecutionDomain, customers.TargetProfile),
            CustomerRows);

        var result = await new RelationQueryPhysicalExecutor([customerReader, loadReader]).ExecuteAsync(
            Request(compilation, "tests/conflicting-inverse-reference"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceResultInvalid
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.LoadsSource
            && diagnostic.Message.Contains("conflicting semantic and correlation", StringComparison.Ordinal));
        Assert.Single(customerReader.Requests);
        Assert.Single(loadReader.Requests);
    }

    [Theory]
    [InlineData(RelationQuerySourceReadState.NotFound, RelationQueryTraversalEvidenceState.Completed, RelationQueryEvidenceCompleteness.Complete)]
    [InlineData(RelationQuerySourceReadState.Partial, RelationQueryTraversalEvidenceState.Completed, RelationQueryEvidenceCompleteness.Partial)]
    [InlineData(RelationQuerySourceReadState.Failed, RelationQueryTraversalEvidenceState.Failed, RelationQueryEvidenceCompleteness.Partial)]
    [InlineData(RelationQuerySourceReadState.Inconclusive, RelationQueryTraversalEvidenceState.Inconclusive, RelationQueryEvidenceCompleteness.Partial)]
    public async Task ExecuteAsync_InverseLookupOutcomeRemainsAttributablePerOwner(
        RelationQuerySourceReadState readState,
        RelationQueryTraversalEvidenceState traversalState,
        RelationQueryEvidenceCompleteness completeness)
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            CreateInverseManyDocument(),
            maximumBatchSize: 2);
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            LoadRows,
            _ => new(readState, evidenceReference: $"tests/inverse-{readState}"));
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customers.Id, customers.ExecutionDomain, customers.TargetProfile),
            CustomerRows);

        var result = await new RelationQueryPhysicalExecutor([customerReader, loadReader]).ExecuteAsync(
            Request(compilation, $"tests/inverse-{readState}"));

        var runtime = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        Assert.NotNull(result.Interpretation);
        Assert.Empty(result.Diagnostics);
        var inverse = Assert.Single(compilation.Plan.InputContract.Traversals);
        var evidence = runtime.Traversals
            .Where(traversal => traversal.Input == inverse.Input.Id)
            .ToArray();
        Assert.Equal(4, evidence.Length);
        Assert.All(evidence, item => Assert.Equal(traversalState, item.State));
        Assert.All(evidence, item => Assert.Equal(completeness, item.Completeness));
        Assert.All(loadReader.Requests, request => Assert.IsType<RelationQueryRelationshipKeyBatchLookup>(request.Constraint));
        Assert.All(
            result.SourceReads.Where(trace => trace.Source == FederatedLoadPhysicalExecutionFixture.LoadsSource),
            trace => Assert.Equal(readState, trace.State));
    }

    [Fact]
    public async Task ExecuteAsync_ReaderProfileMismatchFailsBeforeAnyIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(compilation, mismatchCustomerProfile: true);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/reader-profile-mismatch"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceReaderMismatch
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_MissingReaderFailsBeforeAnyIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(
            [readers.Loads, readers.Customers]).ExecuteAsync(
            Request(compilation, "tests/missing-reader"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceReaderMissing
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_StaleRealizationFailsBeforeAnyIo()
    {
        var query = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var relation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(query);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(new(
            query.Plan,
            query.PhysicalPlan,
            relation.Realization,
            new("tests/stale-realization"),
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(query.Plan)));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.PlanMismatch);
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedSemanticPlanFailsBeforeAnyIo()
    {
        var query = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var relation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(query);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(new(
            relation.Plan,
            query.PhysicalPlan,
            query.Realization,
            new("tests/mismatched-semantic-plan"),
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(relation.Plan)));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.PlanMismatch);
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedSuppliedRootFailsBeforeAnyExternalIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument,
            maximumBatchSize: 2);
        var root = Assert.Single(compilation.Plan.InputContract.Sources);
        var supplied = new RelationQuerySuppliedSourceInput(
            root.Input.Id,
            RelationQueryEvidenceCompleteness.Complete,
            [new("load-1", FederatedLoadRelationFixture.LoadShapeId, fields: [])],
            "tests/malformed-supplied-root");
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(new(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new("tests/malformed-supplied-root"),
            suppliedSources: [supplied],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SuppliedInputInvalid
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.LoadsSource);
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_AlteredProviderProjectionFailsClosedBeforeInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(
            compilation,
            customerResultFactory: request => new(
                RelationQuerySourceReadState.Complete,
                [new(
                    "customer-1",
                    FederatedLoadRelationFixture.CustomerShapeId,
                    fields: [])],
                $"tests/altered/{request.Stage.Value}"));

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/altered-provider-projection"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceResultInvalid
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        Assert.Single(readers.Loads.Requests);
        Assert.Single(readers.Customers.Requests);
        Assert.Empty(readers.Equipment.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_NullProviderResultFailsClosedBeforeInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(
            compilation,
            customerResultFactory: static _ => null!);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/null-provider-result"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceResultInvalid
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        Assert.Single(readers.Loads.Requests);
        Assert.Single(readers.Customers.Requests);
        Assert.Empty(readers.Equipment.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_CumulativeLookupRowsCannotExceedPlacedSourceBuffer()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2,
            customerMaximumBufferedRows: 3);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/cumulative-source-buffer"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        Assert.Single(readers.Loads.Requests);
        Assert.Equal(2, readers.Customers.Requests.Length);
        Assert.Equal(3, readers.Customers.Requests[0].MaximumBufferedRows);
        Assert.Equal(1, readers.Customers.Requests[1].MaximumBufferedRows);
        Assert.Empty(readers.Equipment.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustedSourceBufferStopsBeforeAnotherLookup()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2,
            customerMaximumBufferedRows: 2);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/exhausted-source-buffer"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        Assert.Single(readers.Loads.Requests);
        Assert.Single(readers.Customers.Requests);
        Assert.Equal(2, readers.Customers.Requests[0].MaximumBufferedRows);
        Assert.Empty(readers.Equipment.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_CorrelatedOccurrencesCannotExceedPlanWideLocalRows()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2,
            maximumLocalRows: 10);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/cumulative-local-rows"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded
            && diagnostic.Source == FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        Assert.Single(readers.Loads.Requests);
        Assert.Equal(2, readers.Customers.Requests.Length);
        Assert.Equal(2, readers.Equipment.Requests.Length);
    }

    [Fact]
    public async Task ExecuteAsync_PreCanceledTokenFailsBeforeAnyIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(compilation);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RelationQueryPhysicalExecutor(readers.All)
                .ExecuteAsync(Request(compilation, "tests/pre-canceled"), cancellation.Token)
                .AsTask());

        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringEnumerationStopsBeforeRelatedReads()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        using CancellationTokenSource cancellation = new();
        var readers = CreateReaders(
            compilation,
            afterLoadRead: _ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RelationQueryPhysicalExecutor(readers.All)
                .ExecuteAsync(
                    Request(compilation, "tests/canceled-during-enumeration"),
                    cancellation.Token)
                .AsTask());

        Assert.Single(readers.Loads.Requests);
        Assert.Empty(readers.Customers.Requests);
        Assert.Empty(readers.Equipment.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationBetweenLookupBatchesStopsBeforeNextRead()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        using CancellationTokenSource cancellation = new();
        var readers = CreateReaders(
            compilation,
            afterCustomerRead: _ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RelationQueryPhysicalExecutor(readers.All)
                .ExecuteAsync(
                    Request(compilation, "tests/canceled-between-lookup-batches"),
                    cancellation.Token)
                .AsTask());

        Assert.Single(readers.Loads.Requests);
        Assert.Single(readers.Customers.Requests);
        Assert.Empty(readers.Equipment.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_BoundedExplicitEquijoinMatchesCanonicalInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            CreateExplicitEquijoinDocument(),
            maximumBatchSize: 2,
            maximumLocalRows: 9);
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            LoadRows);
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customers.Id, customers.ExecutionDomain, customers.TargetProfile),
            CustomerRows);

        var result = await new RelationQueryPhysicalExecutor([customerReader, loadReader]).ExecuteAsync(
            Request(compilation, "tests/bounded-explicit-equijoin"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Single(loadReader.Requests);
        Assert.Single(customerReader.Requests);
        Assert.IsType<RelationQueryBoundedEnumeration>(loadReader.Requests[0].Constraint);
        Assert.IsType<RelationQueryBoundedEnumeration>(customerReader.Requests[0].Constraint);
        AssertRequestFields(
            loadReader.Requests,
            FederatedLoadRelationFixture.LoadCustomerIdPath,
            FederatedLoadRelationFixture.LoadIdPath);
        AssertRequestFields(
            customerReader.Requests,
            FederatedLoadRelationFixture.CustomerIdPath,
            FederatedLoadRelationFixture.CustomerNamePath);
        Assert.Single(
            compilation.PhysicalPlan.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.LocalCorrelation);
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var interpretation = Assert.IsType<RelationQueryExecutionResult>(result.Interpretation);
        var direct = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            evidence,
            RelationRequirementGapPolicy.Conventional));
        AssertEquivalent(interpretation, direct);
        AssertProjectedCustomerRows(interpretation);
    }

    [Fact]
    public async Task ExecuteAsync_EquijoinIdentityProofRejectsMismatchedSemanticIdentity()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            CreateExplicitEquijoinDocument(),
            maximumBatchSize: 2,
            maximumLocalRows: 9);
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> invalidCustomerRows =
        [
            DeterministicRelationQuerySourceReader.SourceRow.Create(
                "physical-customer-1",
                (FederatedLoadRelationFixture.CustomerIdPath, ObservationValue.FromString("customer-1")),
                (FederatedLoadRelationFixture.CustomerNamePath, ObservationValue.FromString("Customer One")))
        ];
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            LoadRows);
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customers.Id, customers.ExecutionDomain, customers.TargetProfile),
            invalidCustomerRows);

        var result = await new RelationQueryPhysicalExecutor([customerReader, loadReader]).ExecuteAsync(
            Request(compilation, "tests/equijoin-identity-mismatch"));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceResultInvalid);
    }

    [Theory]
    [InlineData(RelationQuerySourceReadState.NotFound, RelationQueryTraversalEvidenceState.Completed, RelationQueryEvidenceCompleteness.Complete)]
    [InlineData(RelationQuerySourceReadState.Partial, RelationQueryTraversalEvidenceState.Completed, RelationQueryEvidenceCompleteness.Partial)]
    [InlineData(RelationQuerySourceReadState.Failed, RelationQueryTraversalEvidenceState.Failed, RelationQueryEvidenceCompleteness.Partial)]
    [InlineData(RelationQuerySourceReadState.Inconclusive, RelationQueryTraversalEvidenceState.Inconclusive, RelationQueryEvidenceCompleteness.Partial)]
    public async Task ExecuteAsync_ForwardLookupOutcomeRemainsAttributablePerOwner(
        RelationQuerySourceReadState readState,
        RelationQueryTraversalEvidenceState traversalState,
        RelationQueryEvidenceCompleteness completeness)
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(
            compilation,
            customerResultFactory: _ => new(
                readState,
                evidenceReference: $"tests/customer-{readState}"));

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, $"tests/customer-{readState}"));

        Assert.NotEqual(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Evidence);
        Assert.NotNull(result.Interpretation);
        var customerTraversal = compilation.Plan.InputContract.Traversals.Single(
            traversal => traversal.Input.Traversal == FederatedLoadRelationFixture.CustomerTraversalNodeId);
        var evidence = result.Evidence.Traversals
            .Where(traversal => traversal.Input == customerTraversal.Input.Id)
            .ToArray();
        Assert.Equal(5, evidence.Length);
        Assert.All(evidence, item => Assert.Equal(traversalState, item.State));
        Assert.All(evidence, item => Assert.Equal(completeness, item.Completeness));
        Assert.All(
            result.SourceReads.Where(trace => trace.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource),
            trace => Assert.Equal(readState, trace.State));
    }

    [Fact]
    public async Task ExecuteAsync_PartialEmptyPriorSiblingTraversalSuppressesDownstreamReads()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(
            compilation,
            customerResultFactory: _ => new(
                RelationQuerySourceReadState.Partial,
                evidenceReference: "tests/partial-empty-customer"));

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/partial-empty-prior-sibling"));

        Assert.NotEqual(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(readers.Equipment.Requests);
        Assert.DoesNotContain(
            result.SourceReads,
            static trace => trace.Source == FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        var runtime = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var customer = compilation.Plan.InputContract.Traversals.Single(
            traversal => traversal.Input.Traversal == FederatedLoadRelationFixture.CustomerTraversalNodeId);
        var equipment = compilation.Plan.InputContract.Traversals.Single(
            traversal => traversal.Input.Traversal == FederatedLoadRelationFixture.EquipmentTraversalNodeId);
        var customerEvidence = runtime.Traversals
            .Where(traversal => traversal.Input == customer.Input.Id)
            .ToArray();
        var equipmentEvidence = runtime.Traversals
            .Where(traversal => traversal.Input == equipment.Input.Id)
            .ToArray();
        Assert.Equal(5, customerEvidence.Length);
        Assert.All(customerEvidence, static evidence =>
        {
            Assert.Equal(RelationQueryTraversalEvidenceState.Completed, evidence.State);
            Assert.Equal(RelationQueryEvidenceCompleteness.Partial, evidence.Completeness);
            Assert.Empty(evidence.Results);
        });
        Assert.Equal(5, equipmentEvidence.Length);
        Assert.All(equipmentEvidence, static evidence =>
            Assert.Equal(RelationQueryTraversalEvidenceState.NotApplicable, evidence.State));
    }

    [Fact]
    public async Task ExecuteAsync_SeparatedTraversalCorrelationConversionFailsBeforeAnyIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var customer = compilation.Plan.InputContract.Traversals.Single(
            traversal => traversal.Input.Traversal == FederatedLoadRelationFixture.CustomerTraversalNodeId);
        var customerReference = compilation.Plan.RequirementGraph.Inputs
            .OfType<RelationQueryFieldInput>()
            .Single(input => input.Binding == customer.From
                && input.Field.Shape == customer.Definition.SourceShape
                && input.Field.Path == customer.Definition.SourceReference);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(
                compilation,
                "tests/separated-traversal-conversion",
                [new(customerReference.Id, occurrence: null, "tests/customer-reference-conversion")]));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid
            && diagnostic.Input == customerReference.Id
            && diagnostic.EvidenceReference == "tests/customer-reference-conversion");
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_TraversalSourceConversionFailsBeforeAnyIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var source = Assert.Single(compilation.Plan.InputContract.Sources);
        var readers = CreateReaders(compilation);

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(
                compilation,
                "tests/traversal-source-conversion",
                [new(source.Input.Id, occurrence: null, "tests/load-source-conversion")]));

        Assert.Equal(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Interpretation);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid
            && diagnostic.Input == source.Input.Id
            && diagnostic.EvidenceReference == "tests/load-source-conversion");
        AssertNoIo(readers);
    }

    [Fact]
    public async Task ExecuteAsync_PartialIdentityBatchIsCompleteForReturnedKeysOnly()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var readers = CreateReaders(
            compilation,
            customerResultFactory: request =>
            {
                var lookup = Assert.IsType<RelationQueryIdentityBatchLookup>(request.Constraint);
                var returnedIdentity = lookup.Identities[0];
                var row = CustomerRows.Single(candidate => candidate.Identity == returnedIdentity);
                return new(
                    RelationQuerySourceReadState.Partial,
                    [new(
                        row.Identity,
                        request.Shape,
                        [
                            .. request.Fields.Select(field => row.Fields.TryGetValue(field.SemanticPath, out var value)
                                ? value.ToResult(field)
                                : new RelationQuerySourceReadFieldResult(
                                    field,
                                    RelationQuerySourceReadFieldState.Missing))
                        ])],
                    "tests/partial-identity-batch");
            });

        var result = await new RelationQueryPhysicalExecutor(readers.All).ExecuteAsync(
            Request(compilation, "tests/partial-identity-batch"));

        Assert.NotEqual(RelationQueryPhysicalExecutionStatus.Failed, result.Status);
        var runtime = Assert.IsType<RelationQueryRuntimeEvidence>(result.Evidence);
        var customerTraversal = compilation.Plan.InputContract.Traversals.Single(
            traversal => traversal.Input.Traversal == FederatedLoadRelationFixture.CustomerTraversalNodeId);
        var loadIdentities = runtime.Sources.Single().Occurrences.ToDictionary(
            static occurrence => occurrence.Id,
            static occurrence => occurrence.ObservationIdentity!,
            EqualityComparer<RelationQueryOccurrenceId>.Default);
        var completenessByLoad = runtime.Traversals
            .Where(traversal => traversal.Input == customerTraversal.Input.Id)
            .ToDictionary(
                traversal => loadIdentities[traversal.From],
                static traversal => traversal.Completeness,
                StringComparer.Ordinal);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, completenessByLoad["load-1"]);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, completenessByLoad["load-2"]);
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, completenessByLoad["load-3"]);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, completenessByLoad["load-4"]);
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, completenessByLoad["load-5"]);
    }

    static RelationQueryPhysicalExecutionRequest Request(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        string evaluation,
        ImmutableArray<RelationQueryConversionFailureEvidence> conversionFailures = default,
        IRelationRequirementGapPolicy? requirementGapPolicy = null) => new(
        compilation.Plan,
        compilation.PhysicalPlan,
        compilation.Realization,
        new(evaluation),
        conversionFailures: conversionFailures,
        capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan),
        requirementGapPolicy: requirementGapPolicy);

    static ReaderSet CreateReaders(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        bool mismatchCustomerProfile = false,
        Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult>? customerResultFactory = null,
        Action<RelationQuerySourceReadRequest>? afterLoadRead = null,
        Action<RelationQuerySourceReadRequest>? afterCustomerRead = null)
    {
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var equipment = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        var customerProfile = mismatchCustomerProfile
            ? new RelationQueryTargetCapabilityProfile(
                customers.TargetProfile.Target,
                new($"{customers.TargetProfile.Id.Value}/mismatch"),
                customers.TargetProfile.SupportedDefinitionSchemaVersions,
                customers.TargetProfile.SupportedCompilerProfiles,
                customers.TargetProfile.Capabilities,
                customers.TargetProfile.OperatingBoundaries,
                customers.TargetProfile.Description)
            : customers.TargetProfile;

        return new(
            new(
                new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
                LoadRows,
                afterRead: afterLoadRead),
            new(
                new(customers.Id, customers.ExecutionDomain, customerProfile),
                CustomerRows,
                customerResultFactory,
                afterCustomerRead),
            new(
                new(equipment.Id, equipment.ExecutionDomain, equipment.TargetProfile),
                EquipmentRows));
    }

    static readonly ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> LoadRows =
    [
        Load("load-1", "customer-1", "equipment-1", "Open", 10m),
        Load("load-2", "customer-1", "equipment-2", "Open", 20m),
        Load("load-3", "customer-2", "equipment-1", "Closed", 30m),
        Load("load-4", "customer-3", "equipment-3", "Open", 40m),
        Load("load-5", "customer-4", "equipment-4", "Closed", 50m)
    ];

    static readonly ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> CustomerRows =
    [
        Customer("customer-1", "Customer One", "Priority"),
        Customer("customer-2", "Customer Two", "Standard"),
        Customer("customer-3", "Customer Three", "Standard"),
        Customer("customer-4", "Customer Four", "Priority")
    ];

    static readonly ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> EquipmentRows =
    [
        Equipment("equipment-1", "TRUCK-001", "Tractor"),
        Equipment("equipment-2", "TRAILER-002", "Trailer"),
        Equipment("equipment-3", "TRUCK-003", "Tractor"),
        Equipment("equipment-4", "TRAILER-004", "Trailer")
    ];

    static DeterministicRelationQuerySourceReader.SourceRow Load(
        string id,
        string customer,
        string equipment,
        string status,
        decimal amount) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (FederatedLoadRelationFixture.LoadIdPath, ObservationValue.FromString(id)),
        (FederatedLoadRelationFixture.LoadCustomerIdPath, ObservationValue.FromString(customer)),
        (FederatedLoadRelationFixture.LoadEquipmentIdPath, ObservationValue.FromString(equipment)),
        (FederatedLoadRelationFixture.LoadStatusPath, ObservationValue.FromString(status)),
        (FederatedLoadRelationFixture.LoadAmountPath, ObservationValue.FromDecimal(amount)));

    static DeterministicRelationQuerySourceReader.SourceRow Customer(
        string id,
        string name,
        string type) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (FederatedLoadRelationFixture.CustomerIdPath, ObservationValue.FromString(id)),
        (FederatedLoadRelationFixture.CustomerNamePath, ObservationValue.FromString(name)),
        (FederatedLoadRelationFixture.CustomerTypePath, ObservationValue.FromString(type)));

    static DeterministicRelationQuerySourceReader.SourceRow Equipment(
        string id,
        string number,
        string type) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (FederatedLoadRelationFixture.EquipmentIdPath, ObservationValue.FromString(id)),
        (FederatedLoadRelationFixture.EquipmentNumberPath, ObservationValue.FromString(number)),
        (FederatedLoadRelationFixture.EquipmentTypePath, ObservationValue.FromString(type)));

    static void AssertRequestFields(
        ImmutableArray<RelationQuerySourceReadRequest> requests,
        params FieldPath[] expected)
    {
        var normalizedExpected = expected
            .OrderBy(static path => path.ToString(), StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(
            normalizedExpected,
            request.Fields.Select(static field => field.SemanticPath)
                .OrderBy(static path => path.ToString(), StringComparer.Ordinal)
                .ToArray()));
        Assert.All(requests, static request => Assert.All(request.Fields, static field =>
            Assert.Equal(RelationQuerySourceReadFieldPurpose.SemanticInput, field.Purpose)));
    }

    static void AssertIdentityBatches(
        ImmutableArray<RelationQuerySourceReadRequest> requests,
        int batchSize,
        params string[] expectedKeys)
    {
        var constraints = requests
            .Select(static request => Assert.IsType<RelationQueryIdentityBatchLookup>(request.Constraint))
            .ToArray();
        Assert.Equal((expectedKeys.Length + batchSize - 1) / batchSize, constraints.Length);
        Assert.All(constraints, constraint => Assert.InRange(constraint.Identities.Length, 1, batchSize));
        var actual = constraints.SelectMany(static constraint => constraint.Identities).ToArray();
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
    }

    static void AssertEquivalent(
        RelationQueryExecutionResult actual,
        RelationQueryExecutionResult expected)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.QueryResults.Length, actual.QueryResults.Length);
        for (var resultIndex = 0; resultIndex < expected.QueryResults.Length; resultIndex++)
        {
            var expectedResult = expected.QueryResults[resultIndex];
            var actualResult = actual.QueryResults[resultIndex];
            Assert.Equal(expectedResult.Result, actualResult.Result);
            Assert.Equal(expectedResult.Kind, actualResult.Kind);
            Assert.Equal(expectedResult.Shape, actualResult.Shape);
            Assert.Equal(expectedResult.State, actualResult.State);
            Assert.Equal(expectedResult.Rows.Length, actualResult.Rows.Length);
            for (var rowIndex = 0; rowIndex < expectedResult.Rows.Length; rowIndex++)
            {
                var expectedRow = expectedResult.Rows[rowIndex];
                var actualRow = actualResult.Rows[rowIndex];
                Assert.Equal(expectedRow.Value, actualRow.Value);
                Assert.Equal(expectedRow.Identity, actualRow.Identity);
                Assert.Equal(expectedRow.Root, actualRow.Root);
                Assert.Equal(
                    expectedRow.InputOccurrences.ToArray(),
                    actualRow.InputOccurrences.ToArray());
                Assert.Equal(expectedRow.UnresolvedGaps.ToArray(), actualRow.UnresolvedGaps.ToArray());
            }
        }
    }

    static void AssertProjectedRows(RelationQueryExecutionResult result)
    {
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(5, rows.Length);
        var projected = rows.ToDictionary(
            static row => row.Value.GetProperty(FederatedLoadRelationFixture.SearchIdFieldName).String!,
            static row => (
                Customer: row.Value.GetProperty(FederatedLoadRelationFixture.SearchCustomerNameFieldName).String,
                Equipment: row.Value.GetProperty(FederatedLoadRelationFixture.SearchEquipmentNumberFieldName).String),
            StringComparer.Ordinal);
        Assert.Equal(("Customer One", "TRUCK-001"), projected["load-1"]);
        Assert.Equal(("Customer One", "TRAILER-002"), projected["load-2"]);
        Assert.Equal(("Customer Two", "TRUCK-001"), projected["load-3"]);
        Assert.Equal(("Customer Three", "TRUCK-003"), projected["load-4"]);
        Assert.Equal(("Customer Four", "TRAILER-004"), projected["load-5"]);
    }

    static void AssertProjectedCustomerRows(RelationQueryExecutionResult result)
    {
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(5, rows.Length);
        var projected = rows.ToDictionary(
            static row => row.Value.GetProperty(FederatedLoadRelationFixture.SearchIdFieldName).String!,
            static row => row.Value.GetProperty(FederatedLoadRelationFixture.SearchCustomerNameFieldName).String,
            StringComparer.Ordinal);
        Assert.Equal("Customer One", projected["load-1"]);
        Assert.Equal("Customer One", projected["load-2"]);
        Assert.Equal("Customer Two", projected["load-3"]);
        Assert.Equal("Customer Three", projected["load-4"]);
        Assert.Equal("Customer Four", projected["load-5"]);
    }

    static RelationQueryDocument CreateExplicitEquijoinDocument()
    {
        var customers = new QueryNodeId("equijoin-customers");
        var join = new QueryNodeId("equijoin-load-customers");
        var projection = new QueryNodeId("project-equijoin-loads");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new("federated-load-customer-equijoin"),
            new("FederatedLoadCustomerEquijoin"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    FederatedLoadRelationFixture.LoadBinding,
                    FederatedLoadRelationFixture.LoadShapeId),
                new SourceQueryNode(
                    customers,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.CustomerShapeId),
                new JoinQueryNode(
                    join,
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    customers,
                    JoinKind.Inner,
                    Expr.Eq(
                        Expr.Field(
                            FederatedLoadRelationFixture.LoadBinding,
                            FederatedLoadRelationFixture.LoadCustomerIdPath),
                        Expr.Field(
                            FederatedLoadRelationFixture.CustomerBinding,
                            FederatedLoadRelationFixture.CustomerIdPath))),
                new ProjectQueryNode(
                    projection,
                    join,
                    FederatedLoadRelationFixture.SearchBinding,
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    [
                        new ProjectionAssignment(
                            FederatedLoadRelationFixture.SearchIdAssignmentId,
                            FederatedLoadRelationFixture.SearchIdPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadIdPath)),
                        new ProjectionAssignment(
                            FederatedLoadRelationFixture.SearchCustomerNameAssignmentId,
                            FederatedLoadRelationFixture.SearchCustomerNamePath,
                            Expr.Field(
                                FederatedLoadRelationFixture.CustomerBinding,
                                FederatedLoadRelationFixture.CustomerNamePath))
                    ])
            ]),
            [new RowsQueryResultDefinition(FederatedLoadRelationFixture.RowsResultId, projection)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateInverseManyDocument()
    {
        var customerSource = new QueryNodeId("inverse-customers");
        var inverseLoads = new QueryNodeId("inverse-customer-loads");
        var projection = new QueryNodeId("project-inverse-loads");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new("federated-inverse-load-query"),
            new("FederatedInverseLoadQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    customerSource,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.CustomerShapeId),
                new TraverseRelationshipQueryNode(
                    inverseLoads,
                    customerSource,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.LoadCustomerRelationshipId,
                    RelationshipTraversalDirection.Inverse,
                    FederatedLoadRelationFixture.LoadBinding,
                    JoinKind.Inner,
                    QueryInputRequirement.Required),
                new ProjectQueryNode(
                    projection,
                    inverseLoads,
                    FederatedLoadRelationFixture.SearchBinding,
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    [
                        new ProjectionAssignment(
                            new("assign-inverse-load-id"),
                            FederatedLoadRelationFixture.SearchIdPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadIdPath)),
                        new ProjectionAssignment(
                            new("assign-inverse-customer-name"),
                            FederatedLoadRelationFixture.SearchCustomerNamePath,
                            Expr.Field(
                                FederatedLoadRelationFixture.CustomerBinding,
                                FederatedLoadRelationFixture.CustomerNamePath))
                    ])
            ]),
            [new RowsQueryResultDefinition(FederatedLoadRelationFixture.RowsResultId, projection)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static void AssertNoIo(ReaderSet readers)
    {
        Assert.Empty(readers.Loads.Requests);
        Assert.Empty(readers.Customers.Requests);
        Assert.Empty(readers.Equipment.Requests);
    }

    sealed record ReaderSet(
        DeterministicRelationQuerySourceReader Loads,
        DeterministicRelationQuerySourceReader Customers,
        DeterministicRelationQuerySourceReader Equipment)
    {
        public ImmutableArray<IRelationQuerySourceReader> All => [Loads, Customers, Equipment];
    }
}
