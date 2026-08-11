using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Compilation;

/// <summary>Stable diagnostic codes emitted by target-independent Transition compilation.</summary>
public static class TransitionCompilationDiagnosticCodes
{
    /// <summary>A lexical binding identity is redeclared within one definition.</summary>
    public const string BindingDuplicate = "transitions.compilation.binding.duplicate";

    /// <summary>A declared exhaustive branch construct was statically disproven.</summary>
    public const string ExhaustivenessDisproven = "transitions.compilation.branch.exhaustivenessDisproven";

    /// <summary>A declared exhaustive branch construct could not be proven by the restricted proof model.</summary>
    public const string ExhaustivenessUnknown = "transitions.compilation.branch.exhaustivenessUnknown";

    /// <summary>A reachable Transition control path falls through without a terminal outcome.</summary>
    public const string OutcomeMissing = "transitions.compilation.outcome.missing";

    /// <summary>A node follows an unconditional terminal outcome in the same realized sequence.</summary>
    public const string NodeUnreachable = "transitions.compilation.node.unreachable";

    /// <summary>Two overlapping patches can execute on the same realized path.</summary>
    public const string WriteOverlap = "transitions.compilation.write.overlap";

    /// <summary>An authored patch targets a compiler-owned computed field.</summary>
    public const string ComputedFieldWrite = "transitions.compilation.write.computedField";

    /// <summary>A sparse patch target is absent from the known aggregate contract.</summary>
    public const string PatchTargetUnknown = "transitions.compilation.patch.targetUnknown";

    /// <summary>A sparse patch operation is incompatible with its target contract.</summary>
    public const string PatchContractMismatch = "transitions.compilation.patch.contractMismatch";

    /// <summary>Computed fields contain a dependency cycle.</summary>
    public const string DerivedFieldCycle = "transitions.compilation.derived.cycle";

    /// <summary>A computed field depends on the complete observation instead of finite field paths.</summary>
    public const string DerivedFieldWholeObservation = "transitions.compilation.derived.wholeObservation";

    /// <summary>An applicable invariant predicate is statically false.</summary>
    public const string InvariantDisproven = "transitions.compilation.invariant.disproven";

    /// <summary>A MoveMachine node has no exact linked Machine edge evidence.</summary>
    public const string MachineEdgeUnresolved = "transitions.compilation.machine.edgeUnresolved";

    /// <summary>A linked Machine configuration assignment is incompatible with its aggregate target.</summary>
    public const string MachineConfigurationMismatch = "transitions.compilation.machine.configurationMismatch";

    /// <summary>A grouped aggregate is outside the restricted Transition v1 expression language.</summary>
    public const string GroupedAggregateUnsupported = "transitions.compilation.expression.groupedAggregateUnsupported";
}

/// <summary>
/// Compiles canonical Transition IR into deterministic target-independent control, access, effect, and dependency evidence.
/// </summary>
/// <remarks>
/// Compilation performs no I/O and selects no storage backend. It proves the restricted static properties needed by
/// reference interpreters and storage planners while retaining exact canonical and producer-source provenance.
/// Emission expressions are analyzed here, while referenced interaction resolution and linked payload typing belong
/// to the execution-definition linking phase so this compiler remains deterministic over one supplied document.
/// </remarks>
public static class TransitionStaticCompiler
{
    /// <summary>Compiles one exact fingerprinted Transition definition document.</summary>
    /// <param name="document">Canonical shared execution-definition document.</param>
    /// <param name="graph">Optional exact shape graph used to resolve qualified contracts and computed fields.</param>
    /// <param name="machineLinks">
    /// Optional immutable edge slices projected from exact Cohesive.Machines definitions.
    /// </param>
    /// <returns>A complete target-independent plan, or partial analysis with structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Canonical semantic content has no stable JSON representation.</exception>
    /// <exception cref="NotSupportedException">Canonical semantic content contains an unsupported runtime type.</exception>
    public static TransitionCompilationResult Compile(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph = null,
        TransitionMachineLinkCatalog? machineLinks = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var structural = graph is null
            ? TransitionDefinitionDocuments.Validate(document)
            : TransitionDefinitionDocuments.Validate(document, graph);
        if (!structural.IsValid)
        {
            return new(
                document,
                definition: null,
                analysis: null,
                plan: null,
                Normalize(structural.Diagnostics));
        }

        var definition = document.GetDefinition<TransitionDefinition>();
        Context context = new(document, definition, graph, machineLinks ?? TransitionMachineLinkCatalog.Empty);
        return context.Compile();
    }

