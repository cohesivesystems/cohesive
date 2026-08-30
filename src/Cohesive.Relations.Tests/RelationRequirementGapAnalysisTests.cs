using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationRequirementGapAnalysisTests
{
    [Fact]
    public void Analyze_CompleteEvidenceHasNoGapsDecisionsOrDiagnostics()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1"),
                    ValueField(inputs.CustomerName, customer, "Acme")
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])]));

        Assert.True(result.IsEvidenceValid);
        Assert.True(result.IsConclusive);
        Assert.False(result.HasErrors);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.Decisions);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_ProvidedEmptySourceIsDistinctFromSourceThatWasNotProvided()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);

        var notProvided = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(inputs.Source, RelationQuerySourceEvidenceState.NotProvided)
                ]));
        var providedEmpty = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(inputs.Source, RelationQuerySourceEvidenceState.Provided, [])
                ]));

        Assert.Equal(RelationRequirementGapCause.InputNotProvided, Assert.Single(notProvided.Gaps).Cause);
        Assert.Empty(providedEmpty.Gaps);
        Assert.Empty(providedEmpty.Diagnostics);
    }

    [Fact]
    public void SourceEvidence_ConventionalConstructorPreservesExistingCompletenessDefaults()
    {
        var provided = new RelationQuerySourceEvidence(
            new("source/input"),
            RelationQuerySourceEvidenceState.Provided);
        var notProvided = new RelationQuerySourceEvidence(
            new("source/input"),
            RelationQuerySourceEvidenceState.NotProvided);
        var failed = new RelationQuerySourceEvidence(
            new("source/input"),
            RelationQuerySourceEvidenceState.Failed);
        var inconclusive = new RelationQuerySourceEvidence(
            new("source/input"),
            RelationQuerySourceEvidenceState.Inconclusive);

        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, provided.Completeness);
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, notProvided.Completeness);
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, failed.Completeness);
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, inconclusive.Completeness);
    }

    [Fact]
    public void SourceEvidence_ExplicitCompletenessRoundTripsAndRejectsCompleteNonResults()
    {
        var partial = new RelationQuerySourceEvidence(
            new("source/input"),
            RelationQuerySourceEvidenceState.Provided,
            RelationQueryEvidenceCompleteness.Partial,
            evidenceReference: "tests/partial-source");

        var json = JsonSerializer.Serialize(partial);
        var roundTripped = JsonSerializer.Deserialize<RelationQuerySourceEvidence>(json);

        Assert.Equal(partial, roundTripped);
        Assert.Throws<ArgumentException>(() => new RelationQuerySourceEvidence(
            new("source/input"),
            RelationQuerySourceEvidenceState.Inconclusive,
            RelationQueryEvidenceCompleteness.Complete));
    }

    [Fact]
    public void Analyze_ExplicitlyUnattemptedTraversalProducesOneCausalGapWithoutDownstreamCascade()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ResolutionNotAttempted, gap.Cause);
        Assert.Equal(inputs.Traversal, gap.Input.Id);
        Assert.Equal(load.Id, gap.Occurrence?.Id);
        Assert.Contains(inputs.CustomerName, gap.BlockedInputs);
        Assert.Contains(inputs.CustomerIdentity, gap.BlockedInputs);
        Assert.Contains(
            gap.RequiredFields,
            field => field.Shape == LoadCustomerRelationFixture.CustomerShapeId
                && field.Path == LoadCustomerRelationFixture.CustomerNamePath);
        Assert.DoesNotContain(
            result.Gaps,
            candidate => candidate.Cause is RelationRequirementGapCause.RequiredFieldNotLoaded
                or RelationRequirementGapCause.ObservationIdentityMissing);
        Assert.Same(plan.Provenance, gap.Provenance);
        Assert.Same(plan.Demand, gap.Demand);
        Assert.NotEmpty(gap.SuggestedResolutions);
        Assert.All(gap.Impacts, static impact => Assert.NotEmpty(impact.Traces));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.RequirementGapResolutionNotAttempted
                && diagnostic.Gap == gap.Id);
    }

    [Fact]
    public void Analyze_NotApplicableTraversalDoesNotInventReferenceOrTraversalGaps()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields: [ValueField(inputs.LoadId, load, "load-1")],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotApplicable)
                ]));

        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(RelationQueryTraversalEvidenceState.Failed, RelationRequirementGapCause.ResolutionFailed)]
    [InlineData(RelationQueryTraversalEvidenceState.Rejected, RelationRequirementGapCause.RelatedObservationRejected)]
    [InlineData(
        RelationQueryTraversalEvidenceState.Inconclusive,
        RelationRequirementGapCause.InputAcquisitionInconclusive)]
    public void Analyze_TraversalFailureRejectionAndInconclusiveRemainDistinctCausalGaps(
        RelationQueryTraversalEvidenceState state,
        RelationRequirementGapCause expectedCause)
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        state,
                        evidenceReference: "tests/traversal-result")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(expectedCause, gap.Cause);
        Assert.Equal("tests/traversal-result", gap.EvidenceReference);
        Assert.Equal(state != RelationQueryTraversalEvidenceState.Inconclusive, result.IsConclusive);
        Assert.DoesNotContain(
            result.Gaps,
            candidate => candidate.Cause is RelationRequirementGapCause.RequiredFieldNotLoaded
                or RelationRequirementGapCause.ObservationIdentityMissing);
    }

    [Theory]
    [InlineData(
        RelationQueryFieldEvidenceState.NotLoaded,
        RelationRequirementGapCause.ReferenceFieldNotLoaded)]
    [InlineData(
        RelationQueryFieldEvidenceState.Null,
        RelationRequirementGapCause.ReferenceValueNull)]
    [InlineData(
        RelationQueryFieldEvidenceState.Missing,
        RelationRequirementGapCause.ReferenceValueMissing)]
    [InlineData(
        RelationQueryFieldEvidenceState.Inconclusive,
        RelationRequirementGapCause.InputAcquisitionInconclusive)]
    public void Analyze_UnavailableReferenceStopsAtTheReferenceBoundary(
        RelationQueryFieldEvidenceState state,
        RelationRequirementGapCause expectedCause)
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    new(inputs.CustomerReference, load.Id, state)
                ],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(expectedCause, gap.Cause);
        Assert.Equal(inputs.CustomerReference, gap.Input.Id);
        Assert.Equal(state, gap.ValueContext?.ObservedState);
        Assert.Equal(RelationQueryTraversalEvidenceState.NotAttempted, gap.RelationshipContext?.ObservedState);
        Assert.Contains(inputs.Traversal, gap.BlockedInputs);
        Assert.Contains(inputs.CustomerName, gap.BlockedInputs);
        Assert.Equal(state != RelationQueryFieldEvidenceState.Inconclusive, result.IsConclusive);
    }

    [Fact]
    public void Analyze_AuthoritativeEmptyTraversalProducesRelatedObservationNotFound()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-404")
                ],
                traversals: [CompletedTraversal(inputs, load, [])]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.RelatedObservationNotFound, gap.Cause);
        Assert.Equal(inputs.Traversal, gap.Input.Id);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, gap.RelationshipContext?.Completeness);
        Assert.Equal(0, gap.RelationshipContext?.ObservedCount);
        Assert.Equal(
            ObservationValue.FromString("customer-404"),
            gap.RelationshipContext?.ReferenceValue);
    }

    [Fact]
    public void Analyze_CompletedForwardTraversalRejectsResultThatDoesNotMatchScalarReference()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var unexpectedCustomer = CustomerOccurrence("customer-2");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals: [CompletedTraversal(inputs, load, [unexpectedCustomer])]));

        Assert.False(result.IsEvidenceValid);
        Assert.False(result.IsConclusive);
        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict
                && diagnostic.Input == inputs.Traversal
                && diagnostic.Occurrence == load.Id
                && diagnostic.Message.Contains("1 result occurrence", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_CountCompatibleIdentitylessForwardResultProducesSemanticIdentityGap()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var unidentifiedCustomer = new RelationQueryObservationOccurrence(
            new("customer-result-1"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1"),
                    ValueField(inputs.CustomerName, unidentifiedCustomer, "Acme")
                ],
                traversals: [CompletedTraversal(inputs, load, [unidentifiedCustomer])]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ObservationIdentityMissing, gap.Cause);
        Assert.Equal(inputs.CustomerIdentity, gap.Input.Id);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict);
    }

    [Fact]
    public void Analyze_EmptyReferenceCollectionAndEmptyCompletedTraversalHaveNoGap()
    {
        var plan = Compile(shapeDocuments: WithManyCustomerReferences());
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, ObservationValue.FromArray([]))
                ],
                traversals: [CompletedTraversal(inputs, load, [])]));

        Assert.True(result.IsConclusive);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_EmptyReferenceCollectionRejectsIdentitylessCompletedResult()
    {
        var plan = Compile(shapeDocuments: WithManyCustomerReferences());
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var unidentifiedCustomer = new RelationQueryObservationOccurrence(
            new("customer-result-1"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, ObservationValue.FromArray([]))
                ],
                traversals: [CompletedTraversal(inputs, load, [unidentifiedCustomer])]));

        Assert.False(result.IsEvidenceValid);
        Assert.False(result.IsConclusive);
        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict
                && diagnostic.Input == inputs.Traversal
                && diagnostic.Occurrence == load.Id
                && diagnostic.Message.Contains("1 result occurrence", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_CompletedCollectionTraversalReportsMissingReferencedIdentity()
    {
        var plan = Compile(shapeDocuments: WithManyCustomerReferences());
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var firstCustomer = CustomerOccurrence("customer-1");
        var references = ObservationValue.FromArray(
        [
            ObservationValue.FromString("customer-1"),
            ObservationValue.FromString("customer-2")
        ]);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, references),
                    ValueField(inputs.CustomerName, firstCustomer, "Acme")
                ],
                traversals: [CompletedTraversal(inputs, load, [firstCustomer])]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.RelatedObservationNotFound, gap.Cause);
        Assert.Equal(inputs.Traversal, gap.Input.Id);
        Assert.Equal(references, gap.RelationshipContext?.ReferenceValue);
        Assert.Equal(1, gap.RelationshipContext?.ObservedCount);
    }

    [Fact]
    public void Analyze_CompletedCollectionTraversalRejectsUnreferencedResultIdentity()
    {
        var plan = Compile(shapeDocuments: WithManyCustomerReferences());
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var expectedCustomer = CustomerOccurrence("customer-1");
        var unexpectedCustomer = CustomerOccurrence("customer-2");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(
                        inputs.CustomerReference,
                        load,
                        ObservationValue.FromArray([ObservationValue.FromString("customer-1")]))
                ],
                traversals:
                [
                    CompletedTraversal(inputs, load, [expectedCustomer, unexpectedCustomer])
                ]));

        Assert.False(result.IsConclusive);
        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict
                && diagnostic.Input == inputs.Traversal
                && diagnostic.Occurrence == load.Id
                && diagnostic.Message.Contains("1 result occurrence", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_FailedSourceProducesOneAcquisitionGapAndSuppressesDescendants()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(
                        inputs.Source,
                        RelationQuerySourceEvidenceState.Failed,
                        evidenceReference: "tests/source-failure")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.InputAcquisitionFailed, gap.Cause);
        Assert.Equal(inputs.Source, gap.Input.Id);
        Assert.Null(gap.Occurrence);
        Assert.Equal("tests/source-failure", gap.EvidenceReference);
        Assert.Contains(inputs.Traversal, gap.BlockedInputs);
        Assert.Contains(inputs.CustomerName, gap.BlockedInputs);
    }

    [Fact]
    public void Analyze_InconclusiveSourceProducesDistinctNonconclusiveAcquisitionGap()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(
                        inputs.Source,
                        RelationQuerySourceEvidenceState.Inconclusive,
                        evidenceReference: "tests/source-inconclusive")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.True(result.IsEvidenceValid);
        Assert.False(result.IsConclusive);
        Assert.Equal(RelationRequirementGapCause.InputAcquisitionInconclusive, gap.Cause);
        Assert.Equal(inputs.Source, gap.Input.Id);
        Assert.Null(gap.Occurrence);
        Assert.Equal("tests/source-inconclusive", gap.EvidenceReference);
        Assert.Contains(RelationRequirementGapResolutionKind.RetryAcquisition, gap.SuggestedResolutions);
        Assert.Contains(inputs.Traversal, gap.BlockedInputs);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code
                    == RelationRuntimeDiagnosticCodes.RequirementGapInputAcquisitionInconclusive
                && diagnostic.Gap == gap.Id);
    }

    [Fact]
    public void Analyze_PartialProvidedSourceRetainsRowsWithoutClaimingCompleteSourceSet()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(
                        inputs.Source,
                        RelationQuerySourceEvidenceState.Provided,
                        RelationQueryEvidenceCompleteness.Partial,
                        [load],
                        "tests/source-partial")
                ],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1"),
                    ValueField(inputs.CustomerName, customer, "Acme")
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])]));

        Assert.True(result.IsEvidenceValid);
        Assert.False(result.IsConclusive);
        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
                && diagnostic.Input == inputs.Source
                && diagnostic.EvidenceReference == "tests/source-partial");
    }

    [Fact]
    public void Analyze_PartialCompletedTraversalIsNotConclusiveAndDoesNotClaimNotFound()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-404")
                ],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [],
                        RelationQueryEvidenceCompleteness.Partial)
                ]));

        Assert.True(result.IsEvidenceValid);
        Assert.False(result.IsConclusive);
        Assert.DoesNotContain(
            result.Gaps,
            static gap => gap.Cause == RelationRequirementGapCause.RelatedObservationNotFound);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(
        RelationQueryFieldEvidenceState.NotLoaded,
        RelationRequirementGapCause.RequiredFieldNotLoaded)]
    [InlineData(
        RelationQueryFieldEvidenceState.Null,
        RelationRequirementGapCause.RequiredValueNull)]
    [InlineData(
        RelationQueryFieldEvidenceState.Missing,
        RelationRequirementGapCause.RequiredValueMissing)]
    [InlineData(
        RelationQueryFieldEvidenceState.Inconclusive,
        RelationRequirementGapCause.InputAcquisitionInconclusive)]
    public void Analyze_UnavailableRelatedFieldIsDiagnosedOnItsCustomerOccurrence(
        RelationQueryFieldEvidenceState state,
        RelationRequirementGapCause expectedCause)
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1"),
                    new(inputs.CustomerName, customer.Id, state)
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(expectedCause, gap.Cause);
        Assert.Equal(inputs.CustomerName, gap.Input.Id);
        Assert.Equal(customer.Id, gap.Occurrence?.Id);
        Assert.Equal(state, gap.ValueContext?.ObservedState);
        Assert.Empty(gap.BlockedInputs);
        Assert.Equal(state != RelationQueryFieldEvidenceState.Inconclusive, result.IsConclusive);
    }

    [Fact]
    public void Analyze_MissingRelatedObservationIdentityIsAnOccurrenceScopedGap()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = new RelationQueryObservationOccurrence(
            new("customer-1"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1"),
                    ValueField(inputs.CustomerName, customer, "Acme")
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ObservationIdentityMissing, gap.Cause);
        Assert.Equal(inputs.CustomerIdentity, gap.Input.Id);
        Assert.Equal(customer.Id, gap.Occurrence?.Id);
    }

    [Fact]
    public void Analyze_UnavailableCapabilityProducesAnEvaluationWideGap()
    {
        var plan = Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.AggregationResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadAggregateShapeId,
                            LoadCustomerRelationFixture.AggregateLoadCountPath)
                    ])
            ]));
        var capability = Assert.Single(
            plan.InputContract.Capabilities,
            candidate => candidate.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Count));
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/evaluation-1"),
            plan,
            RelationQueryEvidenceCompleteness.Partial,
            capabilities:
            [
                new(
                    capability.Input.Id,
                    RelationQueryCapabilityEvidenceState.Unavailable,
                    "tests/capability-probe")
            ]);

        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.CapabilityUnavailable, gap.Cause);
        Assert.Equal(capability.Input.Id, gap.Input.Id);
        Assert.Null(gap.Occurrence);
        Assert.Equal("tests/capability-probe", gap.EvidenceReference);
    }

    [Fact]
    public void Analyze_AtMostOneTraversalWithTwoResultsProducesCardinalityViolationWithoutFieldCascade()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var first = CustomerOccurrence("customer-1");
        var second = CustomerOccurrence("customer-2");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals: [CompletedTraversal(inputs, load, [second, first])]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.CardinalityViolation, gap.Cause);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, gap.RelationshipContext?.ExpectedCardinality);
        Assert.Equal(2, gap.RelationshipContext?.ObservedCount);
        Assert.DoesNotContain(
            result.Gaps,
            candidate => candidate.Cause is RelationRequirementGapCause.RequiredFieldNotLoaded
                or RelationRequirementGapCause.ObservationIdentityMissing);
    }

    [Fact]
    public void Analyze_TwoRootOccurrencesKeepEvidenceAndGapsIsolated()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var availableLoad = LoadOccurrence("load-available");
        var missingLoad = LoadOccurrence("load-missing");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, missingLoad, availableLoad)],
                fields:
                [
                    ValueField(inputs.LoadId, availableLoad, "load-available"),
                    ValueField(inputs.CustomerReference, availableLoad, "customer-1"),
                    ValueField(inputs.CustomerName, customer, "Acme"),
                    ValueField(inputs.LoadId, missingLoad, "load-missing"),
                    ValueField(inputs.CustomerReference, missingLoad, "customer-2")
                ],
                traversals:
                [
                    CompletedTraversal(inputs, availableLoad, [customer]),
                    new(
                        inputs.Traversal,
                        missingLoad.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ResolutionNotAttempted, gap.Cause);
        Assert.Equal(missingLoad.Id, gap.Occurrence?.Id);
        Assert.DoesNotContain(result.Gaps, candidate => candidate.Occurrence?.Id == availableLoad.Id);
    }

    [Fact]
    public void Analyze_ParentTraversalGapSuppressesTheEntireMultiHopDescendantCascade()
    {
        var plan = CompileMultiHopQuery();
        var source = Assert.Single(plan.InputContract.Sources);
        var first = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        var second = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == new QueryNodeId("related-loads"));
        var loadId = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var customerReference = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var relatedLoadId = FieldInput(
            plan,
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, [load])
                ],
                fields:
                [
                    ValueField(loadId, load, "load-1"),
                    ValueField(customerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        first.Input.Id,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ResolutionNotAttempted, gap.Cause);
        Assert.Equal(first.Input.Id, gap.Input.Id);
        Assert.Contains(second.Input.Id, gap.BlockedInputs);
        Assert.Contains(relatedLoadId, gap.BlockedInputs);
    }

    [Fact]
    public void Analyze_MissingInverseAnchorIdentitySuppressesItsMultiHopDescendants()
    {
        var plan = CompileMultiHopQuery();
        var source = Assert.Single(plan.InputContract.Sources);
        var first = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        var second = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == new QueryNodeId("related-loads"));
        var identity = Assert.Single(plan.InputContract.Identities);
        var loadId = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var customerReference = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var relatedLoadId = FieldInput(
            plan,
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var load = LoadOccurrence("load-1");
        var customer = new RelationQueryObservationOccurrence(
            new("customer-1"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, [load])
                ],
                fields:
                [
                    ValueField(loadId, load, "load-1"),
                    ValueField(customerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        first.Input.Id,
                        load.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [customer],
                        RelationQueryEvidenceCompleteness.Complete),
                    new(
                        second.Input.Id,
                        customer.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ObservationIdentityMissing, gap.Cause);
        Assert.Equal(identity.Input.Id, gap.Input.Id);
        Assert.Equal(customer.Id, gap.Occurrence?.Id);
        Assert.Contains(second.Input.Id, gap.BlockedInputs);
        Assert.Contains(relatedLoadId, gap.BlockedInputs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Analyze_CompletedInverseTraversalAcceptsZeroOrManyResults(int resultCount)
    {
        var plan = CompileMultiHopQuery();
        var source = Assert.Single(plan.InputContract.Sources);
        var first = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        var second = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == new QueryNodeId("related-loads"));
        var loadId = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var customerReference = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var relatedLoadId = FieldInput(
            plan,
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var relatedCustomerReference = FieldInput(
            plan,
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");
        var related = Enumerable.Range(1, resultCount)
            .Select(index => new RelationQueryObservationOccurrence(
                new($"related-load-{index}"),
                new("relatedLoad"),
                LoadCustomerRelationFixture.LoadShapeId,
                $"load-{index + 1}"))
            .ToImmutableArray();

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, [load])
                ],
                fields:
                [
                    ValueField(loadId, load, "load-1"),
                    ValueField(customerReference, load, "customer-1"),
                    .. related.Select((occurrence, index) =>
                        ValueField(relatedLoadId, occurrence, $"load-{index + 2}")),
                    .. related.Select(occurrence =>
                        ValueField(relatedCustomerReference, occurrence, "customer-1"))
                ],
                traversals:
                [
                    new(
                        first.Input.Id,
                        load.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [customer],
                        RelationQueryEvidenceCompleteness.Complete),
                    new(
                        second.Input.Id,
                        customer.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        related,
                        RelationQueryEvidenceCompleteness.Complete)
                ]));

        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_EvidenceOrderDoesNotAffectGapsDecisionsOrDiagnostics()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var first = LoadOccurrence("load-a");
        var second = LoadOccurrence("load-b");
        ImmutableArray<RelationQueryFieldEvidence> fields =
        [
            ValueField(inputs.LoadId, first, "load-a"),
            ValueField(inputs.CustomerReference, first, "customer-a"),
            ValueField(inputs.LoadId, second, "load-b"),
            ValueField(inputs.CustomerReference, second, "customer-b")
        ];
        ImmutableArray<RelationQueryTraversalEvidence> traversals =
        [
            new(
                inputs.Traversal,
                first.Id,
                RelationQueryTraversalEvidenceState.NotAttempted),
            CompletedTraversal(inputs, second, [])
        ];

        var firstResult = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, first, second)],
                fields: fields,
                traversals: traversals));
        var reversedResult = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, second, first)],
                fields: [.. fields.Reverse()],
                traversals: [.. traversals.Reverse()]));

        Assert.Equal(ResultSignature(firstResult), ResultSignature(reversedResult));
    }

    [Fact]
    public void Analyze_IdOnlyDemandDoesNotRequirePrunedTraversalOrCustomerEvidence()
    {
        var plan = Compile(
            LoadCustomerRelationFixture.OptionalTraversalRelationDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    LoadCustomerRelationFixture.SearchIdPath)
            ]));
        var source = Assert.Single(plan.InputContract.Sources);
        var id = Assert.Single(source.Fields);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(
                        source.Input.Id,
                        RelationQuerySourceEvidenceState.Provided,
                        [load])
                ],
                fields: [ValueField(id.Input.Id, load, "load-1")]));

        Assert.Empty(plan.InputContract.Traversals);
        Assert.Empty(plan.InputContract.Identities);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_OptionalTraversalGapDoesNotBecomeRequiredThroughBlockedFields()
    {
        var plan = Compile(LoadCustomerRelationFixture.OptionalTraversalRelationDocument);
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ResolutionNotAttempted, gap.Cause);
        Assert.All(gap.Impacts, static impact =>
            Assert.Equal(QueryInputRequirement.Optional, impact.Requirement));
        Assert.All(result.Decisions, static decision =>
            Assert.Equal(RelationRequirementGapReportingKind.Suppress, decision.Reporting));
        Assert.False(result.HasErrors);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_DuplicateAndUnknownEvidenceProducesStructuredDiagnosticsInsteadOfGaps()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var duplicate = ValueField(inputs.LoadId, load, "load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    duplicate,
                    duplicate,
                    ValueField(new("tests/unknown-input"), load, "unknown")
                ]));

        Assert.False(result.IsConclusive);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.Decisions);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceDuplicate
                && diagnostic.Input == inputs.LoadId
                && diagnostic.Occurrence == load.Id);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.InputUnknown
                && diagnostic.Input == new RelationQueryInputId("tests/unknown-input"));
    }

    [Fact]
    public void Analyze_ConflictingDuplicateEvidenceIsQuarantinedDeterministically()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        RelationQueryFieldEvidence valid = new(
            inputs.LoadId,
            load.Id,
            RelationQueryFieldEvidenceState.Value,
            ObservationValue.FromString("load-1"),
            "tests/valid-field");
        RelationQueryFieldEvidence conflicting = new(
            inputs.LoadId,
            load.Id,
            RelationQueryFieldEvidenceState.Value,
            ObservationValue.FromInt64(42),
            "tests/conflicting-field");

        var forward = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields: [valid, conflicting]));
        var reversed = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields: [conflicting, valid]));

        Assert.True(forward.Diagnostics.SequenceEqual(reversed.Diagnostics));
        var diagnostic = Assert.Single(forward.Diagnostics);
        Assert.Equal(RelationRuntimeDiagnosticCodes.EvidenceDuplicate, diagnostic.Code);
        Assert.Equal(inputs.LoadId, diagnostic.Input);
        Assert.Equal(load.Id, diagnostic.Occurrence);
        Assert.Null(diagnostic.EvidenceReference);
        Assert.Empty(forward.Gaps);
        Assert.Empty(reversed.Gaps);
    }

    [Fact]
    public void Analyze_DuplicateEvidenceRemainsGroupedWhenLegacyCompositeSortKeysCollide()
    {
        var plan = Compile();
        RelationQueryInputId duplicateInput = new("a\u001fb");
        RelationQueryOccurrenceId duplicateOwner = new("c");
        RelationQueryInputId collidingInput = new("a");
        RelationQueryOccurrenceId collidingOwner = new("b\u001fc");
        RelationQueryFieldEvidence duplicate = new(
            duplicateInput,
            duplicateOwner,
            RelationQueryFieldEvidenceState.NotLoaded,
            evidenceReference: "tests/duplicate");
        RelationQueryFieldEvidence collision = new(
            collidingInput,
            collidingOwner,
            RelationQueryFieldEvidenceState.NotLoaded,
            evidenceReference: "tests/collision");
        var evidence = Evidence(plan, fields: [duplicate, collision, duplicate]);

        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence);

        Assert.Equal(
            [collidingInput, duplicateInput, duplicateInput],
            evidence.Fields.Select(static field => field.Input));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceDuplicate
                && diagnostic.Input == duplicateInput
                && diagnostic.Occurrence == duplicateOwner);
    }

    [Fact]
    public void RuntimeEvidence_AlreadyCanonicalImmutableStorageIsRetained()
    {
        var plan = Compile();
        RelationQueryOccurrenceId owner = new("owner");
        ImmutableArray<RelationQueryFieldEvidence> fields =
        [
            new(
                new("a"),
                owner,
                RelationQueryFieldEvidenceState.NotLoaded,
                evidenceReference: "tests/a"),
            new(
                new("b"),
                owner,
                RelationQueryFieldEvidenceState.NotLoaded,
                evidenceReference: "tests/b")
        ];

        var evidence = Evidence(plan, fields: fields);

        Assert.True(fields == evidence.Fields);
    }

    [Fact]
    public void Analyze_EvidenceKindMismatchProducesStructuredDiagnosticInsteadOfGaps()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.Source, load, "not-source-evidence")
                ]));

        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.InputKindMismatch
                && diagnostic.Input == inputs.Source
                && diagnostic.Occurrence == load.Id);
    }

    [Fact]
    public void Analyze_FieldAttachedToWrongBindingOccurrenceProducesStructuredDiagnostic()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.CustomerName, load, "wrong-owner")
                ]));

        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict
                && diagnostic.Input == inputs.CustomerName
                && diagnostic.Occurrence == load.Id);
    }

    [Fact]
    public void Analyze_ConversionFailureMustBelongToTheCompiledInputOccurrence()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                conversionFailures:
                [
                    new(inputs.CustomerName, load.Id, "tests/conversion-1")
                ]));

        Assert.False(result.IsConclusive);
        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict
                && diagnostic.Input == inputs.CustomerName
                && diagnostic.Occurrence == load.Id);
    }

    [Fact]
    public void Analyze_AttributedConversionFailureProducesOneConversionGap()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])],
                conversionFailures:
                [
                    new(inputs.CustomerName, customer.Id, "tests/conversion-customer-name")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(inputs.CustomerName, gap.Input.Id);
        Assert.Equal(customer.Id, gap.Occurrence?.Id);
        Assert.Equal("tests/conversion-customer-name", gap.EvidenceReference);
        Assert.Empty(gap.BlockedInputs);
    }

    [Fact]
    public void Analyze_SourceConversionFailureBlocksDescendantsOfProvidedOccurrences()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])],
                conversionFailures:
                [
                    new(inputs.Source, occurrence: null, evidenceReference: "tests/conversion-source"),
                    new(inputs.CustomerReference, load.Id, "tests/conversion-reference"),
                    new(inputs.CustomerName, customer.Id, "tests/conversion-customer-name")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(inputs.Source, gap.Input.Id);
        Assert.Null(gap.Occurrence);
        Assert.Contains(inputs.Traversal, gap.BlockedInputs);
        Assert.DoesNotContain(
            result.Gaps,
            static candidate => candidate.Cause is RelationRequirementGapCause.ResolutionNotAttempted
                or RelationRequirementGapCause.RequiredFieldNotLoaded);
    }

    [Fact]
    public void Analyze_ForwardReferenceConversionSuppressesMultiHopDescendantConversions()
    {
        var plan = CompileMultiHopQuery();
        var source = Assert.Single(plan.InputContract.Sources);
        var first = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        var second = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == new QueryNodeId("related-loads"));
        var loadId = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var customerReference = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var relatedLoadId = FieldInput(
            plan,
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var load = LoadOccurrence("z-load");
        var customer = CustomerOccurrence("m-customer");
        var relatedLoad = new RelationQueryObservationOccurrence(
            new("a-related-load"),
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            "related-load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, [load])
                ],
                fields:
                [
                    ValueField(loadId, load, "z-load"),
                    new(
                        customerReference,
                        load.Id,
                        RelationQueryFieldEvidenceState.Failed)
                ],
                traversals:
                [
                    new(
                        first.Input.Id,
                        load.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [customer],
                        RelationQueryEvidenceCompleteness.Complete),
                    new(
                        second.Input.Id,
                        customer.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [relatedLoad],
                        RelationQueryEvidenceCompleteness.Complete)
                ],
                conversionFailures:
                [
                    new(relatedLoadId, relatedLoad.Id, "tests/descendant-conversion"),
                    new(customerReference, load.Id, "tests/reference-conversion")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(customerReference, gap.Input.Id);
        Assert.Equal(load.Id, gap.Occurrence?.Id);
        Assert.Contains(first.Input.Id, gap.BlockedInputs);
        Assert.Contains(second.Input.Id, gap.BlockedInputs);
        Assert.DoesNotContain(result.Gaps, candidate => candidate.Input.Id == relatedLoadId);
    }

    [Fact]
    public void Analyze_InverseAnchorConversionSuppressesDescendantConversionBeforeOrdering()
    {
        var plan = CompileMultiHopQuery();
        var source = Assert.Single(plan.InputContract.Sources);
        var first = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        var second = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.Input.Traversal == new QueryNodeId("related-loads"));
        var identity = Assert.Single(plan.InputContract.Identities);
        var loadId = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var customerReference = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var relatedLoadId = FieldInput(
            plan,
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var load = LoadOccurrence("load-1");
        var customer = new RelationQueryObservationOccurrence(
            new("z-customer"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId,
            "customer-1");
        var relatedLoad = new RelationQueryObservationOccurrence(
            new("a-related-load"),
            new("relatedLoad"),
            LoadCustomerRelationFixture.LoadShapeId,
            "related-load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources:
                [
                    new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, [load])
                ],
                fields:
                [
                    ValueField(loadId, load, "load-1"),
                    ValueField(customerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        first.Input.Id,
                        load.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [customer],
                        RelationQueryEvidenceCompleteness.Complete),
                    new(
                        second.Input.Id,
                        customer.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        [relatedLoad],
                        RelationQueryEvidenceCompleteness.Complete)
                ],
                conversionFailures:
                [
                    new(relatedLoadId, relatedLoad.Id, "tests/descendant-conversion"),
                    new(identity.Input.Id, customer.Id, "tests/inverse-anchor-conversion")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(identity.Input.Id, gap.Input.Id);
        Assert.Equal(customer.Id, gap.Occurrence?.Id);
        Assert.Contains(second.Input.Id, gap.BlockedInputs);
        Assert.DoesNotContain(result.Gaps, candidate => candidate.Input.Id == relatedLoadId);
    }

    [Fact]
    public void Analyze_NotApplicableTraversalSuppressesCorrelationOnlyConversionFailure()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields: [ValueField(inputs.LoadId, load, "load-1")],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotApplicable)
                ],
                conversionFailures:
                [
                    new(inputs.CustomerReference, load.Id, "tests/pruned-reference-conversion")
                ]));

        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_EvidenceFromAnotherCompiledDemandIsRejected()
    {
        var fullPlan = Compile();
        var inputs = Inputs.For(fullPlan);
        var load = LoadOccurrence("load-1");
        var evidence = Evidence(fullPlan, sources: [ProvidedSource(inputs, load)]);
        var narrowedPlan = Compile(
            demand: RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    LoadCustomerRelationFixture.SearchIdPath)
            ]));

        var result = RelationRequirementGapAnalyzer.Analyze(narrowedPlan, evidence);

        Assert.False(result.IsEvidenceValid);
        Assert.False(result.IsConclusive);
        Assert.Empty(result.Gaps);
        var mismatch = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.PlanMismatch);
        Assert.Equal(
            "Runtime evidence belongs to a different compiled relation/query input contract. Mismatched components: demand, inputs.",
            mismatch.Message);
    }

    [Fact]
    public void Analyze_PlanMismatchIdentifiesDefinitionComponent()
    {
        var originalPlan = Compile();
        var inputs = Inputs.For(originalPlan);
        var load = LoadOccurrence("load-1");
        var evidence = Evidence(originalPlan, sources: [ProvidedSource(inputs, load)]);
        var changedPlan = Compile(LoadCustomerRelationFixture.OptionalTraversalRelationDocument);

        var result = RelationRequirementGapAnalyzer.Analyze(changedPlan, evidence);

        var mismatch = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.PlanMismatch);
        Assert.Equal(
            "Runtime evidence belongs to a different compiled relation/query input contract. Mismatched components: definition.",
            mismatch.Message);
    }

    [Fact]
    public void Analyze_EvidenceMatchesAnEquivalentRehydratedCompiledPlan()
    {
        var firstPlan = Compile();
        var inputs = Inputs.For(firstPlan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");
        var evidence = Evidence(
            firstPlan,
            sources: [ProvidedSource(inputs, load)],
            fields:
            [
                ValueField(inputs.LoadId, load, "load-1"),
                ValueField(inputs.CustomerReference, load, "customer-1"),
                ValueField(inputs.CustomerName, customer, "Acme")
            ],
            traversals: [CompletedTraversal(inputs, load, [customer])]);
        var rehydratedPlan = Compile(shapeDocuments: RehydrateShapeDocuments());

        var result = RelationRequirementGapAnalyzer.Analyze(rehydratedPlan, evidence);

        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analyze_ShapeSemanticsParticipateInPlanMatchingAndGapIdentity()
    {
        var originalPlan = Compile();
        var changedPlan = Compile(shapeDocuments: WithNullableCustomerName());
        var original = UnattemptedTraversalEvidence(originalPlan);
        var changed = UnattemptedTraversalEvidence(changedPlan);

        var mismatch = RelationRequirementGapAnalyzer.Analyze(changedPlan, original.Evidence);
        var originalGap = Assert.Single(RelationRequirementGapAnalyzer.Analyze(originalPlan, original.Evidence).Gaps);
        var changedGap = Assert.Single(RelationRequirementGapAnalyzer.Analyze(changedPlan, changed.Evidence).Gaps);

        var diagnostic = Assert.Single(
            mismatch.Diagnostics,
            static candidate => candidate.Code == RelationRuntimeDiagnosticCodes.PlanMismatch);
        Assert.Equal(
            "Runtime evidence belongs to a different compiled relation/query input contract. Mismatched components: shapes.",
            diagnostic.Message);
        Assert.NotEqual(originalGap.Id, changedGap.Id);
    }

    [Fact]
    public void Analyze_RelationshipCatalogSemanticsParticipateInPlanMatchingAndGapIdentity()
    {
        var originalPlan = Compile();
        var changedRelationship = LoadCustomerRelationFixture.LoadCustomerRelationship with
        {
            SourceReferenceUniqueness = SourceReferenceUniqueness.GloballyUnique
        };
        var changedCatalog = RelationshipCatalogDocument.FromCatalog(
            new RelationshipCatalog([changedRelationship]));
        var changedPlan = Compile(relationshipCatalogDocument: changedCatalog);
        var original = UnattemptedTraversalEvidence(originalPlan);
        var changed = UnattemptedTraversalEvidence(changedPlan);

        var mismatch = RelationRequirementGapAnalyzer.Analyze(changedPlan, original.Evidence);
        var originalGap = Assert.Single(RelationRequirementGapAnalyzer.Analyze(originalPlan, original.Evidence).Gaps);
        var changedGap = Assert.Single(RelationRequirementGapAnalyzer.Analyze(changedPlan, changed.Evidence).Gaps);

        Assert.Equal(originalGap.Input.Id, changedGap.Input.Id);
        var diagnostic = Assert.Single(
            mismatch.Diagnostics,
            static candidate => candidate.Code == RelationRuntimeDiagnosticCodes.PlanMismatch);
        Assert.Equal(
            "Runtime evidence belongs to a different compiled relation/query input contract. Mismatched components: catalog.",
            diagnostic.Message);
        Assert.NotEqual(originalGap.Id, changedGap.Id);
    }

    [Fact]
    public void Analyze_LoadedNullDoesNotCreateAGapWhenTheCompiledFieldContractIsNullable()
    {
        var plan = Compile(shapeDocuments: WithNullableCustomerName());
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var customer = CustomerOccurrence("customer-1");

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1"),
                    new(
                        inputs.CustomerName,
                        customer.Id,
                        RelationQueryFieldEvidenceState.Null)
                ],
                traversals: [CompletedTraversal(inputs, load, [customer])]));

        Assert.Empty(result.Gaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParameterEvidence_JsonRoundTripPreservesAbsenceMissingNullAndConcreteValue()
    {
        var input = new RelationQueryInputId("input/parameter/tests/value");
        RelationQueryParameterEvidence[] values =
        [
            new(input, RelationQueryParameterEvidenceState.NotProvided),
            new(input, RelationQueryParameterEvidenceState.Missing),
            new(input, RelationQueryParameterEvidenceState.Null),
            new(
                input,
                RelationQueryParameterEvidenceState.Provided,
                ObservationValue.FromString("provided"))
        ];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(values, options);
        var roundTripped = Assert.IsType<RelationQueryParameterEvidence[]>(
            JsonSerializer.Deserialize<RelationQueryParameterEvidence[]>(json, options));

        Assert.Collection(
            roundTripped,
            value =>
            {
                Assert.Equal(RelationQueryParameterEvidenceState.NotProvided, value.State);
                Assert.Null(value.Value);
            },
            value =>
            {
                Assert.Equal(RelationQueryParameterEvidenceState.Missing, value.State);
                Assert.Null(value.Value);
            },
            value =>
            {
                Assert.Equal(RelationQueryParameterEvidenceState.Null, value.State);
                Assert.Null(value.Value);
            },
            value =>
            {
                Assert.Equal(RelationQueryParameterEvidenceState.Provided, value.State);
                Assert.Equal(ObservationValue.FromString("provided"), value.Value);
            });
    }

    [Fact]
    public void ParameterEvidence_RejectsAmbiguousNullAndMissingProvidedPayloads()
    {
        var input = new RelationQueryInputId("input/parameter/tests/value");

        Assert.Throws<ArgumentException>(() =>
            new RelationQueryParameterEvidence(
                input,
                RelationQueryParameterEvidenceState.Provided));
        Assert.Throws<ArgumentException>(() =>
            new RelationQueryParameterEvidence(
                input,
                RelationQueryParameterEvidenceState.Provided,
                ObservationValue.Null));
        Assert.Throws<ArgumentException>(() =>
            new RelationQueryParameterEvidence(
                input,
                RelationQueryParameterEvidenceState.Provided,
                ObservationValue.Undefined));
        Assert.Throws<ArgumentException>(() =>
            new RelationQueryParameterEvidence(
                input,
                RelationQueryParameterEvidenceState.Null,
                ObservationValue.FromString("unexpected")));
        Assert.Throws<ArgumentException>(() =>
            new RelationQueryParameterEvidence(
                input,
                RelationQueryParameterEvidenceState.Missing,
                ObservationValue.FromString("unexpected")));
    }

    [Fact]
    public void Analyze_ExplicitNullParameterIsAcceptedWhenItsCompiledDefaultMakesItNullable()
    {
        var plan = CompileParameterProjection(ObservationValue.Null);
        var cursor = Assert.Single(
            plan.InputContract.Parameters,
            parameter => parameter.Definition.Id == LoadCustomerRelationFixture.CursorParameterId);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                completeness: RelationQueryEvidenceCompleteness.Partial,
                parameters:
                [
                    new(
                        cursor.Input.Id,
                        RelationQueryParameterEvidenceState.Null)
                ]));

        Assert.Equal(FieldNullability.Nullable, cursor.ValueContract.Nullability);
        Assert.DoesNotContain(
            result.Gaps,
            gap => gap.Input.Id == cursor.Input.Id);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Input == cursor.Input.Id);
    }

    [Theory]
    [InlineData(RelationQueryParameterEvidenceState.NotProvided)]
    [InlineData(RelationQueryParameterEvidenceState.Missing)]
    public void Analyze_OptionalParameterAbsenceAndSemanticMissingDoNotCreateAGap(
        RelationQueryParameterEvidenceState state)
    {
        var plan = CompileParameterProjection(defaultValue: null);
        var parameter = Assert.Single(plan.InputContract.Parameters);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                completeness: RelationQueryEvidenceCompleteness.Partial,
                parameters:
                [
                    new(parameter.Input.Id, state)
                ]));

        Assert.DoesNotContain(result.Gaps, gap => gap.Input.Id == parameter.Input.Id);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Input == parameter.Input.Id);
    }

    [Theory]
    [InlineData(RelationQueryParameterEvidenceState.NotProvided, RelationRequirementGapCause.InputNotProvided)]
    [InlineData(RelationQueryParameterEvidenceState.Missing, RelationRequirementGapCause.RequiredValueMissing)]
    [InlineData(RelationQueryParameterEvidenceState.Failed, RelationRequirementGapCause.InputAcquisitionFailed)]
    public void Analyze_RequiredParameterAbsenceMissingAndFailureRemainDistinct(
        RelationQueryParameterEvidenceState state,
        RelationRequirementGapCause expectedCause)
    {
        var plan = CompileParameterProjection(defaultValue: null, FieldPresence.Required);
        var parameter = Assert.Single(plan.InputContract.Parameters);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                completeness: RelationQueryEvidenceCompleteness.Partial,
                parameters:
                [
                    new(
                        parameter.Input.Id,
                        state,
                        evidenceReference: "tests/parameter-state")
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(expectedCause, gap.Cause);
        Assert.Equal(parameter.Input.Id, gap.Input.Id);
        Assert.Null(gap.Occurrence);
        Assert.Equal("tests/parameter-state", gap.EvidenceReference);
    }

    [Fact]
    public void Analyze_ExplicitNullParameterProducesAGapWhenItsCompiledContractIsNonNullable()
    {
        var plan = CompileParameterProjection(defaultValue: null, FieldPresence.Required);
        var cursor = Assert.Single(plan.InputContract.Parameters);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                completeness: RelationQueryEvidenceCompleteness.Partial,
                parameters:
                [
                    new(
                        cursor.Input.Id,
                        RelationQueryParameterEvidenceState.Null)
                ]));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(FieldNullability.NonNullable, cursor.ValueContract.Nullability);
        Assert.Equal(RelationRequirementGapCause.RequiredValueNull, gap.Cause);
        Assert.Equal(cursor.Input.Id, gap.Input.Id);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.RequirementGapRequiredValueNull
                && diagnostic.Input == cursor.Input.Id);
    }

    [Fact]
    public void Analyze_ParameterValueContradictingItsCompiledTypeProducesStructuredDiagnostic()
    {
        var plan = CompileParameterProjection(defaultValue: null);
        var cursor = Assert.Single(plan.InputContract.Parameters);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            Evidence(
                plan,
                completeness: RelationQueryEvidenceCompleteness.Partial,
                parameters:
                [
                    new(
                        cursor.Input.Id,
                        RelationQueryParameterEvidenceState.Provided,
                        ObservationValue.FromInt64(42))
                ]));

        Assert.Empty(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ValueContractMismatch
                && diagnostic.Input == cursor.Input.Id);
    }

    [Fact]
    public void Analyze_PolicyProjectionPreservesGapsAndMakesConventionVersusOverrideExplicit()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var evidence = Evidence(
            plan,
            sources: [ProvidedSource(inputs, load)],
            fields:
            [
                ValueField(inputs.LoadId, load, "load-1"),
                ValueField(inputs.CustomerReference, load, "customer-1")
            ],
            traversals:
            [
                new(
                    inputs.Traversal,
                    load.Id,
                    RelationQueryTraversalEvidenceState.NotAttempted)
            ]);

        var conventional = RelationRequirementGapAnalyzer.Analyze(plan, evidence);
        var explicitPolicy = new RelationRequirementGapPolicy(
            new("tests/suppress-output-v1"),
            RelationRequirementGapPolicySource.Explicit,
            static (_, _) => new(
                RelationRequirementGapDisposition.SuppressOutput,
                RelationRequirementGapReportingKind.Suppress));
        var overridden = RelationRequirementGapAnalyzer.Analyze(plan, evidence, explicitPolicy);

        Assert.Equal(
            conventional.Gaps.Select(static gap => gap.Id),
            overridden.Gaps.Select(static gap => gap.Id));
        Assert.NotEmpty(conventional.Decisions);
        Assert.All(conventional.Decisions, decision =>
        {
            Assert.Equal(RelationRequirementGapPolicy.Conventional.Id, decision.Policy);
            Assert.Equal(RelationRequirementGapPolicySource.Convention, decision.Source);
            Assert.Equal(RelationRequirementGapDispositionKind.Unresolved, decision.Disposition.Kind);
        });
        Assert.Contains(
            conventional.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.RequirementGapResolutionNotAttempted);
        Assert.All(overridden.Decisions, decision =>
        {
            Assert.Equal(explicitPolicy.Id, decision.Policy);
            Assert.Equal(RelationRequirementGapPolicySource.Explicit, decision.Source);
            Assert.Equal(RelationRequirementGapDispositionKind.SuppressOutput, decision.Disposition.Kind);
            Assert.Equal(RelationRequirementGapReportingKind.Suppress, decision.Reporting);
        });
        Assert.DoesNotContain(
            overridden.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.RequirementGapResolutionNotAttempted);
    }

    [Fact]
    public void Analyze_NullSubstitutionIsRejectedByNonNullableOutputContract()
    {
        var (plan, evidence) = UnattemptedTraversalEvidence();
        var policy = new RelationRequirementGapPolicy(
            new("tests/null-substitution-v1"),
            RelationRequirementGapPolicySource.Explicit,
            static (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath
                    ? RelationRequirementGapDisposition.SubstituteNull
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence, policy);

        Assert.Single(result.Gaps);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.NullSubstitutionInvalid);
        Assert.All(
            result.Decisions.Where(static decision =>
                decision.Impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath),
            static decision => Assert.Equal(
                RelationRequirementGapDispositionKind.Unresolved,
                decision.Disposition.Kind));
    }

    [Fact]
    public void Analyze_NullSubstitutionIsAcceptedByNullableOutputContract()
    {
        var plan = Compile(shapeDocuments: WithNullableCustomerName());
        var (_, evidence) = UnattemptedTraversalEvidence(plan);
        var policy = new RelationRequirementGapPolicy(
            new("tests/nullable-substitution-v1"),
            RelationRequirementGapPolicySource.Explicit,
            static (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath
                    ? RelationRequirementGapDisposition.SubstituteNull
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence, policy);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.NullSubstitutionInvalid);
        Assert.All(
            result.Decisions.Where(static decision =>
                decision.Impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath),
            static decision => Assert.Equal(
                RelationRequirementGapDispositionKind.SubstituteNull,
                decision.Disposition.Kind));
    }

    [Fact]
    public void RelationRequirementGapDisposition_JsonRoundTripPreservesNullAndConcreteDefaultKinds()
    {
        RelationRequirementGapDisposition[] values =
        [
            RelationRequirementGapDisposition.Unresolved,
            RelationRequirementGapDisposition.SuppressOutput,
            RelationRequirementGapDisposition.SubstituteNull,
            RelationRequirementGapDisposition.UseDefault(ObservationValue.FromString("fallback"))
        ];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(values, options);
        var roundTripped = Assert.IsType<RelationRequirementGapDisposition[]>(
            JsonSerializer.Deserialize<RelationRequirementGapDisposition[]>(json, options));

        Assert.Collection(
            roundTripped,
            value =>
            {
                Assert.Equal(RelationRequirementGapDispositionKind.Unresolved, value.Kind);
                Assert.Null(value.Substitution);
            },
            value =>
            {
                Assert.Equal(RelationRequirementGapDispositionKind.SuppressOutput, value.Kind);
                Assert.Null(value.Substitution);
            },
            value =>
            {
                Assert.Equal(RelationRequirementGapDispositionKind.SubstituteNull, value.Kind);
                Assert.Null(value.Substitution);
            },
            value =>
            {
                Assert.Equal(RelationRequirementGapDispositionKind.SubstituteDefault, value.Kind);
                Assert.Equal(ObservationValue.FromString("fallback"), value.Substitution);
            });
    }

    [Fact]
    public void RelationRequirementGapDisposition_RejectsNullAndMissingSemanticDefaults()
    {
        Assert.Throws<ArgumentException>(() =>
            RelationRequirementGapDisposition.UseDefault(ObservationValue.Null));
        Assert.Throws<ArgumentException>(() =>
            RelationRequirementGapDisposition.UseDefault(ObservationValue.Undefined));
    }

    [Fact]
    public void Analyze_ExplicitSemanticDefaultIsAcceptedByOutputContract()
    {
        var (plan, evidence) = UnattemptedTraversalEvidence();
        var semanticDefault = ObservationValue.FromString("Unknown customer");
        var policy = new RelationRequirementGapPolicy(
            new("tests/default-substitution-v1"),
            RelationRequirementGapPolicySource.Explicit,
            (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath
                    ? RelationRequirementGapDisposition.UseDefault(semanticDefault)
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence, policy);

        Assert.Single(result.Gaps);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.DefaultSubstitutionInvalid);
        Assert.All(
            result.Decisions.Where(static decision =>
                decision.Impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath),
            decision =>
            {
                Assert.Equal(RelationRequirementGapDispositionKind.SubstituteDefault, decision.Disposition.Kind);
                Assert.Equal(semanticDefault, decision.Disposition.Substitution);
            });
    }

    [Fact]
    public void Analyze_DefaultSubstitutionContradictingOutputTypeIsRejected()
    {
        var (plan, evidence) = UnattemptedTraversalEvidence();
        var policy = new RelationRequirementGapPolicy(
            new("tests/invalid-default-substitution-v1"),
            RelationRequirementGapPolicySource.Explicit,
            static (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath
                    ? RelationRequirementGapDisposition.UseDefault(ObservationValue.FromInt64(42))
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence, policy);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.DefaultSubstitutionInvalid);
        Assert.All(
            result.Decisions.Where(static decision =>
                decision.Impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath),
            static decision => Assert.Equal(
                RelationRequirementGapDispositionKind.Unresolved,
                decision.Disposition.Kind));
    }

    [Fact]
    public void Analyze_PolicyReturningNoChoiceThrowsActionableException()
    {
        var plan = Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        var evidence = Evidence(
            plan,
            sources: [ProvidedSource(inputs, load)],
            fields:
            [
                ValueField(inputs.LoadId, load, "load-1"),
                ValueField(inputs.CustomerReference, load, "customer-1")
            ],
            traversals:
            [
                new(
                    inputs.Traversal,
                    load.Id,
                    RelationQueryTraversalEvidenceState.NotAttempted)
            ]);
        var invalidPolicy = new RelationRequirementGapPolicy(
            new("tests/no-choice-v1"),
            RelationRequirementGapPolicySource.Explicit,
            static (_, _) => null!);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelationRequirementGapAnalyzer.Analyze(plan, evidence, invalidPolicy));

        Assert.Contains(invalidPolicy.Id.Value, exception.Message, StringComparison.Ordinal);
    }

    static (CompiledRelationQueryPlan Plan, RelationQueryRuntimeEvidence Evidence) UnattemptedTraversalEvidence(
        CompiledRelationQueryPlan? compiledPlan = null)
    {
        var plan = compiledPlan ?? Compile();
        var inputs = Inputs.For(plan);
        var load = LoadOccurrence("load-1");
        return (
            plan,
            Evidence(
                plan,
                sources: [ProvidedSource(inputs, load)],
                fields:
                [
                    ValueField(inputs.LoadId, load, "load-1"),
                    ValueField(inputs.CustomerReference, load, "customer-1")
                ],
                traversals:
                [
                    new(
                        inputs.Traversal,
                        load.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted)
                ]));
    }

    static CompiledRelationQueryPlan CompileParameterProjection(
        ObservationValue? defaultValue,
        FieldPresence presence = FieldPresence.Optional)
    {
        var document = RelationQueryDocument.FromDefinition(
            new QueryDefinition(
                new("nullable-parameter-query"),
                new("NullableParameterQuery"),
                new LogicalQueryDefinition(
                    nodes:
                    [
                        new SourceQueryNode(
                            LoadCustomerRelationFixture.LoadSourceNodeId,
                            LoadCustomerRelationFixture.LoadBinding,
                            LoadCustomerRelationFixture.LoadShapeId),
                        new ProjectQueryNode(
                            LoadCustomerRelationFixture.ProjectionNodeId,
                            LoadCustomerRelationFixture.LoadSourceNodeId,
                            LoadCustomerRelationFixture.SearchBinding,
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            [
                                new ProjectionAssignment(
                                    LoadCustomerRelationFixture.SearchIdAssignmentId,
                                    LoadCustomerRelationFixture.SearchIdPath,
                                    Expr.Field(
                                        LoadCustomerRelationFixture.LoadBinding,
                                        LoadCustomerRelationFixture.LoadIdPath)),
                                new ProjectionAssignment(
                                    LoadCustomerRelationFixture.SearchCustomerNameAssignmentId,
                                    LoadCustomerRelationFixture.SearchCustomerNamePath,
                                    Expr.Param(LoadCustomerRelationFixture.CursorParameterId.Value))
                            ])
                    ],
                    parameters:
                    [
                        new QueryParameterDefinition(
                            LoadCustomerRelationFixture.CursorParameterId,
                            new ScalarTypeRef(ScalarTypeKind.String),
                            presence,
                            defaultValue)
                    ]),
                [
                    new RowsQueryResultDefinition(
                        LoadCustomerRelationFixture.RowsResultId,
                        LoadCustomerRelationFixture.ProjectionNodeId)
                ]));
        return Compile(
            document,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.RowsResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchIdPath),
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchCustomerNamePath)
                    ])
            ]),
            WithNullableCustomerName());
    }

    static CompiledRelationQueryPlan CompileMultiHopQuery()
    {
        var relatedLoadBinding = new ValueBindingId("relatedLoad");
        var relatedLoadsNode = new QueryNodeId("related-loads");
        var document = RelationQueryDocument.FromDefinition(
            new QueryDefinition(
                new("multi-hop-load-query"),
                new("MultiHopLoadQuery"),
                new LogicalQueryDefinition(
                [
                    new SourceQueryNode(
                        LoadCustomerRelationFixture.LoadSourceNodeId,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new TraverseRelationshipQueryNode(
                        LoadCustomerRelationFixture.CustomerTraversalNodeId,
                        LoadCustomerRelationFixture.LoadSourceNodeId,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadCustomerRelationshipId,
                        RelationshipTraversalDirection.Forward,
                        LoadCustomerRelationFixture.CustomerBinding,
                        JoinKind.Left,
                        QueryInputRequirement.Required),
                    new TraverseRelationshipQueryNode(
                        relatedLoadsNode,
                        LoadCustomerRelationFixture.CustomerTraversalNodeId,
                        LoadCustomerRelationFixture.CustomerBinding,
                        LoadCustomerRelationFixture.LoadCustomerRelationshipId,
                        RelationshipTraversalDirection.Inverse,
                        relatedLoadBinding,
                        JoinKind.Left,
                        QueryInputRequirement.Required),
                    new ProjectQueryNode(
                        LoadCustomerRelationFixture.ProjectionNodeId,
                        relatedLoadsNode,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new ProjectionAssignment(
                                LoadCustomerRelationFixture.SearchIdAssignmentId,
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath)),
                            new ProjectionAssignment(
                                LoadCustomerRelationFixture.SearchCustomerNameAssignmentId,
                                LoadCustomerRelationFixture.SearchCustomerNamePath,
                                Expr.Field(
                                    relatedLoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath))
                        ])
                ]),
                [
                    new RowsQueryResultDefinition(
                        LoadCustomerRelationFixture.RowsResultId,
                        LoadCustomerRelationFixture.ProjectionNodeId)
                ]));
        return Compile(
            document,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.RowsResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchIdPath),
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchCustomerNamePath)
                    ])
            ]));
    }

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument? document = null,
        RelationQueryCompilationDemand? demand = null,
        ImmutableArray<ShapeGraphDocument> shapeDocuments = default,
        RelationshipCatalogDocument? relationshipCatalogDocument = null)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document ?? LoadCustomerRelationFixture.BaselineRelationDocument,
            shapeDocuments.IsDefault ? LoadCustomerRelationFixture.ShapeGraphDocuments : shapeDocuments,
            relationshipCatalogDocument ?? LoadCustomerRelationFixture.RelationshipCatalogDocument,
            demand));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryRuntimeEvidence Evidence(
        CompiledRelationQueryPlan plan,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete,
        ImmutableArray<RelationQuerySourceEvidence> sources = default,
        ImmutableArray<RelationQueryFieldEvidence> fields = default,
        ImmutableArray<RelationQueryTraversalEvidence> traversals = default,
        ImmutableArray<RelationQueryParameterEvidence> parameters = default,
        ImmutableArray<RelationQueryConversionFailureEvidence> conversionFailures = default) =>
        new(
            new("tests/evaluation-1"),
            plan,
            completeness,
            sources,
            fields,
            traversals,
            parameters,
            capabilities:
            [
                .. plan.InputContract.Capabilities.Select(static capability =>
                    new RelationQueryCapabilityEvidence(
                        capability.Input.Id,
                        RelationQueryCapabilityEvidenceState.Available))
            ],
            conversionFailures: conversionFailures);

    static RelationQueryInputId FieldInput(
        CompiledRelationQueryPlan plan,
        ValueBindingId binding,
        QualifiedShapeId shape,
        FieldPath path) =>
        Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Binding == binding
                && input.Field.Shape == shape
                && input.Field.Path == path).Id;

    static ImmutableArray<ShapeGraphDocument> RehydrateShapeDocuments()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return
        [
            .. LoadCustomerRelationFixture.ShapeGraphDocuments.Select(document =>
                JsonSerializer.Deserialize<ShapeGraphDocument>(
                    JsonSerializer.Serialize(document, options),
                    options)
                ?? throw new InvalidOperationException("Failed to rehydrate a shape-graph document."))
        ];
    }

    static ImmutableArray<ShapeGraphDocument> WithNullableCustomerName() =>
    [
        .. LoadCustomerRelationFixture.ShapeGraphDocuments.Select(static document =>
        {
            var graph = document.Graph;
            var shapes = graph.Shapes.Select(shape =>
            {
                var id = shape.Id;
                var makeNullable = id == LoadCustomerRelationFixture.CustomerShapeLocalId
                    || id == LoadCustomerRelationFixture.LoadSearchShapeLocalId;
                if (!makeNullable)
                    return shape;

                var fieldName = id == LoadCustomerRelationFixture.CustomerShapeLocalId
                    ? LoadCustomerRelationFixture.CustomerNameFieldName
                    : LoadCustomerRelationFixture.SearchCustomerNameFieldName;
                return new Shape(
                    shape.Id,
                    [
                        .. shape.Fields.Select(field => field.Name.Value == fieldName
                            ? field with { Nullability = FieldNullability.Nullable }
                            : field)
                    ],
                    shape.Constraints,
                    shape.Annotations);
            });
            return new ShapeGraphDocument(
                document.SchemaVersion,
                new ShapeGraph(
                    graph.Id,
                    [.. shapes],
                    graph.NamedTypes,
                    graph.Diagnostics,
                    graph.Annotations),
                document.Metadata);
        })
    ];

    static ImmutableArray<ShapeGraphDocument> WithManyCustomerReferences() =>
    [
        .. LoadCustomerRelationFixture.ShapeGraphDocuments.Select(static document =>
        {
            var graph = document.Graph;
            var shapes = graph.Shapes.Select(shape =>
            {
                if (shape.Id != LoadCustomerRelationFixture.LoadShapeLocalId)
                {
                    return shape;
                }

                return new Shape(
                    shape.Id,
                    [
                        .. shape.Fields.Select(field =>
                            field.Name.Value == LoadCustomerRelationFixture.LoadCustomerIdFieldName
                                ? field with { Cardinality = FieldCardinality.Many }
                                : field)
                    ],
                    shape.Constraints,
                    shape.Annotations);
            });
            return new ShapeGraphDocument(
                document.SchemaVersion,
                new ShapeGraph(
                    graph.Id,
                    [.. shapes],
                    graph.NamedTypes,
                    graph.Diagnostics,
                    graph.Annotations),
                document.Metadata);
        })
    ];

    static RelationQueryObservationOccurrence LoadOccurrence(string id) =>
        new(
            new(id),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            id);

    static RelationQueryObservationOccurrence CustomerOccurrence(string id) =>
        new(
            new(id),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId,
            id);

    static RelationQuerySourceEvidence ProvidedSource(
        Inputs inputs,
        params RelationQueryObservationOccurrence[] occurrences) =>
        new(
            inputs.Source,
            RelationQuerySourceEvidenceState.Provided,
            [.. occurrences]);

    static RelationQueryFieldEvidence ValueField(
        RelationQueryInputId input,
        RelationQueryObservationOccurrence owner,
        string value) =>
        ValueField(input, owner, ObservationValue.FromString(value));

    static RelationQueryFieldEvidence ValueField(
        RelationQueryInputId input,
        RelationQueryObservationOccurrence owner,
        ObservationValue value) =>
        new(
            input,
            owner.Id,
            RelationQueryFieldEvidenceState.Value,
            value);

    static RelationQueryTraversalEvidence CompletedTraversal(
        Inputs inputs,
        RelationQueryObservationOccurrence from,
        ImmutableArray<RelationQueryObservationOccurrence> results) =>
        new(
            inputs.Traversal,
            from.Id,
            RelationQueryTraversalEvidenceState.Completed,
            results,
            RelationQueryEvidenceCompleteness.Complete);

    static string ResultSignature(RelationRequirementGapAnalysisResult result) =>
        string.Join(
            "\n",
            result.Gaps.Select(static gap => string.Join(
                "|",
                "gap",
                gap.Id.Value,
                gap.Occurrence?.Id.Value,
                gap.Input.Id.Value,
                gap.Cause,
                string.Join(",", gap.BlockedInputs.Select(static input => input.Value))))
            .Concat(result.Decisions.Select(static decision => string.Join(
                "|",
                "decision",
                decision.Gap.Value,
                decision.Impact.Output.Id.Value,
                decision.Impact.Effect,
                decision.Disposition.Kind,
                decision.Reporting,
                decision.Policy.Value)))
            .Concat(result.Diagnostics.Select(static diagnostic => string.Join(
                "|",
                "diagnostic",
                diagnostic.Code,
                diagnostic.Input?.Value,
                diagnostic.Occurrence?.Value,
                diagnostic.Gap?.Value,
                diagnostic.Output?.Id.Value))));

    sealed record Inputs(
        RelationQueryInputId Source,
        RelationQueryInputId LoadId,
        RelationQueryInputId CustomerReference,
        RelationQueryInputId Traversal,
        RelationQueryInputId CustomerIdentity,
        RelationQueryInputId CustomerName)
    {
        public static Inputs For(CompiledRelationQueryPlan plan)
        {
            var source = Assert.Single(plan.InputContract.Sources);
            var traversal = Assert.Single(plan.InputContract.Traversals);
            var identity = Assert.Single(plan.InputContract.Identities);
            return new(
                source.Input.Id,
                FieldInput(
                    plan,
                    LoadCustomerRelationFixture.LoadShapeId,
                    LoadCustomerRelationFixture.LoadIdPath),
                FieldInput(
                    plan,
                    LoadCustomerRelationFixture.LoadShapeId,
                    LoadCustomerRelationFixture.LoadCustomerIdPath),
                traversal.Input.Id,
                identity.Input.Id,
                FieldInput(
                    plan,
                    LoadCustomerRelationFixture.CustomerShapeId,
                    LoadCustomerRelationFixture.CustomerNamePath));
        }

        static RelationQueryInputId FieldInput(
            CompiledRelationQueryPlan plan,
            QualifiedShapeId shape,
            FieldPath path) =>
            Assert.Single(
                plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
                input => input.Field.Shape == shape && input.Field.Path == path).Id;
    }
}
