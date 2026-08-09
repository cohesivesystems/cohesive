using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskProcessRealizationPlanningTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void PlanningProfile_ExplicitlyDisposesEveryDeclaredConstructAndGuarantee()
    {
        var profile = DurableTaskProcessTargetProfile.Planning;
        var actual = profile.Evidence.ToDictionary(static evidence => evidence.Requirement);
        var expectedRequirements = ProcessNodeConstructCatalog.DeclaredRequirements
            .Concat(ProcessInterpreterGuarantees.All)
            .OrderBy(static requirement => requirement.Category)
            .ThenBy(static requirement => requirement.Name, StringComparer.Ordinal);

        Assert.Equal(DurableTaskProcessTargetProfile.Target, profile.Target);
        Assert.Equal(DurableTaskProcessTargetProfile.PlanningProfileId, profile.Id);
        Assert.Equal(expectedRequirements, actual.Keys
            .OrderBy(static requirement => requirement.Category)
            .ThenBy(static requirement => requirement.Name, StringComparer.Ordinal));
        Assert.Equal(profile.Evidence.Length, profile.Evidence.Select(static evidence => evidence.Id).Distinct().Count());

        AssertDisposition(actual, CapabilityRealizationKind.Native,
            ProcessWireNames.ChoiceNode,
            ProcessWireNames.MatchNode,
            ProcessWireNames.ReturnNode,
            ProcessWireNames.FailNode);
        AssertDisposition(actual, CapabilityRealizationKind.Composed,
            ProcessWireNames.InvokeTransitionNode,
            ProcessWireNames.EvaluateRelationNode,
            ProcessWireNames.RequestNode,
            ProcessWireNames.EmitEventNode,
            ProcessWireNames.SendSignalNode,
            ProcessWireNames.ForkNode,
            ProcessWireNames.JoinNode,
            ProcessWireNames.AwaitMatchNode,
            ProcessWireNames.TimerNode,
            ProcessWireNames.ReplyNode,
            ProcessWireNames.DurableCutNode,
            ProcessWireNames.InvokeProcessNode,
            ProcessWireNames.RepeatAcrossActivationNode);
        AssertDisposition(actual, CapabilityRealizationKind.Constrained, ProcessWireNames.ForEachPartitionNode);

        AssertGuaranteeDisposition(actual, CapabilityRealizationKind.Composed,
            ProcessInterpreterGuarantees.ExactDefinitionPinning,
            ProcessInterpreterGuarantees.StableExecutionIdentity,
            ProcessInterpreterGuarantees.DeterministicReplay,
            ProcessInterpreterGuarantees.InputAdmissionAndDisposition,
            ProcessInterpreterGuarantees.LifecycleControl,
            ProcessInterpreterGuarantees.DurableRequestRecovery,
            ProcessInterpreterGuarantees.ForkJoinChildLineage,
            ProcessInterpreterGuarantees.DefinitionAndWorkerEvolution,
            ProcessInterpreterGuarantees.StatusTraceAndExplain);
        AssertGuaranteeDisposition(actual, CapabilityRealizationKind.Constrained,
            ProcessInterpreterGuarantees.ExternalEffectDelivery,
            ProcessInterpreterGuarantees.BoundedWorkAndRecurrence,
            ProcessInterpreterGuarantees.SensitiveAndOversizedPayloads);
        AssertGuaranteeDisposition(actual, CapabilityRealizationKind.Unavailable,
            ProcessInterpreterGuarantees.WholeDefinitionAtomicity);
    }

    [Fact]
    public void Compiler_ProducesAnExactExplainablePlanWithoutReplacingCanonicalAuthority()
    {
        var canonical = ReturnPlan();

        var result = DurableTaskProcessRealizationCompiler.Compile(canonical);

        Assert.True(result.IsSuccessful, Format(result.Realization.Diagnostics));
        var physical = Assert.IsType<DurableTaskProcessRealizationPlan>(result.Plan);
        Assert.Same(canonical, physical.CanonicalPlan);
        Assert.Equal(canonical.DefinitionReference, physical.Definition);
        Assert.Same(result.Realization, physical.Realization);
        Assert.Equal(DurableTaskProcessTargetProfile.PlanningProfileId, physical.Realization.TargetProfile.Id);
        Assert.Equal(
            result.Realization.Inventory.Requirements.Select(static requirement => requirement.Key),
            physical.Requirements.Select(static realization => realization.Requirement.Key));
        Assert.Equal(
            result.Realization.Decisions.Select(static decision => decision.Requirement),
            physical.Requirements.Select(static realization => realization.Decision.Requirement));

        var terminal = Assert.Single(
            physical.Requirements,
            static realization => realization.Requirement.Key
                == ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.ReturnNode));
        Assert.Equal(new ExecutionNodeId("return"), Assert.Single(terminal.Requirement.Nodes));
        Assert.Equal(CapabilityRealizationKind.Native, terminal.Decision.Realization);
        Assert.NotNull(terminal.Decision.Evidence);
        Assert.Contains(result.Realization.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessInterpreterRealizationDiagnosticCodes.RequirementConstrained
            && diagnostic.Requirement == ProcessInterpreterGuarantees.SensitiveAndOversizedPayloads);
    }

    [Fact]
    public void Compiler_IsDeterministicForTheSameExactCanonicalPlan()
    {
        var canonical = ReturnPlan();

        var first = DurableTaskProcessRealizationCompiler.Compile(canonical);
        var second = DurableTaskProcessRealizationCompiler.Compile(canonical);

        Assert.True(first.IsSuccessful, Format(first.Realization.Diagnostics));
        Assert.True(second.IsSuccessful, Format(second.Realization.Diagnostics));
        Assert.Equal(
            Snapshot(Assert.IsType<DurableTaskProcessRealizationPlan>(first.Plan)).ToArray(),
            Snapshot(Assert.IsType<DurableTaskProcessRealizationPlan>(second.Plan)).ToArray());
    }

    [Fact]
    public void Compiler_RejectsWholeDefinitionAtomicityBeforeProducingAPhysicalPlan()
    {
        var canonical = ReturnPlan(new(ProcessAtomicScopeDemand.WholeDefinition));

        var result = DurableTaskProcessRealizationCompiler.Compile(canonical);

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        Assert.Equal(ProcessInterpreterRealizationStatus.NotRealizable, result.Realization.Status);
        var decision = Assert.Single(
            result.Realization.Decisions,
            static candidate => candidate.Requirement == ProcessInterpreterGuarantees.WholeDefinitionAtomicity);
        Assert.Equal(CapabilityRealizationKind.Unavailable, decision.Realization);
        var diagnostic = Assert.Single(
            result.Realization.Diagnostics,
            static candidate => candidate.Code == ProcessInterpreterRealizationDiagnosticCodes.RequirementUnavailable
                && candidate.Requirement == ProcessInterpreterGuarantees.WholeDefinitionAtomicity);
        Assert.Equal(new ExecutionNodeId("return"), Assert.Single(diagnostic.Nodes));
    }

    [Fact]
    public void Compiler_RetainsExactLinkedDefinitionEvidenceInThePhysicalPlan()
    {
        var canonical = RequestPlan(out var requestReference);

        var result = DurableTaskProcessRealizationCompiler.Compile(canonical);

        Assert.True(result.IsSuccessful, Format(result.Realization.Diagnostics));
        var request = Assert.Single(
            Assert.IsType<DurableTaskProcessRealizationPlan>(result.Plan).Requirements,
            static realization => realization.Requirement.Key
                == ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.RequestNode));
        Assert.Equal(requestReference, Assert.Single(request.Requirement.LinkedDefinitions));
        Assert.Equal(CapabilityRealizationKind.Composed, request.Decision.Realization);
        Assert.NotEmpty(request.Decision.AuxiliaryEvidence);
    }

    static void AssertDisposition(
        IReadOnlyDictionary<ProcessInterpreterRequirementKey, ProcessInterpreterCapabilityEvidence> actual,
        CapabilityRealizationKind expected,
        params ReadOnlySpan<string> wireNames)
    {
        foreach (var wireName in wireNames)
        {
            Assert.Equal(expected, actual[ProcessInterpreterRequirementKey.ForConstruct(wireName)].Realization);
        }
    }

    static void AssertGuaranteeDisposition(
        IReadOnlyDictionary<ProcessInterpreterRequirementKey, ProcessInterpreterCapabilityEvidence> actual,
        CapabilityRealizationKind expected,
        params ReadOnlySpan<ProcessInterpreterRequirementKey> guarantees)
    {
        foreach (var guarantee in guarantees)
        {
            Assert.Equal(expected, actual[guarantee].Realization);
        }
    }

    static ImmutableArray<string> Snapshot(DurableTaskProcessRealizationPlan plan) =>
    [
        .. plan.Requirements.Select(static realization => string.Join(
            "|",
            realization.Requirement.Key,
            string.Join(",", realization.Requirement.Nodes.Select(static node => node.Value)),
            string.Join(",", realization.Requirement.LinkedDefinitions.Select(Describe)),
            realization.Decision.Realization,
            realization.Decision.Evidence?.Value,
            string.Join(",", realization.Decision.AuxiliaryEvidence.Select(static evidence => evidence.Value)),
            string.Join(",", realization.Decision.OperatingBoundaries.Select(static boundary => boundary.Value))))
    ];

    static string Describe(ExecutionDefinitionReference reference) =>
        $"{reference.DefinitionId.Value}@{reference.RevisionId.Value}#"
        + $"{reference.Fingerprint.Algorithm}:{reference.Fingerprint.Canonicalization}:{reference.Fingerprint.Value}";

    static CompiledProcessPlan ReturnPlan(ProcessCompilationOptions? options = null) => Compile(
        new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("return"),
            [new ReturnProcessNode(new("return"), Expr.Const("done"))],
            ProcessRecoveryPolicy.ContinueAttempt),
        contracts: null,
        options);

    static CompiledProcessPlan RequestPlan(out ExecutionDefinitionReference requestReference)
    {
        var requestDocument = RequestDocument();
        requestReference = Reference(requestDocument);
        return Compile(
            new CanonicalProcessDefinition(
                StringContract,
                StringContract,
                new("request"),
                [
                    new RequestProcessNode(
                        new("request"),
                        new(requestReference),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        [
                            new(
                                new("request/accepted"),
                                new("accepted"),
                                new(new ProcessEdge(new("edge/request-return"), new("return"))))
                        ]),
                    new ReturnProcessNode(new("return"), Expr.Const("done"))
                ],
                ProcessRecoveryPolicy.ContinueAttempt),
            Catalog(requestDocument));
    }

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? contracts,
        ProcessCompilationOptions? options = null)
    {
        var document = ProcessDefinitionDocuments.Create(
            new("process/durable-task-realization-planning-tests"),
            new("revision/1"),
            definition,
            Provenance());
        var result = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(interactionContracts: contracts),
            options ?? ProcessCompilationOptions.Default);
        Assert.True(result.IsSuccessful, Format(result.Validation));
        return Assert.IsType<CompiledProcessPlan>(result.Plan);
    }

    static ExecutionDefinitionDocument RequestDocument() => InteractionContractDocuments.Create(
        new("interaction/request/durable-task-realization-planning-tests"),
        new("revision/1"),
        new RequestContractDefinition(
            new(StringContract, new("request/v1")),
            new RequestResponseObligation(
                [
                    new RequestResultDefinition(
                        new("accepted"),
                        new(StringContract, new("accepted/v1")))
                ],
                RequestOptionalTerminalSemantics.Unsupported,
                RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Observe,
                RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition,
                RequestRetrySemantics.StableIdentity,
                RequestResolutionSemantics.Reconcile,
                RequestResolutionSemantics.Escalate,
                TimeSpan.FromDays(30))),
        Provenance());

    static InteractionContractCatalog Catalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, Format(validation));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance() => new(
        new("durable-task-process-realization-planning-tests", "1"),
        new("tests/execution-kernel/durable-task-process-realization-planning"),
        DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static string Format(IEnumerable<ProcessInterpreterRealizationDiagnostic> diagnostics) => string.Join(
        Environment.NewLine,
        diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
