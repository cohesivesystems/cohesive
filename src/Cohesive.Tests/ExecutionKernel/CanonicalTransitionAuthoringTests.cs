using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class CanonicalTransitionAuthoringTests
{
    const string SourceReference = "tests/ari-159/review-transition";

    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract ReviewInputContract = new(new ObjectTypeRef(
    [
        new(nameof(ReviewInput.Approved), BooleanContract.Type!),
        new(nameof(ReviewInput.Decision), StringContract.Type!)
    ]));
    static readonly ValueContract ReviewObservationContract = new(new ObjectTypeRef(
    [
        new(nameof(ReviewEntity.Status), StringContract.Type!),
        new(nameof(ReviewEntity.Eligible), BooleanContract.Type!)
    ]));

    [Fact]
    public void TypedCSharpAuthoring_LowersToEquivalentDirectCanonicalIrDeterministically()
    {
        var first = CreateAuthoredTransition();
        var second = CreateAuthoredTransition();
        var directDefinition = CreateDirectDefinition();
        var directDocument = TransitionDefinitionDocuments.Create(
            Identities.Definition,
            Identities.Revision,
            directDefinition,
            Provenance());

        Assert.True(first.IsValid, Format(first.Validation));
        Assert.Equal(ReviewInputContract, first.Definition.Input);
        Assert.Equal(ReviewObservationContract, first.Definition.Observation);
        Assert.Equal(StringContract, first.Definition.Outcome);
        Assert.Equal(directDefinition, first.Definition);
        Assert.Equal(directDefinition.GetHashCode(), first.Definition.GetHashCode());
        Assert.Equal(first.Definition, second.Definition);
        Assert.Equal(first.Document.Metadata.Fingerprint, second.Document.Metadata.Fingerprint);
        Assert.Equal(first.Document.Metadata.Fingerprint, directDocument.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(directDocument),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(first.Document));
        Assert.Equal(first.Document.Metadata.SourceMap, second.Document.Metadata.SourceMap);
    }

    [Fact]
    public void NonSemanticSourceAttribution_DoesNotChangeCompilationOrReferenceMeaning()
    {
        var first = CreateAuthoredTransition("tests/ari-205/producer-a");
        var second = CreateAuthoredTransition("tests/ari-205/producer-b");

        Assert.NotEqual(first.Document.Metadata.Provenance, second.Document.Metadata.Provenance);
        Assert.NotEqual(first.Document.Metadata.SourceMap, second.Document.Metadata.SourceMap);
        Assert.Equal(first.Document.Metadata.Fingerprint, second.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(first.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(second.Document));

        var firstCompilation = first.Compile();
        var secondCompilation = second.Compile();
        Assert.True(firstCompilation.IsSuccessful, Format(firstCompilation.Validation));
        Assert.True(secondCompilation.IsSuccessful, Format(secondCompilation.Validation));
        Assert.Equivalent(firstCompilation.Validation, secondCompilation.Validation, strict: true);
        var firstPlan = Assert.IsType<CompiledTransitionPlan>(firstCompilation.Plan);
        var secondPlan = Assert.IsType<CompiledTransitionPlan>(secondCompilation.Plan);
        Assert.Equal(firstPlan.Definition, secondPlan.Definition);
        Assert.Equivalent(firstPlan.DerivedFields, secondPlan.DerivedFields, strict: true);
        Assert.Equivalent(firstPlan.MachineEdges, secondPlan.MachineEdges, strict: true);

        var input = Object(
            firstPlan.Definition.Input,
            (nameof(ReviewInput.Approved), ObservationValue.FromBool(true)),
            (nameof(ReviewInput.Decision), ObservationValue.FromString("approve")));
        var observation = new[]
        {
            Entry(firstPlan, nameof(ReviewEntity.Status), ObservationValue.FromString("pending")),
            Entry(firstPlan, nameof(ReviewEntity.Eligible), ObservationValue.FromBool(true))
        };
        var firstDecision = TransitionReferenceInterpreter.DecideSparse(
            firstPlan,
            new("ari-205/source-attribution"),
            input,
            observation);
        var secondDecision = TransitionReferenceInterpreter.DecideSparse(
            secondPlan,
            new("ari-205/source-attribution"),
            input,
            observation);

        Assert.Equivalent(firstDecision, secondDecision, strict: true);
    }

    [Fact]
    public void ConventionDerivedBodies_AreDeterministicInspectableAndCollisionSafe()
    {
        var authored = CreateAuthoredTransition();
        var choice = Assert.IsType<ChoiceTransitionNode>(authored.Definition.Body.Steps[1]);
        var approve = Assert.Single(choice.Cases);
        var choiceFallback = Assert.IsType<TransitionFallback>(choice.Fallback);
        var match = Assert.IsType<MatchTransitionNode>(Assert.Single(choiceFallback.Body.Steps));
        var hold = Assert.Single(match.Cases);
        var matchFallback = Assert.IsType<TransitionFallback>(match.Fallback);
        string[] conventionalBodies =
        [
            approve.Body.Id.Value,
            choiceFallback.Body.Id.Value,
            hold.Body.Id.Value,
            matchFallback.Body.Id.Value
        ];

        Assert.Equal(
            [
                TransitionAuthoringIdentities.BodyFor(Identities.ApproveCase).Value,
                TransitionAuthoringIdentities.BodyFor(Identities.ChoiceFallback).Value,
                TransitionAuthoringIdentities.BodyFor(Identities.HoldCase).Value,
                TransitionAuthoringIdentities.BodyFor(Identities.MatchFallback).Value
            ],
            conventionalBodies);
        Assert.Equal(conventionalBodies.Length, conventionalBodies.Distinct(StringComparer.Ordinal).Count());
        string[] explicitConstructs =
        [
            Identities.ApproveCase.Value,
            Identities.ChoiceFallback.Value,
            Identities.HoldCase.Value,
            Identities.MatchFallback.Value,
            Identities.DecisionChoice.Value,
            Identities.DecisionMatch.Value
        ];
        Assert.DoesNotContain(conventionalBodies, explicitConstructs.Contains);
    }

    [Fact]
    [Trait("Category", "ExecutionKernelExample")]
    public void AuthoredDocument_StrictRoundTripCompilesAndReferenceInterprets()
    {
        var authored = CreateAuthoredTransition();
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(authored.Document);

        var validation = TransitionDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.NotNull(restoredDocument);
        Assert.NotNull(restoredDefinition);
        Assert.Equal(authored.Document, restoredDocument);
        Assert.Equal(authored.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));
        Assert.Equal(authored.Document.Metadata.Provenance, restoredDocument.Metadata.Provenance);
        Assert.Equal(authored.Document.Metadata.SourceMap, restoredDocument.Metadata.SourceMap);
        Assert.Equal(authored.Document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);

        var compilation = TransitionStaticCompiler.Compile(restoredDocument);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("ari-159/approved"),
            Object(
                restoredDefinition.Input,
                (nameof(ReviewInput.Approved), ObservationValue.FromBool(true)),
                (nameof(ReviewInput.Decision), ObservationValue.FromString("approve"))),
            [
                Entry(plan, nameof(ReviewEntity.Status), ObservationValue.FromString("pending")),
                Entry(plan, nameof(ReviewEntity.Eligible), ObservationValue.FromBool(true))
            ]);

        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Equal("approved", decision.Outcome?.Value?.String);
        var patch = Assert.Single(decision.Patch);
        Assert.Equal(nameof(ReviewEntity.Status), patch.Path.ToString());
        Assert.Equal("approved", patch.After.Value?.String);
        var emission = Assert.Single(decision.Emissions);
        Assert.Equal(EmissionContract(), emission.Contract);
        Assert.Equal("approve", emission.Payload.Value?.String);
    }

    [Fact]
    public void SubjectCreation_IsCanonicalInputDerivedAndRequiresAbsentExecution()
    {
        var authored = TransitionAuthoring.Create<ReviewEntity, ReviewCreationInput, string>(
            ReviewEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(new("transition/review/create"), new("review/create/body")),
            transition => transition
                .CreatesFrom(
                    new("review/create/initialize"),
                    input => new ReviewInitialState(input.Status, input.Eligible))
                .Invariant(
                    new("review/create/invariant/status"),
                    entity => entity.Status != "")
                .Return(
                    new("review/create/outcome"),
                    TransitionOutcomeDisposition.Applied,
                    "created"));

        Assert.True(authored.IsValid, Format(authored.Validation));
        var creation = Assert.IsType<TransitionSubjectCreation>(authored.Definition.SubjectCreation);
        Assert.Equal("review/create/initialize", creation.Id.Value);
        Assert.Contains(
            authored.Document.Metadata.SourceMap.Entries,
            static entry => entry.SemanticPath?.ToString() == "/subjectCreation");

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(authored.Document);
        var roundTrip = TransitionDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);
        Assert.True(roundTrip.IsValid, Format(roundTrip));
        Assert.Equal(authored.Document, restoredDocument);
        Assert.Equal(authored.Definition, restoredDefinition);

        var compilation = TransitionStaticCompiler.Compile(restoredDocument!);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        Assert.Single(plan.Analysis.GetRequirements<TransitionSubjectCreationRequirement>());
        Assert.Empty(plan.Analysis.GetRequirements<TransitionObservationRequirement>());
        Assert.Contains(
            plan.Analysis.ExpressionSites,
            static site => site.Kind == TransitionExpressionSiteKind.SubjectInitializer);
        var input = Object(
            authored.Definition.Input,
            (nameof(ReviewCreationInput.Status), ObservationValue.FromString("pending")),
            (nameof(ReviewCreationInput.Eligible), ObservationValue.FromBool(true)));

        var created = TransitionReferenceInterpreter.DecideCreation(
            plan,
            new("review/create/activation"),
            input);

        Assert.Equal(TransitionDecisionKind.Applied, created.Kind);
        Assert.True(created.GuaranteeDemands.CommitRequired);
        Assert.Empty(created.GuaranteeDemands.ConcurrencyObservations);
        Assert.Equal("created", created.Outcome?.Value?.String);
        var initial = Assert.IsType<PortableValue>(created.Evidence.InitialObservation);
        var initialValue = Assert.IsType<ObservationValue>(initial.Value);
        Assert.Equal("pending", initialValue.Fields![nameof(ReviewEntity.Status)].GetString());
        Assert.True(initialValue.Fields[nameof(ReviewEntity.Eligible)].GetBoolean());
        Assert.Equal(
            TransitionTraceEventKind.SubjectInitialized,
            created.Evidence.Trace[0].Kind);

        var againstExisting = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("review/create/existing"),
            input,
            initial);
        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, againstExisting.Kind);
        Assert.Contains(
            againstExisting.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.SubjectStateInvalid);
    }

    [Fact]
    public void SubjectCreation_NormalizesTypedCollectionOccurrenceAndPreservesExactCardinality()
    {
        var authored = TransitionAuthoring.Create<CollectionReviewEntity, CollectionReviewCreationInput, string>(
            CollectionReviewEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(new("transition/review/create-collection"), new("review/create-collection/body")),
            transition => transition
                .CreatesFrom(
                    new("review/create-collection/initialize"),
                    input => new CollectionReviewInitialState(input.Status, input.Tags))
                .Return(
                    new("review/create-collection/outcome"),
                    TransitionOutcomeDisposition.Applied,
                    "created"));

        Assert.True(authored.IsValid, Format(authored.Validation));
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(authored.Document);
        var roundTrip = TransitionDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);
        Assert.True(roundTrip.IsValid, Format(roundTrip));
        Assert.Equal(authored.Document, restoredDocument);
        Assert.Equal(authored.Definition, restoredDefinition);

        var compilation = TransitionStaticCompiler.Compile(restoredDocument!);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var created = TransitionReferenceInterpreter.DecideCreation(
            plan,
            new("review/create-collection/activation"),
            Object(
                authored.Definition.Input,
                (nameof(CollectionReviewCreationInput.Status), ObservationValue.FromString("pending")),
                (nameof(CollectionReviewCreationInput.Tags), ObservationValue.FromArray([
                    ObservationValue.FromString("semantic"),
                    ObservationValue.FromString("training")
                ]))));

        Assert.Equal(TransitionDecisionKind.Applied, created.Kind);
        var initial = Assert.IsType<PortableValue>(created.Evidence.InitialObservation);
        var fields = Assert.IsType<ObservationValue>(initial.Value).Fields!;
        Assert.Equal(
            ["semantic", "training"],
            fields[nameof(CollectionReviewEntity.Tags)].EnumerateArray().Select(static value => value.GetString()));

        var exception = Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<CollectionReviewEntity, CollectionReviewCreationInput, string>(
                CollectionReviewEntity.Instance.Definition.Shape,
                AuxiliaryMetadata(new("transition/review/create-wrong-cardinality"), new("review/create-wrong-cardinality/body")),
                transition => transition
                    .CreatesFrom(
                        new("review/create-wrong-cardinality/initialize"),
                        input => new WrongCardinalityReviewInitialState(input.Status, input.Tags[0]))
                    .Return(
                        new("review/create-wrong-cardinality/outcome"),
                        TransitionOutcomeDisposition.Applied,
                        "created")));
        Assert.Contains("project exactly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubjectCreation_PreservesDeclaredPortableJsonDocumentAsTypedState()
    {
        var authored = TransitionAuthoring.Create<PortableDocumentEntity, PortableDocumentCreationInput, string>(
            PortableDocumentEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(
                new("transition/portable-document/create"),
                new("portable-document/create/body")),
            transition => transition
                .CreatesFrom(
                    new("portable-document/create/initialize"),
                    input => new PortableDocumentInitialState(input.Id, input.Document))
                .Return(
                    new("portable-document/create/outcome"),
                    TransitionOutcomeDisposition.Applied,
                    "created"));

        Assert.True(authored.IsValid, Format(authored.Validation));
        var compilation = TransitionStaticCompiler.Compile(authored.Document);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var document = new PortableTransitionDocument(
            new Dictionary<string, string> { ["protocol"] = "FIX" });
        var decision = TransitionReferenceInterpreter.DecideCreation(
            plan,
            new("portable-document/create/activation"),
            Object(
                authored.Definition.Input,
                (nameof(PortableDocumentCreationInput.Id), ObservationValue.FromString("document/1")),
                (nameof(PortableDocumentCreationInput.Document), ObservationValue.FromObject(document))));

        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        var initial = Assert.IsType<PortableValue>(decision.Evidence.InitialObservation);
        var observed = Assert.IsType<ObservationValue>(initial.Value)
            .GetProperty(nameof(PortableDocumentEntity.Document));
        Assert.Equal("FIX", observed.GetProperty(nameof(PortableTransitionDocument.Content))
            .GetProperty("protocol")
            .GetString());
    }

    [Fact]
    public void SubjectCreation_RejectsProjectionThatDoesNotMatchAuthoritativeObservation()
    {
        var exception = Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<ReviewEntity, ReviewCreationInput, string>(
                ReviewEntity.Instance.Definition.Shape,
                AuxiliaryMetadata(new("transition/review/create-invalid"), new("review/create-invalid/body")),
                transition => transition
                    .CreatesFrom(
                        new("review/create-invalid/initialize"),
                        input => new IncompleteReviewInitialState(input.Status))
                    .Return(
                        new("review/create-invalid/outcome"),
                        TransitionOutcomeDisposition.Applied,
                        "created")));

        Assert.Contains("project exactly the entity observation fields", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubjectCreation_CompilerRejectsInitializerObservationAccess()
    {
        var observation = ValueContract.FromShape(ReviewEntity.Instance.Definition.Shape);
        var definition = new TransitionDefinition(
            new(new ObjectTypeRef(
            [
                new(nameof(ReviewCreationInput.Status), new ScalarTypeRef(ScalarTypeKind.String)),
                new(nameof(ReviewCreationInput.Eligible), new ScalarTypeRef(ScalarTypeKind.Bool))
            ])),
            observation,
            StringContract,
            [],
            new(
                new("review/create-observation/body"),
                [
                    new OutcomeTransitionNode(
                        new("review/create-observation/outcome"),
                        TransitionOutcomeDisposition.Applied,
                        Expr.Const("created"))
                ]),
            subjectCreation: new(
                new("review/create-observation/initialize"),
                Expr.BoundValue(TransitionBindingIds.Observation)));
        var document = TransitionDefinitionDocuments.Create(
            new("transition/review/create-observation"),
            new("revision/1"),
            definition,
            Provenance());

        var compilation = TransitionStaticCompiler.Compile(document);

        Assert.False(compilation.IsSuccessful);
        Assert.Contains(
            compilation.Validation.Diagnostics,
            static diagnostic => diagnostic.Location
                == "/definition/subjectCreation/initialObservation");
    }

    [Fact]
    public void TypedAuthoring_SourceMapCoversEveryRepresentativeConstructAndMapsValidationDiagnostics()
    {
        var authored = CreateAuthoredTransition();
        var paths = authored.Document.Metadata.SourceMap.Entries
            .Select(static entry => entry.SemanticPath!.Value.ToString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(20, paths.Count);
        Assert.Contains("/preconditions/0", paths);
        Assert.Contains("/body", paths);
        Assert.Contains("/body/steps/0", paths);
        Assert.Contains("/body/steps/1/cases/0", paths);
        Assert.Contains("/body/steps/1/cases/0/body/steps/1", paths);
        Assert.Contains("/body/steps/1/fallback/body/steps/0/cases/0/body/steps/0", paths);
        Assert.Contains("/body/steps/1/fallback/body/steps/0/fallback/body/steps/0", paths);
        Assert.Contains("/invariants/0", paths);
        Assert.All(
            authored.Document.Metadata.SourceMap.Entries,
            entry => Assert.StartsWith(SourceReference, entry.Reference, StringComparison.Ordinal));

        var invalid = CreateInvalidTransitionWithDuplicateIdentity();
        var duplicate = Assert.Single(
            invalid.Validation.Diagnostics,
            diagnostic => diagnostic.Code == TransitionDefinitionDiagnosticCodes.NodeIdentityDuplicate);

        Assert.False(invalid.IsValid);
        Assert.Equal("/definition/body/steps/1/id", duplicate.Location);
        Assert.NotNull(duplicate.Evidence);
        Assert.NotEmpty(duplicate.Evidence.SourceReferences);
        Assert.All(
            duplicate.Evidence.SourceReferences,
            reference => Assert.StartsWith(SourceReference, reference, StringComparison.Ordinal));
        Assert.Contains(
            duplicate.Evidence.SourceReferences,
            static reference => reference.Contains(nameof(CreateInvalidTransitionWithDuplicateIdentity), StringComparison.Ordinal));

        var persisted = Encoding.UTF8.GetString(
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(invalid.Document));
        var roundTripValidation = TransitionDefinitionDocuments.TryDeserialize(
            persisted,
            out var roundTripDocument,
            out var roundTripDefinition);
        var roundTripDuplicate = Assert.Single(
            roundTripValidation.Diagnostics,
            diagnostic => diagnostic.Code == TransitionDefinitionDiagnosticCodes.NodeIdentityDuplicate);

        Assert.NotNull(roundTripDocument);
        Assert.Null(roundTripDefinition);
        Assert.NotEmpty(roundTripDuplicate.Evidence?.SourceReferences ?? []);
        Assert.All(
            roundTripDuplicate.Evidence!.SourceReferences,
            reference => Assert.StartsWith(SourceReference, reference, StringComparison.Ordinal));
    }

    [Fact]
    public void TypedHandle_ContainsCanonicalDocumentWithoutDelegateOrLegacyDefinitionAuthority()
    {
        var authored = CreateAuthoredTransition();
        var handleType = authored.GetType();
        var fields = handleType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var properties = handleType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, static field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(properties, static property => typeof(Delegate).IsAssignableFrom(property.PropertyType));
        Assert.Null(handleType.Assembly.GetType("Cohesive.Transitions.Model.TransitionDefinition"));
        Assert.Contains(properties, static property => property.Name == nameof(authored.Document)
            && property.PropertyType == typeof(ExecutionDefinitionDocument));
        Assert.Contains(properties, static property => property.Name == nameof(authored.Definition)
            && property.PropertyType == typeof(CanonicalTransitionDefinition));
    }

    [Fact]
    public void TypedAuthoring_RejectsCapturedRuntimeStateOutsidePortableExpressionSubset()
    {
        var captured = "approved";

        var exception = Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<ReviewEntity, ReviewInput, string>(
                ReviewEntity.Instance.Definition.Shape,
                Metadata(),
                transition => transition
                    .Set(
                        Identities.SetApproved,
                        entity => entity.Status,
                        (_, _) => captured)
                    .Return(Identities.ApprovedOutcome, TransitionOutcomeDisposition.Applied, "approved")));

        Assert.Contains("not portable canonical Transition semantics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedAuthoring_RejectsCapturedMembershipCollections()
    {
        var captured = new[] { "pending", "held" };

        var exception = Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<ReviewEntity, ReviewInput, string>(
                ReviewEntity.Instance.Definition.Shape,
                AuxiliaryMetadata(Identities.MembershipDefinition, Identities.MembershipBody),
                transition => transition
                    .Requires(
                        Identities.MembershipAdmission,
                        (entity, _) => entity.Status.IsOneOf(captured),
                        (_, _) => "rejected")
                    .Return(Identities.MembershipOutcome, TransitionOutcomeDisposition.NoChange, "accepted")));

        Assert.Contains("Captured membership value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionContains_LowersToPortableMembershipAndInterpretsAgainstTheCompleteCollection()
    {
        var authored = TransitionAuthoring.Create<ObservedIdentityEntity, EvaluationIdentityInput, string>(
            ObservedIdentityEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(Identities.CollectionContainsDefinition, Identities.CollectionContainsBody),
            transition => transition
                .Requires(
                    Identities.CollectionContainsAdmission,
                    (entity, input) => entity.ObservedIds.Contains(input.EvaluationId),
                    (_, _) => "missing")
                .Return(
                    Identities.CollectionContainsOutcome,
                    TransitionOutcomeDisposition.NoChange,
                    "observed"));

        Assert.True(authored.IsValid, Format(authored.Validation));
        var contains = Assert.IsType<CallExpr>(Assert.Single(authored.Definition.Preconditions).Predicate);
        Assert.Equal(ExprFunctionNames.Contains, contains.Function);
        Assert.Equal(
            nameof(ObservedIdentityEntity.ObservedIds),
            Assert.IsType<FieldExpr>(contains.Arguments[0]).Path.ToString());
        Assert.Equal(
            nameof(EvaluationIdentityInput.EvaluationId),
            Assert.IsType<ParameterExpr>(contains.Arguments[1]).Parameter);

        var compilation = authored.Compile();
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("ari-181/collection-contains"),
            Object(
                authored.Definition.Input,
                (nameof(EvaluationIdentityInput.EvaluationId), ObservationValue.FromString("evaluation/first"))),
            [
                Entry(
                    plan,
                    nameof(ObservedIdentityEntity.ObservedIds),
                    ObservationValue.FromArray([
                        ObservationValue.FromString("evaluation/first"),
                        ObservationValue.FromString("evaluation/intervening")
                    ]))
            ]);

        Assert.Equal(TransitionDecisionKind.NoChange, decision.Kind);
        Assert.Equal("observed", decision.Outcome?.Value?.String);
    }

    [Fact]
    public void UnrelatedInstanceContains_IsNotReinterpretedAsPortableCollectionMembership()
    {
        var exception = Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<ReviewEntity, UnrelatedMembershipInput, string>(
                ReviewEntity.Instance.Definition.Shape,
                AuxiliaryMetadata(Identities.UnrelatedContainsDefinition, Identities.UnrelatedContainsBody),
                transition => transition
                    .Requires(
                        Identities.UnrelatedContainsAdmission,
                        (_, input) => input.Membership.Contains(input.Candidate),
                        (_, _) => "missing")
                    .Return(
                        Identities.UnrelatedContainsOutcome,
                        TransitionOutcomeDisposition.NoChange,
                        "observed")));

        Assert.Contains("Unsupported method call", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionCount_LowersToCanonicalInt64AndInterpretsInvariant()
    {
        var authored = TransitionAuthoring.Create<AlignedLedgerEntity, LedgerCountInput, string>(
            AlignedLedgerEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(Identities.CollectionCountDefinition, Identities.CollectionCountBody),
            transition =>
            {
                transition.Requires(
                    Identities.CollectionCountAdmission,
                    (entity, input) => input.ExpectedIds.Count() == entity.ObservedIds.Count,
                    (_, _) => "unexpected-count");
                transition.Return(
                    Identities.CollectionCountOutcome,
                    TransitionOutcomeDisposition.NoChange,
                    "aligned");
                transition.Invariant(
                    Identities.CollectionCountInvariant,
                    entity => entity.ObservedIds.Count == entity.Evaluations.Count);
            });

        Assert.True(authored.IsValid, Format(authored.Validation));
        var admissionEquality = Assert.IsType<BinaryExpr>(Assert.Single(authored.Definition.Preconditions).Predicate);
        var enumerableCount = Assert.IsType<CallExpr>(admissionEquality.Left);
        Assert.Equal(ExprFunctionNames.Count, enumerableCount.Function);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Int64), enumerableCount.ReturnType);
        var equality = Assert.IsType<BinaryExpr>(Assert.Single(authored.Definition.Invariants).Predicate);
        var left = Assert.IsType<CallExpr>(equality.Left);
        var right = Assert.IsType<CallExpr>(equality.Right);
        Assert.Equal(ExprFunctionNames.Count, left.Function);
        Assert.Equal(ExprFunctionNames.Count, right.Function);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Int64), left.ReturnType);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Int64), right.ReturnType);

        var compilation = authored.Compile();
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var aligned = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("ari-181/collection-count/aligned"),
            Object(
                authored.Definition.Input,
                (nameof(LedgerCountInput.ExpectedIds), ObservationValue.FromArray([
                    ObservationValue.FromString("evaluation/1")
                ]))),
            [
                Entry(
                    plan,
                    nameof(AlignedLedgerEntity.ObservedIds),
                    ObservationValue.FromArray([ObservationValue.FromString("evaluation/1")])),
                Entry(
                    plan,
                    nameof(AlignedLedgerEntity.Evaluations),
                    ObservationValue.FromArray([ObservationValue.FromString("evaluation/1")]))
            ]);
        var misaligned = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("ari-181/collection-count/misaligned"),
            Object(
                authored.Definition.Input,
                (nameof(LedgerCountInput.ExpectedIds), ObservationValue.FromArray([
                    ObservationValue.FromString("evaluation/1")
                ]))),
            [
                Entry(
                    plan,
                    nameof(AlignedLedgerEntity.ObservedIds),
                    ObservationValue.FromArray([ObservationValue.FromString("evaluation/1")])),
                Entry(
                    plan,
                    nameof(AlignedLedgerEntity.Evaluations),
                    ObservationValue.FromArray([]))
            ]);

        Assert.Equal(TransitionDecisionKind.NoChange, aligned.Kind);
        Assert.Equal(TransitionDecisionKind.InvalidDefinition, misaligned.Kind);
        Assert.Contains(
            misaligned.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.InvariantViolated);
    }

    [Fact]
    public void MatchThroughLocal_PreservesOptionalNullableAndSerializedInputContract()
    {
        var authored = TransitionAuthoring.Create<ReviewEntity, OptionalDecisionInput, string>(
            ReviewEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(Identities.OptionalDefinition, Identities.OptionalBody),
            transition =>
            {
                var decision = transition.Let(
                    Identities.OptionalLocal,
                    Identities.OptionalBinding,
                    (_, input) => input.Decision);
                transition.Match(
                    Identities.OptionalMatch,
                    decision,
                    match => match
                        .Absent(
                            Identities.AbsentCase,
                            branch => branch.Return(
                                Identities.AbsentOutcome,
                                TransitionOutcomeDisposition.NoChange,
                                "absent"))
                        .Case(
                            Identities.NullCase,
                            pattern: null,
                            branch => branch.Return(
                                Identities.NullOutcome,
                                TransitionOutcomeDisposition.NoChange,
                                "null"))
                        .Fallback(
                            Identities.OptionalFallback,
                            branch => branch.Return(
                                Identities.PresentOutcome,
                                TransitionOutcomeDisposition.NoChange,
                                "present")));
            });

        Assert.True(authored.IsValid, Format(authored.Validation));
        var local = Assert.IsType<LetTransitionNode>(authored.Definition.Body.Steps[0]);
        var parameter = Assert.IsType<ParameterExpr>(local.Value);
        var match = Assert.IsType<MatchTransitionNode>(authored.Definition.Body.Steps[1]);
        Assert.Equal("decision", parameter.Parameter);
        Assert.Equal(FieldPresence.Optional, local.Contract.Presence);
        Assert.Equal(FieldNullability.Nullable, local.Contract.Nullability);
        Assert.Equal(local.Contract, match.Contract);
        Assert.Equal(PortableValueState.Absent, match.Cases[0].Pattern.State);
        Assert.Equal(PortableValueState.Null, match.Cases[1].Pattern.State);
    }

    [Fact]
    public void TypedInput_LowersWholeBindingAndObjectMembersInCanonicalOrder()
    {
        var echo = TransitionAuthoring.Create<ReviewEntity, ReorderedValue, ReorderedValue>(
            ReviewEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(Identities.EchoDefinition, Identities.EchoBody),
            transition => transition.Return(
                Identities.EchoOutcome,
                TransitionOutcomeDisposition.NoChange,
                (_, input) => input));
        var projected = TransitionAuthoring.Create<ReviewEntity, ReorderedValue, ReorderedValue>(
            ReviewEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(Identities.ProjectedDefinition, Identities.ProjectedBody),
            transition => transition.Return(
                Identities.ProjectedOutcome,
                TransitionOutcomeDisposition.NoChange,
                (_, input) => new ReorderedValue(input.Z, input.A)));

        Assert.True(echo.IsValid, Format(echo.Validation));
        Assert.True(projected.IsValid, Format(projected.Validation));
        Assert.Equal(
            TransitionBindingIds.Input,
            Assert.IsType<BindingExpr>(Assert.IsType<OutcomeTransitionNode>(echo.Definition.Body.Steps[0]).Value).Binding);
        var objectCall = Assert.IsType<CallExpr>(
            Assert.IsType<OutcomeTransitionNode>(projected.Definition.Body.Steps[0]).Value);
        Assert.Equal(ExprFunctionNames.Object, objectCall.Function);
        Assert.Equal("A", Assert.IsType<ConstantExpr>(objectCall.Arguments[0]).Value.String);
        Assert.Equal("Z", Assert.IsType<ConstantExpr>(objectCall.Arguments[2]).Value.String);
        Assert.Equal(
            ["A", "Z"],
            Assert.IsType<ObjectTypeRef>(projected.Definition.Outcome.Type).Fields
                .Select(static field => field.Name)
                .ToArray());
    }

    [Fact]
    public void OwnedChildIdentity_UsesTheCanonicalSerializedMemberName()
    {
        var authored = TransitionAuthoring.Create<OwnedReviewEntity, OwnedChildInput, string>(
            OwnedReviewEntity.Instance.Definition.Shape,
            AuxiliaryMetadata(Identities.OwnedDefinition, Identities.OwnedBody),
            transition => transition
                .UpsertOwnedChild(
                    Identities.OwnedUpsert,
                    entity => entity.Children,
                    child => child.ChildId,
                    (_, input) => input.ChildId,
                    (_, input) => new OwnedChild(input.ChildId, input.Name))
                .Return(Identities.OwnedOutcome, TransitionOutcomeDisposition.Applied, "upserted"));

        Assert.True(authored.IsValid, Format(authored.Validation));
        var update = Assert.IsType<UpdateTransitionNode>(authored.Definition.Body.Steps[0]);
        var operation = Assert.IsType<UpsertOwnedChildTransitionPatch>(update.Operation);
        Assert.Equal("child_id", operation.IdentityPath.ToString());
        var children = Assert.Single(
            Assert.IsType<ObjectTypeRef>(authored.Definition.Observation.Type).Fields,
            static field => field.Name == nameof(OwnedReviewEntity.Children));
        Assert.Contains(
            Assert.IsType<ObjectTypeRef>(children.Type).Fields,
            static field => field.Name == "child_id");
    }

    static Transition<ReviewEntity, ReviewInput, string> CreateAuthoredTransition(
        string sourceReference = SourceReference) =>
        TransitionAuthoring.Create<ReviewEntity, ReviewInput, string>(
            ReviewEntity.Instance.Definition.Shape,
            Metadata(sourceReference),
            transition =>
            {
                transition.Requires(
                    Identities.PendingAdmission,
                    (entity, _) => entity.Status == "pending",
                    (_, _) => "notPending");
                var decision = transition.Let(
                    Identities.DecisionLocal,
                    Identities.DecisionBinding,
                    (_, input) => input.Decision);
                transition.Choose(
                    Identities.DecisionChoice,
                    choice => choice
                        .Case(
                            id: Identities.ApproveCase,
                            predicate: (entity, input) => entity.Eligible && input.Approved,
                            branch => branch
                                .Set(Identities.SetApproved, entity => entity.Status, "approved")
                                .Emit(
                                    Identities.ApprovedEmission,
                                    EmissionContract(),
                                    (_, input) => input.Decision)
                                .Return(
                                    Identities.ApprovedOutcome,
                                    TransitionOutcomeDisposition.Applied,
                                    "approved"))
                        .Fallback(
                            Identities.ChoiceFallback,
                            branch => branch.Match(
                                Identities.DecisionMatch,
                                decision,
                                match => match
                                    .Case(
                                        Identities.HoldCase,
                                        "hold",
                                        matchBranch => matchBranch
                                            .Set(Identities.SetHeld, entity => entity.Status, "held")
                                            .Return(
                                                Identities.HeldOutcome,
                                                TransitionOutcomeDisposition.Applied,
                                                "held"))
                                    .Fallback(
                                        Identities.MatchFallback,
                                        matchBranch => matchBranch.Return(
                                            Identities.IgnoredOutcome,
                                            TransitionOutcomeDisposition.NoChange,
                                            "ignored")))));
                transition.Invariant(
                    Identities.ValidStatusInvariant,
                    entity => entity.Status != "invalid");
            });

    static Transition<ReviewEntity, ReviewInput, string> CreateInvalidTransitionWithDuplicateIdentity() =>
        TransitionAuthoring.Create<ReviewEntity, ReviewInput, string>(
            ReviewEntity.Instance.Definition.Shape,
            Metadata(),
            transition => transition
                .Return(Identities.DuplicateOutcome, TransitionOutcomeDisposition.NoChange, "first")
                .Return(Identities.DuplicateOutcome, TransitionOutcomeDisposition.NoChange, "second"));

    static CanonicalTransitionDefinition CreateDirectDefinition()
    {
        var approvedBody = new SequenceTransitionNode(
            TransitionAuthoringIdentities.BodyFor(Identities.ApproveCase),
            [
                new UpdateTransitionNode(
                    Identities.SetApproved,
                    FieldPath.FromField(nameof(ReviewEntity.Status)),
                    new SetTransitionPatch(Expr.Const("approved"))),
                new EmitTransitionNode(
                    Identities.ApprovedEmission,
                    EmissionContract(),
                    Expr.Param(nameof(ReviewInput.Decision))),
                new OutcomeTransitionNode(
                    Identities.ApprovedOutcome,
                    TransitionOutcomeDisposition.Applied,
                    Expr.Const("approved"))
            ]);
        var holdBody = new SequenceTransitionNode(
            TransitionAuthoringIdentities.BodyFor(Identities.HoldCase),
            [
                new UpdateTransitionNode(
                    Identities.SetHeld,
                    FieldPath.FromField(nameof(ReviewEntity.Status)),
                    new SetTransitionPatch(Expr.Const("held"))),
                new OutcomeTransitionNode(
                    Identities.HeldOutcome,
                    TransitionOutcomeDisposition.Applied,
                    Expr.Const("held"))
            ]);
        var ignoredBody = new SequenceTransitionNode(
            TransitionAuthoringIdentities.BodyFor(Identities.MatchFallback),
            [
                new OutcomeTransitionNode(
                    Identities.IgnoredOutcome,
                    TransitionOutcomeDisposition.NoChange,
                    Expr.Const("ignored"))
            ]);
        var decisionMatch = new MatchTransitionNode(
            Identities.DecisionMatch,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            Expr.BoundValue(Identities.DecisionBinding),
            StringContract,
            [
                new(
                    Identities.HoldCase,
                    PortableValue.Concrete(StringContract, ObservationValue.FromString("hold")),
                    holdBody)
            ],
            new(Identities.MatchFallback, ignoredBody));
        var choiceFallbackBody = new SequenceTransitionNode(
            TransitionAuthoringIdentities.BodyFor(Identities.ChoiceFallback),
            [decisionMatch]);
        var decisionChoice = new ChoiceTransitionNode(
            Identities.DecisionChoice,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            [
                new(
                    Identities.ApproveCase,
                    Expr.And(
                        Expr.Field(nameof(ReviewEntity.Eligible)),
                        Expr.Param(nameof(ReviewInput.Approved))),
                    approvedBody)
            ],
            new(Identities.ChoiceFallback, choiceFallbackBody));

        return new(
            ReviewInputContract,
            ReviewObservationContract,
            StringContract,
            [
                new(
                    Identities.PendingAdmission,
                    Expr.Eq(Expr.Field(nameof(ReviewEntity.Status)), Expr.Const("pending")),
                    Expr.Const("notPending"))
            ],
            new(
                Identities.Body,
                [
                    new LetTransitionNode(
                        Identities.DecisionLocal,
                        Identities.DecisionBinding,
                        StringContract,
                        Expr.Param(nameof(ReviewInput.Decision))),
                    decisionChoice
                ]),
            [
                new(
                    Identities.ValidStatusInvariant,
                    Expr.Ne(Expr.Field(nameof(ReviewEntity.Status)), Expr.Const("invalid")))
            ]);
    }

    static TransitionAuthoringMetadata Metadata(string sourceReference = SourceReference) => new(
        Identities.Definition,
        Identities.Revision,
        Identities.Body,
        Provenance(sourceReference),
        displayName: "Review decision",
        description: "Representative ARI-159 C# authoring fixture.");

    static TransitionAuthoringMetadata AuxiliaryMetadata(
        ExecutionDefinitionId definition,
        ExecutionNodeId body) => new(
        definition,
        Identities.Revision,
        body,
        Provenance());

    static ExecutionProvenance Provenance(string sourceReference = SourceReference) => new(
        new(TransitionAuthoring.Producer),
        new(sourceReference),
        DocumentOrigin.Generated);

    static ExecutionDefinitionReference EmissionContract() => new(
        new("interaction/review-approved"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('a', 64)));

    static PortableValue Object(
        ValueContract contract,
        params (string Name, ObservationValue Value)[] fields) => PortableValue.Concrete(
        contract,
        ObservationValue.FromObject(fields.ToDictionary(static field => field.Name, static field => field.Value)));

    static TransitionObservationEntry Entry(
        CompiledTransitionPlan plan,
        string path,
        ObservationValue value)
    {
        var fieldPath = FieldPath.FromField(path);
        var field = Assert.Single(
            Assert.IsType<ObjectTypeRef>(plan.Definition.Observation.Type).Fields,
            candidate => candidate.Name == path);
        var contract = new ValueContract(
            field.Type,
            cardinality: field.Cardinality,
            presence: field.Presence,
            nullability: field.Nullability);
        return new(
            TransitionObservationAccess.At(fieldPath),
            PortableValue.Concrete(contract, value));
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity}:{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));

    sealed class ReviewEntity : Entity<ReviewEntity>
    {
        public ReviewEntity()
        {
            Status = MutableField<string>(nameof(Status));
            Eligible = MutableField<bool>(nameof(Eligible));
        }

        public Field<string> Status { get; }

        public Field<bool> Eligible { get; }
    }

    sealed record ReviewInput(bool Approved, string Decision);

    sealed record ReviewCreationInput(string Status, bool Eligible);

    sealed record ReviewInitialState(string Status, bool Eligible);

    sealed record CollectionReviewCreationInput(string Status, IReadOnlyList<string> Tags);

    sealed record CollectionReviewInitialState(string Status, IReadOnlyList<string> Tags);

    sealed record WrongCardinalityReviewInitialState(string Status, string Tags);

    sealed record IncompleteReviewInitialState(string Status);

    sealed class CollectionReviewEntity : Entity<CollectionReviewEntity>
    {
        public CollectionReviewEntity()
        {
            Status = MutableField<string>(nameof(Status));
            Tags = MutableField<IReadOnlyList<string>>(nameof(Tags));
        }

        public Field<string> Status { get; }

        public Field<IReadOnlyList<string>> Tags { get; }
    }

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record PortableTransitionDocument(IReadOnlyDictionary<string, string> Content);

    sealed record PortableDocumentCreationInput(string Id, PortableTransitionDocument Document);

    sealed record PortableDocumentInitialState(string Id, PortableTransitionDocument Document);

    sealed class PortableDocumentEntity : Entity<PortableDocumentEntity>
    {
        public PortableDocumentEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Document = MutableField<PortableTransitionDocument>(nameof(Document));
        }

        public Field<string> Id { get; }

        public Field<PortableTransitionDocument> Document { get; }
    }

    sealed record OptionalDecisionInput(
        [property: JsonPropertyName("decision")] string? Decision);

    sealed record ReorderedValue(string Z, string A);

    sealed record OwnedChild(
        [property: JsonPropertyName("child_id")] string ChildId,
        string Name);

    sealed record OwnedChildInput(string ChildId, string Name);

    sealed class OwnedReviewEntity : Entity<OwnedReviewEntity>
    {
        public OwnedReviewEntity()
        {
            Children = MutableField<IReadOnlyList<OwnedChild>>(nameof(Children));
        }

        public Field<IReadOnlyList<OwnedChild>> Children { get; }
    }

    sealed class ObservedIdentityEntity : Entity<ObservedIdentityEntity>
    {
        public ObservedIdentityEntity()
        {
            ObservedIds = MutableField<IReadOnlyList<string>>(nameof(ObservedIds));
        }

        public Field<IReadOnlyList<string>> ObservedIds { get; }
    }

    sealed class AlignedLedgerEntity : Entity<AlignedLedgerEntity>
    {
        public AlignedLedgerEntity()
        {
            ObservedIds = MutableField<IReadOnlyList<string>>(nameof(ObservedIds));
            Evaluations = MutableField<IReadOnlyList<string>>(nameof(Evaluations));
        }

        public Field<IReadOnlyList<string>> ObservedIds { get; }

        public Field<IReadOnlyList<string>> Evaluations { get; }
    }

    sealed record EvaluationIdentityInput(string EvaluationId);

    sealed record LedgerCountInput(IReadOnlyList<string> ExpectedIds);

    sealed record UnrelatedMembershipInput(UnrelatedMembership Membership, string Candidate);

    sealed record UnrelatedMembership(string Token)
    {
        public bool Contains(string candidate) => string.Equals(Token, candidate, StringComparison.Ordinal);
    }

    static class Identities
    {
        public static readonly ExecutionDefinitionId Definition = new("transition/review-decision");
        public static readonly ExecutionRevisionId Revision = new("revision/1");
        public static readonly ExecutionNodeId Body = new("review/body");
        public static readonly ExecutionNodeId PendingAdmission = new("review/admission/pending");
        public static readonly ExecutionNodeId DecisionLocal = new("review/let/decision");
        public static readonly ValueBindingId DecisionBinding = new("review.binding.decision");
        public static readonly ExecutionNodeId DecisionChoice = new("review/choice/decision");
        public static readonly ExecutionNodeId ApproveCase = new("review/choice/approve");
        public static readonly ExecutionNodeId SetApproved = new("review/update/approved");
        public static readonly ExecutionNodeId ApprovedEmission = new("review/emit/approved");
        public static readonly ExecutionNodeId ApprovedOutcome = new("review/outcome/approved");
        public static readonly ExecutionNodeId ChoiceFallback = new("review/choice/fallback");
        public static readonly ExecutionNodeId DecisionMatch = new("review/match/decision");
        public static readonly ExecutionNodeId HoldCase = new("review/match/hold");
        public static readonly ExecutionNodeId SetHeld = new("review/update/held");
        public static readonly ExecutionNodeId HeldOutcome = new("review/outcome/held");
        public static readonly ExecutionNodeId MatchFallback = new("review/match/fallback");
        public static readonly ExecutionNodeId IgnoredOutcome = new("review/outcome/ignored");
        public static readonly ExecutionNodeId ValidStatusInvariant = new("review/invariant/status");
        public static readonly ExecutionNodeId DuplicateOutcome = new("review/outcome/duplicate");
        public static readonly ExecutionDefinitionId MembershipDefinition = new("transition/review-membership");
        public static readonly ExecutionNodeId MembershipBody = new("review-membership/body");
        public static readonly ExecutionNodeId MembershipAdmission = new("review-membership/admission");
        public static readonly ExecutionNodeId MembershipOutcome = new("review-membership/outcome");
        public static readonly ExecutionDefinitionId OptionalDefinition = new("transition/review-optional");
        public static readonly ExecutionNodeId OptionalBody = new("review-optional/body");
        public static readonly ExecutionNodeId OptionalLocal = new("review-optional/local");
        public static readonly ValueBindingId OptionalBinding = new("review.optional.decision");
        public static readonly ExecutionNodeId OptionalMatch = new("review-optional/match");
        public static readonly ExecutionNodeId AbsentCase = new("review-optional/case/absent");
        public static readonly ExecutionNodeId AbsentOutcome = new("review-optional/outcome/absent");
        public static readonly ExecutionNodeId NullCase = new("review-optional/case/null");
        public static readonly ExecutionNodeId NullOutcome = new("review-optional/outcome/null");
        public static readonly ExecutionNodeId OptionalFallback = new("review-optional/fallback");
        public static readonly ExecutionNodeId PresentOutcome = new("review-optional/outcome/present");
        public static readonly ExecutionDefinitionId EchoDefinition = new("transition/review-echo");
        public static readonly ExecutionNodeId EchoBody = new("review-echo/body");
        public static readonly ExecutionNodeId EchoOutcome = new("review-echo/outcome");
        public static readonly ExecutionDefinitionId ProjectedDefinition = new("transition/review-projected");
        public static readonly ExecutionNodeId ProjectedBody = new("review-projected/body");
        public static readonly ExecutionNodeId ProjectedOutcome = new("review-projected/outcome");
        public static readonly ExecutionDefinitionId CollectionContainsDefinition = new("transition/review-collection-contains");
        public static readonly ExecutionNodeId CollectionContainsBody = new("review-collection-contains/body");
        public static readonly ExecutionNodeId CollectionContainsAdmission = new("review-collection-contains/admission");
        public static readonly ExecutionNodeId CollectionContainsOutcome = new("review-collection-contains/outcome");
        public static readonly ExecutionDefinitionId UnrelatedContainsDefinition = new("transition/review-unrelated-contains");
        public static readonly ExecutionNodeId UnrelatedContainsBody = new("review-unrelated-contains/body");
        public static readonly ExecutionNodeId UnrelatedContainsAdmission = new("review-unrelated-contains/admission");
        public static readonly ExecutionNodeId UnrelatedContainsOutcome = new("review-unrelated-contains/outcome");
        public static readonly ExecutionDefinitionId CollectionCountDefinition = new("transition/review-collection-count");
        public static readonly ExecutionNodeId CollectionCountBody = new("review-collection-count/body");
        public static readonly ExecutionNodeId CollectionCountAdmission = new("review-collection-count/admission");
        public static readonly ExecutionNodeId CollectionCountOutcome = new("review-collection-count/outcome");
        public static readonly ExecutionNodeId CollectionCountInvariant = new("review-collection-count/invariant");
        public static readonly ExecutionDefinitionId OwnedDefinition = new("transition/review-owned-child");
        public static readonly ExecutionNodeId OwnedBody = new("review-owned-child/body");
        public static readonly ExecutionNodeId OwnedUpsert = new("review-owned-child/upsert");
        public static readonly ExecutionNodeId OwnedOutcome = new("review-owned-child/outcome");
    }
}
