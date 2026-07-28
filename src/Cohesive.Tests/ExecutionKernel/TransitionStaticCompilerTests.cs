using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TransitionStaticCompilerTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract Int64Contract = new(new ScalarTypeRef(ScalarTypeKind.Int64));

    [Fact]
    public void Compile_NestedChoiceMatchAndParentLet_ProducesPathSensitiveRequirements()
    {
        var definition = RoutingDefinition();

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        Assert.NotNull(result.Plan);
        var analysis = result.Plan.Analysis;
        var writes = analysis.GetRequirements<TransitionWriteRequirement>();
        var status = Assert.Single(writes, static write => write.Path.Matches("status") && !write.IsDerived);
        Assert.Equal(TransitionRequirementStrength.Must, status.InvocationStrength);
        Assert.Equal(4, status.Occurrences.Length);
        var emissions = analysis.GetRequirements<TransitionEmissionRequirement>();
        Assert.Equal(TransitionRequirementStrength.May, Assert.Single(emissions).InvocationStrength);
        var observations = analysis.GetRequirements<TransitionObservationRequirement>();
        Assert.DoesNotContain(observations, static requirement => HasPath(requirement, "unused"));
        var eligible = Assert.Single(observations, static requirement => HasPath(requirement, "eligible"));
        Assert.Equal(TransitionRequirementStrength.May, eligible.InvocationStrength);
        Assert.Contains(eligible.Occurrences, static occurrence =>
            (occurrence.Influence & TransitionObservationInfluence.Branch) != 0);
        var match = Assert.Single(analysis.Branches, static branch => branch.Node.Value == "decision-match");
        Assert.Equal(TransitionProofStatus.Proven, match.Coverage);
        Assert.All(
            analysis.ExpressionSites,
            static site => Assert.False(string.IsNullOrWhiteSpace(site.Analysis.Site.DiagnosticLocation)));
    }

    [Fact]
    public void Compile_ExclusiveBranchesMayWriteSamePath_ButSequentialWritesFail()
    {
        var terminal = Outcome("done", TransitionOutcomeDisposition.Applied, "ok");
        var choice = new ChoiceTransitionNode(
            new("choice"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [new(new("case"), Expr.Param("flag"), Sequence("case-body", Set("case-write", "status", "yes")))],
            new(new("fallback"), Sequence("fallback-body", Set("fallback-write", "status", "no"))));
        var valid = Definition(
            Sequence("root", choice, terminal),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));
        var invalid = Definition(
            Sequence(
                "root",
                Set("first", "status", "one"),
                Set("second", "status", "two"),
                terminal),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var validResult = TransitionStaticCompiler.Compile(Document(valid));
        var invalidResult = TransitionStaticCompiler.Compile(Document(invalid));

        Assert.True(validResult.IsSuccessful, Format(validResult.Validation));
        Assert.Equal(
            TransitionRequirementStrength.Must,
            Assert.Single(validResult.Plan!.Analysis.GetRequirements<TransitionWriteRequirement>()).InvocationStrength);
        Assert.False(invalidResult.IsSuccessful);
        var diagnostic = Assert.Single(
            invalidResult.Validation.Diagnostics,
            static value => value.Code == TransitionCompilationDiagnosticCodes.WriteOverlap);
        Assert.Equal("second", diagnostic.Evidence?.Subject);
        Assert.Contains("/definition/body/steps/0/path", diagnostic.Evidence!.RelatedLocations);
    }

    [Fact]
    public void Compile_ChoiceProof_DistinguishesUnknownAndImpossible()
    {
        var unknown = Definition(
            Sequence(
                "root",
                new ChoiceTransitionNode(
                    new("choice"),
                    TransitionCaseSelection.OrderedFirstMatch,
                    TransitionBranchCompleteness.Exhaustive,
                    [new(new("unknown-case"), Expr.Param("flag"), Sequence("unknown-body", Outcome("unknown-outcome")))])),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))));
        var proven = Definition(
            Sequence(
                "root",
                new ChoiceTransitionNode(
                    new("choice"),
                    TransitionCaseSelection.OrderedFirstMatch,
                    TransitionBranchCompleteness.Exhaustive,
                    [
                        new(
                            new("always"),
                            Expr.Or(Expr.Const(true), Expr.Param("flag")),
                            Sequence("always-body", Outcome("always-outcome"))),
                        new(new("impossible"), Expr.Const(true), Sequence("impossible-body", Set("dead-write", "status", "dead"), Outcome("dead-outcome")))
                    ])),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var unknownResult = TransitionStaticCompiler.Compile(Document(unknown));
        var provenResult = TransitionStaticCompiler.Compile(Document(proven));

        Assert.False(unknownResult.IsSuccessful);
        Assert.Contains(
            unknownResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.ExhaustivenessUnknown);
        Assert.True(provenResult.IsSuccessful, Format(provenResult.Validation));
        var branch = Assert.Single(provenResult.Plan!.Analysis.Branches);
        Assert.Equal(TransitionProofStatus.Proven, branch.Coverage);
        Assert.Equal(
            TransitionProofStatus.Impossible,
            Assert.Single(branch.Alternatives, static alternative => alternative.Node.Value == "impossible").Status);
        Assert.Empty(provenResult.Plan.Analysis.GetRequirements<TransitionWriteRequirement>());
    }

    [Fact]
    public void Compile_DynamicMatchDomain_RemainsOpen_AndKnownConstantReportsMissingWitness()
    {
        ValueContract decision = new(new EnumTypeRef("Decision", ["approve", "hold"]));
        var dynamic = MatchDefinition(decision, includeHold: true);
        var known = Definition(
            Sequence(
                "known-root",
                new MatchTransitionNode(
                    new("known-match"),
                    TransitionCaseSelection.OrderedFirstMatch,
                    TransitionBranchCompleteness.Exhaustive,
                    new LiteralExpr(decision.Type!, ObservationValue.FromString("hold")),
                    decision,
                    [
                        new(
                            new("approve"),
                            EnumValue(decision, "approve"),
                            Sequence("approve-body", Outcome("approve-outcome")))
                    ])));

        var dynamicResult = TransitionStaticCompiler.Compile(Document(dynamic));
        var knownResult = TransitionStaticCompiler.Compile(Document(known));

        Assert.False(dynamicResult.IsSuccessful);
        var dynamicDiagnostic = Assert.Single(
            dynamicResult.Validation.Diagnostics,
            static value => value.Code == TransitionCompilationDiagnosticCodes.ExhaustivenessUnknown);
        Assert.Contains("open family of failed values", dynamicDiagnostic.Message, StringComparison.Ordinal);

        Assert.False(knownResult.IsSuccessful);
        var knownDiagnostic = Assert.Single(
            knownResult.Validation.Diagnostics,
            static value => value.Code == TransitionCompilationDiagnosticCodes.ExhaustivenessDisproven);
        Assert.Contains("hold", knownDiagnostic.Evidence?.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OptionalNullableMatch_DistinguishesAbsentFromNull()
    {
        ValueContract optionalNullableString = new(
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        var match = new MatchTransitionNode(
            new("optional-match"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            Expr.Field("maybeStatus"),
            optionalNullableString,
            [
                new(
                    new("absent-case"),
                    PortableValue.Absent(optionalNullableString),
                    Sequence("absent-body", Set("absent-write", "status", "absent"))),
                new(
                    new("null-case"),
                    PortableValue.Null(optionalNullableString),
                    Sequence("null-body", Set("null-write", "status", "null")))
            ],
            new(new("value-fallback"), Sequence(
                "value-body",
                Set("value-write", "status", "value"))));
        var definition = Definition(
            Sequence("root", match, Outcome("outcome")),
            observation: ObjectContract(
                new ObjectFieldTypeDef(
                    "maybeStatus",
                    new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable),
                new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var branch = Assert.Single(result.Plan!.Analysis.Branches);
        Assert.Equal(TransitionProofStatus.Proven, branch.Coverage);
        Assert.Equal(
            ["absent-case", "null-case", "value-fallback"],
            branch.Alternatives.Select(static alternative => alternative.Node.Value));
        var status = Assert.Single(
            result.Plan.Analysis.GetRequirements<TransitionWriteRequirement>(),
            static write => write.Path.Matches("status"));
        Assert.Equal(TransitionRequirementStrength.Must, status.InvocationStrength);
        Assert.Equal(3, status.Occurrences.Length);
        var absent = Assert.Single(status.Occurrences, static occurrence => occurrence.Node.Value == "absent-write");
        var @null = Assert.Single(status.Occurrences, static occurrence => occurrence.Node.Value == "null-write");
        Assert.NotEqual(absent.Condition, @null.Condition);
        var observation = Assert.Single(
            result.Plan.Analysis.GetRequirements<TransitionObservationRequirement>(),
            static requirement => HasPath(requirement, "maybeStatus"));
        Assert.True((observation.Influences & TransitionObservationInfluence.Branch) != 0);
    }

    [Fact]
    public void Compile_DuplicateMatchArm_IsImpossibleAndExcludedFromMayRequirements()
    {
        var match = new MatchTransitionNode(
            new("boolean-match"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            Expr.Param("flag"),
            BooleanContract,
            [
                new(
                    new("true-case"),
                    PortableValue.Concrete(BooleanContract, ObservationValue.FromBool(true)),
                    Sequence("true-body", Set("live-write", "live", "yes"))),
                new(
                    new("duplicate-true-case"),
                    PortableValue.Concrete(BooleanContract, ObservationValue.FromBool(true)),
                    Sequence("duplicate-body", Set("dead-write", "dead", "never")))
            ],
            new(new("false-fallback"), Sequence(
                "false-body",
                Set("fallback-write", "fallback", "no"))));
        var definition = Definition(
            Sequence("root", match, Outcome("outcome")),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(
                new ObjectFieldTypeDef("live", new ScalarTypeRef(ScalarTypeKind.String)),
                new ObjectFieldTypeDef("dead", new ScalarTypeRef(ScalarTypeKind.String)),
                new ObjectFieldTypeDef("fallback", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var branch = Assert.Single(result.Plan!.Analysis.Branches);
        Assert.Equal(
            TransitionProofStatus.Impossible,
            Assert.Single(
                branch.Alternatives,
                static alternative => alternative.Node.Value == "duplicate-true-case").Status);
        var writes = result.Plan.Analysis.GetRequirements<TransitionWriteRequirement>();
        Assert.DoesNotContain(writes, static write => write.Path.Matches("dead"));
        Assert.Contains(writes, static write => write.Path.Matches("live"));
        Assert.Contains(writes, static write => write.Path.Matches("fallback"));
        Assert.All(writes, static write => Assert.Equal(TransitionRequirementStrength.May, write.InvocationStrength));
    }

    [Fact]
    public void Compile_ConcreteBooleanCases_DoNotEraseFallbackForPortableUnknownOrFailedValues()
    {
        var match = new MatchTransitionNode(
            new("boolean-match"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            Expr.Param("flag"),
            BooleanContract,
            [
                new(
                    new("true-case"),
                    PortableValue.Concrete(BooleanContract, ObservationValue.FromBool(true)),
                    Sequence("true-body", Set("true-write", "status", "true"))),
                new(
                    new("false-case"),
                    PortableValue.Concrete(BooleanContract, ObservationValue.FromBool(false)),
                    Sequence("false-body", Set("false-write", "status", "false")))
            ],
            new(new("portable-fallback"), Sequence("fallback-body", Set("fallback-write", "fallback", "fallback"))));
        var definition = Definition(
            Sequence("root", match, Outcome("outcome")),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(
                new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String)),
                new ObjectFieldTypeDef("fallback", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var branch = Assert.Single(result.Plan!.Analysis.Branches);
        Assert.Equal(
            TransitionProofStatus.Unknown,
            Assert.Single(
                branch.Alternatives,
                static alternative => alternative.Node.Value == "portable-fallback").Status);
        Assert.Contains(
            result.Plan.Analysis.GetRequirements<TransitionWriteRequirement>(),
            static requirement => requirement.Path.Matches("fallback"));
    }

    [Theory]
    [InlineData(true, ExprAnalysisDiagnosticCodes.BindingNotVisible)]
    [InlineData(false, TransitionCompilationDiagnosticCodes.BindingDuplicate)]
    public void Compile_LetBindings_AreForwardOnlyAndDefinitionUnique(bool useBeforeDeclaration, string expectedCode)
    {
        ValueBindingId binding = new("selected");
        TransitionNode[] steps = useBeforeDeclaration
            ?
            [
                Set("write", "status", Expr.BoundValue(binding)),
                new LetTransitionNode(new("let"), binding, StringContract, Expr.Const("ready")),
                Outcome("outcome")
            ]
            :
            [
                new LetTransitionNode(new("first-let"), binding, StringContract, Expr.Const("ready")),
                new ChoiceTransitionNode(
                    new("choice"),
                    TransitionCaseSelection.OrderedFirstMatch,
                    TransitionBranchCompleteness.Fallback,
                    [new(new("case"), Expr.Const(true), Sequence(
                        "case-body",
                        new LetTransitionNode(new("second-let"), binding, StringContract, Expr.Const("again")),
                        Outcome("case-outcome")))],
                    new(new("fallback"), Sequence("fallback-body", Outcome("fallback-outcome"))))
            ];
        var definition = Definition(
            Sequence("root", steps),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Compile_BranchLocalLet_IsNotVisibleAfterChoice()
    {
        ValueBindingId branchBinding = new("branchValue");
        var choice = new ChoiceTransitionNode(
            new("choice"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [new(new("case"), Expr.Param("flag"), Sequence(
                "case-body",
                new LetTransitionNode(
                    new("case-let"),
                    branchBinding,
                    StringContract,
                    Expr.Const("case"))))],
            new(new("fallback"), Sequence(
                "fallback-body",
                new LetTransitionNode(
                    new("fallback-let"),
                    new("fallbackValue"),
                    StringContract,
                    Expr.Const("fallback")))));
        var definition = Definition(
            Sequence(
                "root",
                choice,
                Set("write", "status", Expr.BoundValue(branchBinding)),
                Outcome("outcome")),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == ExprAnalysisDiagnosticCodes.BindingNotVisible);
        Assert.Equal("/definition/body/steps/1/operation/value", diagnostic.Location);
        Assert.Equal("write", diagnostic.Evidence?.Subject);
    }

    [Fact]
    public void Compile_DominatingWriteConflictsWithWritesInEachRealizedBranch()
    {
        var choice = new ChoiceTransitionNode(
            new("choice"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [new(new("case"), Expr.Param("flag"), Sequence(
                "case-body",
                Set("case-write", "status", "case")))],
            new(new("fallback"), Sequence(
                "fallback-body",
                Set("fallback-write", "status", "fallback"))));
        var definition = Definition(
            Sequence(
                "root",
                Set("dominating-write", "status", "before"),
                choice,
                Outcome("outcome")),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.False(result.IsSuccessful);
        var diagnostics = result.Validation.Diagnostics
            .Where(static diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.WriteOverlap)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Equal(["case-write", "fallback-write"], diagnostics.Select(static diagnostic => diagnostic.Evidence?.Subject));
        Assert.All(diagnostics, static diagnostic => Assert.Equal(
            ["/definition/body/steps/0/path"],
            diagnostic.Evidence?.RelatedLocations));
    }

    [Fact]
    public void Compile_RepeatedAndRoundTrippedInput_ProducesDeterministicSemanticOutput()
    {
        var document = Document(RoutingDefinition());
        var restored = ExecutionDefinitionJsonSerializer.Deserialize(
            ExecutionDefinitionJsonSerializer.Serialize(document));

        var first = TransitionStaticCompiler.Compile(document);
        var second = TransitionStaticCompiler.Compile(restored);

        Assert.True(first.IsSuccessful, Format(first.Validation));
        Assert.True(second.IsSuccessful, Format(second.Validation));
        Assert.Equal(
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(first.Plan!.Document),
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(second.Plan!.Document));
        Assert.Equal(first.Plan.Definition, second.Plan.Definition);
        Assert.Equal(
            first.Plan.Analysis.ExpressionSites.Select(static site => (
                site.Node,
                site.Kind,
                site.Analysis.Site.Id,
                site.Analysis.ResultCategory,
                site.Analysis.KnownResult,
                site.Analysis.KnownConstant)).ToArray(),
            second.Plan.Analysis.ExpressionSites.Select(static site => (
                site.Node,
                site.Kind,
                site.Analysis.Site.Id,
                site.Analysis.ResultCategory,
                site.Analysis.KnownResult,
                site.Analysis.KnownConstant)).ToArray());
        Assert.Equal(
            DomainSignature(first.Plan.Analysis),
            DomainSignature(second.Plan.Analysis));
        Assert.Equal(
            first.Plan.Analysis.Requirements
                .Select(requirement => RequirementSignature(first.Plan.Analysis, requirement))
                .ToArray(),
            second.Plan.Analysis.Requirements
                .Select(requirement => RequirementSignature(second.Plan.Analysis, requirement))
                .ToArray());
        Assert.Equal(
            first.Plan.Analysis.Branches
                .Select(branch => BranchSignature(first.Plan.Analysis, branch))
                .ToArray(),
            second.Plan.Analysis.Branches
                .Select(branch => BranchSignature(second.Plan.Analysis, branch))
                .ToArray());
        Assert.Equal(
            first.Plan.Analysis.DerivedFields.Select(DerivedFieldSignature).ToArray(),
            second.Plan.Analysis.DerivedFields.Select(DerivedFieldSignature).ToArray());
        Assert.Equal(first.Validation.Diagnostics.ToArray(), second.Validation.Diagnostics.ToArray());
        Assert.Throws<ArgumentException>(() =>
            first.Plan.Analysis.Conditions.Format(second.Plan.Analysis.CommitDomain));
    }

    [Fact]
    public void Compile_RequiresOutcomeAndRejectsReachableTailAfterOutcome()
    {
        var missing = Definition(Sequence("root", Set("write", "status", "updated")),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));
        var unreachable = Definition(
            Sequence("root", Outcome("outcome"), Set("late", "status", "late")),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var missingResult = TransitionStaticCompiler.Compile(Document(missing));
        var unreachableResult = TransitionStaticCompiler.Compile(Document(unreachable));

        Assert.Contains(
            missingResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.OutcomeMissing);
        Assert.Contains(
            unreachableResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.NodeUnreachable);
    }

    [Fact]
    public void Compile_IncrementAddsSparseReadAndCommitValidationDemand()
    {
        var definition = Definition(
            Sequence(
                "root",
                new UpdateTransitionNode(
                    new("increment"),
                    FieldPath.FromField("score"),
                    new IncrementTransitionPatch(Expr.Const(1))),
                Outcome("outcome")),
            observation: ObjectContract(new ObjectFieldTypeDef("score", new ScalarTypeRef(ScalarTypeKind.Int64))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var requirement = Assert.Single(
            result.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>(),
            static value => HasPath(value, "score"));
        Assert.Equal(TransitionRequirementStrength.Must, requirement.InvocationStrength);
        Assert.True(requirement.RequiresCommitValidation);
        Assert.True((requirement.Influences & TransitionObservationInfluence.PatchTarget) != 0);
    }

    [Fact]
    public void Compile_UnknownAndAmbientIntrinsicsFailClosedWithCapabilityProvenance()
    {
        var unknown = Definition(Sequence(
            "root",
            new LetTransitionNode(
                new("clock"),
                new("now"),
                StringContract,
                new CallExpr(
                    "clock.now",
                    [],
                    new ScalarTypeRef(ScalarTypeKind.String))),
            Outcome("outcome")));
        var ambient = Definition(Sequence(
            "root",
            new LetTransitionNode(
                new("rows"),
                new("sourceRows"),
                new ValueContract(new JsonTypeRef(JsonTypeKind.Array)),
                new CallExpr(
                    ExprFunctionNames.SourceRows,
                    [],
                    new JsonTypeRef(JsonTypeKind.Array))),
            Outcome("outcome")));

        var unknownResult = TransitionStaticCompiler.Compile(Document(unknown));
        var ambientResult = TransitionStaticCompiler.Compile(Document(ambient));

        Assert.Contains(
            unknownResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.FunctionUnknown);
        Assert.Contains(
            ambientResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.AmbientCapabilityUnavailable);
        var capabilities = ambientResult.Analysis!.GetRequirements<TransitionCapabilityRequirement>();
        Assert.Contains(capabilities, static requirement =>
            requirement.Capability.Capability == ExprCapabilities.SourceSet);
    }

    [Theory]
    [InlineData(TransitionOutcomeDisposition.Applied)]
    [InlineData(TransitionOutcomeDisposition.NoChange)]
    [InlineData(TransitionOutcomeDisposition.DomainRejected)]
    public void Compile_TerminalEmissionIntent_DefinesCommitAndFreshnessDomain(
        TransitionOutcomeDisposition disposition)
    {
        var definition = Definition(
            Sequence(
                "root",
                new EmitTransitionNode(new("emit"), EmissionContract(), Expr.Field("payload")),
                Outcome("outcome", disposition)),
            observation: ObjectContract(
                new ObjectFieldTypeDef("payload", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var analysis = result.Plan!.Analysis;
        var emission = Assert.Single(analysis.GetRequirements<TransitionEmissionRequirement>());
        Assert.Equal(EmissionContract(), emission.Contract);
        Assert.Equal(TransitionRequirementStrength.Must, emission.InvocationStrength);
        var payload = Assert.Single(
            analysis.GetRequirements<TransitionObservationRequirement>(),
            static requirement => HasPath(requirement, "payload"));
        Assert.Equal(TransitionRequirementStrength.Must, payload.CommitValidationInvocationStrength);
        Assert.True(analysis.Conditions.Implies(analysis.InvocationDomain, analysis.CommitDomain));
    }

    [Fact]
    public void Compile_DiagnosticsRetainOwningNodeAndDeepestSourceMapping()
    {
        var definition = Definition(
            Sequence(
                "root",
                Set("bad-write", "status", Expr.Const(1)),
                Outcome("outcome")),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));
        ExecutionSourceMap sourceMap = new(
        [
            new("src/Review.cs:10", new(["body", "steps", "0"])),
            new("src/Review.cs:12", new(["body", "steps", "0", "operation", "value"]))
        ]);

        var result = TransitionStaticCompiler.Compile(Document(definition, sourceMap));

        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Equal("bad-write", diagnostic.Evidence?.Subject);
        Assert.Equal(["src/Review.cs:12"], diagnostic.Evidence?.SourceReferences);
        Assert.Equal("/definition/body/steps/0/operation/value", diagnostic.Location);
    }

    [Fact]
    public void Compile_CanonicalValidationDiagnosticsPreserveDetailsAndResolveAuthoredSource()
    {
        var definition = Definition(
            Sequence(
                "root",
                Outcome("duplicate"),
                Outcome("duplicate")));
        ExecutionSourceMap sourceMap = new(
        [
            new("src/Review.cs:20", new(["body", "steps", "0"])),
            new("src/Review.cs:30", new(["body", "steps", "1"]))
        ]);

        var result = TransitionStaticCompiler.Compile(Document(definition, sourceMap));

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == TransitionDefinitionDiagnosticCodes.NodeIdentityDuplicate);
        Assert.Equal("/definition/body/steps/1/id", diagnostic.Location);
        Assert.Equal("canonicalValidation", diagnostic.Evidence?.Stage);
        Assert.Equal(["src/Review.cs:30"], diagnostic.Evidence?.SourceReferences);
        Assert.Contains(
            "/body/steps/0/id",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_DerivedFieldsBuildDependencyAndAffectedClosure_AndCyclesFail()
    {
        var graph = DerivedGraph(cycle: false);
        ShapeId reviewShape = new("review");
        var observation = ValueContract.FromShape(graph.GetShape(reviewShape), graph.Qualify(reviewShape));
        var definition = Definition(
            Sequence("root", Set("set-raw", "raw", Expr.Const(20L)), Outcome("outcome")),
            observation: observation);

        var result = TransitionStaticCompiler.Compile(Document(definition), graph);

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var normalized = Assert.Single(
            result.Plan!.Analysis.DerivedFields,
            static field => field.Field.Matches("normalized"));
        var eligible = Assert.Single(
            result.Plan.Analysis.DerivedFields,
            static field => field.Field.Matches("eligible"));
        Assert.Equal(["raw"], normalized.BaseDependencies.Select(static path => path.ToString()));
        Assert.Equal(["raw"], eligible.BaseDependencies.Select(static path => path.ToString()));
        Assert.True(normalized.AffectedByWrites);
        Assert.True(eligible.AffectedByWrites);
        Assert.Equal(
            2,
            result.Plan.Analysis.GetRequirements<TransitionWriteRequirement>()
                .Count(static write => write.IsDerived));
        var observations = result.Plan.Analysis.GetRequirements<TransitionObservationRequirement>();
        Assert.Single(observations);
        AssertPatchTargetRead(observations, "raw", "set-raw");
        Assert.DoesNotContain(
            observations,
            static requirement => HasPath(requirement, "normalized")
                                  || HasPath(requirement, "eligible"));
        var derivedAdd = Assert.Single(
            result.Plan.Analysis.GetRequirements<TransitionCapabilityRequirement>(),
            requirement => requirement.Capability.Capability == ExprCapabilities.ForBinary(BinaryOperator.Add)
                           && requirement.Occurrences.Any(static occurrence =>
                               (occurrence.Influence & TransitionObservationInfluence.DerivedField) != 0));
        Assert.Equal(TransitionRequirementStrength.Must, derivedAdd.InvocationStrength);

        var cycleGraph = DerivedGraph(cycle: true);
        var cycleObservation = ValueContract.FromShape(
            cycleGraph.GetShape(reviewShape),
            cycleGraph.Qualify(reviewShape));
        var cycleResult = TransitionStaticCompiler.Compile(
            Document(Definition(Sequence("root", Outcome("outcome")), observation: cycleObservation)),
            cycleGraph);
        Assert.Contains(
            cycleResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.DerivedFieldCycle);
    }

    [Fact]
    public void Compile_SetBaseForComputedInvariant_DoesNotDemandStaleCandidateObservations()
    {
        ShapeId reviewShape = new("review");
        ShapeGraph graph = new(
            new("computed-invariant"),
            [
                new(
                    reviewShape,
                    [
                        new(new FieldName("raw"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        Computed("normalized", Expr.Add(Expr.Field("raw"), Expr.Const(1)))
                    ])
            ]);
        var observation = ValueContract.FromShape(
            graph.GetShape(reviewShape),
            graph.Qualify(reviewShape));
        CanonicalTransitionDefinition definition = new(
            new(new JsonTypeRef(JsonTypeKind.Object)),
            observation,
            StringContract,
            [],
            Sequence("root", Set("set-raw", "raw", Expr.Const(20L)), Outcome("outcome")),
            [new(new("normalized-invariant"), Expr.Gt(Expr.Field("normalized"), Expr.Const(10)))]);

        var result = TransitionStaticCompiler.Compile(Document(definition), graph);

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var observations = result.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>();
        Assert.Single(observations);
        AssertPatchTargetRead(observations, "raw", "set-raw");
        Assert.DoesNotContain(observations, static requirement => HasPath(requirement, "normalized"));
        Assert.Contains(
            result.Plan.Analysis.GetRequirements<TransitionWriteRequirement>(),
            static requirement => requirement.IsDerived && requirement.Path.Matches("normalized"));
    }

    [Fact]
    public void Compile_ConditionalComputedField_PreservesExclusiveReadAndCapabilityConditions()
    {
        ShapeId reviewShape = new("review");
        var conditional = new ConditionalExpr(
            Expr.Field("flag"),
            Expr.Add(Expr.Field("left"), Expr.Const(1)),
            Expr.Mul(Expr.Field("right"), Expr.Const(2)),
            new ScalarTypeRef(ScalarTypeKind.Int64));
        ShapeGraph graph = new(
            new("conditional-computed"),
            [
                new(
                    reviewShape,
                    [
                        new(new FieldName("flag"), new ScalarTypeRef(ScalarTypeKind.Bool)),
                        new(new FieldName("left"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        new(new FieldName("right"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        Computed("selected", conditional)
                    ])
            ]);
        var observation = ValueContract.FromShape(
            graph.GetShape(reviewShape),
            graph.Qualify(reviewShape));
        var definition = Definition(
            Sequence("root", Set("set-left", "left", Expr.Const(20L)), Outcome("outcome")),
            observation: observation);

        var result = TransitionStaticCompiler.Compile(Document(definition), graph);

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var analysis = result.Plan!.Analysis;
        var observations = analysis.GetRequirements<TransitionObservationRequirement>();
        var flag = Assert.Single(observations, static requirement => HasPath(requirement, "flag"));
        AssertPatchTargetRead(observations, "left", "set-left");
        var right = Assert.Single(observations, static requirement => HasPath(requirement, "right"));
        Assert.Equal(3, observations.Count());
        Assert.Equal(
            TransitionObservationInfluence.Calculation | TransitionObservationInfluence.DerivedField,
            flag.Influences);
        Assert.Equal(
            TransitionObservationInfluence.Calculation | TransitionObservationInfluence.DerivedField,
            right.Influences);
        Assert.Equal(TransitionRequirementStrength.Must, flag.InvocationStrength);
        Assert.Equal(TransitionRequirementStrength.May, right.InvocationStrength);

        var capabilities = analysis.GetRequirements<TransitionCapabilityRequirement>();
        var add = Assert.Single(
            capabilities,
            requirement => requirement.Capability.Capability == ExprCapabilities.ForBinary(BinaryOperator.Add));
        var multiply = Assert.Single(
            capabilities,
            requirement => requirement.Capability.Capability == ExprCapabilities.ForBinary(BinaryOperator.Mul));
        Assert.Equal(TransitionRequirementStrength.May, add.InvocationStrength);
        Assert.Equal(TransitionRequirementStrength.May, multiply.InvocationStrength);
        Assert.True(analysis.Conditions.AreMutuallyExclusive(add.Condition, multiply.Condition));
    }

    [Fact]
    public void Compile_DownstreamComputedField_ReadsUnaffectedMaterializedDependencyWithoutExpandingIt()
    {
        ShapeId reviewShape = new("review");
        var selected = new ConditionalExpr(
            Expr.Field("flag"),
            Expr.Field("left"),
            Expr.Field("right"),
            new ScalarTypeRef(ScalarTypeKind.Int64));
        ShapeGraph graph = new(
            new("materialized-computed-dag"),
            [
                new(
                    reviewShape,
                    [
                        new(new FieldName("flag"), new ScalarTypeRef(ScalarTypeKind.Bool)),
                        new(new FieldName("left"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        new(new FieldName("right"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        new(new FieldName("version"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        Computed("selected", selected),
                        Computed("final", Expr.Add(Expr.Field("selected"), Expr.Field("version")))
                    ])
            ]);
        var observation = ValueContract.FromShape(
            graph.GetShape(reviewShape),
            graph.Qualify(reviewShape));
        var definition = Definition(
            Sequence("root", Set("set-version", "version", Expr.Const(2L)), Outcome("outcome")),
            observation: observation);

        var result = TransitionStaticCompiler.Compile(Document(definition), graph);

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var analysis = result.Plan!.Analysis;
        var derivedWrites = analysis.GetRequirements<TransitionWriteRequirement>()
            .Where(static requirement => requirement.IsDerived)
            .ToArray();
        Assert.Collection(
            derivedWrites,
            static requirement => Assert.True(requirement.Path.Matches("final")));
        var observations = analysis.GetRequirements<TransitionObservationRequirement>();
        var selectedRead = Assert.Single(observations, static requirement => HasPath(requirement, "selected"));
        AssertPatchTargetRead(observations, "version", "set-version");
        Assert.Equal(2, observations.Count());
        Assert.Equal(
            TransitionObservationInfluence.Calculation | TransitionObservationInfluence.DerivedField,
            selectedRead.Influences);
        Assert.Equal(TransitionRequirementStrength.Must, selectedRead.InvocationStrength);
        Assert.DoesNotContain(
            observations,
            static requirement => HasPath(requirement, "flag")
                                  || HasPath(requirement, "left")
                                  || HasPath(requirement, "right"));
    }

    [Fact]
    public void Compile_NestedComputedFieldDependency_UsesOwningComputedNodeForClosureAndPropagation()
    {
        ShapeId reviewShape = new("review");
        var nested = new ObjectTypeRef(
            [new ObjectFieldTypeDef("child", new ScalarTypeRef(ScalarTypeKind.Int64))]);
        ShapeGraph graph = new(
            new("nested-computed-dag"),
            [
                new(
                    reviewShape,
                    [
                        new(new FieldName("source"), nested),
                        new(new FieldName("other"), nested),
                        new(
                            new FieldName("z"),
                            nested,
                            role: FieldRole.Computed,
                            mutability: FieldMutability.Computed,
                            compute: new(Expr.Field("source"))),
                        Computed("a", Expr.Add(Expr.Field("z.child"), Expr.Const(1)))
                    ])
            ]);
        var observation = ValueContract.FromShape(
            graph.GetShape(reviewShape),
            graph.Qualify(reviewShape));
        var definition = Definition(
            Sequence("root", Set("set-source", "source", Expr.Field("other")), Outcome("outcome")),
            observation: observation);

        var result = TransitionStaticCompiler.Compile(Document(definition), graph);

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var analysis = result.Plan!.Analysis;
        Assert.Equal(
            ["a", "z"],
            analysis.GetRequirements<TransitionWriteRequirement>()
                .Where(static requirement => requirement.IsDerived)
                .Select(static requirement => requirement.Path.ToString())
                .ToArray());
        var downstream = Assert.Single(
            analysis.DerivedFields,
            static field => field.Field.Matches("a"));
        Assert.True(downstream.AffectedByWrites);
        Assert.Equal(["source"], downstream.BaseDependencies.Select(static path => path.ToString()));
        var observations = analysis.GetRequirements<TransitionObservationRequirement>();
        var other = Assert.Single(observations, static requirement => HasPath(requirement, "other"));
        AssertPatchTargetRead(observations, "source", "set-source");
        Assert.Equal(2, observations.Count());
        Assert.Equal(TransitionObservationInfluence.Calculation, other.Influences);
        Assert.DoesNotContain(
            observations,
            static requirement => HasPath(requirement, "z")
                                  || HasPath(requirement, "z.child"));
    }

    [Fact]
    public void Compile_ComputedFieldsAndInvariants_CannotDependOnInvocationInput()
    {
        ShapeId reviewShape = new("review");
        ShapeGraph graph = new(
            new("state-only-scope"),
            [
                new(
                    reviewShape,
                    [
                        new(new FieldName("raw"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        Computed("normalized", Expr.Param("increment"))
                    ])
            ]);
        var observation = ValueContract.FromShape(
            graph.GetShape(reviewShape),
            graph.Qualify(reviewShape));
        var computedDefinition = Definition(
            Sequence("computed-root", Outcome("computed-outcome")),
            input: ObjectContract(new ObjectFieldTypeDef("increment", new ScalarTypeRef(ScalarTypeKind.Int64))),
            observation: observation);
        CanonicalTransitionDefinition invariantDefinition = new(
            ObjectContract(new ObjectFieldTypeDef("allow", new ScalarTypeRef(ScalarTypeKind.Bool))),
            ObjectContract(new ObjectFieldTypeDef("valid", new ScalarTypeRef(ScalarTypeKind.Bool))),
            StringContract,
            [],
            Sequence("invariant-root", Outcome("invariant-outcome", TransitionOutcomeDisposition.NoChange)),
            [
                new(
                    new("state-invariant"),
                    Expr.Field(TransitionBindingIds.Input, FieldPath.FromField("allow")))
            ]);

        var computedResult = TransitionStaticCompiler.Compile(Document(computedDefinition), graph);
        var invariantResult = TransitionStaticCompiler.Compile(Document(invariantDefinition));

        Assert.Contains(
            computedResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ParameterNotDeclared
                                 && diagnostic.Evidence!.Subject == "compiler/computed/normalized");
        Assert.Contains(
            invariantResult.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.BindingNotVisible
                                 && diagnostic.Evidence!.Subject == "state-invariant");
    }

    [Fact]
    public void Compile_NoChangeAcceptedPath_RetainsInvariantRequirements()
    {
        CanonicalTransitionDefinition definition = new(
            new(new JsonTypeRef(JsonTypeKind.Object)),
            ObjectContract(new ObjectFieldTypeDef("valid", new ScalarTypeRef(ScalarTypeKind.Bool))),
            StringContract,
            [],
            Sequence("root", Outcome("outcome", TransitionOutcomeDisposition.NoChange)),
            [new(new("valid-invariant"), Expr.Field("valid"))]);

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var observation = Assert.Single(
            result.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>(),
            static requirement => HasPath(requirement, "valid"));
        Assert.Equal(TransitionRequirementStrength.Must, observation.InvocationStrength);
        Assert.Equal(TransitionObservationInfluence.Invariant, observation.Influences);
        Assert.Contains(
            observation.Occurrences,
            occurrence => occurrence.Node.Value == "valid-invariant"
                          && result.Plan.Analysis.Conditions.Format(occurrence.Condition) == "true");
    }

    [Fact]
    public void Compile_InvariantReadsCandidateFieldSuppliedByPatch_NotStaleObservation()
    {
        CanonicalTransitionDefinition definition = new(
            new(new JsonTypeRef(JsonTypeKind.Object)),
            ObjectContract(new ObjectFieldTypeDef("valid", new ScalarTypeRef(ScalarTypeKind.Bool))),
            StringContract,
            [],
            Sequence(
                "root",
                new UpdateTransitionNode(
                    new("establish-valid"),
                    FieldPath.FromField("valid"),
                    new SetTransitionPatch(Expr.Const(true))),
                Outcome("outcome")),
            [new(new("valid-invariant"), Expr.Field("valid"))]);

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var observations = result.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>();
        Assert.Single(observations);
        AssertPatchTargetRead(observations, "valid", "establish-valid");
        Assert.Contains(
            result.Plan.Analysis.ExpressionSites,
            static site => site.Node.Value == "valid-invariant"
                           && site.Kind == TransitionExpressionSiteKind.InvariantPredicate);
    }

    [Fact]
    public void Compile_StaticallyFalseApplicableInvariant_FailsClosedAndRemovesAcceptedCommitDomain()
    {
        CanonicalTransitionDefinition definition = new(
            new(new JsonTypeRef(JsonTypeKind.Object)),
            new(new JsonTypeRef(JsonTypeKind.Object)),
            StringContract,
            [],
            Sequence("root", Outcome("outcome")),
            [new(new("impossible-invariant"), Expr.Const(false))]);

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == TransitionCompilationDiagnosticCodes.InvariantDisproven);
        Assert.Equal("impossible-invariant", diagnostic.Evidence?.Subject);
        Assert.NotNull(result.Analysis);
        Assert.False(result.Analysis.Conditions.IsSatisfiable(result.Analysis.AcceptedDomain));
        Assert.False(result.Analysis.Conditions.IsSatisfiable(result.Analysis.CommitDomain));
        Assert.DoesNotContain(
            result.Analysis.GetRequirements<TransitionOutcomeRequirement>(),
            static requirement => requirement.DecisionKind == TransitionDecisionKind.Applied);
    }

    [Fact]
    public void Compile_ExpressionEvaluationConditions_RemoveDeadReadsAndPartitionConditionalReads()
    {
        var deadReadDefinition = new CanonicalTransitionDefinition(
            new(new JsonTypeRef(JsonTypeKind.Object)),
            ObjectContract(new ObjectFieldTypeDef("secret", new ScalarTypeRef(ScalarTypeKind.Bool))),
            StringContract,
            [
                new TransitionAdmissionRule(
                    new("deny"),
                    new BinaryExpr(BinaryOperator.And, Expr.Const(false), Expr.Field("secret")),
                    Expr.Const("denied"))
            ],
            Sequence("dead-root", Outcome("unreachable-applied")),
            []);
        var deadReadResult = TransitionStaticCompiler.Compile(Document(deadReadDefinition));

        Assert.True(deadReadResult.IsSuccessful, Format(deadReadResult.Validation));
        Assert.DoesNotContain(
            deadReadResult.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>(),
            static requirement => HasPath(requirement, "secret"));

        var conditional = new ConditionalExpr(
            Expr.Param("flag"),
            Expr.Field("whenTrue"),
            Expr.Field("whenFalse"),
            new ScalarTypeRef(ScalarTypeKind.String));
        var conditionalDefinition = Definition(
            Sequence(
                "conditional-root",
                new OutcomeTransitionNode(
                    new("conditional-outcome"),
                    TransitionOutcomeDisposition.Applied,
                    conditional)),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(
                new ObjectFieldTypeDef("whenTrue", new ScalarTypeRef(ScalarTypeKind.String)),
                new ObjectFieldTypeDef("whenFalse", new ScalarTypeRef(ScalarTypeKind.String))));
        var conditionalResult = TransitionStaticCompiler.Compile(Document(conditionalDefinition));

        Assert.True(conditionalResult.IsSuccessful, Format(conditionalResult.Validation));
        var analysis = conditionalResult.Plan!.Analysis;
        var reads = analysis.GetRequirements<TransitionObservationRequirement>();
        var whenTrue = Assert.Single(reads, static requirement => HasPath(requirement, "whenTrue"));
        var whenFalse = Assert.Single(reads, static requirement => HasPath(requirement, "whenFalse"));
        Assert.Equal(TransitionRequirementStrength.May, whenTrue.InvocationStrength);
        Assert.Equal(TransitionRequirementStrength.May, whenFalse.InvocationStrength);
        Assert.True(analysis.Conditions.AreMutuallyExclusive(whenTrue.Condition, whenFalse.Condition));
    }

    [Fact]
    public void Compile_WholeObservationAccessIsFirstClass_AndWholeInputIsNotAnObservation()
    {
        var aggregate = ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String)));
        var observationDefinition = new CanonicalTransitionDefinition(
            new(new JsonTypeRef(JsonTypeKind.Object)),
            aggregate,
            aggregate,
            [],
            Sequence(
                "observation-root",
                new LetTransitionNode(
                    new("snapshot"),
                    new("snapshot-value"),
                    aggregate,
                    Expr.BoundValue(TransitionBindingIds.Observation)),
                new OutcomeTransitionNode(
                    new("observation-outcome"),
                    TransitionOutcomeDisposition.Applied,
                    Expr.BoundValue(new("snapshot-value")))),
            []);
        var observationResult = TransitionStaticCompiler.Compile(Document(observationDefinition));

        Assert.True(observationResult.IsSuccessful, Format(observationResult.Validation));
        var whole = Assert.Single(
            observationResult.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>(),
            static requirement => requirement.Access.IsWhole);
        Assert.Equal(TransitionRequirementStrength.Must, whole.InvocationStrength);
        Assert.Equal(TransitionRequirementStrength.Must, whole.CommitValidationInvocationStrength);
        Assert.Equal(2, whole.Occurrences.Length);

        var inputDefinition = new CanonicalTransitionDefinition(
            aggregate,
            new(new JsonTypeRef(JsonTypeKind.Object)),
            aggregate,
            [],
            Sequence(
                "input-root",
                new OutcomeTransitionNode(
                    new("input-outcome"),
                    TransitionOutcomeDisposition.Applied,
                    Expr.BoundValue(TransitionBindingIds.Input))),
            []);
        var inputResult = TransitionStaticCompiler.Compile(Document(inputDefinition));

        Assert.True(inputResult.IsSuccessful, Format(inputResult.Validation));
        Assert.Empty(inputResult.Plan!.Analysis.GetRequirements<TransitionObservationRequirement>());
    }

    [Fact]
    public void Compile_LetLineageRetainsProducerGuardsWhenConsumedByOutcome()
    {
        ValueBindingId selected = new("selected");
        var selectedValue = new ConditionalExpr(
            Expr.Param("flag"),
            Expr.Field("whenTrue"),
            Expr.Field("whenFalse"),
            new ScalarTypeRef(ScalarTypeKind.String));
        var definition = Definition(
            Sequence(
                "root",
                new LetTransitionNode(new("select"), selected, StringContract, selectedValue),
                new OutcomeTransitionNode(
                    new("outcome"),
                    TransitionOutcomeDisposition.Applied,
                    Expr.BoundValue(selected))),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(
                new ObjectFieldTypeDef("whenTrue", new ScalarTypeRef(ScalarTypeKind.String)),
                new ObjectFieldTypeDef("whenFalse", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var analysis = result.Plan!.Analysis;
        var reads = analysis.GetRequirements<TransitionObservationRequirement>();
        var whenTrue = Assert.Single(reads, static requirement => HasPath(requirement, "whenTrue"));
        var whenFalse = Assert.Single(reads, static requirement => HasPath(requirement, "whenFalse"));
        Assert.Equal(TransitionRequirementStrength.May, whenTrue.InvocationStrength);
        Assert.Equal(TransitionRequirementStrength.May, whenFalse.InvocationStrength);
        Assert.True(analysis.Conditions.AreMutuallyExclusive(whenTrue.Condition, whenFalse.Condition));
        Assert.Contains(
            whenTrue.Occurrences,
            static occurrence => occurrence.Node.Value == "select"
                                 && occurrence.Influence == TransitionObservationInfluence.Calculation);
        Assert.Contains(
            whenTrue.Occurrences,
            static occurrence => occurrence.Node.Value == "outcome"
                                 && occurrence.Influence == TransitionObservationInfluence.Outcome);
    }

    [Fact]
    public void Compile_ComplementaryChoicePredicates_AreCorrelatedAndProveExhaustive()
    {
        var flag = Expr.Param("flag");
        var choice = new ChoiceTransitionNode(
            new("complementary-choice"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Exhaustive,
            [
                new(new("true-case"), flag, Sequence("true-body", Set("true-write", "status", "yes"))),
                new(
                    new("false-case"),
                    new UnaryExpr(UnaryOperator.Not, flag),
                    Sequence("false-body", Set("false-write", "status", "no")))
            ]);
        var definition = Definition(
            Sequence("root", choice, Outcome("outcome")),
            input: ObjectContract(new ObjectFieldTypeDef("flag", new ScalarTypeRef(ScalarTypeKind.Bool))),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        var analysis = result.Plan!.Analysis;
        var branch = Assert.Single(analysis.Branches);
        Assert.Equal(TransitionProofStatus.Proven, branch.Coverage);
        Assert.True(analysis.Conditions.AreMutuallyExclusive(
            branch.Alternatives[0].Condition,
            branch.Alternatives[1].Condition));
        var write = Assert.Single(analysis.GetRequirements<TransitionWriteRequirement>());
        Assert.Equal(TransitionRequirementStrength.Must, write.InvocationStrength);
    }

    [Fact]
    public void Compile_ConditionDomains_ClassifyAdmittedReadsAndCommitFreshnessExactly()
    {
        var admittedDefinition = new CanonicalTransitionDefinition(
            ObjectContract(new ObjectFieldTypeDef("allow", new ScalarTypeRef(ScalarTypeKind.Bool))),
            ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))),
            StringContract,
            [new TransitionAdmissionRule(new("allow"), Expr.Param("allow"), Expr.Const("denied"))],
            Sequence(
                "admitted-root",
                new OutcomeTransitionNode(
                    new("admitted-outcome"),
                    TransitionOutcomeDisposition.Applied,
                    Expr.Field("status"))),
            []);
        var admittedResult = TransitionStaticCompiler.Compile(Document(admittedDefinition));

        Assert.True(admittedResult.IsSuccessful, Format(admittedResult.Validation));
        var admittedAnalysis = admittedResult.Plan!.Analysis;
        var admittedRead = Assert.Single(admittedAnalysis.GetRequirements<TransitionObservationRequirement>());
        Assert.Equal(TransitionRequirementStrength.May, admittedRead.InvocationStrength);
        Assert.True(admittedAnalysis.Conditions.TryGetStrength(
            admittedRead.Condition,
            admittedAnalysis.AdmittedDomain,
            out var admittedStrength));
        Assert.Equal(TransitionRequirementStrength.Must, admittedStrength);

        var commitChoice = new ChoiceTransitionNode(
            new("commit-choice"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [
                new(
                    new("apply-case"),
                    Expr.Field("apply"),
                    Sequence(
                        "apply-body",
                        new OutcomeTransitionNode(
                            new("applied"),
                            TransitionOutcomeDisposition.Applied,
                            Expr.Field("status"))))
            ],
            new(
                new("reject-fallback"),
                Sequence(
                    "reject-body",
                    new OutcomeTransitionNode(
                        new("rejected"),
                        TransitionOutcomeDisposition.DomainRejected,
                        Expr.Field("reason")))));
        var commitDefinition = Definition(
            Sequence("commit-root", commitChoice),
            observation: ObjectContract(
                new ObjectFieldTypeDef("apply", new ScalarTypeRef(ScalarTypeKind.Bool)),
                new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String)),
                new ObjectFieldTypeDef("reason", new ScalarTypeRef(ScalarTypeKind.String))));
        var commitResult = TransitionStaticCompiler.Compile(Document(commitDefinition));

        Assert.True(commitResult.IsSuccessful, Format(commitResult.Validation));
        var commitAnalysis = commitResult.Plan!.Analysis;
        var commitReads = commitAnalysis.GetRequirements<TransitionObservationRequirement>();
        var apply = Assert.Single(commitReads, static requirement => HasPath(requirement, "apply"));
        var status = Assert.Single(commitReads, static requirement => HasPath(requirement, "status"));
        var reason = Assert.Single(commitReads, static requirement => HasPath(requirement, "reason"));
        Assert.True(apply.RequiresCommitValidation);
        Assert.True(status.RequiresCommitValidation);
        Assert.False(reason.RequiresCommitValidation);
        Assert.True(commitAnalysis.Conditions.TryGetStrength(
            status.Condition,
            commitAnalysis.CommitDomain,
            out var commitStrength));
        Assert.Equal(TransitionRequirementStrength.Must, commitStrength);
        Assert.False(commitAnalysis.Conditions.IsSatisfiableWithin(reason.Condition, commitAnalysis.CommitDomain));
    }

    [Fact]
    public void Compile_IncrementOperandMustMatchExactNumericTargetContract()
    {
        var definition = Definition(
            Sequence(
                "root",
                new UpdateTransitionNode(
                    new("increment"),
                    FieldPath.FromField("score"),
                    new IncrementTransitionPatch(Expr.Const(1.5m))),
                Outcome("outcome")),
            observation: ObjectContract(new ObjectFieldTypeDef("score", new ScalarTypeRef(ScalarTypeKind.Int64))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.False(result.IsSuccessful);
        Assert.Contains(
            result.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Compile_DelimiterBearingNodeIds_DoNotCollideConditionAtoms()
    {
        var first = new ChoiceTransitionNode(
            new("a:b"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [
                new(
                    new("c"),
                    Expr.Param("first"),
                    Sequence(
                        "first-case-body",
                        new LetTransitionNode(new("first-case-let"), new("firstCase"), StringContract, Expr.Const("case"))))
            ],
            new(
                new("first-fallback"),
                Sequence(
                    "first-fallback-body",
                    new LetTransitionNode(new("first-fallback-let"), new("firstFallback"), StringContract, Expr.Const("fallback")))));
        var second = new ChoiceTransitionNode(
            new("a"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [
                new(
                    new("b:c"),
                    Expr.Param("second"),
                    Sequence(
                        "second-case-body",
                        new LetTransitionNode(new("second-case-let"), new("secondCase"), StringContract, Expr.Const("case"))))
            ],
            new(
                new("second-fallback"),
                Sequence(
                    "second-fallback-body",
                    new LetTransitionNode(new("second-fallback-let"), new("secondFallback"), StringContract, Expr.Const("fallback")))));
        var definition = Definition(
            Sequence("root", first, second, Outcome("outcome")),
            input: ObjectContract(
                new ObjectFieldTypeDef("first", new ScalarTypeRef(ScalarTypeKind.Bool)),
                new ObjectFieldTypeDef("second", new ScalarTypeRef(ScalarTypeKind.Bool))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
        Assert.Equal(2, result.Plan!.Analysis.Branches.Length);
    }

    [Fact]
    public void Compile_UserBindingMatchingFormerCompilerTarget_DoesNotCollideDuringPathResolution()
    {
        var definition = Definition(
            Sequence(
                "root",
                new LetTransitionNode(
                    new("let"),
                    new("transition.compiler.target.update"),
                    StringContract,
                    Expr.Const("value")),
                Set("update", "status", "updated"),
                Outcome("outcome")),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.True(result.IsSuccessful, Format(result.Validation));
    }

    [Fact]
    public void Compile_MoreThanTenSteps_PreservesTraversalOrderForSitesAndWriteDiagnostics()
    {
        List<TransitionNode> steps = [];
        for (var index = 0; index <= 10; index++)
        {
            steps.Add(index is 2 or 10
                ? Set($"write-{index}", "status", index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : new LetTransitionNode(
                    new($"let-{index}"),
                    new($"binding-{index}"),
                    StringContract,
                    Expr.Const("value")));
        }
        steps.Add(Outcome("outcome"));
        var definition = Definition(
            new(new("root"), [.. steps]),
            observation: ObjectContract(new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String))));

        var result = TransitionStaticCompiler.Compile(Document(definition));

        Assert.False(result.IsSuccessful);
        Assert.Equal(
            steps.Select(static step => step.Id),
            result.Analysis!.ExpressionSites.Select(static site => site.Node));
        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == TransitionCompilationDiagnosticCodes.WriteOverlap);
        Assert.Equal("write-10", diagnostic.Evidence?.Subject);
        Assert.Equal("/definition/body/steps/10/path", diagnostic.Location);
        Assert.Equal(["/definition/body/steps/2/path"], diagnostic.Evidence?.RelatedLocations);
    }

    static CanonicalTransitionDefinition RoutingDefinition()
    {
        ValueContract decision = new(new EnumTypeRef("Decision", ["approve", "hold"]));
        ValueBindingId selected = new("selectedDecision");
        var input = ObjectContract(
            new ObjectFieldTypeDef("approved", new ScalarTypeRef(ScalarTypeKind.Bool)),
            new ObjectFieldTypeDef("decision", decision.Type!));
        var observation = ObjectContract(
            new ObjectFieldTypeDef("status", new ScalarTypeRef(ScalarTypeKind.String)),
            new ObjectFieldTypeDef("eligible", new ScalarTypeRef(ScalarTypeKind.Bool)),
            new ObjectFieldTypeDef("unused", new ScalarTypeRef(ScalarTypeKind.String)));
        var match = new MatchTransitionNode(
            new("decision-match"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            Expr.BoundValue(selected),
            decision,
            [
                new(new("approve-case"), EnumValue(decision, "approve"), Sequence(
                    "approve-body",
                    Set("approve-write", "status", "approved"),
                    new EmitTransitionNode(new("emit"), EmissionContract(), Expr.Const("approved")),
                    Outcome("approve-outcome", TransitionOutcomeDisposition.Applied, "approved"))),
                new(new("hold-case"), EnumValue(decision, "hold"), Sequence(
                    "hold-body",
                    Set("hold-write", "status", "held"),
                    Outcome("hold-outcome", TransitionOutcomeDisposition.DomainRejected, "held")))
            ],
            new(new("portable-fallback"), Sequence(
                "portable-fallback-body",
                Set("portable-fallback-write", "status", "unresolved"),
                Outcome("portable-fallback-outcome", TransitionOutcomeDisposition.DomainRejected, "unresolved"))));
        var choice = new ChoiceTransitionNode(
            new("eligibility-choice"),
            TransitionCaseSelection.OrderedFirstMatch,
            TransitionBranchCompleteness.Fallback,
            [new(new("eligible-case"), Expr.And(Expr.Param("approved"), Expr.Field("eligible")), Sequence("eligible-body", match))],
            new(new("ineligible-fallback"), Sequence(
                "ineligible-body",
                Set("ineligible-write", "status", "ineligible"),
                Outcome("ineligible-outcome", TransitionOutcomeDisposition.NoChange, "ineligible"))));
        return new(
            input,
            observation,
            StringContract,
            [],
            Sequence(
                "root",
                new LetTransitionNode(new("bind-decision"), selected, decision, Expr.Param("decision")),
                choice));
    }

    static CanonicalTransitionDefinition MatchDefinition(ValueContract contract, bool includeHold)
    {
        var cases = new List<TransitionMatchCase>
        {
            new(new("approve"), EnumValue(contract, "approve"), Sequence("approve-body", Outcome("approve-outcome")))
        };
        if (includeHold)
        {
            cases.Add(new(new("hold"), EnumValue(contract, "hold"), Sequence("hold-body", Outcome("hold-outcome"))));
        }

        return Definition(
            Sequence(
                "root",
                new MatchTransitionNode(
                    new("match"),
                    TransitionCaseSelection.OrderedFirstMatch,
                    TransitionBranchCompleteness.Exhaustive,
                    Expr.Param("decision"),
                    contract,
                    [.. cases])),
            input: ObjectContract(new ObjectFieldTypeDef("decision", contract.Type!)));
    }

    static ShapeGraph DerivedGraph(bool cycle)
    {
        var fields = cycle
            ? new FieldDefinition[]
            {
                Computed("a", Expr.Add(Expr.Field("b"), Expr.Const(1))),
                Computed("b", Expr.Add(Expr.Field("a"), Expr.Const(1)))
            }
            :
            [
                new(new FieldName("raw"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                Computed("normalized", Expr.Add(Expr.Field("raw"), Expr.Const(1))),
                new(
                    new FieldName("eligible"),
                    new ScalarTypeRef(ScalarTypeKind.Bool),
                    role: FieldRole.Computed,
                    mutability: FieldMutability.Computed,
                    compute: new(Expr.Gt(Expr.Field("normalized"), Expr.Const(10))))
            ];
        return new(new("review-graph"), [new(new("review"), [.. fields])]);
    }

    static FieldDefinition Computed(string name, Expr expression) => new(
        new FieldName(name),
        new ScalarTypeRef(ScalarTypeKind.Int64),
        role: FieldRole.Computed,
        mutability: FieldMutability.Computed,
        compute: new(expression));

    static CanonicalTransitionDefinition Definition(
        SequenceTransitionNode body,
        ValueContract? input = null,
        ValueContract? observation = null) => new(
        input ?? new(new JsonTypeRef(JsonTypeKind.Object)),
        observation ?? new(new JsonTypeRef(JsonTypeKind.Object)),
        StringContract,
        [],
        body);

    static ValueContract ObjectContract(params ObjectFieldTypeDef[] fields) =>
        new(new ObjectTypeRef([.. fields]));

    static SequenceTransitionNode Sequence(string id, params TransitionNode[] nodes) => new(new(id), [.. nodes]);

    static OutcomeTransitionNode Outcome(
        string id,
        TransitionOutcomeDisposition disposition = TransitionOutcomeDisposition.Applied,
        string value = "ok") => new(new(id), disposition, Expr.Const(value));

    static UpdateTransitionNode Set(string id, string path, string value) => Set(id, path, Expr.Const(value));

    static UpdateTransitionNode Set(string id, string path, Expr value) => new(
        new(id),
        FieldPath.FromField(path),
        new SetTransitionPatch(value));

    static PortableValue EnumValue(ValueContract contract, string value) =>
        PortableValue.Concrete(contract, ObservationValue.FromString(value));

    static ExecutionDefinitionDocument Document(
        CanonicalTransitionDefinition definition,
        ExecutionSourceMap? sourceMap = null) => TransitionDefinitionDocuments.Create(
        new("transition/test"),
        new("revision/1"),
        definition,
        new(
            new("transition-static-compiler-tests", "1"),
            new("tests/execution-kernel/transition-static-compiler"),
            DocumentOrigin.Generated),
        sourceMap: sourceMap);

    static ExecutionDefinitionReference EmissionContract() => new(
        new("interaction/reviewed"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('0', 64)));

    static string RequirementSignature(
        TransitionSemanticAnalysis analysis,
        TransitionSemanticRequirement requirement)
    {
        var identity = requirement switch
        {
            TransitionObservationRequirement observation =>
                $"observation:{observation.Access}:{observation.Influences}:"
                + $"{analysis.Conditions.Format(observation.CommitValidationCondition)}:"
                + $"{observation.CommitValidationInvocationStrength}:"
                + string.Join(",", observation.CommitValidationOccurrences.Select(occurrence =>
                    OccurrenceSignature(analysis, occurrence))),
            TransitionWriteRequirement write => $"write:{write.Path}:{write.IsDerived}",
            TransitionEmissionRequirement emission =>
                $"emission:{emission.Contract.DefinitionId.Value}:{emission.Contract.RevisionId.Value}:{emission.Contract.Fingerprint.Value}",
            TransitionCapabilityRequirement capability =>
                $"capability:{capability.Capability.Kind}:{capability.Capability.Capability.Value}",
            TransitionOutcomeRequirement outcome => $"outcome:{outcome.DecisionKind}",
            _ => throw new InvalidOperationException(
                $"Unsupported Transition requirement '{requirement.GetType().Name}'.")
        };
        var occurrences = string.Join(
            ";",
            requirement.Occurrences.Select(occurrence => OccurrenceSignature(analysis, occurrence)));
        return $"{identity}|{analysis.Conditions.Format(requirement.Condition)}|{requirement.InvocationStrength}|{occurrences}";
    }

    static string OccurrenceSignature(
        TransitionSemanticAnalysis analysis,
        TransitionRequirementOccurrence occurrence) =>
        $"{occurrence.Node.Value}:{analysis.Conditions.Format(occurrence.Condition)}:{occurrence.Location}:"
        + $"{occurrence.Site?.Value}:{occurrence.SchemaLocation}:{occurrence.Influence}:"
        + string.Join(',', occurrence.SourceReferences);

    static string DomainSignature(TransitionSemanticAnalysis analysis) => string.Join(
        "|",
        analysis.Conditions.Format(analysis.InvocationDomain),
        analysis.Conditions.Format(analysis.AdmittedDomain),
        analysis.Conditions.Format(analysis.AcceptedDomain),
        analysis.Conditions.Format(analysis.CommitDomain),
        string.Join(",", analysis.Conditions.Atoms));

    static string DerivedFieldSignature(TransitionDerivedFieldAnalysis field) => string.Join(
        "|",
        field.Field,
        string.Join(",", field.DirectDependencies),
        string.Join(",", field.BaseDependencies),
        field.AffectedByWrites);

    static string BranchSignature(
        TransitionSemanticAnalysis analysis,
        TransitionBranchAnalysis branch) => string.Join(
        "|",
        branch.Node.Value,
        analysis.Conditions.Format(branch.Domain),
        branch.Coverage,
        branch.Reason,
        string.Join(",", branch.Alternatives.Select(alternative =>
            $"{alternative.Node.Value}:{alternative.Status}:{analysis.Conditions.Format(alternative.Condition)}:{alternative.Reason}")),
        string.Join(",", branch.UncoveredValues));

    static TransitionObservationRequirement AssertPatchTargetRead(
        IEnumerable<TransitionObservationRequirement> requirements,
        string path,
        string node)
    {
        var requirement = Assert.Single(requirements, value => HasPath(value, path));
        const TransitionObservationInfluence influence =
            TransitionObservationInfluence.Calculation | TransitionObservationInfluence.PatchTarget;
        Assert.Equal(influence, requirement.Influences);
        Assert.True(requirement.RequiresCommitValidation);

        var occurrence = Assert.Single(requirement.Occurrences);
        Assert.Equal(node, occurrence.Node.Value);
        Assert.Equal(influence, occurrence.Influence);
        Assert.Null(occurrence.Site);
        Assert.EndsWith("/path", occurrence.Location);

        var commitOccurrence = Assert.Single(requirement.CommitValidationOccurrences);
        Assert.Equal(node, commitOccurrence.Node.Value);
        Assert.Equal(influence, commitOccurrence.Influence);
        return requirement;
    }

    static bool HasPath(TransitionObservationRequirement requirement, string path) =>
        requirement.Access.Path is { } fieldPath && fieldPath.Matches(path);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location} {diagnostic.SchemaLocation}: {diagnostic.Message}"));
}
