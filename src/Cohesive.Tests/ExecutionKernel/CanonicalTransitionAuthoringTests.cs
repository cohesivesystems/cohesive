using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;
using LegacyTransitionDefinition = Cohesive.Transitions.Model.TransitionDefinition;

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
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(LegacyTransitionDefinition));
        Assert.DoesNotContain(properties, static property => property.PropertyType == typeof(LegacyTransitionDefinition));
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

    static Transition<ReviewEntity, ReviewInput, string> CreateAuthoredTransition() =>
        TransitionAuthoring.Create<ReviewEntity, ReviewInput, string>(
            ReviewEntity.Instance.Definition.Shape,
            Metadata(),
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
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
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
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
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

    static TransitionAuthoringMetadata Metadata() => new(
        Identities.Definition,
        Identities.Revision,
        Identities.Body,
        Provenance(),
        displayName: "Review decision",
        description: "Representative ARI-159 C# authoring fixture.");

    static TransitionAuthoringMetadata AuxiliaryMetadata(
        ExecutionDefinitionId definition,
        ExecutionNodeId body) => new(
        definition,
        Identities.Revision,
        body,
        Provenance());

    static ExecutionProvenance Provenance() => new(
        new(TransitionAuthoring.Producer),
        new(SourceReference),
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
        public static readonly ExecutionDefinitionId OwnedDefinition = new("transition/review-owned-child");
        public static readonly ExecutionNodeId OwnedBody = new("review-owned-child/body");
        public static readonly ExecutionNodeId OwnedUpsert = new("review-owned-child/upsert");
        public static readonly ExecutionNodeId OwnedOutcome = new("review-owned-child/outcome");
    }
}
