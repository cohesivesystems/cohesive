using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessCancellationFinalizationTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ProcessChildOutcomeMapping Mapping = new(
        new("completed"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    [Fact]
    public void Validator_RejectsMultipleFinalizersAndOrdinaryGraphReachability()
    {
        var process = Reference("process/cancellation-finalizer", 'a');
        RequestContractReference request = new(Reference("request/cancellation-finalizer", 'b'));
        var first = new CancellationFinalizerProcessNode(new("cancel/first"), process, request, Mapping);
        var second = new CancellationFinalizerProcessNode(new("cancel/second"), process, request, Mapping);
        var duplicate = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("return"),
            [new ReturnProcessNode(new("return"), Expr.Const("done")), first, second],
            ProcessRecoveryPolicy.ContinueAttempt);
        var entry = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            first.Id,
            [first],
            ProcessRecoveryPolicy.ContinueAttempt);
        var targeted = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("cut"),
            [
                new DurableCutProcessNode(new("cut"), new(new("edge/cut-finalizer"), first.Id)),
                first
            ],
            ProcessRecoveryPolicy.ContinueAttempt);

        var duplicateValidation = ProcessDefinitionValidator.Validate(duplicate);
        var entryValidation = ProcessDefinitionValidator.Validate(entry);
        var targetValidation = ProcessDefinitionValidator.Validate(targeted);

        Assert.Contains(duplicateValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDefinitionDiagnosticCodes.CancellationFinalizerInvalid
            && diagnostic.Evidence?.Subject == "cancel/second");
        Assert.Contains(entryValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDefinitionDiagnosticCodes.CancellationFinalizerInvalid
            && diagnostic.Location == "/entry");
        Assert.Contains(targetValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDefinitionDiagnosticCodes.CancellationFinalizerInvalid
            && diagnostic.Evidence?.Subject == "edge/cut-finalizer");
    }

    [Fact]
    public void Compiler_InventoriesExactFinalizerEffectsDependenciesAndTargetGap()
    {
        var fixture = CompileFinalizerPlan();

        var requests = ProcessRequestRequirementCollector.Collect(fixture.Plan);
        var request = Assert.Single(requests.Requirements);
        Assert.Equal(new ExecutionNodeId("cancel/finalize"), request.Node);
        Assert.Equal(ProcessRequestRequirementKind.ChildProcessInvocation, request.Kind);
        Assert.Equal(fixture.Request, request.Request);

        Assert.Equal(
            [
                ProcessEffectKind.DurableWait,
                ProcessEffectKind.ExternalInteraction,
                ProcessEffectKind.ChildProcess,
                ProcessEffectKind.Compensation
            ],
            fixture.Plan.EffectSummary.Effects
                .Where(static effect => effect.Node == new ExecutionNodeId("cancel/finalize"))
                .Select(static effect => effect.Kind));
        Assert.Contains(fixture.Plan.EffectSummary.Resources, resource =>
            resource.Node == new ExecutionNodeId("cancel/finalize")
            && resource.Resource == fixture.ChildProcess);
        Assert.Contains(fixture.Plan.EffectSummary.Resources, resource =>
            resource.Node == new ExecutionNodeId("cancel/finalize")
            && resource.Resource == fixture.Request.Definition);

        var requirements = ProcessInterpreterRequirementCollector.Collect(fixture.Plan);
        var construct = Assert.Single(requirements.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterRequirementKey.ForConstruct(
                ProcessWireNames.CancellationFinalizerNode));
        Assert.Equal(new ExecutionNodeId("cancel/finalize"), Assert.Single(construct.Nodes));
        Assert.Contains(fixture.ChildProcess, construct.LinkedDefinitions);
        Assert.Contains(fixture.Request.Definition, construct.LinkedDefinitions);
        Assert.Contains(requirements.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterGuarantees.InputAdmissionAndDisposition);
        Assert.Contains(requirements.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterGuarantees.DurableRequestRecovery);
        Assert.Contains(requirements.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterGuarantees.ForkJoinChildLineage);

        var planning = DurableTaskProcessRealizationCompiler.Compile(fixture.Plan);
        var executable = DurableTaskProcessRealizationCompiler.CompileExecutable(fixture.Plan);
        AssertTargetUnavailable(planning);
        AssertTargetUnavailable(executable);
    }

    [Fact]
    public void Validator_RequiresTheCanonicalFinalizerInputAndAcknowledgementContracts()
    {
        var fixture = CompileFinalizerPlan();
        var expectedInput = ProcessCancellationFinalizationContracts.Input(StringContract);
        var wrongInput = ProcessDefinitionValidator.Validate(
            fixture.Plan.Definition,
            new ProcessDefinitionValidationContext(
                definitions:
                [
                    new(
                        fixture.ChildProcess,
                        ProcessDefinitionLinkKind.Process,
                        StringContract,
                        ProcessCancellationFinalizationContracts.Acknowledgement,
                        [],
                        ProcessRecoveryPolicy.ContinueAttempt)
                ],
                interactionContracts: fixture.Catalog));
        var wrongResult = ProcessDefinitionValidator.Validate(
            fixture.Plan.Definition,
            new ProcessDefinitionValidationContext(
                definitions:
                [
                    new(
                        fixture.ChildProcess,
                        ProcessDefinitionLinkKind.Process,
                        expectedInput,
                        StringContract,
                        [],
                        ProcessRecoveryPolicy.ContinueAttempt)
                ],
                interactionContracts: fixture.Catalog));

        Assert.Contains(wrongInput.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDefinitionDiagnosticCodes.CancellationFinalizerInvalid
            && diagnostic.Message.Contains("input", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wrongResult.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDefinitionDiagnosticCodes.CancellationFinalizerInvalid
            && diagnostic.Message.Contains("result", StringComparison.OrdinalIgnoreCase));
    }

    static void AssertTargetUnavailable(DurableTaskProcessPlanningResult result)
    {
        var key = ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.CancellationFinalizerNode);
        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        Assert.Equal(
            CapabilityRealizationKind.Unavailable,
            Assert.Single(result.Realization.Decisions, decision => decision.Requirement == key).Realization);
        var diagnostic = Assert.Single(result.Realization.Diagnostics, candidate =>
            candidate.Code == ProcessInterpreterRealizationDiagnosticCodes.RequirementUnavailable
            && candidate.Requirement == key);
        Assert.Equal(new ExecutionNodeId("cancel/finalize"), Assert.Single(diagnostic.Nodes));
    }

    static FinalizerFixture CompileFinalizerPlan()
    {
        var input = ProcessCancellationFinalizationContracts.Input(StringContract);
        var requestDocument = InteractionContractDocuments.Create(
            new("interaction/request/cancellation-finalization-tests"),
            new("revision/1"),
            new RequestContractDefinition(
                new(input, new("cancellation-finalizer-input/v1")),
                new RequestResponseObligation(
                    [
                        new RequestResultDefinition(
                            Mapping.Completed,
                            new(
                                ProcessCancellationFinalizationContracts.Acknowledgement,
                                new("cancellation-acknowledgement/v1"))),
                        new RequestFailureDefinition(Mapping.Failed, StringSchema("finalizer-failed/v1")),
                        new RequestFailureDefinition(Mapping.Cancelled, StringSchema("finalizer-cancelled/v1")),
                        new RequestFailureDefinition(Mapping.Terminated, StringSchema("finalizer-terminated/v1"))
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
        RequestContractReference request = new(Reference(requestDocument));
        var child = Reference("process/cancellation-finalization-tests/finalizer", 'c');
        var definition = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("return"),
            [
                new ReturnProcessNode(new("return"), Expr.Const("done")),
                new CancellationFinalizerProcessNode(new("cancel/finalize"), child, request, Mapping)
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(
            new("process/cancellation-finalization-tests"),
            new("revision/1"),
            definition,
            Provenance());
        var catalogValidation = InteractionContractCatalog.TryCreate([requestDocument], out var catalog);
        Assert.True(catalogValidation.IsValid, Format(catalogValidation));
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(
                definitions:
                [
                    new(
                        child,
                        ProcessDefinitionLinkKind.Process,
                        input,
                        ProcessCancellationFinalizationContracts.Acknowledgement,
                        [],
                        ProcessRecoveryPolicy.ContinueAttempt)
                ],
                interactionContracts: catalog));
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        return new(
            Assert.IsType<CompiledProcessPlan>(compilation.Plan),
            child,
            request,
            Assert.IsType<InteractionContractCatalog>(catalog));
    }

    static InteractionValueSchema StringSchema(string revision) => new(StringContract, new(revision));

    static ExecutionDefinitionReference Reference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance() => new(
        new("tests.process-cancellation-finalization", "1"),
        new("tests/ari-462/cancellation-finalization"),
        DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed record FinalizerFixture(
        CompiledProcessPlan Plan,
        ExecutionDefinitionReference ChildProcess,
        RequestContractReference Request,
        InteractionContractCatalog Catalog);
}
