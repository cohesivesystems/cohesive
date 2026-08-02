using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class CanonicalTransitionIrTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void RepresentativeDirectIr_RoundTripsThroughSharedEnvelopeDeterministically()
    {
        var definition = RepresentativeDefinition();
        var document = CreateDocument(definition);

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document);
        var validation = TransitionDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.NotNull(restoredDocument);
        Assert.NotNull(restoredDefinition);
        Assert.Equal(TransitionDefinitionDocuments.Kind, document.Kind);
        Assert.Equal(document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(restoredDocument));
        Assert.Equal(definition, restoredDefinition);
        Assert.Equal(definition.Input, restoredDefinition.Input);
        Assert.Equal(definition.Observation, restoredDefinition.Observation);
        Assert.Equal(definition.Outcome, restoredDefinition.Outcome);
        Assert.Equal(definition.Preconditions.Length, restoredDefinition.Preconditions.Length);
        Assert.Equal(definition.Invariants.Length, restoredDefinition.Invariants.Length);
        Assert.Equal(definition.Body.Steps.Length, restoredDefinition.Body.Steps.Length);
        Assert.Equal(
            "46a774479e211b1aa39fe664909797d95e24391f456f91b6be261a061b14cb4f",
            document.Metadata.Fingerprint.Value);
    }

    [Fact]
    public void EveryClosedNodeVariant_RoundTripsWithItsStableWireDiscriminator()
    {
        var terminal = Sequence(
            "terminal-body",
            new OutcomeTransitionNode(
                new("terminal-outcome"),
                TransitionOutcomeDisposition.NoChange,
                Expr.Const("held")));
        TransitionNode[] nodes =
        [
            Sequence("sequence", terminal),
            new LetTransitionNode(new("let"), new("status"), StringContract, Expr.Const("pending")),
            new ChoiceTransitionNode(
                new("choice"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [new(new("choice-case"), Expr.Const(true), terminal)],
                new(new("choice-fallback"), terminal)),
            new MatchTransitionNode(
                new("match"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                Expr.Const("pending"),
                StringContract,
                [new(new("match-case"), ConcreteString("pending"), terminal)],
                new(new("match-fallback"), terminal)),
            new UpdateTransitionNode(
                new("update"),
                FieldPath.FromField("status"),
                new SetTransitionPatch(Expr.Const("approved"))),
            new EmitTransitionNode(
                new("emit"),
                EmissionContract(),
                Expr.Const("reviewed")),
            new MoveMachineTransitionNode(
                new("move-machine"),
                MachineReference(),
                new("edge/approve"),
                Expr.Const("held")),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved"))
        ];
        string[] discriminators =
        [
            TransitionWireNames.SequenceNode,
            TransitionWireNames.LetNode,
            TransitionWireNames.ChoiceNode,
            TransitionWireNames.MatchNode,
            TransitionWireNames.UpdateNode,
            TransitionWireNames.EmitNode,
            TransitionWireNames.MoveMachineNode,
            TransitionWireNames.OutcomeNode
        ];
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        for (var index = 0; index < nodes.Length; index++)
        {
            var json = JsonSerializer.Serialize<TransitionNode>(nodes[index], options);
            var restored = JsonSerializer.Deserialize<TransitionNode>(json, options);

            Assert.NotNull(restored);
            Assert.IsType(nodes[index].GetType(), restored);
            Assert.Contains(
                $"\"{TransitionWireNames.NodeDiscriminator}\":\"{discriminators[index]}\"",
                json,
                StringComparison.Ordinal);
            Assert.Equal(json, JsonSerializer.Serialize<TransitionNode>(restored, options));
        }
    }

    [Fact]
    public void EverySparsePatchVariant_RoundTripsWithItsStableWireDiscriminator()
    {
        var patches = SparsePatches();
        string[] discriminators =
        [
            TransitionWireNames.SetPatch,
            TransitionWireNames.RemovePatch,
            TransitionWireNames.IncrementPatch,
            TransitionWireNames.AddToSetPatch,
            TransitionWireNames.AppendPatch,
            TransitionWireNames.UpsertOwnedChildPatch,
            TransitionWireNames.RemoveOwnedChildPatch
        ];
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        for (var index = 0; index < patches.Length; index++)
        {
            var json = JsonSerializer.Serialize<TransitionPatchOperation>(patches[index], options);
            var restored = JsonSerializer.Deserialize<TransitionPatchOperation>(json, options);

            Assert.NotNull(restored);
            Assert.IsType(patches[index].GetType(), restored);
            Assert.Contains(
                $"\"{TransitionWireNames.PatchDiscriminator}\":\"{discriminators[index]}\"",
                json,
                StringComparison.Ordinal);
            Assert.Equal(json, JsonSerializer.Serialize<TransitionPatchOperation>(restored, options));
        }
    }

    [Fact]
    public void PatchAlgebraAndStableNodeIdentities_AreFingerprintBearing()
    {
        var patchFingerprints = SparsePatches()
            .Select(operation => Fingerprint(MinimalDefinition(Sequence(
                "root",
                new UpdateTransitionNode(
                    new("update"),
                    FieldPath.FromField("value"),
                    operation),
                new OutcomeTransitionNode(
                    new("outcome"),
                    TransitionOutcomeDisposition.Applied,
                    Expr.Const("approved"))))))
            .ToArray();
        var firstIdentity = MinimalDefinition(Sequence(
            "root",
            new OutcomeTransitionNode(
                new("outcome/first"),
                TransitionOutcomeDisposition.NoChange,
                Expr.Const("held"))));
        var secondIdentity = MinimalDefinition(Sequence(
            "root",
            new OutcomeTransitionNode(
                new("outcome/second"),
                TransitionOutcomeDisposition.NoChange,
                Expr.Const("held"))));

        Assert.Equal(patchFingerprints.Length, patchFingerprints.Distinct().Count());
        Assert.NotEqual(Fingerprint(firstIdentity), Fingerprint(secondIdentity));
    }

    [Fact]
    public void DecisionKindsAndAuthorableOutcomes_AreClosedAndDistinct()
    {
        Assert.Equal(
            [
                TransitionDecisionKind.Unspecified,
                TransitionDecisionKind.Applied,
                TransitionDecisionKind.NoChange,
                TransitionDecisionKind.AdmissionRejected,
                TransitionDecisionKind.DomainRejected,
                TransitionDecisionKind.Conflict,
                TransitionDecisionKind.InvalidDefinition,
                TransitionDecisionKind.InfrastructureFailure
            ],
            Enum.GetValues<TransitionDecisionKind>());

        Assert.Equal(
            [
                TransitionOutcomeDisposition.Unspecified,
                TransitionOutcomeDisposition.Applied,
                TransitionOutcomeDisposition.NoChange,
                TransitionOutcomeDisposition.DomainRejected
            ],
            Enum.GetValues<TransitionOutcomeDisposition>());
        Assert.Equal(
            [
                CaseSelection.Unspecified,
                CaseSelection.OrderedFirstMatch
            ],
            Enum.GetValues<CaseSelection>());
        Assert.Equal(
            [
                BranchCompleteness.Unspecified,
                BranchCompleteness.Exhaustive,
                BranchCompleteness.Fallback
            ],
            Enum.GetValues<BranchCompleteness>());

        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        Assert.Equal(
            "\"Applied\"",
            JsonSerializer.Serialize(TransitionDecisionKind.Applied, options));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TransitionDecisionKind>("1", options));
    }

    [Fact]
    public void LetBinding_HasOneIdentityForWholeValueAndFieldReferences()
    {
        ValueBindingId scalarBinding = new("currentStatus");
        ValueBindingId objectBinding = new("review");
        var definition = MinimalDefinition(Sequence(
            "root",
            new LetTransitionNode(
                new("bind-status"),
                scalarBinding,
                StringContract,
                Expr.Const("pending")),
            new LetTransitionNode(
                new("bind-review"),
                objectBinding,
                new(new ObjectTypeRef([new("reviewer", new ScalarTypeRef(ScalarTypeKind.String))])),
                Expr.Const(ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                {
                    ["reviewer"] = ObservationValue.FromString("caseworker")
                }))),
            new UpdateTransitionNode(
                new("set-status"),
                FieldPath.FromField("status"),
                new SetTransitionPatch(Expr.BoundValue(scalarBinding))),
            new UpdateTransitionNode(
                new("set-reviewer"),
                FieldPath.FromField("reviewer"),
                new SetTransitionPatch(Expr.Field(objectBinding, "reviewer"))),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.NoChange,
                Expr.BoundValue(scalarBinding))));

        var validation = TransitionDefinitionDocuments.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(CreateDocument(definition)),
            out _,
            out var restored);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.NotNull(restored);
        var scalarReference = Assert.IsType<BindingExpr>(
            Assert.IsType<SetTransitionPatch>(
                Assert.IsType<UpdateTransitionNode>(restored.Body.Steps[2]).Operation).Value);
        var fieldReference = Assert.IsType<FieldExpr>(
            Assert.IsType<SetTransitionPatch>(
                Assert.IsType<UpdateTransitionNode>(restored.Body.Steps[3]).Operation).Value);
        Assert.Equal(scalarBinding, scalarReference.Binding);
        Assert.Equal(objectBinding, fieldReference.Binding);

        var analysis = ExprAnalyzer.Analyze(new(
            new("transition/let/current-status"),
            scalarReference,
            new(bindings: [new(scalarBinding, StringContract)])));
        Assert.True(analysis.IsValid, FormatDiagnostics(analysis.Validation));
        Assert.Equal(StringContract, analysis.KnownResult);
        Assert.Equal(scalarBinding, Assert.Single(analysis.Requirements.Bindings));
        Assert.Empty(analysis.Requirements.Parameters);
    }

    [Fact]
    public void EmitContractReference_IsTheSingleInteractionAuthority()
    {
        var eventDefinition = MinimalDefinition(Sequence(
            "root",
            new EmitTransitionNode(
                new("emit"),
                EmissionContract("interaction/reviewed-event"),
                Expr.Const("reviewed")),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved"))));
        var requestDefinition = MinimalDefinition(Sequence(
            "root",
            new EmitTransitionNode(
                new("emit"),
                EmissionContract("interaction/review-request"),
                Expr.Const("reviewed")),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved"))));

        Assert.Null(typeof(EmitTransitionNode).GetProperty("Kind"));
        Assert.NotEqual(Fingerprint(eventDefinition), Fingerprint(requestDefinition));
    }

    [Fact]
    public void MoveMachineNode_RoundTripsThroughSharedEnvelopeWithItsExactReference()
    {
        var machine = MachineReference();
        var definition = MinimalDefinition(Sequence(
            "root",
            new MoveMachineTransitionNode(
                new("move-machine"),
                machine,
                new("edge/approve"),
                Expr.Const("held")),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved"))));
        var document = CreateDocument(definition);

        var validation = TransitionDefinitionDocuments.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(document),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.NotNull(restoredDocument);
        Assert.NotNull(restoredDefinition);
        Assert.Equal(document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);
        var movement = Assert.IsType<MoveMachineTransitionNode>(restoredDefinition.Body.Steps[0]);
        Assert.Equal(machine, movement.Machine);
        Assert.Equal(new ExecutionNodeId("edge/approve"), movement.Edge);
        Assert.Equal(Expr.Const("held"), movement.Rejection);
    }

    [Fact]
    public void MoveMachineReferenceEdgeAndRejection_AreFingerprintBearing()
    {
        var baseline = MachineMovementDefinition(
            MachineReference(),
            new("edge/approve"),
            Expr.Const("held"));
        var changedMachineFingerprint = MachineMovementDefinition(
            MachineReference(fingerprintDigit: '2'),
            new("edge/approve"),
            Expr.Const("held"));
        var changedEdge = MachineMovementDefinition(
            MachineReference(),
            new("edge/reject"),
            Expr.Const("held"));
        var changedRejection = MachineMovementDefinition(
            MachineReference(),
            new("edge/approve"),
            Expr.Const("notEligible"));

        ExecutionDefinitionFingerprint[] fingerprints =
        [
            Fingerprint(baseline),
            Fingerprint(changedMachineFingerprint),
            Fingerprint(changedEdge),
            Fingerprint(changedRejection)
        ];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
    }

    [Fact]
    public void OrderedSequencesChoicesAndMatches_AreFingerprintBearing()
    {
        var firstUpdate = new UpdateTransitionNode(
            new("set-status"),
            FieldPath.FromField("status"),
            new SetTransitionPatch(Expr.Const("approved")));
        var secondUpdate = new UpdateTransitionNode(
            new("append-note"),
            FieldPath.FromField("notes"),
            new AppendTransitionPatch(Expr.Const("reviewed")));
        var outcome = new OutcomeTransitionNode(
            new("complete"),
            TransitionOutcomeDisposition.Applied,
            Expr.Const("approved"));
        var ordered = MinimalDefinition(Sequence("root", firstUpdate, secondUpdate, outcome));
        var reversed = MinimalDefinition(Sequence("root", secondUpdate, firstUpdate, outcome));

        var approved = Sequence(
            "approved-body",
            new OutcomeTransitionNode(
                new("approved"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved")));
        var held = Sequence(
            "held-body",
            new OutcomeTransitionNode(
                new("held"),
                TransitionOutcomeDisposition.DomainRejected,
                Expr.Const("held")));
        var firstChoice = MinimalDefinition(Sequence(
            "root",
            new ChoiceTransitionNode(
                new("choose"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                [
                    new(new("approve"), Expr.Param("approved"), approved),
                    new(new("hold"), Expr.Const(true), held)
                ])));
        var reversedChoice = MinimalDefinition(Sequence(
            "root",
            new ChoiceTransitionNode(
                new("choose"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                [
                    new(new("hold"), Expr.Const(true), held),
                    new(new("approve"), Expr.Param("approved"), approved)
                ])));
        var firstMatch = MinimalDefinition(Sequence(
            "root",
            new MatchTransitionNode(
                new("match"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                Expr.Param("decision"),
                StringContract,
                [
                    new(new("approved-case"), ConcreteString("approved"), approved),
                    new(new("held-case"), ConcreteString("held"), held)
                ])));
        var reversedMatch = MinimalDefinition(Sequence(
            "root",
            new MatchTransitionNode(
                new("match"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                Expr.Param("decision"),
                StringContract,
                [
                    new(new("held-case"), ConcreteString("held"), held),
                    new(new("approved-case"), ConcreteString("approved"), approved)
                ])));

        Assert.NotEqual(Fingerprint(ordered), Fingerprint(reversed));
        Assert.NotEqual(Fingerprint(firstChoice), Fingerprint(reversedChoice));
        Assert.NotEqual(Fingerprint(firstMatch), Fingerprint(reversedMatch));
    }

    [Fact]
    public void Validator_RejectsDuplicateStableIdentitiesAcrossNestedBranches()
    {
        var caseBody = Sequence(
            "case-body",
            new OutcomeTransitionNode(
                new("shared-outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved")));
        var fallbackBody = Sequence(
            "fallback-body",
            new OutcomeTransitionNode(
                new("shared-outcome"),
                TransitionOutcomeDisposition.DomainRejected,
                Expr.Const("held")));
        var definition = MinimalDefinition(Sequence(
            "root",
            new ChoiceTransitionNode(
                new("choice"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [new(new("case"), Expr.Const(true), caseBody)],
                new(new("fallback"), fallbackBody))));

        var validation = TransitionDefinitionValidator.Validate(definition);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(TransitionDefinitionDiagnosticCodes.NodeIdentityDuplicate, diagnostic.Code);
        Assert.Equal("/body/steps/0/fallback/body/steps/0/id", diagnostic.Location);
        Assert.Contains("/body/steps/0/cases/0/body/steps/0/id", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsDefaultNodeIdentityAndEmptyBody()
    {
        var definition = MinimalDefinition(new(default, []));

        var validation = TransitionDefinitionValidator.Validate(definition);

        Assert.Equal(
            [
                (TransitionDefinitionDiagnosticCodes.NodeIdentityMissing, "/body/id"),
                (TransitionDefinitionDiagnosticCodes.SequenceEmpty, "/body/steps")
            ],
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void Validator_RejectsDefaultWholeBindingIdentity()
    {
        var definition = MinimalDefinition(Sequence(
            "root",
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.NoChange,
                new BindingExpr(default))));

        var diagnostic = Assert.Single(TransitionDefinitionValidator.Validate(definition).Diagnostics);

        Assert.Equal(PortableExecutionDiagnosticCodes.InvalidNode, diagnostic.Code);
        Assert.Equal("/body/steps/0/value/binding", diagnostic.Location);
    }

    [Fact]
    public void Validator_RejectsAnIncompleteEmissionReferenceAtTheContractPath()
    {
        var definition = MinimalDefinition(Sequence(
            "root",
            new EmitTransitionNode(new("emit"), null!, Expr.Const("reviewed")),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved"))));

        var diagnostic = Assert.Single(TransitionDefinitionValidator.Validate(definition).Diagnostics);

        Assert.Equal(TransitionDefinitionDiagnosticCodes.EmissionContractInvalid, diagnostic.Code);
        Assert.Equal("/body/steps/0/contract", diagnostic.Location);
    }

    [Fact]
    public void Validator_RejectsIndeterminateMatchPatternStatesAtTheStatePath()
    {
        var contract = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        PortableValue[] invalidPatterns =
        [
            PortableValue.Missing(contract),
            PortableValue.Unknown(contract),
            PortableValue.Failed(
                contract,
                new(
                    "tests.match.failed",
                    DiagnosticSeverity.Error,
                    "The test Match value could not be acquired."))
        ];

        foreach (var pattern in invalidPatterns)
        {
            var caseTerminal = Sequence(
                "case-terminal-body",
                new OutcomeTransitionNode(
                    new("case-terminal"),
                    TransitionOutcomeDisposition.NoChange,
                    Expr.Const("held")));
            var fallbackTerminal = Sequence(
                "fallback-terminal-body",
                new OutcomeTransitionNode(
                    new("fallback-terminal"),
                    TransitionOutcomeDisposition.NoChange,
                    Expr.Const("held")));
            var definition = MinimalDefinition(Sequence(
                "root",
                new MatchTransitionNode(
                    new("match"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    Expr.Const("held"),
                    contract,
                    [new(new("case"), pattern, caseTerminal)],
                    new(new("fallback"), fallbackTerminal))));

            var diagnostic = Assert.Single(TransitionDefinitionValidator.Validate(definition).Diagnostics);

            Assert.Equal(TransitionDefinitionDiagnosticCodes.MatchPatternStateInvalid, diagnostic.Code);
            Assert.Equal("/body/steps/0/cases/0/pattern/state", diagnostic.Location);
        }
    }

    [Fact]
    public void Validator_ReportsInvalidCombinationsInDeterministicPathOrder()
    {
        var matchCaseBody = Sequence(
            "match-case-body",
            new OutcomeTransitionNode(
                new("match-case-outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved")));
        var matchFallbackBody = Sequence(
            "match-fallback-body",
            new OutcomeTransitionNode(
                new("match-fallback-outcome"),
                TransitionOutcomeDisposition.DomainRejected,
                Expr.Const("held")));
        var terminal = new OutcomeTransitionNode(
            new("terminal"),
            TransitionOutcomeDisposition.NoChange,
            Expr.Const("held"));
        var definition = MinimalDefinition(Sequence(
            "root",
            new ChoiceTransitionNode(
                new("choice"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                []),
            new MatchTransitionNode(
                new("match"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                Expr.Const("approved"),
                StringContract,
                [new(new("match-case"), ConcreteBoolean(true), matchCaseBody)],
                new(new("match-fallback"), matchFallbackBody)),
            new UpdateTransitionNode(new("update"), default, null!),
            terminal));

        var validation = TransitionDefinitionValidator.Validate(definition);

        Assert.Equal(
            [
                (TransitionDefinitionDiagnosticCodes.ChoiceCasesEmpty, "/body/steps/0/cases"),
                (TransitionDefinitionDiagnosticCodes.FallbackContractInvalid, "/body/steps/0/fallback"),
                (TransitionDefinitionDiagnosticCodes.MatchPatternContractMismatch, "/body/steps/1/cases/0/pattern/contract"),
                (TransitionDefinitionDiagnosticCodes.FallbackContractInvalid, "/body/steps/1/fallback"),
                (TransitionDefinitionDiagnosticCodes.RequiredMemberMissing, "/body/steps/2/operation"),
                (TransitionDefinitionDiagnosticCodes.PatchPathInvalid, "/body/steps/2/path")
            ],
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void Validator_RejectsUnspecifiedRequiredEnumValuesAtPrecisePaths()
    {
        var terminal = Sequence(
            "terminal-body",
            new OutcomeTransitionNode(
                new("terminal"),
                TransitionOutcomeDisposition.Unspecified,
                Expr.Const("held")));
        var definition = MinimalDefinition(Sequence(
            "root",
            new ChoiceTransitionNode(
                new("choice"),
                CaseSelection.Unspecified,
                BranchCompleteness.Unspecified,
                [new(new("case"), Expr.Const(true), terminal)])));

        var validation = TransitionDefinitionValidator.Validate(definition);

        Assert.Equal(
            [
                (TransitionDefinitionDiagnosticCodes.EnumUnsupported, "/body/steps/0/cases/0/body/steps/0/disposition"),
                (TransitionDefinitionDiagnosticCodes.EnumUnsupported, "/body/steps/0/completeness"),
                (TransitionDefinitionDiagnosticCodes.EnumUnsupported, "/body/steps/0/selection")
            ],
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void MissingRequiredEnumMembers_DeserializeToInvalidSentinels()
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var choice = Assert.IsType<ChoiceTransitionNode>(JsonSerializer.Deserialize<TransitionNode>(
            """{"$node":"choice","id":"choice","cases":[]}""",
            options));
        var definition = MinimalDefinition(Sequence("root", choice));

        var validation = TransitionDefinitionValidator.Validate(definition);

        Assert.Equal(
            [
                (TransitionDefinitionDiagnosticCodes.ChoiceCasesEmpty, "/body/steps/0/cases"),
                (TransitionDefinitionDiagnosticCodes.EnumUnsupported, "/body/steps/0/completeness"),
                (TransitionDefinitionDiagnosticCodes.EnumUnsupported, "/body/steps/0/selection")
            ],
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void Facade_RejectsOmittedDefaultMembersWithAValidRawFingerprint()
    {
        var json = RewriteDefinition(
            CreateDocument(MinimalDefinition(Sequence(
                "root",
                new OutcomeTransitionNode(
                    new("outcome"),
                    TransitionOutcomeDisposition.NoChange,
                    Expr.Const("held"))))),
            static definition =>
            {
                Assert.True(definition.Remove("preconditions"));
                Assert.True(definition.Remove("invariants"));
            });

        var validation = TransitionDefinitionDocuments.TryDeserialize(
            json,
            out var document,
            out var definition);

        Assert.NotNull(document);
        Assert.Null(definition);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(TransitionDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical, diagnostic.Code);
        Assert.Equal("/definition", diagnostic.Location);
    }

    [Fact]
    public void Facade_RejectsAnOmittedRequiredEnumThroughCanonicalAndSemanticValidation()
    {
        var document = CreateDocument(ChoiceDefinition());
        var json = RewriteDefinition(document, static definition =>
        {
            var choice = definition["body"]!["steps"]![0]!.AsObject();
            Assert.True(choice.Remove("selection"));
        });

        var validation = TransitionDefinitionDocuments.TryDeserialize(
            json,
            out _,
            out var definition);

        Assert.Null(definition);
        Assert.Equal(
            [
                (TransitionDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical, "/definition"),
                (TransitionDefinitionDiagnosticCodes.EnumUnsupported, "/definition/body/steps/0/selection")
            ],
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void Facade_RejectsNumericWrongCaseAndUnknownEnumTokensAtTheExactPath()
    {
        foreach (var token in new[] { "1", "\"orderedFirstMatch\"", "\"Parallel\"" })
        {
            var json = RewriteDefinition(CreateDocument(ChoiceDefinition()), definition =>
                definition["body"]!["steps"]![0]!["selection"] = JsonNode.Parse(token));

            var validation = TransitionDefinitionDocuments.TryDeserialize(
                json,
                out _,
                out var definition);

            Assert.Null(definition);
            var diagnostic = Assert.Single(validation.Diagnostics);
            Assert.Equal(TransitionDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid, diagnostic.Code);
            Assert.Equal("/definition/body/steps/0/selection", diagnostic.Location);
        }
    }

    [Fact]
    public void Facade_RejectsOpaqueRuntimeTypesAtPreciseDefinitionPaths()
    {
        var opaqueInput = new ValueContract(new ObjectTypeRef(
        [
            new(
                "values",
                new ArrayTypeRef(new OpaqueRuntimeTypeRef("System.Object")))
        ]));
        var definition = new CanonicalTransitionDefinition(
            opaqueInput,
            new ValueContract(new JsonTypeRef()),
            StringContract,
            [],
            Sequence(
                "root",
                new LetTransitionNode(
                    new("let"),
                    new("selected"),
                    StringContract,
                    new ConditionalExpr(
                        Expr.Const(true),
                        Expr.Const("approved"),
                        Expr.Const("held"))),
                new OutcomeTransitionNode(
                    new("outcome"),
                    TransitionOutcomeDisposition.Applied,
                    Expr.Const("approved"))));
        var document = CreateDocument(definition);

        var validation = TransitionDefinitionDocuments.Validate(document);

        Assert.Equal(
            [
                (PortableExecutionDiagnosticCodes.OpaqueRuntimeType, "/definition/body/steps/0/value/returnType"),
                (PortableExecutionDiagnosticCodes.OpaqueRuntimeType, "/definition/input/type/fields/0/type/elementType")
            ],
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void Facade_RejectsNonTransitionKindWithoutProjectingDefinition()
    {
        var definition = MinimalDefinition(Sequence(
            "root",
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.NoChange,
                Expr.Const("held"))));
        var wrongKind = ExecutionDefinitionDocument.Create(
            new("process"),
            new("transition/review"),
            new("revision/1"),
            definition,
            Provenance());
        var json = ExecutionDefinitionJsonSerializer.Serialize(wrongKind);

        var validation = TransitionDefinitionDocuments.TryDeserialize(
            json,
            out var document,
            out var restoredDefinition);

        Assert.NotNull(document);
        Assert.Null(restoredDefinition);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(TransitionDefinitionDocumentDiagnosticCodes.KindMismatch, diagnostic.Code);
        Assert.Equal("/kind", diagnostic.Location);
    }

    [Fact]
    public void ClosedNodeAndPatchUnions_RejectUnknownDiscriminators()
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TransitionNode>(
            """{"$node":"wait","id":"wait"}""",
            options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TransitionPatchOperation>(
            """{"$patch":"replace"}""",
            options));
    }

    [Fact]
    public void Facade_RejectsUnknownUnionDiscriminatorsAtTheirNestedPaths()
    {
        var unknownNode = RewriteDefinition(CreateDocument(ChoiceDefinition()), static definition =>
            definition["body"]!["steps"]![0]![TransitionWireNames.NodeDiscriminator] = "wait");
        var patchDefinition = MinimalDefinition(Sequence(
            "root",
            new UpdateTransitionNode(
                new("update"),
                FieldPath.FromField("status"),
                new SetTransitionPatch(Expr.Const("approved"))),
            new OutcomeTransitionNode(
                new("outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved"))));
        var unknownPatch = RewriteDefinition(CreateDocument(patchDefinition), static definition =>
            definition["body"]!["steps"]![0]!["operation"]![TransitionWireNames.PatchDiscriminator] = "replace");

        var nodeValidation = TransitionDefinitionDocuments.TryDeserialize(
            unknownNode,
            out _,
            out var nodeDefinition);
        var patchValidation = TransitionDefinitionDocuments.TryDeserialize(
            unknownPatch,
            out _,
            out var restoredPatchDefinition);

        Assert.Null(nodeDefinition);
        Assert.Null(restoredPatchDefinition);
        var nodeDiagnostic = Assert.Single(nodeValidation.Diagnostics);
        var patchDiagnostic = Assert.Single(patchValidation.Diagnostics);
        Assert.Equal(TransitionDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid, nodeDiagnostic.Code);
        Assert.Equal("/definition/body/steps/0", nodeDiagnostic.Location);
        Assert.Equal(TransitionDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid, patchDiagnostic.Code);
        Assert.Equal("/definition/body/steps/0/operation", patchDiagnostic.Location);
    }

    [Fact]
    public void CanonicalModelClosure_HasOnlyClosedPortableDataAuthority()
    {
        var nodeTypes = DerivedTypes(typeof(TransitionNode));
        Assert.Equal(
            [
                typeof(SequenceTransitionNode),
                typeof(LetTransitionNode),
                typeof(ChoiceTransitionNode),
                typeof(MatchTransitionNode),
                typeof(UpdateTransitionNode),
                typeof(EmitTransitionNode),
                typeof(MoveMachineTransitionNode),
                typeof(OutcomeTransitionNode)
            ],
            nodeTypes);
        var patchTypes = DerivedTypes(typeof(TransitionPatchOperation));
        Assert.Equal(
            [
                typeof(SetTransitionPatch),
                typeof(RemoveTransitionPatch),
                typeof(IncrementTransitionPatch),
                typeof(AddToSetTransitionPatch),
                typeof(AppendTransitionPatch),
                typeof(UpsertOwnedChildTransitionPatch),
                typeof(RemoveOwnedChildTransitionPatch)
            ],
            patchTypes);
        Assert.All(nodeTypes, static type => Assert.True(type.IsSealed));
        Assert.All(patchTypes, static type => Assert.True(type.IsSealed));
        Assert.Empty(FindRuntimeAuthority(typeof(CanonicalTransitionDefinition)));
        Assert.Empty(FindPublicSettersInCanonicalTransitionIr());
    }

    static CanonicalTransitionDefinition RepresentativeDefinition()
    {
        var input = new ValueContract(new ObjectTypeRef(
        [
            new("approved", new ScalarTypeRef(ScalarTypeKind.Bool)),
            new("decision", new ScalarTypeRef(ScalarTypeKind.String)),
            new("childId", new ScalarTypeRef(ScalarTypeKind.String)),
            new("child", new JsonTypeRef(JsonTypeKind.Object))
        ]));
        var observation = new ValueContract(new ObjectTypeRef(
        [
            new("status", new ScalarTypeRef(ScalarTypeKind.String)),
            new("score", new ScalarTypeRef(ScalarTypeKind.Int64)),
            new("tags", new ScalarTypeRef(ScalarTypeKind.String), cardinality: FieldCardinality.Many),
            new("notes", new ScalarTypeRef(ScalarTypeKind.String), cardinality: FieldCardinality.Many),
            new("children", new JsonTypeRef(JsonTypeKind.Object), cardinality: FieldCardinality.Many)
        ]));
        var outcome = new ValueContract(new EnumTypeRef(
            "ReviewOutcome",
            ["admissionRejected", "approved", "held", "notEligible"]));
        ValueBindingId decisionBinding = new("selectedDecision");
        var approved = Sequence(
            "approved-body",
            new OutcomeTransitionNode(
                new("approved-outcome"),
                TransitionOutcomeDisposition.Applied,
                Expr.Const("approved")));
        var held = Sequence(
            "held-body",
            new OutcomeTransitionNode(
                new("held-outcome"),
                TransitionOutcomeDisposition.DomainRejected,
                Expr.Const("held")));
        var notEligible = Sequence(
            "not-eligible-body",
            new OutcomeTransitionNode(
                new("not-eligible-outcome"),
                TransitionOutcomeDisposition.DomainRejected,
                Expr.Const("notEligible")));
        var match = new MatchTransitionNode(
            new("match-decision"),
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            Expr.BoundValue(decisionBinding),
            StringContract,
            [new(new("approved-case"), ConcreteString("approved"), approved)],
            new(new("held-fallback"), held));

        return new(
            input,
            observation,
            outcome,
            [
                new(
                    new("status-is-pending"),
                    Expr.Eq(Expr.Field("status"), Expr.Const("pending")),
                    Expr.Const("admissionRejected"))
            ],
            Sequence(
                "review-root",
                new LetTransitionNode(
                    new("bind-decision"),
                    decisionBinding,
                    StringContract,
                    Expr.Param("decision")),
                new UpdateTransitionNode(
                    new("set-status"),
                    FieldPath.FromField("status"),
                    new SetTransitionPatch(Expr.Const("reviewed"))),
                new UpdateTransitionNode(
                    new("increment-score"),
                    FieldPath.FromField("score"),
                    new IncrementTransitionPatch(Expr.Const(1))),
                new UpdateTransitionNode(
                    new("add-tag"),
                    FieldPath.FromField("tags"),
                    new AddToSetTransitionPatch(Expr.Const("reviewed"))),
                new UpdateTransitionNode(
                    new("append-note"),
                    FieldPath.FromField("notes"),
                    new AppendTransitionPatch(Expr.Const("reviewed"))),
                new UpdateTransitionNode(
                    new("clear-legacy-status"),
                    FieldPath.FromField("legacyStatus"),
                    new RemoveTransitionPatch()),
                new UpdateTransitionNode(
                    new("upsert-child"),
                    FieldPath.FromField("children"),
                    new UpsertOwnedChildTransitionPatch(
                        FieldPath.FromField("id"),
                        Expr.Param("childId"),
                        Expr.Param("child"))),
                new UpdateTransitionNode(
                    new("remove-child"),
                    FieldPath.FromField("removedChildren"),
                    new RemoveOwnedChildTransitionPatch(
                        FieldPath.FromField("id"),
                        Expr.Param("childId"))),
                new EmitTransitionNode(
                    new("emit-review-request"),
                    EmissionContract(),
                    Expr.Param("decision")),
                new ChoiceTransitionNode(
                    new("choose-review-result"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [new(new("eligible"), Expr.Param("approved"), Sequence("eligible-body", match))],
                    new(new("not-eligible"), notEligible))),
            [
                new(
                    new("status-is-known"),
                    Expr.Ne(Expr.Field("status"), Expr.Null()))
            ]);
    }

    static CanonicalTransitionDefinition MinimalDefinition(SequenceTransitionNode body) =>
        new(
            new ValueContract(new JsonTypeRef()),
            new ValueContract(new JsonTypeRef()),
            StringContract,
            [],
            body);

    static CanonicalTransitionDefinition ChoiceDefinition()
    {
        var terminal = Sequence(
            "terminal-body",
            new OutcomeTransitionNode(
                new("terminal"),
                TransitionOutcomeDisposition.NoChange,
                Expr.Const("held")));
        return MinimalDefinition(Sequence(
            "root",
            new ChoiceTransitionNode(
                new("choice"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                [new(new("case"), Expr.Const(true), terminal)])));
    }

    static CanonicalTransitionDefinition MachineMovementDefinition(
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        Expr rejection) => MinimalDefinition(Sequence(
        "root",
        new MoveMachineTransitionNode(
            new("move-machine"),
            machine,
            edge,
            rejection),
        new OutcomeTransitionNode(
            new("outcome"),
            TransitionOutcomeDisposition.Applied,
            Expr.Const("approved"))));

    static ExecutionDefinitionDocument CreateDocument(CanonicalTransitionDefinition definition) =>
        TransitionDefinitionDocuments.Create(
            new("transition/review"),
            new("revision/1"),
            definition,
            Provenance());

    static ExecutionDefinitionFingerprint Fingerprint(CanonicalTransitionDefinition definition) =>
        CreateDocument(definition).Metadata.Fingerprint;

    static SequenceTransitionNode Sequence(string id, params TransitionNode[] steps) =>
        new(new(id), [.. steps]);

    static PortableValue ConcreteString(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static PortableValue ConcreteBoolean(bool value) =>
        PortableValue.Concrete(BooleanContract, ObservationValue.FromBool(value));

    static TransitionPatchOperation[] SparsePatches() =>
    [
        new SetTransitionPatch(Expr.Null()),
        new RemoveTransitionPatch(),
        new IncrementTransitionPatch(Expr.Const(1)),
        new AddToSetTransitionPatch(Expr.Const("safety")),
        new AppendTransitionPatch(Expr.Const("reviewed")),
        new UpsertOwnedChildTransitionPatch(
            FieldPath.FromField("id"),
            Expr.Const("child-1"),
            Expr.Const(ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["id"] = ObservationValue.FromString("child-1")
            }))),
        new RemoveOwnedChildTransitionPatch(
            FieldPath.FromField("id"),
            Expr.Const("child-1"))
    ];

    static ExecutionProvenance Provenance() =>
        new(
            new("direct-transition-ir-tests", "1"),
            new("tests/execution-kernel/canonical-transition-ir"),
            DocumentOrigin.Generated);

    static ExecutionDefinitionReference EmissionContract(
        string definitionId = "interaction/review-request") =>
        new(
            new(definitionId),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string('0', 64)));

    static ExecutionDefinitionReference MachineReference(
        char fingerprintDigit = '1') =>
        new(
            new("machine/review-lifecycle"),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    static string RewriteDefinition(
        ExecutionDefinitionDocument document,
        Action<JsonObject> rewrite)
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var root = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse the execution-definition test document.");
        var definition = root["definition"]?.AsObject()
            ?? throw new InvalidOperationException("The execution-definition test document has no definition object.");
        rewrite(definition);

        using var parsedDefinition = JsonDocument.Parse(definition.ToJsonString(options));
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(
            document.Metadata.SchemaVersion,
            document.Kind,
            parsedDefinition.RootElement,
            document.Extensions);
        var fingerprintNode = root["metadata"]?["fingerprint"]?.AsObject()
            ?? throw new InvalidOperationException("The execution-definition test document has no fingerprint object.");
        fingerprintNode["algorithm"] = fingerprint.Algorithm;
        fingerprintNode["canonicalization"] = fingerprint.Canonicalization;
        fingerprintNode["value"] = fingerprint.Value;
        return root.ToJsonString(options);
    }

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static Type[] DerivedTypes(Type type) =>
        [.. type
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(static attribute => attribute.DerivedType)];

    static IReadOnlyList<string> FindRuntimeAuthority(params Type[] roots)
    {
        Queue<Type> pending = new(roots);
        HashSet<Type> visited = [];
        List<string> forbidden = [];
        while (pending.TryDequeue(out var candidate))
        {
            var type = Nullable.GetUnderlyingType(candidate) ?? candidate;
            if (type.IsArray)
            {
                pending.Enqueue(type.GetElementType()!);
                continue;
            }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    pending.Enqueue(argument);
                }
            }
            if (!visited.Add(type))
            {
                continue;
            }

            if (IsRuntimeAuthority(type))
            {
                forbidden.Add(type.FullName ?? type.Name);
                continue;
            }

            if (!string.Equals(type.Namespace, "Cohesive.Transitions.IR", StringComparison.Ordinal)
                && !(type.Namespace?.StartsWith("Cohesive.", StringComparison.Ordinal) ?? false))
            {
                continue;
            }

            foreach (var attribute in type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false))
            {
                pending.Enqueue(attribute.DerivedType);
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                pending.Enqueue(property.PropertyType);
            }
        }

        forbidden.Sort(StringComparer.Ordinal);
        return forbidden;
    }

    static bool IsRuntimeAuthority(Type type)
    {
        var typeNamespace = type.Namespace ?? string.Empty;
        return typeof(Delegate).IsAssignableFrom(type)
            || type == typeof(Type)
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(Stream).IsAssignableFrom(type)
            || typeof(Task).IsAssignableFrom(type)
            || type == typeof(ValueTask)
            || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)
            || type == typeof(CancellationToken)
            || typeNamespace.StartsWith("Cohesive.Adapters", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Storage", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Transitions.Authoring", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Transitions.Model", StringComparison.Ordinal);
    }

    static IReadOnlyList<string> FindPublicSettersInCanonicalTransitionIr() =>
        [.. typeof(CanonicalTransitionDefinition).Assembly
            .GetTypes()
            .Where(static type => string.Equals(
                type.Namespace,
                "Cohesive.Transitions.IR",
                StringComparison.Ordinal))
            .SelectMany(static type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.SetMethod?.IsPublic == true)
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Order(StringComparer.Ordinal)];
}