    static DocumentValidationResult Normalize(IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        var values = diagnostics.Distinct().ToArray();
        Array.Sort(values, DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(values);
    }

    sealed class Context
    {
        static readonly ExprCapabilityProfile CapabilityProfile =
            TransitionExpressionLanguage.Capabilities;
        static readonly JsonSerializerOptions ExpressionJsonOptions =
            ExecutionDefinitionJsonSerializer.CreateOptions();

        readonly ExecutionDefinitionDocument document;
        readonly TransitionDefinition definition;
        readonly ShapeGraph? graph;
        readonly TransitionMachineLinkCatalog machineLinks;
        readonly TransitionConditionSolver conditions;
        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly List<TransitionExpressionSiteAnalysis> sites = [];
        readonly List<ConditionalFact> facts = [];
        readonly List<TransitionBranchAnalysis> branches = [];
        readonly Dictionary<ExpressionAtomKey, string> expressionAtoms = [];
        readonly HashSet<ValueBindingId> declaredBindings =
        [
            TransitionBindingIds.Input,
            TransitionBindingIds.Observation
        ];
        readonly ImmutableArray<ExprScopeParameter> parameters;
        readonly ValueContract inputContract;
        readonly ValueContract observationContract;
        readonly Dictionary<FieldPath, ComputedFieldState> computedFields = [];
        readonly List<ComputedFieldState> computedOrder = [];
        readonly Dictionary<MachineEdgeKey, TransitionMachineEdgeLink> usedMachineEdges = [];
        TransitionCondition admittedCondition;
        TransitionCondition appliedCondition;
        TransitionCondition domainRejectedCondition;
        TransitionCondition acceptedCondition;
        TransitionCondition invariantsHoldCondition;

        public Context(
            ExecutionDefinitionDocument document,
            TransitionDefinition definition,
            ShapeGraph? graph,
            TransitionMachineLinkCatalog machineLinks)
        {
            this.document = document;
            this.definition = definition;
            this.graph = graph;
            this.machineLinks = machineLinks;
            inputContract = ResolveContract(definition.Input);
            observationContract = ResolveContract(definition.Observation);
            parameters = CreateParameters(inputContract);
            conditions = new([]);
            admittedCondition = conditions.False;
            appliedCondition = conditions.False;
            domainRejectedCondition = conditions.False;
            acceptedCondition = conditions.False;
            invariantsHoldCondition = conditions.True;
        }

        public TransitionCompilationResult Compile()
        {
            var rootScope = CreateRootScope();
            var stateScope = CreateStateScope();
            AnalyzeSubjectCreation();
            AnalyzeComputedFields(stateScope);

            var live = CompileAdmission(rootScope, conditions.True);
            admittedCondition = live;
            var bodyFallthrough = CompileSequence(
                definition.Body,
                "/definition/body",
                live,
                rootScope.Clone(),
                diagnoseUnreachableTail: conditions.IsSatisfiable(live));
            if (conditions.IsSatisfiable(bodyFallthrough))
            {
                AddDiagnostic(
                    TransitionCompilationDiagnosticCodes.OutcomeMissing,
                    "A reachable Transition body path falls through without producing a terminal Outcome.",
                    "/definition/body",
                    definition.Body.Id,
                    stage: "controlFlow",
                    expected: "terminal Outcome on every reachable path",
                    observed: conditions.Format(bodyFallthrough),
                    resolutions: ["Add an Outcome to the uncovered path or make branch coverage explicit with a fallback."]);
            }

            AddDerivedRecomputationFacts();
            AnalyzeInvariants(stateScope);
            ValidateOverlappingWrites();

            var analysis = BuildAnalysis();
            var validation = Normalize(diagnostics);
            CompiledTransitionPlan? plan = validation.IsValid
                ? new(
                    document,
                    definition,
                    analysis,
                    graph,
                    [.. computedOrder.Select(static field => new CompiledTransitionDerivedField(
                        field.Site.Node,
                        field.Path,
                        field.Contract,
                        field.Expression,
                        field.DirectDependencies))],
                    [.. usedMachineEdges.Values
                        .OrderBy(static edge => edge, TransitionStructuralOrdering.MachineEdges)])
                : null;

            return new(document, definition, analysis, plan, validation);
        }

        ScopeState CreateRootScope() => new(
            [
                new(TransitionBindingIds.Input, inputContract),
                new(TransitionBindingIds.Observation, observationContract)
            ],
            parameters,
            new Dictionary<ValueBindingId, ImmutableArray<ObservationDependency>>
            {
                [TransitionBindingIds.Input] = [],
                [TransitionBindingIds.Observation] = [new(TransitionObservationAccess.Whole, conditions.True)]
            });

        ScopeState CreateSubjectCreationScope() => new(
            [new(TransitionBindingIds.Input, inputContract)],
            parameters,
            new Dictionary<ValueBindingId, ImmutableArray<ObservationDependency>>
            {
                [TransitionBindingIds.Input] = []
            });

        void AnalyzeSubjectCreation()
        {
            if (definition.SubjectCreation is not { } creation)
                return;

            _ = AnalyzeSite(
                creation.Id,
                TransitionExpressionSiteKind.SubjectInitializer,
                creation.InitialObservation,
                CreateSubjectCreationScope(),
                Exact(observationContract),
                "/definition/subjectCreation/initialObservation",
                conditions.True,
                TransitionObservationInfluence.None,
                retainObservationFacts: false,
                implicitBinding: TransitionBindingIds.Input);
        }

        ScopeState CreateStateScope() => new(
            [new(TransitionBindingIds.Observation, observationContract)],
            [],
            new Dictionary<ValueBindingId, ImmutableArray<ObservationDependency>>
            {
                [TransitionBindingIds.Observation] = [new(TransitionObservationAccess.Whole, conditions.True)]
            });

        TransitionCondition CompileAdmission(ScopeState scope, TransitionCondition input)
        {
            var remainder = input;
            for (var index = 0; index < definition.Preconditions.Length; index++)
            {
                var rule = definition.Preconditions[index];
                var location = $"/definition/preconditions/{index.ToString(CultureInfo.InvariantCulture)}";
                var predicate = AnalyzeSite(
                    rule.Id,
                    TransitionExpressionSiteKind.AdmissionPredicate,
                    rule.Predicate,
                    scope,
                    ExprExpectation.Boolean,
                    $"{location}/predicate",
                    remainder,
                    TransitionObservationInfluence.Admission);
                var test = TryGetBoolean(predicate, rule.Predicate, out var admissionConstant)
                    ? admissionConstant ? conditions.True : conditions.False
                    : BooleanExpressionCondition(rule.Predicate);
                var rejected = conditions.And(remainder, conditions.Not(test));
                _ = AnalyzeSite(
                    rule.Id,
                    TransitionExpressionSiteKind.AdmissionRejection,
                    rule.Rejection,
                    scope,
                    Exact(definition.Outcome),
                    $"{location}/rejection",
                    rejected,
                    TransitionObservationInfluence.Outcome);
                AddFact(ConditionalFact.Outcome(
                    TransitionDecisionKind.AdmissionRejected,
                    rejected,
                    Origin(rule.Id, $"{location}/rejection", null, TransitionObservationInfluence.Outcome)));
                remainder = conditions.And(remainder, test);
            }

            return remainder;
        }

        TransitionCondition CompileSequence(
            SequenceTransitionNode sequence,
            string location,
            TransitionCondition input,
            ScopeState scope,
            bool diagnoseUnreachableTail)
        {
            var current = input;
            var tailWasTerminated = false;
            for (var index = 0; index < sequence.Steps.Length; index++)
            {
                var step = sequence.Steps[index];
                var stepLocation = $"{location}/steps/{index.ToString(CultureInfo.InvariantCulture)}";
                if (tailWasTerminated && diagnoseUnreachableTail)
                {
                    AddDiagnostic(
                        TransitionCompilationDiagnosticCodes.NodeUnreachable,
                        $"Transition node '{step.Id.Value}' follows a terminal outcome on every path reaching this sequence position.",
                        stepLocation,
                        step.Id,
                        stage: "controlFlow",
                        resolutions: ["Remove the unreachable node or move it before the terminal Outcome."]);
                }

                var before = current;
                current = CompileNode(step, stepLocation, current, scope);
                if (conditions.IsSatisfiable(before) && !conditions.IsSatisfiable(current))
                {
                    tailWasTerminated = true;
                }
            }

            return current;
        }

        TransitionCondition CompileNode(
            TransitionNode node,
            string location,
            TransitionCondition input,
            ScopeState scope) => node switch
            {
                SequenceTransitionNode sequence => CompileSequence(
                    sequence,
                    location,
                    input,
                    scope.Clone(),
                    diagnoseUnreachableTail: conditions.IsSatisfiable(input)),
                LetTransitionNode let => CompileLet(let, location, input, scope),
                ChoiceTransitionNode choice => CompileChoice(choice, location, input, scope),
                MatchTransitionNode match => CompileMatch(match, location, input, scope),
                UpdateTransitionNode update => CompileUpdate(update, location, input, scope),
                EmitTransitionNode emit => CompileEmit(emit, location, input, scope),
                MoveMachineTransitionNode movement => CompileMachineMovement(movement, location, input, scope),
                OutcomeTransitionNode outcome => CompileOutcome(outcome, location, input, scope),
                _ => input
            };

        TransitionCondition CompileLet(
            LetTransitionNode let,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            var analysis = AnalyzeSite(
                let.Id,
                TransitionExpressionSiteKind.LetValue,
                let.Value,
                scope,
                Exact(ResolveContract(let.Contract)),
                $"{location}/value",
                input,
                TransitionObservationInfluence.Calculation);
            if (!declaredBindings.Add(let.Binding))
            {
                AddDiagnostic(
                    TransitionCompilationDiagnosticCodes.BindingDuplicate,
                    $"Value binding '{let.Binding.Value}' is declared more than once in the Transition definition.",
                    $"{location}/binding",
                    let.Id,
                    stage: "bindingFlow",
                    expected: "definition-wide unique durable binding identity",
                    observed: let.Binding.Value,
                    resolutions: ["Assign a distinct stable binding identity to this Let node."]);
                return input;
            }

            var dependencies = CollectObservationDependencies(analysis, scope, let.Value, input);
            scope.Add(new(let.Binding, ResolveContract(let.Contract)), dependencies);
            return input;
        }

        TransitionCondition CompileChoice(
            ChoiceTransitionNode choice,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            var remainder = input;
            var fallthrough = conditions.False;
            var alternatives = ImmutableArray.CreateBuilder<TransitionAlternativeAnalysis>(
                choice.Cases.Length + (choice.Fallback is null ? 0 : 1));
            var hasUnknownPredicate = false;
            var coverageWasProven = false;

            for (var index = 0; index < choice.Cases.Length; index++)
            {
                var choiceCase = choice.Cases[index];
                var caseLocation = $"{location}/cases/{index.ToString(CultureInfo.InvariantCulture)}";
                var analysis = AnalyzeSite(
                    choiceCase.Id,
                    TransitionExpressionSiteKind.ChoicePredicate,
                    choiceCase.Predicate,
                    scope,
                    ExprExpectation.Boolean,
                    $"{caseLocation}/predicate",
                    remainder,
                    TransitionObservationInfluence.Branch);
                var known = TryGetBoolean(analysis, choiceCase.Predicate, out var constant);
                hasUnknownPredicate |= !known;
                var predicate = known
                    ? constant ? conditions.True : conditions.False
                    : BooleanExpressionCondition(choiceCase.Predicate);
                var selected = conditions.And(remainder, predicate);
                var status = !conditions.IsSatisfiable(selected)
                    ? TransitionProofStatus.Impossible
                    : known && constant && conditions.Implies(remainder, selected)
                        ? TransitionProofStatus.Proven
                        : TransitionProofStatus.Unknown;
                alternatives.Add(new(
                    choiceCase.Id,
                    status,
                    ToRef(selected),
                    status switch
                    {
                        TransitionProofStatus.Impossible => "An earlier ordered case or a constant-false predicate makes this case unreachable.",
                        TransitionProofStatus.Proven => "The predicate is true for every value reaching this ordered case.",
                        _ => "The data-dependent predicate may select this ordered case."
                    }));
                var branchFallthrough = CompileSequence(
                    choiceCase.Body,
                    $"{caseLocation}/body",
                    selected,
                    scope.Clone(),
                    diagnoseUnreachableTail: conditions.IsSatisfiable(selected));
                fallthrough = conditions.Or(fallthrough, branchFallthrough);
                remainder = conditions.And(remainder, conditions.Not(predicate));
                if (!conditions.IsSatisfiable(remainder))
                {
                    coverageWasProven = true;
                }
            }

            TransitionProofStatus coverage;
            string reason;
            if (choice.Fallback is { } fallback)
            {
                coverage = TransitionProofStatus.Proven;
                reason = "An explicit fallback covers the ordered predicate remainder.";
                var status = conditions.IsSatisfiable(remainder)
                    ? TransitionProofStatus.Unknown
                    : TransitionProofStatus.Impossible;
                alternatives.Add(new(
                    fallback.Id,
                    status,
                    ToRef(remainder),
                    status == TransitionProofStatus.Impossible
                        ? "Earlier cases cover every feasible value, so the fallback is unreachable."
                        : "The fallback may be selected when no predicate case matches."));
                var fallbackFallthrough = CompileSequence(
                    fallback.Body,
                    $"{location}/fallback/body",
                    remainder,
                    scope.Clone(),
                    diagnoseUnreachableTail: conditions.IsSatisfiable(remainder));
                fallthrough = conditions.Or(fallthrough, fallbackFallthrough);
            }
            else if (coverageWasProven || !conditions.IsSatisfiable(remainder))
            {
                coverage = TransitionProofStatus.Proven;
                reason = "A reachable constant-true case covers the ordered predicate remainder.";
            }
            else if (hasUnknownPredicate)
            {
                coverage = TransitionProofStatus.Unknown;
                reason = "The restricted proof model cannot prove arbitrary data-dependent predicates exhaustive.";
                fallthrough = conditions.Or(fallthrough, remainder);
            }
            else
            {
                coverage = TransitionProofStatus.Disproven;
                reason = "Every predicate is statically false for a reachable remainder.";
                fallthrough = conditions.Or(fallthrough, remainder);
            }

            AddBranchAnalysis(
                choice.Id,
                input,
                coverage,
                reason,
                alternatives.MoveToImmutable(),
                [],
                location);
            return fallthrough;
        }

        TransitionCondition CompileMatch(
            MatchTransitionNode match,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            var analysis = AnalyzeSite(
                match.Id,
                TransitionExpressionSiteKind.MatchValue,
                match.Value,
                scope,
                Exact(ResolveContract(match.Contract)),
                $"{location}/value",
                input,
                TransitionObservationInfluence.Branch);
            var proof = AnalyzeMatchCoverage(match, analysis);
            var remainder = input;
            var fallthrough = conditions.False;
            var alternatives = ImmutableArray.CreateBuilder<TransitionAlternativeAnalysis>(
                match.Cases.Length + (match.Fallback is null ? 0 : 1));
            Dictionary<PortableValue, string> atomsByPattern = [];
            var lastUniqueCase = proof.CasesExhaustDomain
                ? LastUniqueCaseIndex(match.Cases)
                : -1;

            for (var index = 0; index < match.Cases.Length; index++)
            {
                var matchCase = match.Cases[index];
                var caseLocation = $"{location}/cases/{index.ToString(CultureInfo.InvariantCulture)}";
                TransitionCondition predicate;
                if (analysis.Analysis.KnownConstant is { } constant)
                {
                    predicate = PatternMatches(matchCase.Pattern, constant)
                        ? conditions.True
                        : conditions.False;
                }
                else if (atomsByPattern.TryGetValue(matchCase.Pattern, out var duplicateAtom))
                {
                    predicate = conditions.GetOrAddAtom(duplicateAtom);
                }
                else
                {
                    var atom = MatchAtom(match.Id, matchCase.Id);
                    atomsByPattern.Add(matchCase.Pattern, atom);
                    predicate = index == lastUniqueCase
                        ? conditions.True
                        : conditions.GetOrAddAtom(atom);
                }

                var selected = conditions.And(remainder, predicate);
                var status = conditions.IsSatisfiable(selected)
                    ? analysis.Analysis.KnownConstant is not null
                        ? TransitionProofStatus.Proven
                        : TransitionProofStatus.Unknown
                    : TransitionProofStatus.Impossible;
                alternatives.Add(new(
                    matchCase.Id,
                    status,
                    ToRef(selected),
                    status switch
                    {
                        TransitionProofStatus.Proven => "The statically known match value selects this exact pattern.",
                        TransitionProofStatus.Impossible => "An earlier equal pattern or the statically known match value makes this case unreachable.",
                        _ => "The typed exact pattern may be selected at runtime."
                    }));
                var branchFallthrough = CompileSequence(
                    matchCase.Body,
                    $"{caseLocation}/body",
                    selected,
                    scope.Clone(),
                    diagnoseUnreachableTail: conditions.IsSatisfiable(selected));
                fallthrough = conditions.Or(fallthrough, branchFallthrough);
                remainder = conditions.And(remainder, conditions.Not(predicate));
            }

            if (match.Fallback is { } fallback)
            {
                var status = conditions.IsSatisfiable(remainder)
                    ? analysis.Analysis.KnownConstant is not null
                        ? TransitionProofStatus.Proven
                        : TransitionProofStatus.Unknown
                    : TransitionProofStatus.Impossible;
                alternatives.Add(new(
                    fallback.Id,
                    status,
                    ToRef(remainder),
                    status == TransitionProofStatus.Impossible
                        ? "Exact cases cover every feasible value, so the fallback is unreachable."
                        : "The fallback handles the exact-pattern remainder."));
                var fallbackFallthrough = CompileSequence(
                    fallback.Body,
                    $"{location}/fallback/body",
                    remainder,
                    scope.Clone(),
                    diagnoseUnreachableTail: conditions.IsSatisfiable(remainder));
                fallthrough = conditions.Or(fallthrough, fallbackFallthrough);
            }
            else if (proof.Coverage != TransitionProofStatus.Proven)
            {
                fallthrough = conditions.Or(fallthrough, remainder);
            }

            AddBranchAnalysis(
                match.Id,
                input,
                proof.Coverage,
                proof.Reason,
                alternatives.MoveToImmutable(),
                proof.Uncovered,
                location);
            return fallthrough;
        }

        TransitionCondition CompileUpdate(
            UpdateTransitionNode update,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            var target = ResolvePatchTarget(update, location);
            if (TargetsComputedField(update.Path, out var computed))
            {
                AddDiagnostic(
                    TransitionCompilationDiagnosticCodes.ComputedFieldWrite,
                    $"Patch '{update.Id.Value}' targets compiler-owned computed field '{computed}'.",
                    $"{location}/path",
                    update.Id,
                    stage: "dependencyAnalysis",
                    expected: "mutable non-computed aggregate path",
                    observed: update.Path.ToString(),
                    resolutions: ["Write a base dependency and allow the compiler to recompute the derived field."]);
            }

            switch (update.Operation)
            {
                case SetTransitionPatch set:
                    _ = AnalyzeSite(
                        update.Id,
                        TransitionExpressionSiteKind.PatchOperand,
                        set.Value,
                        scope,
                        target is null ? ExprExpectation.Any : Exact(target),
                        $"{location}/operation/value",
                        input,
                        TransitionObservationInfluence.Calculation);
                    AddPatchTargetRead(update, location, input);
                    break;
                case RemoveTransitionPatch:
                    if (target?.Presence == FieldPresence.Required)
                    {
                        AddPatchMismatch(
                            update,
                            location,
                            "A Remove patch cannot satisfy a required-presence target contract.",
                            "optional target presence",
                            "required target presence");
                    }
                    AddPatchTargetRead(update, location, input);
                    break;
                case IncrementTransitionPatch increment:
                    ValidateTargetCategory(update, location, target, ExprResultCategory.Numeric, "numeric");
                    _ = AnalyzeSite(
                        update.Id,
                        TransitionExpressionSiteKind.PatchOperand,
                        increment.Amount,
                        scope,
                        target is null
                            ? new(ExprResultCategory.Numeric)
                            : Exact(target),
                        $"{location}/operation/amount",
                        input,
                        TransitionObservationInfluence.Calculation);
                    AddObservationFact(
                        update.Path,
                        input,
                        Origin(
                            update.Id,
                            $"{location}/path",
                            null,
                            TransitionObservationInfluence.Calculation | TransitionObservationInfluence.PatchTarget));
                    break;
                case AddToSetTransitionPatch add:
                    AnalyzeCollectionElementPatch(update, location, add.Value, target, scope, input, "value");
                    AddPatchTargetRead(update, location, input);
                    break;
                case AppendTransitionPatch append:
                    AnalyzeCollectionElementPatch(update, location, append.Value, target, scope, input, "value");
                    AddPatchTargetRead(update, location, input);
                    break;
                case UpsertOwnedChildTransitionPatch upsert:
                    AnalyzeOwnedChildPatch(
                        update,
                        location,
                        upsert.IdentityPath,
                        upsert.Identity,
                        upsert.Value,
                        target,
                        scope,
                        input);
                    AddPatchTargetRead(update, location, input);
                    break;
                case RemoveOwnedChildTransitionPatch remove:
                    AnalyzeOwnedChildPatch(
                        update,
                        location,
                        remove.IdentityPath,
                        remove.Identity,
                        value: null,
                        target,
                        scope,
                        input);
                    AddPatchTargetRead(update, location, input);
                    break;
            }

            AddFact(ConditionalFact.Write(
                update.Path,
                isDerived: false,
                input,
                Origin(update.Id, $"{location}/path", null, TransitionObservationInfluence.None)));
            return input;
        }

        TransitionCondition CompileEmit(
            EmitTransitionNode emit,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            _ = AnalyzeSite(
                emit.Id,
                TransitionExpressionSiteKind.EmissionPayload,
                emit.Payload,
                scope,
                ExprExpectation.Any,
                $"{location}/payload",
                input,
                TransitionObservationInfluence.Emission);
            AddFact(ConditionalFact.Emission(
                emit.Contract,
                input,
                Origin(emit.Id, location, null, TransitionObservationInfluence.Emission)));
            return input;
        }

        TransitionCondition CompileMachineMovement(
            MoveMachineTransitionNode movement,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            if (!machineLinks.TryGet(movement.Machine, movement.Edge, out var link))
            {
                AddDiagnostic(
                    TransitionCompilationDiagnosticCodes.MachineEdgeUnresolved,
                    $"Machine edge '{movement.Edge.Value}' from exact definition "
                    + $"'{movement.Machine.DefinitionId.Value}' is not linked.",
                    $"{location}/edge",
                    movement.Id,
                    stage: "definitionLinking",
                    expected: "fingerprint-matched Cohesive.Machines edge evidence",
                    observed: $"{movement.Machine.DefinitionId.Value}:{movement.Edge.Value}",
                    resolutions: ["Compile and supply the exact referenced Machine definition through the Machine linker."]);
                return input;
            }

            usedMachineEdges[new(link.Machine, link.Edge)] = link;
            var source = AnalyzeSite(
                movement.Id,
                TransitionExpressionSiteKind.MachineSourceConfiguration,
                link.SourceConfiguration,
                scope,
                ExprExpectation.Boolean,
                $"{location}/linked/sourceConfiguration",
                input,
                TransitionObservationInfluence.Admission | TransitionObservationInfluence.Branch,
                conditionAtomScope: $"machine-source:{movement.Id.Value}");
            var sourceTest = TryGetBoolean(source, link.SourceConfiguration, out var sourceConstant)
                ? sourceConstant ? conditions.True : conditions.False
                : BooleanExpressionCondition(
                    link.SourceConfiguration,
                    $"machine-source:{movement.Id.Value}");
            var rejected = conditions.And(input, conditions.Not(sourceTest));
            _ = AnalyzeSite(
                movement.Id,
                TransitionExpressionSiteKind.MachineRejection,
                movement.Rejection,
                scope,
                Exact(definition.Outcome),
                $"{location}/rejection",
                rejected,
                TransitionObservationInfluence.Outcome);
            AddFact(ConditionalFact.Outcome(
                TransitionDecisionKind.AdmissionRejected,
                rejected,
                Origin(movement.Id, $"{location}/rejection", null, TransitionObservationInfluence.Outcome)));

            var legal = conditions.And(input, sourceTest);
            foreach (var assignment in link.Assignments)
            {
                var assignmentValidation = PortableExecutionValidator.Validate(assignment.Value, graph);
                foreach (var diagnostic in assignmentValidation.Diagnostics)
                    diagnostics.Add(WithEvidence(diagnostic, movement.Id, "definitionLinking"));

                var target = ResolveRelativePath(
                    observationContract,
                    assignment.Path,
                    $"{location}/linked/assignments/{Encode(assignment.Path.ToString())}/path",
                    movement.Id);
                if (target is not null && assignment.Value.Contract != target)
                {
                    AddDiagnostic(
                        TransitionCompilationDiagnosticCodes.MachineConfigurationMismatch,
                        $"Machine edge '{movement.Edge.Value}' assigns path '{assignment.Path}' with a different contract.",
                        $"{location}/linked/assignments/{Encode(assignment.Path.ToString())}/value/contract",
                        movement.Id,
                        stage: "definitionLinking",
                        expected: Describe(target),
                        observed: Describe(assignment.Value.Contract),
                        resolutions: ["Regenerate the Machine link against the exact aggregate Shape revision."]);
                }

                AddObservationFact(
                    assignment.Path,
                    legal,
                    Origin(
                        movement.Id,
                        $"{location}/linked/assignments/{Encode(assignment.Path.ToString())}/path",
                        null,
                        TransitionObservationInfluence.Calculation | TransitionObservationInfluence.PatchTarget));
                AddFact(ConditionalFact.Write(
                    assignment.Path,
                    isDerived: false,
                    legal,
                    Origin(
                        movement.Id,
                        $"{location}/linked/assignments/{Encode(assignment.Path.ToString())}/path",
                        null,
                        TransitionObservationInfluence.None)));
            }

            var targetConfiguration = AnalyzeSite(
                movement.Id,
                TransitionExpressionSiteKind.MachineTargetConfiguration,
                link.TargetConfiguration,
                scope,
                ExprExpectation.Boolean,
                $"{location}/linked/targetConfiguration",
                legal,
                TransitionObservationInfluence.Invariant,
                candidateStateReads: true,
                conditionAtomScope: $"machine-target:{movement.Id.Value}");
            var targetTest = TryGetBoolean(targetConfiguration, link.TargetConfiguration, out var targetConstant)
                ? targetConstant ? conditions.True : conditions.False
                : BooleanExpressionCondition(
                    link.TargetConfiguration,
                    $"machine-target:{movement.Id.Value}");
            invariantsHoldCondition = conditions.And(
                invariantsHoldCondition,
                conditions.Or(conditions.Not(legal), targetTest));
            AddFact(ConditionalFact.MachineMovement(
                movement.Machine,
                movement.Edge,
                legal,
                Origin(movement.Id, location, null, TransitionObservationInfluence.None)));
            return legal;
        }

        TransitionCondition CompileOutcome(
            OutcomeTransitionNode outcome,
            string location,
            TransitionCondition input,
            ScopeState scope)
        {
            _ = AnalyzeSite(
                outcome.Id,
                TransitionExpressionSiteKind.OutcomeValue,
                outcome.Value,
                scope,
                Exact(definition.Outcome),
                $"{location}/value",
                input,
                TransitionObservationInfluence.Outcome);
            AddFact(ConditionalFact.Outcome(
                DecisionKind(outcome.Disposition),
                input,
                Origin(outcome.Id, location, null, TransitionObservationInfluence.Outcome)));
            if (outcome.Disposition is TransitionOutcomeDisposition.Applied
                or TransitionOutcomeDisposition.NoChange)
            {
                acceptedCondition = conditions.Or(acceptedCondition, input);
            }
            if (outcome.Disposition == TransitionOutcomeDisposition.Applied)
            {
                appliedCondition = conditions.Or(appliedCondition, input);
            }
            else if (outcome.Disposition == TransitionOutcomeDisposition.DomainRejected)
            {
                domainRejectedCondition = conditions.Or(domainRejectedCondition, input);
            }

            return conditions.False;
        }

        void AnalyzeCollectionElementPatch(
            UpdateTransitionNode update,
            string location,
            Expr value,
            ValueContract? target,
            ScopeState scope,
            TransitionCondition input,
            string member)
        {
            var element = GetCollectionElement(target);
            if (target is not null && element is null)
            {
                AddPatchMismatch(
                    update,
                    location,
                    "The patch operation requires a collection target.",
                    "collection",
                    Describe(target));
            }

            _ = AnalyzeSite(
                update.Id,
                TransitionExpressionSiteKind.PatchOperand,
                value,
                scope,
                element is null ? ExprExpectation.Any : Exact(element),
                $"{location}/operation/{member}",
                input,
                TransitionObservationInfluence.Calculation);
        }

        void AnalyzeOwnedChildPatch(
            UpdateTransitionNode update,
            string location,
            FieldPath identityPath,
            Expr identity,
            Expr? value,
            ValueContract? target,
            ScopeState scope,
            TransitionCondition input)
        {
            var element = GetCollectionElement(target);
            if (target is not null && element is null)
            {
                AddPatchMismatch(
                    update,
                    location,
                    "An owned-child patch requires a collection target.",
                    "collection of owned children",
                    Describe(target));
            }

            var identityContract = element is null
                ? null
                : ResolveRelativePath(element, identityPath, $"{location}/operation/identityPath", update.Id);
            _ = AnalyzeSite(
                update.Id,
                TransitionExpressionSiteKind.PatchOperand,
                identity,
                scope,
                identityContract is null ? ExprExpectation.Any : Exact(identityContract),
                $"{location}/operation/identity",
                input,
                TransitionObservationInfluence.Calculation);
            if (value is not null)
            {
                _ = AnalyzeSite(
                    update.Id,
                    TransitionExpressionSiteKind.PatchOperand,
                    value,
                    scope,
                    element is null ? ExprExpectation.Any : Exact(element),
                    $"{location}/operation/value",
                    input,
                    TransitionObservationInfluence.Calculation);
            }
        }

        void AddPatchTargetRead(
            UpdateTransitionNode update,
            string location,
            TransitionCondition input) => AddObservationFact(
            update.Path,
            input,
            Origin(
                update.Id,
                $"{location}/path",
                null,
                TransitionObservationInfluence.Calculation | TransitionObservationInfluence.PatchTarget));

        ValueContract? ResolvePatchTarget(
            UpdateTransitionNode update,
            string location)
        {
            var result = ResolveRelativePath(
                observationContract,
                update.Path,
                $"{location}/path",
                update.Id);
            return result;
        }

        ValueContract? ResolveRelativePath(
            ValueContract root,
            FieldPath path,
            string location,
            ExecutionNodeId node)
        {
            ValueBindingId binding = new("transition.compiler.pathRoot");
            ScopeState scope = new(
                [new(binding, root)],
                [],
                new Dictionary<ValueBindingId, ImmutableArray<ObservationDependency>>
                {
                    [binding] = []
                });
            var analysis = ExprAnalyzer.Analyze(new(
                new($"{SitePrefix()}/node/{Encode(node.Value)}/target/{Encode(path.ToString())}"),
                Expr.Field(binding, path),
                scope.ToExprScope(implicitBinding: binding),
                ExprExpectation.Any,
                CapabilityProfile,
                location));
            foreach (var diagnostic in analysis.Validation.Diagnostics)
            {
                diagnostics.Add(WithEvidence(
                    diagnostic.Code == ExprAnalysisDiagnosticCodes.FieldPathUnknown
                        ? diagnostic with { Code = TransitionCompilationDiagnosticCodes.PatchTargetUnknown }
                        : diagnostic,
                    node,
                    "typeAnalysis"));
            }

            return analysis.KnownResult;
        }

        void ValidateTargetCategory(
            UpdateTransitionNode update,
            string location,
            ValueContract? target,
            ExprResultCategory expected,
            string expectedDescription)
        {
            var actual = target?.GetResultCategory() ?? ExprResultCategory.Any;
            if (target is null
                || actual == expected
                || expected == ExprResultCategory.Numeric
                    && actual == ExprResultCategory.Integer)
            {
                return;
            }

            AddPatchMismatch(
                update,
                location,
                $"Patch '{update.Id.Value}' requires a {expectedDescription} target.",
                expectedDescription,
                Describe(target));
        }

        void AddPatchMismatch(
            UpdateTransitionNode update,
            string location,
            string message,
            string expected,
            string observed) => AddDiagnostic(
            TransitionCompilationDiagnosticCodes.PatchContractMismatch,
            message,
            $"{location}/operation",
            update.Id,
            stage: "typeAnalysis",
            expected: expected,
            observed: observed);

        TransitionExpressionSiteAnalysis AnalyzeSite(
            ExecutionNodeId node,
            TransitionExpressionSiteKind kind,
            Expr expression,
            ScopeState scope,
            ExprExpectation expectation,
            string location,
            TransitionCondition condition,
            TransitionObservationInfluence influence,
            bool retainObservationFacts = true,
            bool candidateStateReads = false,
            string? conditionAtomScope = null,
            ValueBindingId? implicitBinding = null)
        {
            if (ContainsGroupedAggregate(expression))
            {
                AddDiagnostic(
                    TransitionCompilationDiagnosticCodes.GroupedAggregateUnsupported,
                    "Grouped aggregate expressions are outside the finite Transition v1 expression language.",
                    location,
                    node,
                    stage: "expressionAnalysis",
                    expected: "ungrouped aggregate or pure collection function",
                    observed: "AggregateExpr with groupBy",
                    resolutions: ["Move grouping into a Cohesive.Relations query and supply its finite result as Transition input."]);
            }

            var siteId = new ExprSiteId(
                $"{SitePrefix()}/node/{Encode(node.Value)}/{SiteKindName(kind)}/{sites.Count.ToString(CultureInfo.InvariantCulture)}");
            var result = ExprAnalyzer.Analyze(new(
                siteId,
                expression,
                scope.ToExprScope(implicitBinding),
                expectation,
                CapabilityProfile,
                location));
            TransitionExpressionSiteAnalysis site = new(node, kind, result);
            sites.Add(site);

            foreach (var diagnostic in result.Validation.Diagnostics)
            {
                diagnostics.Add(WithEvidence(diagnostic, node, "expressionAnalysis"));
            }

            var evaluationConditions = conditions.IsSatisfiable(condition)
                ? AnalyzeExpressionEvaluation(expression, condition, conditionAtomScope)
                : new Dictionary<string, TransitionCondition>(StringComparer.Ordinal);
            foreach (var use in result.CapabilityUses)
            {
                AddFact(ConditionalFact.Capability(
                    use.Requirement,
                    EvaluationCondition(evaluationConditions, use.ExpressionPath, condition),
                    Origin(node, location, siteId, influence, use.ExpressionPath)));
            }

            if (retainObservationFacts)
            {
                foreach (var use in result.FieldUses)
                {
                    var useCondition = EvaluationCondition(
                        evaluationConditions,
                        use.ExpressionPath,
                        condition);
                    foreach (var dependency in ResolveObservationDependencies(use, scope, useCondition))
                    {
                        AddObservationFact(
                            dependency.Access,
                            dependency.Condition,
                            Origin(node, location, siteId, influence, use.ExpressionPath),
                            candidateStateReads);
                    }
                }

                foreach (var use in result.BindingUses)
                {
                    var useCondition = EvaluationCondition(
                        evaluationConditions,
                        use.ExpressionPath,
                        condition);
                    foreach (var dependency in ResolveObservationDependencies(use, scope, useCondition))
                    {
                        AddObservationFact(
                            dependency.Access,
                            dependency.Condition,
                            Origin(node, location, siteId, influence, use.ExpressionPath),
                            candidateStateReads);
                    }
                }
            }

            return site;
        }

        static bool ContainsGroupedAggregate(Expr expression)
        {
            if (expression is AggregateExpr { GroupBy.IsDefaultOrEmpty: false })
                return true;

            return expression switch
            {
                UnaryExpr unary => ContainsGroupedAggregate(unary.Operand),
                BinaryExpr binary => ContainsGroupedAggregate(binary.Left)
                                     || ContainsGroupedAggregate(binary.Right),
                ConditionalExpr conditional => ContainsGroupedAggregate(conditional.Test)
                                                 || ContainsGroupedAggregate(conditional.IfTrue)
                                                 || ContainsGroupedAggregate(conditional.IfFalse),
                CallExpr call => call.Arguments.Any(ContainsGroupedAggregate),
                AggregateExpr aggregate => ContainsGroupedAggregate(aggregate.Source)
                                             || aggregate.GroupBy.Any(ContainsGroupedAggregate),
                _ => false
            };
        }

        Dictionary<string, TransitionCondition> AnalyzeExpressionEvaluation(
            Expr expression,
            TransitionCondition input,
            string? conditionAtomScope = null)
        {
            Dictionary<string, TransitionCondition> result = new(StringComparer.Ordinal);
            Visit(expression, "/", input);
            return result;

            void Visit(Expr current, string expressionPath, TransitionCondition condition)
            {
                if (result.TryGetValue(expressionPath, out var existing))
                {
                    result[expressionPath] = conditions.Or(existing, condition);
                }
                else
                {
                    result.Add(expressionPath, condition);
                }

                switch (current)
                {
                    case UnaryExpr unary:
                        Visit(unary.Operand, ExpressionChild(expressionPath, "operand"), condition);
                        break;
                    case BinaryExpr { Operator: BinaryOperator.And } binary:
                        Visit(binary.Left, ExpressionChild(expressionPath, "left"), condition);
                        Visit(
                            binary.Right,
                            ExpressionChild(expressionPath, "right"),
                            conditions.And(
                                condition,
                                BooleanExpressionCondition(binary.Left, conditionAtomScope)));
                        break;
                    case BinaryExpr { Operator: BinaryOperator.Or } binary:
                        Visit(binary.Left, ExpressionChild(expressionPath, "left"), condition);
                        Visit(
                            binary.Right,
                            ExpressionChild(expressionPath, "right"),
                            conditions.And(
                                condition,
                                conditions.Not(BooleanExpressionCondition(binary.Left, conditionAtomScope))));
                        break;
                    case BinaryExpr binary:
                        Visit(binary.Left, ExpressionChild(expressionPath, "left"), condition);
                        Visit(binary.Right, ExpressionChild(expressionPath, "right"), condition);
                        break;
                    case ConditionalExpr conditional:
                        {
                            Visit(conditional.Test, ExpressionChild(expressionPath, "test"), condition);
                            var test = BooleanExpressionCondition(conditional.Test, conditionAtomScope);
                            Visit(
                                conditional.IfTrue,
                                ExpressionChild(expressionPath, "ifTrue"),
                                conditions.And(condition, test));
                            Visit(
                                conditional.IfFalse,
                                ExpressionChild(expressionPath, "ifFalse"),
                                conditions.And(condition, conditions.Not(test)));
                            break;
                        }
                    case CallExpr call:
                        for (var index = 0; index < call.Arguments.Length; index++)
                        {
                            Visit(
                                call.Arguments[index],
                                ExpressionChild(
                                    expressionPath,
                                    $"arguments/{index.ToString(CultureInfo.InvariantCulture)}"),
                                condition);
                        }
                        break;
                    case AggregateExpr aggregate:
                        Visit(aggregate.Source, ExpressionChild(expressionPath, "source"), condition);
                        for (var index = 0; index < aggregate.GroupBy.Length; index++)
                        {
                            Visit(
                                aggregate.GroupBy[index],
                                ExpressionChild(
                                    expressionPath,
                                    $"groupBy/{index.ToString(CultureInfo.InvariantCulture)}"),
                                condition);
                        }
                        break;
                }
            }
        }

        TransitionCondition BooleanExpressionCondition(
            Expr expression,
            string? conditionAtomScope = null)
        {
            if (TryGetBooleanConstant(expression, out var constant))
            {
                return constant ? conditions.True : conditions.False;
            }

            return expression switch
            {
                UnaryExpr { Operator: UnaryOperator.Not } unary =>
                    conditions.Not(BooleanExpressionCondition(unary.Operand, conditionAtomScope)),
                BinaryExpr { Operator: BinaryOperator.And } binary => conditions.And(
                    BooleanExpressionCondition(binary.Left, conditionAtomScope),
                    BooleanExpressionCondition(binary.Right, conditionAtomScope)),
                BinaryExpr { Operator: BinaryOperator.Or } binary => conditions.Or(
                    BooleanExpressionCondition(binary.Left, conditionAtomScope),
                    BooleanExpressionCondition(binary.Right, conditionAtomScope)),
                ConditionalExpr conditional => conditions.Or(
                    conditions.And(
                        BooleanExpressionCondition(conditional.Test, conditionAtomScope),
                        BooleanExpressionCondition(conditional.IfTrue, conditionAtomScope)),
                    conditions.And(
                        conditions.Not(BooleanExpressionCondition(conditional.Test, conditionAtomScope)),
                        BooleanExpressionCondition(conditional.IfFalse, conditionAtomScope))),
                _ => conditions.GetOrAddAtom(ExpressionAtom(expression, conditionAtomScope))
            };
        }

        string ExpressionAtom(Expr expression, string? conditionAtomScope)
        {
            ExpressionAtomKey key = new(expression, conditionAtomScope);
            if (expressionAtoms.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var canonical = JsonSerializer.SerializeToUtf8Bytes<Expr>(
                expression,
                ExpressionJsonOptions);
            var digest = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
            var atom = conditionAtomScope is null
                ? $"expression:{digest}"
                : $"expression:{AtomComponent(conditionAtomScope)}:{digest}";
            expressionAtoms.Add(key, atom);
            return atom;
        }

        TransitionCondition CandidateObservationCondition(
            TransitionObservationAccess access,
            TransitionCondition useCondition)
        {
            if (access.Path is not { } path)
            {
                return useCondition;
            }

            var suppliedByPatch = facts
                .Where(fact => fact.Kind == FactKind.Write
                               && fact.Path!.Value.IsPrefixOf(path))
                .Aggregate(
                    conditions.False,
                    (current, fact) => conditions.Or(current, fact.Condition));
            return conditions.And(useCondition, conditions.Not(suppliedByPatch));
        }

        static bool TryGetBooleanConstant(Expr expression, out bool value)
        {
            switch (expression)
            {
                case ConstantExpr constant when constant.Value.TryGetBoolean(out value):
                    return true;
                case LiteralExpr literal when literal.Value.TryGetBoolean(out value):
                    return true;
                case UnaryExpr { Operator: UnaryOperator.Not } unary
                    when TryGetBooleanConstant(unary.Operand, out var operand):
                    value = !operand;
                    return true;
                case BinaryExpr { Operator: BinaryOperator.And } binary
                    when TryGetBooleanConstant(binary.Left, out var left):
                    if (!left)
                    {
                        value = false;
                        return true;
                    }
                    return TryGetBooleanConstant(binary.Right, out value);
                case BinaryExpr { Operator: BinaryOperator.Or } binary
                    when TryGetBooleanConstant(binary.Left, out var left):
                    if (left)
                    {
                        value = true;
                        return true;
                    }
                    return TryGetBooleanConstant(binary.Right, out value);
                case ConditionalExpr conditional
                    when TryGetBooleanConstant(conditional.Test, out var test):
                    return TryGetBooleanConstant(test ? conditional.IfTrue : conditional.IfFalse, out value);
                default:
                    value = false;
                    return false;
            }
        }

        static TransitionCondition EvaluationCondition(
            IReadOnlyDictionary<string, TransitionCondition> conditionsByPath,
            string expressionPath,
            TransitionCondition fallback) =>
            conditionsByPath.TryGetValue(expressionPath, out var condition)
                ? condition
                : fallback;

        static string ExpressionChild(string parent, string child) =>
            parent == "/" ? $"/{child}" : $"{parent}/{child}";

        ImmutableArray<ObservationDependency> CollectObservationDependencies(
            TransitionExpressionSiteAnalysis site,
            ScopeState scope,
            Expr expression,
            TransitionCondition input)
        {
            var evaluationConditions = AnalyzeExpressionEvaluation(expression, input);
            Dictionary<TransitionObservationAccess, TransitionCondition> dependencies = [];
            foreach (var use in site.Analysis.FieldUses)
            {
                var useCondition = EvaluationCondition(evaluationConditions, use.ExpressionPath, input);
                foreach (var dependency in ResolveObservationDependencies(use, scope, useCondition))
                {
                    Add(dependency);
                }
            }

            foreach (var use in site.Analysis.BindingUses)
            {
                var useCondition = EvaluationCondition(evaluationConditions, use.ExpressionPath, input);
                foreach (var dependency in ResolveObservationDependencies(use, scope, useCondition))
                {
                    Add(dependency);
                }
            }

            return
            [
                .. dependencies
                    .OrderBy(static entry => entry.Key.SortKey, StringComparer.Ordinal)
                    .Select(static entry => new ObservationDependency(entry.Key, entry.Value))
            ];

            void Add(ObservationDependency dependency)
            {
                if (dependencies.TryGetValue(dependency.Access, out var existing))
                {
                    dependencies[dependency.Access] = conditions.Or(existing, dependency.Condition);
                }
                else
                {
                    dependencies.Add(dependency.Access, dependency.Condition);
                }
            }
        }

        IEnumerable<ObservationDependency> ResolveObservationDependencies(
            ExprFieldUse use,
            ScopeState scope,
            TransitionCondition useCondition)
        {
            var field = use.Requirement;
            if (field.Root != ExprFieldRootKind.Binding || field.Binding is not { } binding)
            {
                yield break;
            }

            if (binding == TransitionBindingIds.Observation)
            {
                yield return new(TransitionObservationAccess.At(field.Path), useCondition);
                yield break;
            }

            if (!scope.TryGetDependencies(binding, out var dependencies))
            {
                yield break;
            }

            foreach (var dependency in dependencies)
            {
                yield return dependency with
                {
                    Condition = conditions.And(dependency.Condition, useCondition)
                };
            }
        }

        IEnumerable<ObservationDependency> ResolveObservationDependencies(
            ExprBindingUse use,
            ScopeState scope,
            TransitionCondition useCondition)
        {
            if (!scope.TryGetDependencies(use.Binding, out var dependencies))
            {
                yield break;
            }

            foreach (var dependency in dependencies)
            {
                yield return dependency with
                {
                    Condition = conditions.And(dependency.Condition, useCondition)
                };
            }
        }

        void AnalyzeComputedFields(ScopeState scope)
        {
            if (!TryResolveObservationShape(out var shape))
            {
                return;
            }

            foreach (var field in shape.Fields
                         .Where(static field => field.Compute is not null)
                         .OrderBy(static field => field.Name.Value, StringComparer.Ordinal))
            {
                var path = FieldPath.FromField(field.Name.Value);
                var node = SyntheticNode("computed", path.ToString());
                var location = $"/shape/fields/{Encode(field.Name.Value)}/compute/expression";
                var site = AnalyzeSite(
                    node,
                    TransitionExpressionSiteKind.ComputedField,
                    field.Compute!.Expression,
                    scope,
                    Exact(ValueContract.FromField(field)),
                    location,
                    conditions.False,
                    TransitionObservationInfluence.DerivedField,
                    retainObservationFacts: false);
                var currentDependencies = CollectComputedDependencies(
                    site,
                    scope,
                    field.Compute.Expression,
                    conditionAtomScope: null,
                    diagnoseWholeObservation: true);
                var candidateDependencies = CollectComputedDependencies(
                    site,
                    scope,
                    field.Compute.Expression,
                    conditionAtomScope: "candidate",
                    diagnoseWholeObservation: false);
                var candidateCapabilities = CollectComputedCapabilities(
                    site,
                    field.Compute.Expression,
                    conditionAtomScope: "candidate");
                computedFields.Add(path, new(
                    path,
                    ValueContract.FromField(field),
                    field.Compute.Expression,
                    site,
                    currentDependencies,
                    candidateDependencies,
                    candidateCapabilities));
            }

            ResolveDerivedClosures();
        }

        ImmutableArray<ObservationDependency> CollectComputedDependencies(
            TransitionExpressionSiteAnalysis site,
            ScopeState scope,
            Expr expression,
            string? conditionAtomScope,
            bool diagnoseWholeObservation)
        {
            var evaluationConditions = AnalyzeExpressionEvaluation(
                expression,
                conditions.True,
                conditionAtomScope);
            Dictionary<TransitionObservationAccess, TransitionCondition> dependencies = [];
            foreach (var use in site.Analysis.FieldUses)
            {
                var useCondition = EvaluationCondition(
                    evaluationConditions,
                    use.ExpressionPath,
                    conditions.True);
                foreach (var dependency in ResolveObservationDependencies(use, scope, useCondition))
                {
                    Add(dependency);
                }
            }

            foreach (var use in site.Analysis.BindingUses)
            {
                var useCondition = EvaluationCondition(
                    evaluationConditions,
                    use.ExpressionPath,
                    conditions.True);
                foreach (var dependency in ResolveObservationDependencies(use, scope, useCondition))
                {
                    Add(dependency);
                }
            }

            if (diagnoseWholeObservation
                && dependencies.Keys.Any(static access => access.IsWhole))
            {
                AddDiagnostic(
                    TransitionCompilationDiagnosticCodes.DerivedFieldWholeObservation,
                    $"Computed field '{site.Node.Value}' depends on the complete aggregate observation, so a finite acyclic dependency closure cannot be established.",
                    site.Analysis.Site.DiagnosticLocation,
                    site.Node,
                    stage: "dependencyAnalysis",
                    expected: "explicit finite aggregate field dependencies",
                    observed: TransitionObservationAccess.Whole.ToString(),
                    resolutions: ["Reference the exact aggregate fields used by the computed expression."]);
            }

            return
            [
                .. dependencies
                    .OrderBy(static entry => entry.Key.SortKey, StringComparer.Ordinal)
                    .Select(static entry => new ObservationDependency(entry.Key, entry.Value))
            ];

            void Add(ObservationDependency dependency)
            {
                if (dependencies.TryGetValue(dependency.Access, out var existing))
                {
                    dependencies[dependency.Access] = conditions.Or(existing, dependency.Condition);
                }
                else
                {
                    dependencies.Add(dependency.Access, dependency.Condition);
                }
            }
        }

        ImmutableArray<ConditionalCapabilityUse> CollectComputedCapabilities(
            TransitionExpressionSiteAnalysis site,
            Expr expression,
            string conditionAtomScope)
        {
            var evaluationConditions = AnalyzeExpressionEvaluation(
                expression,
                conditions.True,
                conditionAtomScope);
            return
            [
                .. site.Analysis.CapabilityUses
                    .Select(use => new ConditionalCapabilityUse(
                        use.Requirement,
                        use.ExpressionPath,
                        EvaluationCondition(
                            evaluationConditions,
                            use.ExpressionPath,
                            conditions.True)))
                    .OrderBy(static use => use.ExpressionPath, StringComparer.Ordinal)
                    .ThenBy(static use => use.Requirement.Kind)
                    .ThenBy(static use => use.Requirement.Capability.Value, StringComparer.Ordinal)
            ];
        }

        void ResolveDerivedClosures()
        {
            Dictionary<FieldPath, VisitState> states = [];
            List<FieldPath> stack = [];
            foreach (var field in computedFields.Keys.OrderBy(
                         static path => path,
                         TransitionStructuralOrdering.FieldPaths))
            {
                _ = ResolveDerived(field, states, stack);
            }
        }

        ImmutableArray<FieldPath> ResolveDerived(
            FieldPath field,
            IDictionary<FieldPath, VisitState> states,
            IList<FieldPath> stack)
        {
            if (states.TryGetValue(field, out var state))
            {
                if (state == VisitState.Complete)
                {
                    return computedFields[field].BaseDependencies;
                }

                if (state == VisitState.Visiting)
                {
                    var start = stack.IndexOf(field);
                    var cycle = stack.Skip(Math.Max(0, start)).Append(field).ToArray();
                    AddDiagnostic(
                        TransitionCompilationDiagnosticCodes.DerivedFieldCycle,
                        $"Computed-field dependency cycle: {string.Join(" -> ", cycle)}.",
                        $"/shape/fields/{Encode(field.ToString())}/compute/expression",
                        computedFields[field].Site.Node,
                        stage: "dependencyAnalysis",
                        relatedLocations:
                        [
                            .. cycle.Distinct()
                                .Select(static member => $"/shape/fields/{Uri.EscapeDataString(member.ToString())}/compute/expression")
                        ],
                        resolutions: ["Break the computed-field cycle by making at least one dependency a base field."]);
                    return [];
                }
            }

            states[field] = VisitState.Visiting;
            stack.Add(field);
            var current = computedFields[field];
            foreach (var dependency in current.DirectDependencies)
            {
                if (TryGetOwningComputedField(dependency, out var owner))
                {
                    _ = ResolveDerived(owner.Path, states, stack);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            states[field] = VisitState.Complete;
            current.CurrentBaseDependencies = ExpandComputedDependencies(
                current.CurrentDirectDependencies,
                static computed => computed.CurrentBaseDependencies);
            current.CandidateBaseDependencies = ExpandComputedDependencies(
                current.CandidateDirectDependencies,
                static computed => computed.CandidateBaseDependencies);
            current.BaseDependencies = SortPaths(
                current.CurrentBaseDependencies
                    .Concat(current.CandidateBaseDependencies)
                    .Select(static dependency => dependency.Access.Path)
                    .OfType<FieldPath>());
            computedOrder.Add(current);
            return current.BaseDependencies;
        }

        ImmutableArray<ObservationDependency> ExpandComputedDependencies(
            ImmutableArray<ObservationDependency> directDependencies,
            Func<ComputedFieldState, ImmutableArray<ObservationDependency>> selectResolved)
        {
            Dictionary<TransitionObservationAccess, TransitionCondition> resolved = [];
            foreach (var direct in directDependencies)
            {
                if (direct.Access.Path is { } path
                    && TryGetOwningComputedField(path, out var computed))
                {
                    foreach (var dependency in selectResolved(computed))
                    {
                        Add(new(
                            dependency.Access,
                            conditions.And(direct.Condition, dependency.Condition)));
                    }
                }
                else
                {
                    Add(direct);
                }
            }

            return
            [
                .. resolved
                    .OrderBy(static entry => entry.Key.SortKey, StringComparer.Ordinal)
                    .Select(static entry => new ObservationDependency(entry.Key, entry.Value))
            ];

            void Add(ObservationDependency dependency)
            {
                if (resolved.TryGetValue(dependency.Access, out var existing))
                {
                    resolved[dependency.Access] = conditions.Or(existing, dependency.Condition);
                }
                else
                {
                    resolved.Add(dependency.Access, dependency.Condition);
                }
            }
        }

        bool TryGetOwningComputedField(
            FieldPath dependency,
            out ComputedFieldState computed)
        {
            ComputedFieldState? best = null;
            foreach (var candidate in computedFields.Values)
            {
                if (!candidate.Path.IsPrefixOf(dependency)
                    || best is not null
                    && best.Path.Segments.Length >= candidate.Path.Segments.Length)
                {
                    continue;
                }

                best = candidate;
            }

            computed = best!;
            return best is not null;
        }

        void AnalyzeInvariants(ScopeState scope)
        {
            for (var index = 0; index < definition.Invariants.Length; index++)
            {
                var invariant = definition.Invariants[index];
                const string CandidateConditionAtomScope = "candidate";
                var analysis = AnalyzeSite(
                    invariant.Id,
                    TransitionExpressionSiteKind.InvariantPredicate,
                    invariant.Predicate,
                    scope,
                    ExprExpectation.Boolean,
                    $"/definition/invariants/{index.ToString(CultureInfo.InvariantCulture)}/predicate",
                    acceptedCondition,
                    TransitionObservationInfluence.Invariant,
                    candidateStateReads: true,
                    conditionAtomScope: CandidateConditionAtomScope);
                var holds = TryGetBoolean(analysis, invariant.Predicate, out var constant)
                    ? constant ? conditions.True : conditions.False
                    : BooleanExpressionCondition(invariant.Predicate, CandidateConditionAtomScope);
                if (!constant && !conditions.IsSatisfiable(holds) && conditions.IsSatisfiable(acceptedCondition))
                {
                    AddDiagnostic(
                        TransitionCompilationDiagnosticCodes.InvariantDisproven,
                        $"Invariant '{invariant.Id.Value}' is statically false on every accepted path.",
                        $"/definition/invariants/{index.ToString(CultureInfo.InvariantCulture)}/predicate",
                        invariant.Id,
                        stage: "proof",
                        expected: "true on every accepted resulting state",
                        observed: "false",
                        resolutions: ["Correct the invariant or establish it through the accepted path's patches."]);
                }
                invariantsHoldCondition = conditions.And(invariantsHoldCondition, holds);
            }
        }

        void AddDerivedRecomputationFacts()
        {
            foreach (var computed in computedOrder)
            {
                var affected = conditions.False;
                var availableWrites = facts
                    .Where(static fact => fact.Kind == FactKind.Write)
                    .ToArray();
                foreach (var write in availableWrites)
                {
                    if (computed.CandidateDirectDependencies.Any(dependency =>
                            dependency.Access.IsWhole
                            || dependency.Access.Path is { } path
                            && path.Overlaps(write.Path!.Value)))
                    {
                        affected = conditions.Or(affected, write.Condition);
                    }
                }

                computed.AffectedCondition = affected;
                if (!conditions.IsSatisfiable(affected))
                {
                    continue;
                }

                AddFact(ConditionalFact.Write(
                    computed.Path,
                    isDerived: true,
                    affected,
                    Origin(
                        computed.Site.Node,
                        computed.Site.Analysis.Site.DiagnosticLocation,
                        computed.Site.Analysis.Site.Id,
                        TransitionObservationInfluence.DerivedField)));
                foreach (var capabilityUse in computed.CandidateCapabilities)
                {
                    var capabilityCondition = conditions.And(affected, capabilityUse.Condition);
                    AddFact(ConditionalFact.Capability(
                        capabilityUse.Requirement,
                        capabilityCondition,
                        Origin(
                            computed.Site.Node,
                            computed.Site.Analysis.Site.DiagnosticLocation,
                            computed.Site.Analysis.Site.Id,
                            TransitionObservationInfluence.DerivedField,
                            capabilityUse.ExpressionPath)));
                }

                foreach (var dependency in computed.CandidateDirectDependencies)
                {
                    AddObservationFact(
                        dependency.Access,
                        conditions.And(affected, dependency.Condition),
                        Origin(
                            computed.Site.Node,
                            computed.Site.Analysis.Site.DiagnosticLocation,
                            computed.Site.Analysis.Site.Id,
                            TransitionObservationInfluence.DerivedField | TransitionObservationInfluence.Calculation),
                        candidateStateReads: true);
                }
            }
        }

        void ValidateOverlappingWrites()
        {
            var writes = facts
                .Where(static fact => fact.Kind == FactKind.Write && !fact.IsDerived)
                .ToArray();
            for (var rightIndex = 0; rightIndex < writes.Length; rightIndex++)
            {
                var right = writes[rightIndex];
                for (var leftIndex = 0; leftIndex < rightIndex; leftIndex++)
                {
                    var left = writes[leftIndex];
                    if (!left.Path!.Value.Overlaps(right.Path!.Value)
                        || conditions.AreMutuallyExclusive(left.Condition, right.Condition))
                    {
                        continue;
                    }

                    AddDiagnostic(
                        TransitionCompilationDiagnosticCodes.WriteOverlap,
                        $"Patch '{right.Origin.Node.Value}' overlaps earlier patch '{left.Origin.Node.Value}' on a feasible realized path.",
                        right.Origin.Location,
                        right.Origin.Node,
                        stage: "effectAnalysis",
                        relatedLocations: [left.Origin.Location],
                        expected: "at most one write to overlapping semantic paths per realized path",
                        observed: $"{left.Path} and {right.Path} under {conditions.Format(conditions.And(left.Condition, right.Condition))}",
                        resolutions: ["Combine the patches or place them in statically mutually exclusive branches."]);
                }
            }
        }

        TransitionSemanticAnalysis BuildAnalysis()
        {
            var emissionCondition = facts
                .Where(static fact => fact.Kind == FactKind.Emission)
                .Aggregate(conditions.False, (current, fact) => conditions.Or(current, fact.Condition));
            var acceptedWithInvariants = conditions.And(acceptedCondition, invariantsHoldCondition);
            var appliedCommit = conditions.And(appliedCondition, invariantsHoldCondition);
            var emissionTerminalDomain = conditions.Or(acceptedWithInvariants, domainRejectedCondition);
            var emissionCommit = conditions.And(emissionCondition, emissionTerminalDomain);
            var commitCondition = conditions.Or(appliedCommit, emissionCommit);
            List<TransitionSemanticRequirement> requirements = [];
            if (definition.SubjectCreation is { } creation)
            {
                var site = sites.Single(candidate =>
                    candidate.Node == creation.Id
                    && candidate.Kind == TransitionExpressionSiteKind.SubjectInitializer);
                requirements.Add(new TransitionSubjectCreationRequirement(
                    ToRef(conditions.True),
                    TransitionRequirementStrength.Must,
                    [new(
                        creation.Id,
                        ToRef(conditions.True),
                        "/definition/subjectCreation",
                        site.Analysis.Site.Id,
                        sourceReferences: SourcesFor("/definition/subjectCreation"))]));
            }

            foreach (var group in facts
                         .Where(fact => conditions.IsSatisfiable(fact.Condition))
                         .GroupBy(static fact => fact.Key)
                         .OrderBy(static group => group.Key.SortKey, StringComparer.Ordinal))
            {
                var groupFacts = group.ToArray();
                var combined = groupFacts.Aggregate(
                    conditions.False,
                    (current, fact) => conditions.Or(current, fact.Condition));
                var acceptedOutcome = group.Key.Kind == FactKind.Outcome
                                      && group.Key.DecisionKind is TransitionDecisionKind.Applied
                                          or TransitionDecisionKind.NoChange;
                if (acceptedOutcome)
                {
                    combined = conditions.And(combined, invariantsHoldCondition);
                }

                if (!conditions.IsSatisfiable(combined))
                {
                    continue;
                }

                var invocationStrength = conditions.Implies(conditions.True, combined)
                    ? TransitionRequirementStrength.Must
                    : TransitionRequirementStrength.May;
                var occurrences = groupFacts
                    .OrderBy(static fact => fact.Origin.Location, StringComparer.Ordinal)
                    .ThenBy(static fact => fact.Origin.SchemaLocation, StringComparer.Ordinal)
                    .ThenBy(static fact => fact.Origin.Node.Value, StringComparer.Ordinal)
                    .Select(fact => CreateOccurrence(
                        fact,
                        acceptedOutcome
                            ? conditions.And(fact.Condition, invariantsHoldCondition)
                            : null))
                    .Where(occurrence => conditions.IsSatisfiable(
                        new TransitionCondition(occurrence.Condition.Node)))
                    .ToImmutableArray();

                if (group.Key.Kind == FactKind.ObservationRead)
                {
                    if (definition.SubjectCreation is not null)
                    {
                        // Creation expressions read only the initializer-derived candidate. Authoritative absence,
                        // not pre-existing field acquisition or freshness, is the external storage requirement.
                        continue;
                    }

                    var commitValidation = BuildCommitValidation(groupFacts, commitCondition);
                    requirements.Add(new TransitionObservationRequirement(
                        group.Key.ObservationAccess!,
                        ToRef(combined),
                        invocationStrength,
                        groupFacts.Aggregate(
                            TransitionObservationInfluence.None,
                            static (current, fact) => current | fact.Origin.Influence),
                        ToRef(commitValidation.Condition),
                        ClassifyOptionalInvocationStrength(commitValidation.Condition),
                        commitValidation.Occurrences,
                        occurrences));
                    continue;
                }

                requirements.Add(group.Key.Kind switch
                {
                    FactKind.Write => new TransitionWriteRequirement(
                        group.Key.Path!.Value,
                        group.Key.IsDerived,
                        ToRef(combined),
                        invocationStrength,
                        occurrences),
                    FactKind.Emission => new TransitionEmissionRequirement(
                        groupFacts[0].Contract!,
                        ToRef(combined),
                        invocationStrength,
                        occurrences),
                    FactKind.MachineMovement => new TransitionMachineMovementRequirement(
                        groupFacts[0].Contract!,
                        groupFacts[0].Edge!.Value,
                        ToRef(combined),
                        invocationStrength,
                        occurrences),
                    FactKind.Capability => new TransitionCapabilityRequirement(
                        group.Key.Capability!.Value,
                        ToRef(combined),
                        invocationStrength,
                        occurrences),
                    FactKind.Outcome => new TransitionOutcomeRequirement(
                        group.Key.DecisionKind!.Value,
                        ToRef(combined),
                        invocationStrength,
                        occurrences),
                    _ => throw new InvalidOperationException($"Unsupported Transition fact kind '{group.Key.Kind}'.")
                });
            }

            var derived = computedFields.Values
                .OrderBy(
                    static field => field.Path,
                    TransitionStructuralOrdering.FieldPaths)
                .Select(field => new TransitionDerivedFieldAnalysis(
                    field.Path,
                    field.DirectDependencies,
                    field.BaseDependencies,
                    conditions.IsSatisfiable(field.AffectedCondition)))
                .ToImmutableArray();
            var conditionModel = new TransitionConditionModel(conditions);
            return new(
                conditionModel,
                ToRef(conditions.True),
                ToRef(admittedCondition),
                ToRef(acceptedWithInvariants),
                ToRef(commitCondition),
                [.. sites],
                [.. requirements],
                [.. branches.OrderBy(static branch => branch.Node.Value, StringComparer.Ordinal)],
                derived);
        }

        (TransitionCondition Condition, ImmutableArray<TransitionRequirementOccurrence> Occurrences)
            BuildCommitValidation(
                IReadOnlyList<ConditionalFact> groupFacts,
                TransitionCondition commitCondition)
        {
            var combined = conditions.False;
            var builder = ImmutableArray.CreateBuilder<TransitionRequirementOccurrence>(groupFacts.Count);
            foreach (var fact in groupFacts
                         .OrderBy(static fact => fact.Origin.Location, StringComparer.Ordinal)
                         .ThenBy(static fact => fact.Origin.SchemaLocation, StringComparer.Ordinal)
                         .ThenBy(static fact => fact.Origin.Node.Value, StringComparer.Ordinal))
            {
                var validationCondition = conditions.And(fact.Condition, commitCondition);
                if (!conditions.IsSatisfiable(validationCondition))
                {
                    continue;
                }

                combined = conditions.Or(combined, validationCondition);
                builder.Add(CreateOccurrence(fact, validationCondition));
            }

            return (combined, builder.ToImmutable());
        }

        TransitionRequirementStrength? ClassifyOptionalInvocationStrength(TransitionCondition condition)
        {
            if (!conditions.IsSatisfiable(condition))
            {
                return null;
            }

            return conditions.Implies(conditions.True, condition)
                ? TransitionRequirementStrength.Must
                : TransitionRequirementStrength.May;
        }

        TransitionRequirementOccurrence CreateOccurrence(
            ConditionalFact fact,
            TransitionCondition? conditionOverride = null) => new(
            fact.Origin.Node,
            ToRef(conditionOverride ?? fact.Condition),
            fact.Origin.Location,
            fact.Origin.Site,
            fact.Origin.SchemaLocation,
            fact.Origin.Influence,
            SourcesFor(fact.Origin.Location));

        void AddBranchAnalysis(
            ExecutionNodeId node,
            TransitionCondition domain,
            TransitionProofStatus coverage,
            string reason,
            ImmutableArray<TransitionAlternativeAnalysis> alternatives,
            ImmutableArray<string> uncovered,
            string location)
        {
            branches.Add(new(node, ToRef(domain), coverage, reason, alternatives, uncovered));
            if (coverage == TransitionProofStatus.Proven)
            {
                return;
            }

            var code = coverage == TransitionProofStatus.Disproven
                ? TransitionCompilationDiagnosticCodes.ExhaustivenessDisproven
                : TransitionCompilationDiagnosticCodes.ExhaustivenessUnknown;
            AddDiagnostic(
                code,
                reason,
                $"{location}/completeness",
                node,
                stage: "proof",
                expected: "proved exhaustive branch coverage",
                observed: uncovered.IsDefaultOrEmpty
                    ? coverage.ToString()
                    : string.Join(", ", uncovered),
                resolutions: ["Add an explicit fallback or use a finite closed domain that the compiler can prove exhaustive."]);
        }

        MatchProof AnalyzeMatchCoverage(
            MatchTransitionNode match,
            TransitionExpressionSiteAnalysis site)
        {
            if (site.Analysis.KnownConstant is { } constant)
            {
                var covered = match.Cases.Any(matchCase => PatternMatches(matchCase.Pattern, constant));
                if (match.Fallback is not null)
                {
                    return new(
                        TransitionProofStatus.Proven,
                        "An explicit fallback covers every value not selected by an exact pattern.",
                        [],
                        covered);
                }

                return covered
                    ? new(
                        TransitionProofStatus.Proven,
                        "The statically known Match value is covered by an exact pattern.",
                        [],
                        CasesExhaustDomain: true)
                    : new(
                        TransitionProofStatus.Disproven,
                        "The statically known Match value is not covered by any exact pattern.",
                        [Describe(constant)],
                        CasesExhaustDomain: false);
            }

            if (match.Fallback is not null)
            {
                return new(
                    TransitionProofStatus.Proven,
                    "An explicit fallback covers every value not selected by an exact pattern.",
                    [],
                    CasesExhaustDomain: false);
            }

            return new(
                TransitionProofStatus.Unknown,
                "Exact PortableValue patterns cannot close a dynamic Match domain because unknown and an open family of failed values remain distinct under the current value contract.",
                [],
                CasesExhaustDomain: false);
        }

        static int LastUniqueCaseIndex(ImmutableArray<TransitionMatchCase> cases)
        {
            HashSet<PortableValue> observed = [];
            var last = -1;
            for (var index = 0; index < cases.Length; index++)
            {
                if (observed.Add(cases[index].Pattern))
                {
                    last = index;
                }
            }

            return last;
        }

        static bool PatternMatches(PortableValue pattern, ObservationValue constant) =>
            pattern.State switch
            {
                PortableValueState.Null => constant.Kind == ObservationValueKind.Null,
                PortableValueState.Absent => constant.Kind == ObservationValueKind.Undefined,
                PortableValueState.Concrete => pattern.Value is { } value && value.Equals(constant),
                _ => false
            };

        void AddObservationFact(
            FieldPath path,
            TransitionCondition condition,
            FactOrigin origin,
            bool candidateStateReads = false) => AddObservationFact(
            TransitionObservationAccess.At(path),
            condition,
            origin,
            candidateStateReads);

        void AddObservationFact(
            TransitionObservationAccess access,
            TransitionCondition condition,
            FactOrigin origin,
            bool candidateStateReads = false)
        {
            var directCondition = candidateStateReads
                ? CandidateObservationCondition(access, condition)
                : condition;
            AddFact(ConditionalFact.Observation(access, directCondition, origin));
        }

        void AddFact(ConditionalFact fact) => facts.Add(fact);

        FactOrigin Origin(
            ExecutionNodeId node,
            string location,
            ExprSiteId? site,
            TransitionObservationInfluence influence,
            string? schemaLocation = null) => new(
            node,
            location,
            site,
            schemaLocation,
            influence);

        void AddDiagnostic(
            string code,
            string message,
            string location,
            ExecutionNodeId node,
            string stage,
            ImmutableArray<string> relatedLocations = default,
            ImmutableArray<string> resolutions = default,
            string? expected = null,
            string? observed = null,
            DiagnosticSeverity severity = DiagnosticSeverity.Error)
        {
            diagnostics.Add(new(
                Code: code,
                Severity: severity,
                Message: message,
                Location: location,
                Evidence: new(
                    stage: stage,
                    subject: node.Value,
                    relatedLocations: relatedLocations,
                    sourceReferences: SourcesFor(location),
                    resolutionOptions: resolutions,
                    expected: expected,
                    observed: observed)));
        }

        DocumentValidationDiagnostic WithEvidence(
            DocumentValidationDiagnostic diagnostic,
            ExecutionNodeId node,
            string stage) => diagnostic with
            {
                Evidence = new(
                    stage: stage,
                    subject: node.Value,
                    relatedLocations: diagnostic.Evidence?.RelatedLocations ?? [],
                    sourceReferences: SourcesFor(diagnostic.Location ?? "/definition"),
                    resolutionOptions: diagnostic.Evidence?.ResolutionOptions ?? [],
                    expected: diagnostic.Evidence?.Expected,
                    observed: diagnostic.Evidence?.Observed)
            };

        ImmutableArray<string> SourcesFor(string? location) =>
            document.Metadata.SourceMap.ResolveReferences(
                location,
                document.Metadata.Provenance.Source.Reference);

        bool TryResolveObservationShape(out Shape shape)
        {
            if (definition.Observation.Shape is { } identity
                && graph is not null
                && graph.TryGetShape(identity, out var resolved))
            {
                shape = resolved;
                return true;
            }

            shape = null!;
            return false;
        }

        ValueContract ResolveContract(ValueContract contract)
        {
            if (contract.Type is not null
                || contract.Shape is not { } identity
                || graph is null
                || !graph.TryGetShape(identity, out var shape))
            {
                return contract;
            }

            var resolved = ValueContract.FromShape(shape, identity);
            return new(
                resolved.Type,
                identity,
                contract.Cardinality,
                contract.Presence,
                contract.Nullability);
        }

        static ImmutableArray<ExprScopeParameter> CreateParameters(ValueContract input)
        {
            if (input.Cardinality != FieldCardinality.Single
                || input.Type is not ObjectTypeRef objectType)
            {
                return [];
            }

            return
            [
                .. objectType.Fields
                    .OrderBy(static field => field.Name, StringComparer.Ordinal)
                    .Select(static field =>
                    {
                        ValueContract value = new(
                            field.Type,
                            cardinality: field.Cardinality,
                            presence: field.Presence,
                            nullability: field.Nullability);
                        return new ExprScopeParameter(field.Name, value, field.Presence);
                    })
            ];
        }

        static ExprExpectation Exact(ValueContract contract) => new(
            contract.GetResultCategory() == ExprResultCategory.Integer
                ? ExprResultCategory.Numeric
                : contract.GetResultCategory(),
            contract);

        static ValueContract? GetCollectionElement(ValueContract? target)
        {
            if (target is null)
            {
                return null;
            }

            if (target.Cardinality == FieldCardinality.Many)
            {
                return target.Type is null
                    ? null
                    : new(target.Type);
            }

            return target.GetEffectiveType() is ArrayTypeRef array
                ? new(array.ElementType)
                : null;
        }

        bool TargetsComputedField(FieldPath path, out string computed)
        {
            foreach (var candidate in computedFields.Values)
            {
                if (candidate.Path.Overlaps(path))
                {
                    computed = candidate.Path.ToString();
                    return true;
                }
            }

            computed = string.Empty;
            return false;
        }

        static ImmutableArray<FieldPath> SortPaths(IEnumerable<FieldPath> paths) =>
        [
            .. paths.Distinct().OrderBy(
                static path => path,
                TransitionStructuralOrdering.FieldPaths)
        ];

        static ImmutableArray<TransitionObservationAccess> SortAccesses(
            IEnumerable<TransitionObservationAccess> accesses) =>
        [
            .. accesses.Distinct()
                .OrderBy(static access => access.IsWhole ? 0 : 1)
                .ThenBy(
                    static access => access.Path.GetValueOrDefault(),
                    TransitionStructuralOrdering.FieldPaths)
        ];

        static bool TryGetBoolean(
            TransitionExpressionSiteAnalysis analysis,
            Expr expression,
            out bool value)
        {
            if (analysis.Analysis.KnownConstant is { } constant
                && constant.TryGetBoolean(out value))
            {
                return true;
            }

            return TryGetBooleanConstant(expression, out value);
        }

        static string Describe(ValueContract contract) =>
            $"{contract.GetResultCategory()} ({contract.Presence}, {contract.Nullability})";

        static string Describe(ObservationValue value) => value.Kind switch
        {
            ObservationValueKind.Undefined => "absent",
            ObservationValueKind.Null => "null",
            _ => value.ToString()
        };

        static string Describe(PortableValue value) => value.State switch
        {
            PortableValueState.Absent => "absent",
            PortableValueState.Null => "null",
            PortableValueState.Concrete => value.Value?.ToString() ?? "concrete",
            _ => value.State.ToString()
        };

        string SitePrefix() => $"transition/{Encode(document.Metadata.DefinitionId.Value)}";

        static string SiteKindName(TransitionExpressionSiteKind kind) => kind switch
        {
            TransitionExpressionSiteKind.AdmissionPredicate => "admissionPredicate",
            TransitionExpressionSiteKind.AdmissionRejection => "admissionRejection",
            TransitionExpressionSiteKind.LetValue => "let",
            TransitionExpressionSiteKind.ChoicePredicate => "choicePredicate",
            TransitionExpressionSiteKind.MatchValue => "match",
            TransitionExpressionSiteKind.PatchOperand => "patch",
            TransitionExpressionSiteKind.EmissionPayload => "emission",
            TransitionExpressionSiteKind.OutcomeValue => "outcome",
            TransitionExpressionSiteKind.InvariantPredicate => "invariant",
            TransitionExpressionSiteKind.ComputedField => "computed",
            TransitionExpressionSiteKind.MachineSourceConfiguration => "machineSource",
            TransitionExpressionSiteKind.MachineRejection => "machineRejection",
            TransitionExpressionSiteKind.MachineTargetConfiguration => "machineTarget",
            TransitionExpressionSiteKind.SubjectInitializer => "subjectInitializer",
            _ => "unknown"
        };

        static string Encode(string value) => Uri.EscapeDataString(value);

        static ExecutionNodeId SyntheticNode(string kind, string value) =>
            new($"compiler/{kind}/{value}");

        static string MatchAtom(ExecutionNodeId match, ExecutionNodeId matchCase) =>
            $"match:{AtomComponent(match.Value)}:{AtomComponent(matchCase.Value)}";

        static string AtomComponent(string value) =>
            $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

        TransitionConditionRef ToRef(TransitionCondition condition) =>
            new(conditions, condition.Node);

        static TransitionDecisionKind DecisionKind(TransitionOutcomeDisposition disposition) => disposition switch
        {
            TransitionOutcomeDisposition.Applied => TransitionDecisionKind.Applied,
            TransitionOutcomeDisposition.NoChange => TransitionDecisionKind.NoChange,
            TransitionOutcomeDisposition.DomainRejected => TransitionDecisionKind.DomainRejected,
            _ => TransitionDecisionKind.InvalidDefinition
        };

        sealed class ScopeState
        {
            readonly List<ExprScopeBinding> bindings;
            readonly ImmutableArray<ExprScopeParameter> parameters;
            readonly Dictionary<ValueBindingId, ImmutableArray<ObservationDependency>> dependencies;

            public ScopeState(
                IEnumerable<ExprScopeBinding> bindings,
                ImmutableArray<ExprScopeParameter> parameters,
                Dictionary<ValueBindingId, ImmutableArray<ObservationDependency>> dependencies)
            {
                this.bindings = [.. bindings];
                this.parameters = parameters.IsDefault ? [] : parameters;
                this.dependencies = dependencies;
            }

            public void Add(
                ExprScopeBinding binding,
                ImmutableArray<ObservationDependency> observationDependencies)
            {
                bindings.Add(binding);
                dependencies[binding.Id] = observationDependencies;
            }

            public bool TryGetDependencies(
                ValueBindingId binding,
                out ImmutableArray<ObservationDependency> observationDependencies) =>
                dependencies.TryGetValue(binding, out observationDependencies);

            public ExprScope ToExprScope(ValueBindingId? implicitBinding = null) => new(
                bindings,
                implicitBinding ?? TransitionBindingIds.Observation,
                parameters);

            public ScopeState Clone() => new(
                bindings,
                parameters,
                dependencies.ToDictionary(static entry => entry.Key, static entry => entry.Value));

        }

        sealed class ComputedFieldState(
            FieldPath path,
            ValueContract contract,
            Expr expression,
            TransitionExpressionSiteAnalysis site,
            ImmutableArray<ObservationDependency> currentDirectDependencies,
            ImmutableArray<ObservationDependency> candidateDirectDependencies,
            ImmutableArray<ConditionalCapabilityUse> candidateCapabilities)
        {
            public FieldPath Path { get; } = path;

            public ValueContract Contract { get; } = contract;

            public Expr Expression { get; } = expression;

            public TransitionExpressionSiteAnalysis Site { get; } = site;

            public ImmutableArray<ObservationDependency> CurrentDirectDependencies { get; } = currentDirectDependencies;

            public ImmutableArray<ObservationDependency> CandidateDirectDependencies { get; } = candidateDirectDependencies;

            public ImmutableArray<FieldPath> DirectDependencies { get; } =
            [
                .. currentDirectDependencies
                    .Concat(candidateDirectDependencies)
                    .Select(static dependency => dependency.Access.Path)
                    .OfType<FieldPath>()
                    .Distinct()
                    .OrderBy(
                        static dependency => dependency,
                        TransitionStructuralOrdering.FieldPaths)
            ];

            public ImmutableArray<ConditionalCapabilityUse> CandidateCapabilities { get; } = candidateCapabilities;

            public ImmutableArray<ObservationDependency> CurrentBaseDependencies { get; set; } = [];

            public ImmutableArray<ObservationDependency> CandidateBaseDependencies { get; set; } = [];

            public ImmutableArray<FieldPath> BaseDependencies { get; set; } = [];

            public TransitionCondition AffectedCondition { get; set; }
        }

        enum VisitState : byte
        {
            Visiting,
            Complete
        }

        enum FactKind : byte
        {
            ObservationRead,
            Write,
            Emission,
            MachineMovement,
            Capability,
            Outcome
        }

        readonly record struct FactOrigin(
            ExecutionNodeId Node,
            string Location,
            ExprSiteId? Site,
            string? SchemaLocation,
            TransitionObservationInfluence Influence);

        readonly record struct ExpressionAtomKey(Expr Expression, string? Scope);

        readonly record struct ObservationDependency(
            TransitionObservationAccess Access,
            TransitionCondition Condition);

        readonly record struct ConditionalCapabilityUse(
            ExprCapabilityRequirement Requirement,
            string ExpressionPath,
            TransitionCondition Condition);

        readonly record struct FactKey(
            FactKind Kind,
            TransitionObservationAccess? ObservationAccess = null,
            FieldPath? Path = null,
            bool IsDerived = false,
            ExecutionDefinitionReference? Contract = null,
            ExecutionNodeId? Edge = null,
            ExprCapabilityRequirement? Capability = null,
            TransitionDecisionKind? DecisionKind = null)
        {
            public string SortKey => Kind switch
            {
                FactKind.ObservationRead => $"0:{ObservationAccess?.SortKey}",
                FactKind.Write => $"1:{(IsDerived ? 1 : 0).ToString(CultureInfo.InvariantCulture)}:{Path}",
                FactKind.Emission => $"2:{Contract?.DefinitionId.Value}:{Contract?.RevisionId.Value}:{Contract?.Fingerprint.Value}",
                FactKind.MachineMovement => $"3:{Contract?.DefinitionId.Value}:{Contract?.RevisionId.Value}:{Contract?.Fingerprint.Value}:{Edge?.Value}",
                FactKind.Capability => $"4:{((int?)Capability?.Kind).GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}:{Capability?.Capability.Value}",
                FactKind.Outcome => $"5:{((int?)DecisionKind).GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}",
                _ => "9"
            };
        }

        sealed class ConditionalFact
        {
            ConditionalFact(
                FactKey key,
                TransitionCondition condition,
                FactOrigin origin,
                ExecutionDefinitionReference? contract = null)
            {
                Key = key;
                Kind = key.Kind;
                ObservationAccess = key.ObservationAccess;
                Path = key.Path;
                IsDerived = key.IsDerived;
                Condition = condition;
                Origin = origin;
                Contract = contract;
            }

            public FactKey Key { get; }

            public FactKind Kind { get; }

            public TransitionObservationAccess? ObservationAccess { get; }

            public FieldPath? Path { get; }

            public bool IsDerived { get; }

            public TransitionCondition Condition { get; }

            public FactOrigin Origin { get; }

            public ExecutionDefinitionReference? Contract { get; }

            public ExecutionNodeId? Edge => Key.Edge;

            public static ConditionalFact Observation(
                TransitionObservationAccess access,
                TransitionCondition condition,
                FactOrigin origin) => new(
                new(FactKind.ObservationRead, ObservationAccess: access),
                condition,
                origin);

            public static ConditionalFact Write(
                FieldPath path,
                bool isDerived,
                TransitionCondition condition,
                FactOrigin origin) => new(
                new(FactKind.Write, Path: path, IsDerived: isDerived),
                condition,
                origin);

            public static ConditionalFact Emission(
                ExecutionDefinitionReference contract,
                TransitionCondition condition,
                FactOrigin origin) => new(
                new(FactKind.Emission, Contract: contract),
                condition,
                origin,
                contract);

            public static ConditionalFact MachineMovement(
                ExecutionDefinitionReference machine,
                ExecutionNodeId edge,
                TransitionCondition condition,
                FactOrigin origin) => new(
                new(FactKind.MachineMovement, Contract: machine, Edge: edge),
                condition,
                origin,
                machine);

            public static ConditionalFact Capability(
                ExprCapabilityRequirement capability,
                TransitionCondition condition,
                FactOrigin origin) => new(
                new(FactKind.Capability, Capability: capability),
                condition,
                origin);

            public static ConditionalFact Outcome(
                TransitionDecisionKind decisionKind,
                TransitionCondition condition,
                FactOrigin origin) => new(
                new(FactKind.Outcome, DecisionKind: decisionKind),
                condition,
                origin);
        }

        readonly record struct MatchProof(
            TransitionProofStatus Coverage,
            string Reason,
            ImmutableArray<string> Uncovered,
            bool CasesExhaustDomain);

        readonly record struct MachineEdgeKey(
            ExecutionDefinitionReference Machine,
            ExecutionNodeId Edge);
    }
}
