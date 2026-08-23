using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessComputationAuthoringTests
{
    [Fact]
    public void GeneratedComputation_IsByteEquivalentToCanonicalBuilderAuthoring()
    {
        var generated = CustomerQueryProcess.Define(Metadata());
        var query = ProcessAuthoringIdentities.NodeFor(new(["body", "query-row"]));
        var returned = ProcessAuthoringIdentities.NodeFor(new(["body", "return-0"]));
        var lowLevel = ProcessAuthoring.Create<string, string>(
            Metadata().WithEntry(query),
            process =>
            {
                var output = process.Output<string>(query, "result");

                process.EvaluateRelation(
                    query,
                    CustomerQueryProcess.Relation,
                    process.Input.Value,
                    process.Continuation(
                        process.Edge(query, "next", returned),
                        output));
                process.Return(returned, output.Value);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(lowLevel.Document.Metadata.Fingerprint, generated.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));

    }

    [Fact]
    public void CancellationFinalizerComputation_IsByteEquivalentToCanonicalBuilderAuthoring()
    {
        var returnId = ProcessAuthoringIdentities.NodeFor(new(["body", "return-0"]));
        var metadata = new ProcessAuthoringMetadata(
            new("process/tests/generated-cancellation-finalizer-parent"),
            new("revision/1"),
            returnId,
            ProcessRecoveryPolicy.ContinueAttempt,
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-469/cancellation-finalizer-parent"),
                DocumentOrigin.Generated));
        var generated = GeneratedCancellableProcess.Define(metadata);
        var finalizer = Assert.Single(generated.Definition.Nodes.OfType<CancellationFinalizerProcessNode>());
        var returned = Assert.Single(generated.Definition.Nodes.OfType<ReturnProcessNode>());
        var lowLevel = ProcessAuthoring.Create<string, string>(
            metadata.WithEntry(returned.Id),
            process =>
            {
                process.OnCancellation(finalizer.Id, GeneratedCancellableProcess.Cancellation);
                process.Return(returned.Id, process.Input.Value);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(new ExecutionNodeId("training/cancel/finalize"), finalizer.Id);
        Assert.Equal(GeneratedCancellableProcess.Cancellation.Process.Reference, finalizer.Process);
        Assert.Equal(GeneratedCancellableProcess.Cancellation.Request, finalizer.Contract);
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
    }

    [Fact]
    public void OutboundInteractionComputation_IsCanonicalRoundTripsAndExecutesWithStableEvidence()
    {
        var metadata = OutboundInteractionMetadata();
        var generated = GeneratedOutboundInteractionProcess.Define(metadata);
        var emitted = Assert.Single(generated.Definition.Nodes.OfType<EmitEventProcessNode>());
        var signalled = Assert.Single(generated.Definition.Nodes.OfType<SendSignalProcessNode>());
        var returned = Assert.Single(generated.Definition.Nodes.OfType<ReturnProcessNode>());
        var lowLevel = ProcessAuthoring.Create<OutboundInteractionInput, string>(
            metadata.WithEntry(emitted.Id),
            process =>
            {
                var target = process.Input.Field(static input => input.Target);
                var payload = process.Input.Field(static input => input.Value);

                process.EmitEvent(
                    emitted.Id,
                    GeneratedOutboundInteractionProcess.Event,
                    payload,
                    process.Edge(emitted.Id, "published", signalled.Id));
                process.SendSignal(
                    signalled.Id,
                    GeneratedOutboundInteractionProcess.Signal,
                    target,
                    payload,
                    process.Edge(signalled.Id, "sent", returned.Id));
                process.Return(returned.Id, payload);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(new ExecutionNodeId("interaction/event"), emitted.Id);
        Assert.Equal(new ExecutionNodeId("interaction/signal"), signalled.Id);
        Assert.Equal(ProcessAuthoringIdentities.EdgeFor(emitted.Id, "published"), emitted.Next.Id);
        Assert.Equal(ProcessAuthoringIdentities.EdgeFor(signalled.Id, "sent"), signalled.Next.Id);
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);
        var restoration = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);
        Assert.True(restoration.IsValid, Format(restoration));
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));

        var catalogValidation = InteractionContractCatalog.TryCreate(
            [
                GeneratedOutboundInteractionProcess.EventDocument,
                GeneratedOutboundInteractionProcess.SignalDocument
            ],
            out var catalog);
        Assert.True(catalogValidation.IsValid, Format(catalogValidation));
        var context = new ProcessDefinitionValidationContext(
            interactionContracts: Assert.IsType<InteractionContractCatalog>(catalog));
        var compilation = generated.Compile(context);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);

        var physical = DurableTaskProcessRealizationCompiler.CompileExecutable(plan);
        Assert.True(
            physical.IsSuccessful,
            string.Join(Environment.NewLine, physical.Realization.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var realization = Assert.IsType<DurableTaskProcessRealizationPlan>(physical.Plan);
        Assert.Equal(
            CapabilityRealizationKind.Constrained,
            Assert.Single(
                realization.Requirements,
                static requirement => requirement.Requirement.Key
                    == ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.EmitEventNode))
                .Decision.Realization);
        Assert.Equal(
            CapabilityRealizationKind.Composed,
            Assert.Single(
                realization.Requirements,
                static requirement => requirement.Requirement.Key
                    == ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.SendSignalNode))
                .Decision.Realization);

        var continuation = new ProcessContinuationIdentity(
            new("process-instance/outbound-interactions"),
            new("attempt/1"));
        var input = PortableValue.Concrete(
            plan.Definition.Input,
            ObservationValue.FromObject(new OutboundInteractionInput("reviewer/42", "approved")));
        var activation = Activation(
            plan,
            continuation,
            id: "activation/outbound-interactions",
            cause: ProcessActivationCause.Start,
            observedAtUtc: new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero),
            causationId: new("emission/source"));
        var host = new ResolvingSignalHost();
        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(plan, continuation, input),
            activation,
            host);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        var domainEvent = Assert.IsType<DomainEventEnvelope>(decision.Emissions[0]);
        var signal = Assert.IsType<SignalEnvelope>(decision.Emissions[1]);
        Assert.Equal(GeneratedOutboundInteractionProcess.Event, domainEvent.Contract);
        Assert.Equal(GeneratedOutboundInteractionProcess.Signal, signal.Contract);
        Assert.Equal("approved", domainEvent.Payload.Value?.String);
        Assert.Equal("approved", signal.Payload.Value?.String);
        Assert.Equal(domainEvent.Context.CorrelationId, signal.Context.CorrelationId);
        Assert.Equal(new EmissionId("emission/source"), domainEvent.Context.CausationId);
        Assert.Equal(domainEvent.Context.CausationId, signal.Context.CausationId);
        Assert.Equal(domainEvent.Context.AuthorityScope, signal.Context.AuthorityScope);
        Assert.Equal(domainEvent.Context.Delivery, signal.Context.Delivery);
        Assert.NotEqual(domainEvent.Context.EmissionId, signal.Context.EmissionId);
        Assert.NotEqual(domainEvent.Context.IdempotencyKey, signal.Context.IdempotencyKey);
        Assert.Equal("reviewer/42", Assert.Single(host.Resolutions).Value.Value?.String);
        Assert.Equal(host.Target, signal.Target);

        var replay = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(plan, continuation, input),
            activation,
            new ResolvingSignalHost());
        var envelopeOptions = InteractionEnvelopeJsonSerializer.CreateOptions();
        Assert.Equal(
            JsonSerializer.Serialize(decision.Emissions, envelopeOptions),
            JsonSerializer.Serialize(replay.Emissions, envelopeOptions));
    }

    [Fact]
    public void TypedForkComputation_IsByteEquivalentToCanonicalBuilderAuthoring()
    {
        var fork = Node("fork-0");
        var join = Owned(fork, "join");
        var auditBranch = Owned(fork, "branch-Audit");
        var auditQuery = Node("fork-0", "branch-Audit", "query-value");
        var notifyBranch = Owned(fork, "branch-Notify");
        var notifyQuery = Node("fork-0", "branch-Notify", "query-value");
        var returned = Node("return-0");
        var generated = GeneratedTypedForkProcess.Define(TypedForkMetadata().WithEntry(fork));
        var lowLevel = ProcessAuthoring.Create<string, string>(
            TypedForkMetadata().WithEntry(fork),
            process =>
            {
                var auditOutput = process.Output<string>(auditQuery, "result");
                var notifyOutput = process.Output<string>(notifyQuery, "result");
                var audit = process.ForkBranch(
                    auditBranch,
                    process.Edge(auditBranch, "start", auditQuery),
                    capacityDomain: "external-services");
                var notify = process.ForkBranch(
                    notifyBranch,
                    process.Edge(notifyBranch, "start", notifyQuery),
                    capacityDomain: "external-services");

                process.EvaluateRelation(
                    auditQuery,
                    GeneratedTypedForkProcess.RecordAudit,
                    process.Input.Value,
                    process.Continuation(
                        process.Edge(auditQuery, "next", join),
                        auditOutput));
                process.EvaluateRelation(
                    notifyQuery,
                    GeneratedTypedForkProcess.NotifyOwner,
                    process.Input.Value,
                    process.Continuation(
                        process.Edge(notifyQuery, "next", join),
                        notifyOutput));
                process.Fork(
                    id: fork,
                    branches: [audit, notify],
                    join: join,
                    limits: new ProcessWorkLimits(
                        maximumItems: 2,
                        maximumStartsPerActivation: 1,
                        maximumParallelism: 1,
                        minimumParallelism: 1),
                    capacityDomains: [new ProcessCapacityDomainLimit("external-services", maximumParallelism: 1)]);
                process.Join(
                    id: join,
                    fork: fork,
                    policy: new ProcessJoinPolicy(
                        mode: ProcessJoinMode.All,
                        requiredCount: 0,
                        failure: ProcessJoinFailurePolicy.FailFast,
                        cancellation: ProcessJoinCancellationPolicy.AwaitRemaining,
                        completionOrder: ProcessJoinCompletionOrder.Unobservable,
                        tieBreak: ProcessJoinTieBreak.BranchIdentity),
                    next: process.Edge(join, "next", returned));

                var result = process.CanonicalValue<string>(
                    new CallExpr(
                        ExprFunctionNames.Concat,
                        [notifyOutput.Expression, auditOutput.Expression],
                        auditOutput.Contract.Type),
                    auditOutput.Contract);
                process.Return(returned, result);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));

        var links = new[]
        {
            new ProcessDefinitionLink(
                GeneratedTypedForkProcess.RecordAudit,
                ProcessDefinitionLinkKind.RelationQuery,
                generated.Definition.Input,
                generated.Definition.Result),
            new ProcessDefinitionLink(
                GeneratedTypedForkProcess.NotifyOwner,
                ProcessDefinitionLinkKind.RelationQuery,
                generated.Definition.Input,
                generated.Definition.Result)
        };
        var linking = new ProcessDefinitionValidationContext(definitions: links);
        var generatedCompilation = generated.Compile(linking);
        var lowLevelCompilation = lowLevel.Compile(linking);
        Assert.True(generatedCompilation.IsSuccessful, Format(generatedCompilation.Validation));
        Assert.True(lowLevelCompilation.IsSuccessful, Format(lowLevelCompilation.Validation));
        var generatedPlan = Assert.IsType<CompiledProcessPlan>(generatedCompilation.Plan);
        var lowLevelPlan = Assert.IsType<CompiledProcessPlan>(lowLevelCompilation.Plan);
        Assert.Equal(lowLevelPlan.DefinitionReference, generatedPlan.DefinitionReference);
        Assert.Equal(lowLevelPlan.Definition, generatedPlan.Definition);
        Assert.Equivalent(lowLevelPlan.Options, generatedPlan.Options, strict: true);
        Assert.Equivalent(lowLevelPlan.EffectSummary, generatedPlan.EffectSummary, strict: true);

        AssertEquivalentReferenceRecovery(generatedPlan, lowLevelPlan);
    }

    [Fact]
    public void PureLocalInsertion_DoesNotRenumberSemanticNodes()
    {
        var original = CustomerQueryProcess.Define(Metadata());
        var withPureLocal = CustomerQueryProcessWithPureLocal.Define(Metadata());

        Assert.Equal(original.Definition, withPureLocal.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(original.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(withPureLocal.Document));
    }

    [Fact]
    public void ConventionIdentities_AreStableAcrossHeterogeneousInsertionAndIndependentReordering()
    {
        var baseline = GeneratedIdentityBaselineProcess.Define(IdentityMetadata());
        var reordered = GeneratedIdentityReorderedProcess.Define(IdentityMetadata());
        var inserted = GeneratedIdentityInsertedProcess.Define(IdentityMetadata());

        Assert.True(baseline.IsValid, Format(baseline.Validation));
        Assert.True(reordered.IsValid, Format(reordered.Validation));
        Assert.True(inserted.IsValid, Format(inserted.Validation));
        var baselineRelations = RelationIdentities(baseline.Definition);
        var reorderedRelations = RelationIdentities(reordered.Definition);
        var insertedRelations = RelationIdentities(inserted.Definition);
        Assert.Equal(baselineRelations, reorderedRelations);
        Assert.Equal(baselineRelations, insertedRelations);
        Assert.Equal(
            Assert.Single(baseline.Definition.Nodes.OfType<ReturnProcessNode>()).Id,
            Assert.Single(reordered.Definition.Nodes.OfType<ReturnProcessNode>()).Id);
        Assert.Equal(
            Assert.Single(baseline.Definition.Nodes.OfType<ReturnProcessNode>()).Id,
            Assert.Single(inserted.Definition.Nodes.OfType<ReturnProcessNode>()).Id);
        Assert.Equal(Node("return-0"), Assert.Single(inserted.Definition.Nodes.OfType<ReturnProcessNode>()).Id);

        var decision = GeneratedDecisionProcess.Define(DecisionMetadata());
        var choice = Assert.Single(decision.Definition.Nodes.OfType<ChoiceProcessNode>());
        var match = Assert.Single(decision.Definition.Nodes.OfType<MatchProcessNode>());
        Assert.Equal(new ExecutionNodeId("decision/category/fast"), Assert.Single(choice.Cases).Id);
        Assert.Equal(Owned(choice.Id, "otherwise"), choice.Fallback?.Id);
        Assert.Equal(Owned(match.Id, "case-Wait"), Assert.Single(match.Cases).Id);
        Assert.Equal(Owned(match.Id, "otherwise"), match.Fallback?.Id);

        static Dictionary<ExecutionDefinitionId, ExecutionNodeId> RelationIdentities(ProcessDefinition definition) =>
            definition.Nodes
                .OfType<EvaluateRelationProcessNode>()
                .ToDictionary(static node => node.Relation.DefinitionId, static node => node.Id);
    }

    [Fact]
    public void GeneratedComputation_HonorsMatchingExplicitEntryAndRejectsConflict()
    {
        var entry = ProcessAuthoringIdentities.NodeFor(new(["body", "query-row"]));

        var generated = CustomerQueryProcess.Define(Metadata().WithEntry(entry));

        Assert.Equal(entry, generated.Definition.Entry);
        Assert.Throws<ArgumentException>(() =>
            CustomerQueryProcess.Define(Metadata().WithEntry(new("conflicting-entry"))));
    }

    [Fact]
    public void GeneratedDocument_StrictlyRestoresWithoutHostLanguageState()
    {
        var generated = CustomerQueryProcess.Define(Metadata());
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);

        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(generated.Document, restoredDocument);
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        Assert.DoesNotContain(
            restoredDocument!.Definition.EnumerateObject(),
            static property => property.Name.Contains("delegate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("expressionTree", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("stateMachine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypedForkDocument_StrictlyRestoresWithoutTupleOrAuthoringPolicyState()
    {
        var generated = GeneratedTypedForkProcess.Define(TypedForkMetadata());
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);
        var json = Encoding.UTF8.GetString(canonical);

        var validation = ProcessDefinitionDocuments.TryDeserialize(
            json,
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        Assert.DoesNotContain("ValueTuple", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessAdmission", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTask", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialForkComputation_ProjectsOnlyTheCanonicalSelectedWinner()
    {
        var generated = GeneratedAnyForkProcess.Define(AnyForkMetadata());

        Assert.True(generated.IsValid, Format(generated.Validation));
        var fork = Assert.Single(generated.Definition.Nodes.OfType<ForkProcessNode>());
        var join = Assert.Single(generated.Definition.Nodes.OfType<JoinProcessNode>());
        var projection = Assert.IsType<ProcessJoinResultProjection>(join.Result);
        var auditBranchId = Owned(fork.Id, "branch-Audit");
        var notifyBranchId = Owned(fork.Id, "branch-Notify");
        var auditQuery = Assert.IsType<EvaluateRelationProcessNode>(
            generated.Definition.Nodes.Single(node => node.Id == fork.Branches.Single(branch => branch.Id == auditBranchId).Start.Target));
        var notifyQuery = Assert.IsType<EvaluateRelationProcessNode>(
            generated.Definition.Nodes.Single(node => node.Id == fork.Branches.Single(branch => branch.Id == notifyBranchId).Start.Target));
        var returned = Assert.IsType<ReturnProcessNode>(
            generated.Definition.Nodes.Single(node => node.Id == join.Next.Target));
        var lowLevel = ProcessAuthoring.Create<string, string>(
            AnyForkMetadata().WithEntry(fork.Id),
            process =>
            {
                var auditOutput = process.Output<string>(
                    auditQuery.Continuation.Output!.Binding,
                    auditQuery.Continuation.Output.Contract);
                var notifyOutput = process.Output<string>(
                    notifyQuery.Continuation.Output!.Binding,
                    notifyQuery.Continuation.Output.Contract);
                var selected = process.Output<ProcessJoinWinner<string>>(
                    projection.Output.Binding,
                    projection.Output.Contract);
                var auditBranch = process.ForkBranch(
                    auditBranchId,
                    process.Edge(auditBranchId, "start", auditQuery.Id));
                var notifyBranch = process.ForkBranch(
                    notifyBranchId,
                    process.Edge(notifyBranchId, "start", notifyQuery.Id));
                var auditResult = process.CanonicalValue<string>(
                    projection.Branches.Single(branch => branch.Branch == auditBranchId).Result,
                    projection.ResultContract);
                var notifyResult = process.CanonicalValue<string>(
                    projection.Branches.Single(branch => branch.Branch == notifyBranchId).Result,
                    projection.ResultContract);

                process.EvaluateRelation(
                    auditQuery.Id,
                    GeneratedTypedForkProcess.RecordAudit,
                    process.Input.Value,
                    process.Continuation(process.Edge(auditQuery.Id, "next", join.Id), auditOutput));
                process.EvaluateRelation(
                    notifyQuery.Id,
                    GeneratedTypedForkProcess.NotifyOwner,
                    process.Input.Value,
                    process.Continuation(process.Edge(notifyQuery.Id, "next", join.Id), notifyOutput));
                process.Fork(fork.Id, [auditBranch, notifyBranch], join.Id);
                var result = process.JoinResult(
                    selected,
                    projection.ResultContract,
                    [
                        process.JoinBranchResult(auditBranchId, auditResult),
                        process.JoinBranchResult(notifyBranchId, notifyResult)
                    ]);
                process.Join(
                    join.Id,
                    fork.Id,
                    join.Policy,
                    process.Edge(join.Id, "next", returned.Id),
                    result);
                process.Return(
                    returned.Id,
                    process.CanonicalValue<string>(returned.Result, generated.Definition.Result));
            });

        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        Assert.Equal(ProcessJoinMode.Any, join.Policy.Mode);
        Assert.Equal(ProcessJoinCancellationPolicy.CancelRemaining, join.Policy.Cancellation);
        Assert.Equal(fork.Branches.Select(static branch => branch.Id), projection.Branches.Select(static branch => branch.Branch));
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.String), projection.ResultContract.Type);
        Assert.IsType<ObjectTypeRef>(projection.Output.Contract.Type);

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);
        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));

        var linking = new ProcessDefinitionValidationContext(
            definitions:
            [
                new(
                    GeneratedTypedForkProcess.RecordAudit,
                    ProcessDefinitionLinkKind.RelationQuery,
                    generated.Definition.Input,
                    projection.ResultContract),
                new(
                    GeneratedTypedForkProcess.NotifyOwner,
                    ProcessDefinitionLinkKind.RelationQuery,
                    generated.Definition.Input,
                    projection.ResultContract)
            ]);
        var compilation = generated.Compile(linking);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        var continuation = new ProcessContinuationIdentity(new("process-instance/any-fork"), new("attempt/1"));
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            continuation,
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromString("work")));
        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            new(
                id: new("activation/any-fork/1"),
                cause: ProcessActivationCause.Start,
                observedAtUtc: new DateTimeOffset(2026, 8, 4, 13, 0, 0, TimeSpan.Zero),
                context: new(
                    authorityScope: new("authority/tests", "tenant/cohesive"),
                    correlationId: new("correlation/any-fork"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: generated.Document.Metadata.Provenance)),
            EchoRelationHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        var resolvedFork = Assert.Single(decision.State.Forks);
        Assert.True(resolvedFork.SelectedBranches.SequenceEqual([auditBranchId]));
        Assert.Equal(
            PortableValue.Concrete(
                plan.Definition.Result,
                ObservationValue.FromString($"{auditBranchId.Value}:audit:work")),
            decision.State.Terminal.Detail?.Value);
        var stateOptions = InteractionEnvelopeJsonSerializer.CreateOptions();
        var restoredState = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(
                JsonSerializer.Serialize(decision.State, stateOptions),
                stateOptions));
        Assert.True(
            ProcessContinuationValidator.Validate(plan, restoredState).IsValid,
            Format(ProcessContinuationValidator.Validate(plan, restoredState)));
    }

    [Fact]
    public void RequiredForkComputation_ProjectsTheCanonicalSelectedSet()
    {
        var generated = GeneratedRequiredForkProcess.Define(RequiredForkMetadata());

        Assert.True(generated.IsValid, Format(generated.Validation));
        var join = Assert.Single(generated.Definition.Nodes.OfType<JoinProcessNode>());
        Assert.Equal(ProcessJoinMode.RequiredCount, join.Policy.Mode);
        Assert.Equal(2, join.Policy.RequiredCount);
        Assert.Equal(ProcessJoinCancellationPolicy.ContinueRemaining, join.Policy.Cancellation);
        var projection = Assert.IsType<ProcessJoinResultProjection>(join.Result);
        Assert.IsType<ArrayTypeRef>(projection.Output.Contract.Type);

        var compilation = generated.Compile(new ProcessDefinitionValidationContext(
            definitions:
            [
                new(
                    GeneratedTypedForkProcess.RecordAudit,
                    ProcessDefinitionLinkKind.RelationQuery,
                    generated.Definition.Input,
                    projection.ResultContract),
                new(
                    GeneratedTypedForkProcess.NotifyOwner,
                    ProcessDefinitionLinkKind.RelationQuery,
                    generated.Definition.Input,
                    projection.ResultContract),
                new(
                    CustomerQueryProcess.Relation,
                    ProcessDefinitionLinkKind.RelationQuery,
                    generated.Definition.Input,
                    projection.ResultContract)
            ]));
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        var continuation = new ProcessContinuationIdentity(new("process-instance/required-fork"), new("attempt/1"));
        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(
                plan,
                continuation,
                PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromString("work"))),
            Activation(
                plan,
                continuation,
                id: "activation/required-fork",
                cause: ProcessActivationCause.Start,
                observedAtUtc: new DateTimeOffset(2026, 8, 4, 14, 0, 0, TimeSpan.Zero)),
            EchoRelationHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(
            PortableValue.Concrete(plan.Definition.Result, ObservationValue.FromInt64(2)),
            decision.State.Terminal.Detail?.Value);
        Assert.True(ProcessContinuationValidator.Validate(plan, decision.State).IsValid);
    }

    [Fact]
    public void DurableWaitAndRequestComputation_LowersOnlyToCanonicalNodesAndPolicies()
    {
        var generated = GeneratedDurableWaitProcess.Define(DurableWaitMetadata());

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.Single(generated.Definition.Nodes.OfType<TimerProcessNode>());
        var request = Assert.Single(generated.Definition.Nodes.OfType<RequestProcessNode>());
        Assert.Equal(2, request.Outcomes.Length);
        var awaitMatch = Assert.Single(generated.Definition.Nodes.OfType<AwaitMatchProcessNode>());
        Assert.Equal(ProcessAwaitArbitration.ExclusivePriorityThenClauseId, awaitMatch.Arbitration);
        Assert.Equal(ProcessAwaitInputDisposition.Observe, awaitMatch.LateInput);
        Assert.Equal(ProcessAwaitInputDisposition.Reject, awaitMatch.StaleInput);
        Assert.Equal(ProcessAwaitInputDisposition.ReusePriorDisposition, awaitMatch.DuplicateInput);
        Assert.Equal(ProcessAwaitMissingTargetDisposition.DeadLetter, awaitMatch.MissingTarget);
        Assert.Equal(TimeSpan.FromDays(7), awaitMatch.RetentionHorizon);
        Assert.Equal(4, awaitMatch.Clauses.Length);
        Assert.Equal(3, awaitMatch.Clauses.OfType<ProcessAwaitInteractionClause>().Count());
        Assert.Single(awaitMatch.Clauses.OfType<ProcessAwaitTimerClause>());
        var inboundRequest = Assert.Single(
            awaitMatch.Clauses.OfType<ProcessAwaitInteractionClause>(),
            static clause => clause.Contract is RequestContractReference);
        Assert.NotNull(inboundRequest.RequestObligation);
        Assert.Contains(generated.Definition.Nodes, static node => node is ReplyProcessNode);

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);
        var json = Encoding.UTF8.GetString(canonical);
        var validation = ProcessDefinitionDocuments.TryDeserialize(
            json,
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        Assert.DoesNotContain("delegate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("callback", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProcessTask", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAny", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNestedDecisions_AreDifferentiallyEquivalentAndStrictlyRecoverable()
    {
        ExecutionNodeId choice = new("decision/category");
        ExecutionNodeId fastCase = new("decision/category/fast");
        var choiceFallback = Owned(choice, "otherwise");
        ExecutionNodeId match = new("decision/state");
        var waitCase = Owned(match, "case-Wait");
        var matchFallback = Owned(match, "otherwise");
        ExecutionNodeId timer = new("decision/timer");
        var returned = Node("return-0");
        var metadata = DecisionMetadata().WithEntry(choice);
        var generated = GeneratedDecisionProcess.Define(metadata);
        var syntaxNormalized = GeneratedDecisionProcessWithTrailingBranchReturn.Define(metadata);
        var lowLevel = ProcessAuthoring.Create<DecisionProcessInput, string>(
            metadata,
            process =>
            {
                var category = process.Input.Field(static input => input.Category);
                var state = process.Input.Field(static input => input.State);
                var dueAt = process.Input.Field(static input => input.DueAt);
                var fast = process.CanonicalValue<bool>(
                    Expr.Eq(category.Expression, Expr.Const("fast")),
                    new(new ScalarTypeRef(ScalarTypeKind.Bool)));
                var categoryCase = process.ChoiceCase(
                    fastCase,
                    fast,
                    process.Edge(fastCase, "next", match));
                var stateCase = process.MatchCase(
                    waitCase,
                    state,
                    "wait",
                    process.Edge(waitCase, "next", timer));

                process.Choice(
                    choice,
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [categoryCase],
                    process.Fallback(
                        choiceFallback,
                        process.Edge(choiceFallback, "next", returned)));
                process.Match(
                    match,
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    state,
                    [stateCase],
                    process.Fallback(
                        matchFallback,
                        process.Edge(matchFallback, "next", returned)));
                process.Timer(timer, dueAt, process.Edge(timer, "next", returned));
                process.Return(returned, state);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(syntaxNormalized.IsValid, Format(syntaxNormalized.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(lowLevel.Document.Metadata.Fingerprint, generated.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        Assert.Equal(generated.Definition, syntaxNormalized.Definition);
        Assert.Equal(generated.Document.Metadata.Fingerprint, syntaxNormalized.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(syntaxNormalized.Document));
        Assert.Equal(
            CaseSelection.OrderedFirstMatch,
            Assert.Single(generated.Definition.Nodes.OfType<ChoiceProcessNode>()).Selection);
        Assert.Equal(
            BranchCompleteness.Fallback,
            Assert.Single(generated.Definition.Nodes.OfType<MatchProcessNode>()).Completeness);
        var sourceMap = Assert.IsType<ExecutionSourceMap>(generated.Document.Metadata.SourceMap);
        Assert.Contains(
            sourceMap.Entries,
            static entry => entry.Description?.Contains("ProcessComputationAuthoringTests.cs", StringComparison.Ordinal) == true);

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);
        var json = Encoding.UTF8.GetString(canonical);
        var restoration = ProcessDefinitionDocuments.TryDeserialize(
            json,
            out var restoredDocument,
            out var restoredDefinition);
        Assert.True(restoration.IsValid, Format(restoration));
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        Assert.DoesNotContain("ProcessChoiceArm", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessMatchArm", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTask", json, StringComparison.Ordinal);
        Assert.DoesNotContain("delegate", json, StringComparison.OrdinalIgnoreCase);

        var generatedCompilation = generated.Compile(new ProcessDefinitionValidationContext());
        var lowLevelCompilation = lowLevel.Compile(new ProcessDefinitionValidationContext());
        Assert.True(generatedCompilation.IsSuccessful, Format(generatedCompilation.Validation));
        Assert.True(lowLevelCompilation.IsSuccessful, Format(lowLevelCompilation.Validation));
        var generatedPlan = Assert.IsType<CompiledProcessPlan>(generatedCompilation.Plan);
        var lowLevelPlan = Assert.IsType<CompiledProcessPlan>(lowLevelCompilation.Plan);
        Assert.Equal(lowLevelPlan.Definition, generatedPlan.Definition);
        Assert.Equivalent(lowLevelPlan.Options, generatedPlan.Options, strict: true);
        Assert.Equivalent(lowLevelPlan.EffectSummary, generatedPlan.EffectSummary, strict: true);

        var dueAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var input = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromObject(new DecisionProcessInput("fast", "wait", dueAtUtc)));
        var continuation = new ProcessContinuationIdentity(new("process-instance/decision"), new("attempt/1"));
        var generatedState = ProcessReferenceInterpreter.Create(generatedPlan, continuation, input);
        var lowLevelState = ProcessReferenceInterpreter.Create(lowLevelPlan, continuation, input);
        var started = Activation(
            generatedPlan,
            continuation,
            id: "activation/decision/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: dueAtUtc.AddMinutes(-1));
        var generatedCut = ProcessReferenceInterpreter.Activate(
            generatedPlan,
            generatedState,
            started,
            EchoRelationHost.Instance);
        var lowLevelCut = ProcessReferenceInterpreter.Activate(
            lowLevelPlan,
            lowLevelState,
            started,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.DurableCut, generatedCut.Disposition);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelCut.State, options),
            JsonSerializer.Serialize(generatedCut.State, options));

        var restoredState = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(
                JsonSerializer.Serialize(generatedCut.State, options),
                options));
        var due = Activation(
            generatedPlan,
            continuation,
            id: "activation/decision/due",
            cause: ProcessActivationCause.Timer,
            observedAtUtc: dueAtUtc);
        var generatedCompleted = ProcessReferenceInterpreter.Activate(
            generatedPlan,
            restoredState,
            due,
            EchoRelationHost.Instance);
        var lowLevelCompleted = ProcessReferenceInterpreter.Activate(
            lowLevelPlan,
            lowLevelCut.State,
            due,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, generatedCompleted.Disposition);
        Assert.Equal(
            lowLevelCompleted.Evidence.Trace.Select(static trace =>
                (trace.Sequence, trace.Kind, trace.Node, trace.BranchOrClause, trace.Detail)),
            generatedCompleted.Evidence.Trace.Select(static trace =>
                (trace.Sequence, trace.Kind, trace.Node, trace.BranchOrClause, trace.Detail)));
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelCompleted.State, options),
            JsonSerializer.Serialize(generatedCompleted.State, options));
        Assert.Equal(
            PortableValue.Concrete(generated.Definition.Result, ObservationValue.FromString("wait")),
            generatedCompleted.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void SignalTimerRace_IsByteEquivalentAndRecoversIdenticallyToLowLevelAuthoring()
    {
        var awaitMatch = Node("await-match-0");
        var signalClause = Owned(awaitMatch, "interaction-clause-Signalled");
        var timerClause = Owned(awaitMatch, "timer-clause-TimedOut");
        var signalReturned = ProcessAuthoringIdentities.NodeFor(new(
            ["body", "await-match-0", "interaction-clause-Signalled", "return-0"]));
        var timerReturned = ProcessAuthoringIdentities.NodeFor(new(
            ["body", "await-match-0", "timer-clause-TimedOut", "return-0"]));
        var metadata = SignalTimerMetadata().WithEntry(awaitMatch);
        var generated = GeneratedSignalTimerWaitProcess.Define(metadata);
        var lowLevel = ProcessAuthoring.Create<SignalTimerWaitInput, string>(
            metadata,
            process =>
            {
                var signalInput = process.Output<Signalled>(signalClause, "input");
                var dueAt = process.Input.Field(static input => input.DueAt);
                var result = process.Input.Field(static input => input.Value);
                var signal = process.AwaitInteractionClause(
                    signalClause,
                    GeneratedSignalTimerWaitProcess.Signal,
                    signalInput,
                    requestObligation: null,
                    guard: null,
                    priority: 10,
                    process.Continuation(process.Edge(signalClause, "next", signalReturned)));
                var timer = process.AwaitTimerClause(
                    timerClause,
                    dueAt,
                    priority: 0,
                    process.Continuation(process.Edge(timerClause, "next", timerReturned)));

                process.AwaitMatch(
                    awaitMatch,
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [signal, timer],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.DeadLetter,
                    TimeSpan.FromDays(7));
                process.Return(signalReturned, signalInput.Field(static input => input.Value));
                process.Return(timerReturned, result);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));

        var catalogValidation = InteractionContractCatalog.TryCreate(
            [GeneratedSignalTimerWaitProcess.SignalDocument],
            out var catalog);
        Assert.True(catalogValidation.IsValid, Format(catalogValidation));
        var context = new ProcessDefinitionValidationContext(
            interactionContracts: Assert.IsType<InteractionContractCatalog>(catalog));
        var generatedCompilation = generated.Compile(context);
        var lowLevelCompilation = lowLevel.Compile(context);
        Assert.True(generatedCompilation.IsSuccessful, Format(generatedCompilation.Validation));
        Assert.True(lowLevelCompilation.IsSuccessful, Format(lowLevelCompilation.Validation));
        AssertEquivalentSignalWaitRecovery(
            Assert.IsType<CompiledProcessPlan>(generatedCompilation.Plan),
            Assert.IsType<CompiledProcessPlan>(lowLevelCompilation.Plan));
    }

    [Fact]
    public void ChildProcessComputation_IsByteEquivalentToCanonicalBuilderAuthoring()
    {
        var generated = GeneratedChildInvocationProcess.Define(ChildInvocationMetadata());
        var invocation = Assert.Single(generated.Definition.Nodes.OfType<InvokeProcessProcessNode>());
        var returned = Assert.Single(generated.Definition.Nodes.OfType<ReturnProcessNode>());
        var lowLevel = ProcessAuthoring.Create<string, string>(
            ChildInvocationMetadata().WithEntry(invocation.Id),
            process =>
            {
                var outcomes = ImmutableArray.CreateBuilder<ProcessRequestOutcomeBranch>(invocation.Outcomes.Length);
                foreach (var outcome in invocation.Outcomes)
                {
                    var output = process.Output<string>(
                        outcome.Continuation.Output!.Binding,
                        outcome.Continuation.Output.Contract);
                    outcomes.Add(process.RequestOutcome(
                        outcome.Id,
                        outcome.Outcome,
                        process.Continuation(
                            process.Edge(outcome.Id, "next", returned.Id),
                            output)));
                }

                process.InvokeProcess(
                    invocation.Id,
                    GeneratedChildInvocationProcess.Child,
                    GeneratedChildInvocationProcess.Request,
                    GeneratedChildInvocationProcess.Mapping,
                    process.Input.Value,
                    ProcessChildPurpose.Work,
                    ProcessChildCancellationPolicy.Propagate,
                    outcomes.MoveToImmutable());
                process.Return(returned.Id, process.Input.Value);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        Assert.Equal(ProcessChildPurpose.Work, invocation.Purpose);
        Assert.Equal(ProcessChildCancellationPolicy.Propagate, invocation.Cancellation);
        Assert.Equal(
            new[]
            {
                GeneratedChildInvocationProcess.Mapping.Completed,
                GeneratedChildInvocationProcess.Mapping.Failed,
                GeneratedChildInvocationProcess.Mapping.Cancelled,
                GeneratedChildInvocationProcess.Mapping.Terminated
            }.ToImmutableHashSet(),
            invocation.Outcomes.Select(static outcome => outcome.Outcome).ToImmutableHashSet());
        Assert.All(invocation.Outcomes, static outcome => Assert.NotNull(outcome.Continuation.Output));

        var json = Encoding.UTF8.GetString(ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document));
        Assert.DoesNotContain("ProcessTask", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessProjection", json, StringComparison.Ordinal);
        Assert.DoesNotContain("delegate", json, StringComparison.OrdinalIgnoreCase);

        var context = ChildProcessContext(GeneratedChildInvocationProcess.Child);
        var generatedCompilation = generated.Compile(context);
        var lowLevelCompilation = lowLevel.Compile(context);
        Assert.True(generatedCompilation.IsSuccessful, Format(generatedCompilation.Validation));
        Assert.True(lowLevelCompilation.IsSuccessful, Format(lowLevelCompilation.Validation));
        AssertEquivalentChildRecovery(
            Assert.IsType<CompiledProcessPlan>(generatedCompilation.Plan),
            Assert.IsType<CompiledProcessPlan>(lowLevelCompilation.Plan));
    }

    [Fact]
    public void TypedChildProtocolInvocation_IsByteEquivalentToRawExactReferenceAuthoring()
    {
        var metadata = new ProcessAuthoringMetadata(
            new("process/tests/typed-child-parent"),
            new("1"),
            ProcessRecoveryPolicy.ContinueAttempt,
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-367/typed-child-parent"),
                DocumentOrigin.User));
        var typed = GeneratedTypedChildInvocationProcess.Define(metadata);
        var raw = GeneratedRawProtocolChildInvocationProcess.Define(metadata);

        Assert.True(typed.IsValid, Format(typed.Validation));
        Assert.True(raw.IsValid, Format(raw.Validation));
        Assert.Equal(raw.Definition, typed.Definition);
        Assert.Equal(raw.Document.Metadata.Fingerprint, typed.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(raw.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(typed.Document));

        var invocation = Assert.Single(typed.Definition.Nodes.OfType<InvokeProcessProcessNode>());
        Assert.Equal(GeneratedTypedChildInvocationProtocol.Protocol.Process.Reference, invocation.Process);
        Assert.Equal(GeneratedTypedChildInvocationProtocol.Protocol.Request, invocation.Contract);
        Assert.Equal(GeneratedTypedChildInvocationProtocol.Protocol.OutcomeMapping, invocation.OutcomeMapping);
    }

    [Fact]
    public void TypedRequestProtocolEffect_IsByteEquivalentAndExecutesIdenticallyToRawAuthoring()
    {
        var metadata = new ProcessAuthoringMetadata(
            new("process/tests/typed-request-effect"),
            new("1"),
            ProcessRecoveryPolicy.ContinueAttempt,
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-423/typed-request-effect"),
                DocumentOrigin.User));
        var typed = GeneratedTypedRequestEffectProcess.Define(metadata);
        var raw = GeneratedRawRequestEffectProcess.Define(metadata);

        Assert.True(typed.IsValid, Format(typed.Validation));
        Assert.True(raw.IsValid, Format(raw.Validation));
        Assert.Equal(raw.Definition, typed.Definition);
        Assert.Equal(raw.Document.Metadata.Fingerprint, typed.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(raw.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(typed.Document));

        var request = Assert.Single(typed.Definition.Nodes.OfType<RequestProcessNode>());
        Assert.Equal(GeneratedTypedRequestEffectProtocol.Protocol.Request, request.Contract);
        Assert.Equal(
            GeneratedTypedRequestEffectProtocol.Protocol.Cases.Select(static item => item.Id),
            request.Outcomes.Select(static item => item.Outcome));
        Assert.All(
            request.Outcomes,
            outcome => Assert.Equal(
                ProcessAuthoringIdentities.NodeForRequestOutcome(request.Id, outcome.Outcome),
                outcome.Id));

        var context = new ProcessDefinitionValidationContext(
            interactionContracts: GeneratedTypedRequestEffectProtocol.Protocol.Catalog);
        var typedCompilation = typed.Compile(context);
        var rawCompilation = raw.Compile(context);
        Assert.True(typedCompilation.IsSuccessful, Format(typedCompilation.Validation));
        Assert.True(rawCompilation.IsSuccessful, Format(rawCompilation.Validation));
        var typedPlan = Assert.IsType<CompiledProcessPlan>(typedCompilation.Plan);
        var rawPlan = Assert.IsType<CompiledProcessPlan>(rawCompilation.Plan);
        Assert.Equivalent(rawPlan.EffectSummary, typedPlan.EffectSummary, strict: true);

        var continuation = new ProcessContinuationIdentity(
            new("process-instance/typed-request-effect"),
            new("attempt/1"));
        var input = PortableValue.Concrete(
            typed.Definition.Input,
            ObservationValue.FromObject(new TrainingSubmission("dataset/42")));
        var activation = Activation(
            typedPlan,
            continuation,
            id: "activation/typed-request-effect/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var typedCut = ProcessReferenceInterpreter.Activate(
            typedPlan,
            ProcessReferenceInterpreter.Create(typedPlan, continuation, input),
            activation,
            EchoRelationHost.Instance);
        var rawCut = ProcessReferenceInterpreter.Activate(
            rawPlan,
            ProcessReferenceInterpreter.Create(rawPlan, continuation, input),
            activation,
            EchoRelationHost.Instance);
        Assert.Equal(rawCut.Disposition, typedCut.Disposition);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        Assert.Equal(
            JsonSerializer.Serialize(rawCut.State, options),
            JsonSerializer.Serialize(typedCut.State, options));
        Assert.Equal(
            rawCut.Evidence.Trace.Select(static trace =>
                (trace.Sequence, trace.Kind, trace.Node, trace.BranchOrClause, trace.Detail)),
            typedCut.Evidence.Trace.Select(static trace =>
                (trace.Sequence, trace.Kind, trace.Node, trace.BranchOrClause, trace.Detail)));
    }

    [Fact]
    public void CompensationComputation_PreservesTheCanonicalChildPurposeAndDurabilityProtocol()
    {
        var generated = GeneratedCompensationProcess.Define(CompensationMetadata());
        var invocation = Assert.Single(generated.Definition.Nodes.OfType<InvokeProcessProcessNode>());
        var returned = Assert.Single(generated.Definition.Nodes.OfType<ReturnProcessNode>());
        var lowLevel = ProcessAuthoring.Create<string, string>(
            CompensationMetadata().WithEntry(invocation.Id),
            process =>
            {
                var outcomes = ImmutableArray.CreateBuilder<ProcessRequestOutcomeBranch>(invocation.Outcomes.Length);
                foreach (var outcome in invocation.Outcomes)
                {
                    var output = process.Output<string>(
                        outcome.Continuation.Output!.Binding,
                        outcome.Continuation.Output.Contract);
                    outcomes.Add(process.RequestOutcome(
                        outcome.Id,
                        outcome.Outcome,
                        process.Continuation(
                            process.Edge(outcome.Id, "next", returned.Id),
                            output)));
                }

                process.InvokeProcess(
                    invocation.Id,
                    GeneratedCompensationProcess.Child,
                    GeneratedChildInvocationProcess.Request,
                    GeneratedChildInvocationProcess.Mapping,
                    process.Input.Value,
                    ProcessChildPurpose.Compensation,
                    ProcessChildCancellationPolicy.Propagate,
                    outcomes.MoveToImmutable());
                process.Return(returned.Id, process.Input.Value);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        Assert.Equal(ProcessChildPurpose.Compensation, invocation.Purpose);

        var compilation = generated.Compile(ChildProcessContext(GeneratedCompensationProcess.Child));
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        Assert.Contains(
            plan.EffectSummary.Effects,
            effect => effect.Node == invocation.Id && effect.Kind == ProcessEffectKind.Compensation);

        var continuation = new ProcessContinuationIdentity(
            new("process-instance/compensation"),
            new("attempt/1"));
        var input = PortableValue.Concrete(
            plan.Definition.Input,
            ObservationValue.FromString("reservation/42"));
        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(plan, continuation, input),
            Activation(
                plan,
                continuation,
                id: "activation/compensation/start",
                cause: ProcessActivationCause.Start,
                observedAtUtc: new(2026, 8, 4, 22, 5, 0, TimeSpan.Zero)),
            EchoRelationHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, decision.Disposition);
        Assert.Equal(ProcessChildPurpose.Compensation, Assert.Single(decision.State.Children).Purpose);
        Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));
    }

    [Fact]
    public void PartitionComputation_IsByteEquivalentAndRecoversWithExactBounds()
    {
        var generated = GeneratedPartitionProcess.Define(PartitionMetadata());
        var partition = Assert.Single(generated.Definition.Nodes.OfType<ForEachPartitionProcessNode>());
        var returned = Assert.Single(generated.Definition.Nodes.OfType<ReturnProcessNode>());
        var lowLevel = ProcessAuthoring.Create<PartitionProcessInput, string>(
            PartitionMetadata().WithEntry(partition.Id),
            process =>
            {
                var partitions = process.Input.Field(static input => input.Partitions);
                var item = process.Output<PartitionItem>(
                    partition.Partition.Binding,
                    partition.Partition.Contract);
                var progress = item.Field(static value => value.Id);
                var childInput = process.CanonicalValue<string>(
                    partition.ChildInput,
                    new(new ScalarTypeRef(ScalarTypeKind.String)));
                var capacity = item.Field(static value => value.Target);

                process.ForEachPartition(
                    partition.Id,
                    partitions,
                    item,
                    progress,
                    GeneratedPartitionProcess.Child,
                    GeneratedChildInvocationProcess.Request,
                    GeneratedChildInvocationProcess.Mapping,
                    childInput,
                    new ProcessWorkLimits(
                        maximumItems: 3,
                        maximumStartsPerActivation: 2,
                        maximumParallelism: 2),
                    ProcessPartitionFailurePolicy.FailFast,
                    capacity,
                    [
                        new("target/a", maximumParallelism: 1),
                        new("target/b", maximumParallelism: 1)
                    ],
                    ProcessChildCancellationPolicy.Propagate,
                    process.Edge(partition.Id, "completed", returned.Id),
                    process.Edge(partition.Id, "failed", returned.Id));
                process.Return(returned.Id, process.Input.Field(static input => input.Value));
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        Assert.Equal(new(3, 2, 2), partition.Limits);
        Assert.Equal(ProcessPartitionFailurePolicy.FailFast, partition.Failure);
        Assert.Equal(
            [("target/a", 1), ("target/b", 1)],
            partition.CapacityDomains.Select(static domain => (domain.Identity, domain.MaximumParallelism)));

        var context = ChildProcessContext(GeneratedPartitionProcess.Child);
        var generatedCompilation = generated.Compile(context);
        var lowLevelCompilation = lowLevel.Compile(context);
        Assert.True(generatedCompilation.IsSuccessful, Format(generatedCompilation.Validation));
        Assert.True(lowLevelCompilation.IsSuccessful, Format(lowLevelCompilation.Validation));
        AssertEquivalentPartitionRecovery(
            Assert.IsType<CompiledProcessPlan>(generatedCompilation.Plan),
            Assert.IsType<CompiledProcessPlan>(lowLevelCompilation.Plan));
    }

    [Fact]
    public void RecurrenceComputation_IsByteEquivalentAndRecoversAtTheDurableBoundary()
    {
        var generated = GeneratedRecurrenceProcess.Define(RecurrenceMetadata());
        var recurrence = Assert.Single(generated.Definition.Nodes.OfType<RepeatAcrossActivationProcessNode>());
        var query = Assert.Single(generated.Definition.Nodes.OfType<EvaluateRelationProcessNode>());
        var returned = Assert.Single(generated.Definition.Nodes.OfType<ReturnProcessNode>());
        var lowLevel = ProcessAuthoring.Create<string, string>(
            RecurrenceMetadata().WithEntry(query.Id),
            process =>
            {
                var observation = process.Output<string>(
                    query.Continuation.Output!.Binding,
                    query.Continuation.Output.Contract);
                var continueWhen = process.CanonicalValue<bool>(
                    recurrence.ContinueWhen,
                    new(new ScalarTypeRef(ScalarTypeKind.Bool)));

                process.EvaluateRelation(
                    query.Id,
                    GeneratedRecurrenceProcess.Poll,
                    process.Input.Value,
                    process.Continuation(
                        process.Edge(query.Id, "next", recurrence.Id),
                        observation));
                process.RepeatAcrossActivation<string>(
                    recurrence.Id,
                    continueWhen,
                    observation.Value,
                    new ProcessRecurrencePolicy(
                        maximumOccurrences: 3,
                        maximumUnchangedProgressOccurrences: 1),
                    process.Edge(recurrence.Id, "repeat", query.Id),
                    process.Edge(recurrence.Id, "completed", returned.Id),
                    process.Edge(recurrence.Id, "exhausted", returned.Id),
                    process.Edge(recurrence.Id, "stalled", returned.Id));
                process.Return(returned.Id, observation.Value);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        Assert.Equal(new(3, 1), recurrence.Policy);
        Assert.Equal(query.Id, recurrence.Repeat.Target);
        Assert.Equal(returned.Id, recurrence.Completed.Target);
        Assert.Equal(returned.Id, recurrence.Exhausted.Target);
        Assert.Equal(returned.Id, recurrence.Stalled.Target);

        var context = RelationContext(GeneratedRecurrenceProcess.Poll);
        var generatedCompilation = generated.Compile(context);
        var lowLevelCompilation = lowLevel.Compile(context);
        Assert.True(generatedCompilation.IsSuccessful, Format(generatedCompilation.Validation));
        Assert.True(lowLevelCompilation.IsSuccessful, Format(lowLevelCompilation.Validation));
        AssertEquivalentRecurrenceRecovery(
            Assert.IsType<CompiledProcessPlan>(generatedCompilation.Plan),
            Assert.IsType<CompiledProcessPlan>(lowLevelCompilation.Plan));

        var json = Encoding.UTF8.GetString(ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document));
        Assert.DoesNotContain("ProcessTask", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessProjection", json, StringComparison.Ordinal);
        Assert.DoesNotContain("delegate", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApproveCustomerProcess_CoversSequentialBranchingAndParallelAuthoringConstructs()
    {
        var generated = ApproveCustomerProcess.Define(ApproveCustomerMetadata());

        Assert.All(
            generated.Definition.Nodes.OfType<ReturnProcessNode>(),
            returned => Assert.Equal(
                generated.Definition.Result.Type,
                Assert.IsType<CallExpr>(returned.Result).ReturnType));

        Assert.True(
            generated.IsValid,
            Format(generated.Validation) + Environment.NewLine + string.Join(
                Environment.NewLine,
                generated.Definition.Nodes.Select(static (node, index) => $"{index}: {node.GetType().Name} {node.Id.Value}")));
        Assert.Equal(2, generated.Definition.Nodes.OfType<EvaluateRelationProcessNode>().Count());
        Assert.Single(generated.Definition.Nodes.OfType<InvokeTransitionProcessNode>());
        Assert.Equal(4, generated.Definition.Nodes.OfType<RequestProcessNode>().Count());
        Assert.Equal(2, generated.Definition.Nodes.OfType<ChoiceProcessNode>().Count());
        Assert.Single(generated.Definition.Nodes.OfType<MatchProcessNode>());
        Assert.Equal(6, generated.Definition.Nodes.OfType<ReturnProcessNode>().Count());
        var reviewWait = Assert.Single(generated.Definition.Nodes.OfType<AwaitMatchProcessNode>());
        Assert.Equal(2, reviewWait.Clauses.OfType<ProcessAwaitInteractionClause>().Count());
        Assert.Single(reviewWait.Clauses.OfType<ProcessAwaitTimerClause>());

        var fork = Assert.Single(generated.Definition.Nodes.OfType<ForkProcessNode>());
        var join = Assert.Single(generated.Definition.Nodes.OfType<JoinProcessNode>());
        Assert.Equal(join.Id, fork.Join);
        Assert.Equal(fork.Id, join.Fork);
        Assert.Equal(ProcessJoinMode.All, join.Policy.Mode);
        Assert.Equal(ProcessJoinFailurePolicy.FailFast, join.Policy.Failure);
        Assert.Equal(ProcessJoinCancellationPolicy.AwaitRemaining, join.Policy.Cancellation);
        Assert.Equal(ProcessJoinCompletionOrder.Unobservable, join.Policy.CompletionOrder);
        Assert.Equal(ProcessJoinTieBreak.BranchIdentity, join.Policy.TieBreak);
        Assert.Equal(2, fork.Branches.Length);
        Assert.Equal(ProcessWorkLimits.EagerFiniteSet(itemCount: 2), fork.Limits);
        Assert.Empty(fork.CapacityDomains);

        foreach (var branch in fork.Branches)
        {
            var request = Assert.IsType<RequestProcessNode>(
                generated.Definition.Nodes.Single(node => node.Id == branch.Start.Target));
            var outcome = Assert.Single(request.Outcomes);
            Assert.Equal(join.Id, outcome.Continuation.Edge.Target);
        }
    }

    static ProcessAuthoringMetadata Metadata() => new(
        new("process/generated-query"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-66/process-computation"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata ApproveCustomerMetadata() => new(
        new("process/approve-customer"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-66/approve-customer"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata TypedForkMetadata() => new(
        new("process/generated-typed-fork"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-227/typed-fork"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata AnyForkMetadata() => new(
        new("process/generated-any-fork"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-228/any-fork"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata RequiredForkMetadata() => new(
        new("process/generated-required-fork"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-228/required-fork"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata DurableWaitMetadata() => new(
        new("process/generated-durable-wait"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-228/durable-wait"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata OutboundInteractionMetadata() => new(
        new("process/generated-outbound-interactions"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-336/outbound-interactions"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata SignalTimerMetadata() => new(
        new("process/generated-signal-timer-wait"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-228/signal-timer-wait"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata ChildInvocationMetadata() => new(
        new("process/generated-child-invocation"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-229/child-invocation"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata CompensationMetadata() => new(
        new("process/generated-compensation"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-230/compensation"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata PartitionMetadata() => new(
        new("process/generated-partition-work"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-229/partition-work"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata RecurrenceMetadata() => new(
        new("process/generated-recurrence"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-230/recurrence"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata DecisionMetadata() => new(
        new("process/generated-explicit-decision"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-231/explicit-decision"),
            DocumentOrigin.User));

    static ProcessAuthoringMetadata IdentityMetadata() => new(
        new("process/generated-identity-stability"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-231/identity-stability"),
            DocumentOrigin.User));

    static ProcessDefinitionValidationContext RelationContext(ExecutionDefinitionReference relation)
    {
        var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        return new(
            definitions:
            [
                new(
                    relation,
                    ProcessDefinitionLinkKind.RelationQuery,
                    text,
                    text)
            ]);
    }

    static ProcessDefinitionValidationContext ChildProcessContext(ExecutionDefinitionReference child)
    {
        var catalogValidation = InteractionContractCatalog.TryCreate(
            [GeneratedChildInvocationProcess.RequestDocument],
            out var catalog);
        Assert.True(catalogValidation.IsValid, Format(catalogValidation));
        var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        return new(
            definitions:
            [
                new(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    text,
                    text,
                    processDependencies: [],
                    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt)
            ],
            interactionContracts: Assert.IsType<InteractionContractCatalog>(catalog));
    }

    static ExecutionNodeId Node(params string[] path) =>
        ProcessAuthoringIdentities.NodeFor(new(["body", .. path]));

    static ExecutionNodeId Owned(ExecutionNodeId owner, string role) =>
        ProcessAuthoringIdentities.NodeFor(owner, role);

    static void AssertEquivalentReferenceRecovery(
        CompiledProcessPlan generated,
        CompiledProcessPlan lowLevel)
    {
        var continuation = new ProcessContinuationIdentity(
            processInstanceId: new("process-instance/typed-fork"),
            processAttemptId: new("process-attempt/1"));
        var input = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromString("work"));
        var generatedState = ProcessReferenceInterpreter.Create(generated, continuation, input);
        var lowLevelState = ProcessReferenceInterpreter.Create(lowLevel, continuation, input);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        ProcessActivationDecision? generatedDecision = null;
        List<string> history = [];

        for (var activation = 0; activation < 4; activation++)
        {
            var context = new ProcessActivation(
                id: new($"activation/typed-fork/{activation}"),
                cause: activation == 0 ? ProcessActivationCause.Start : ProcessActivationCause.Continue,
                observedAtUtc: new DateTimeOffset(2026, 8, 4, 12, activation, 0, TimeSpan.Zero),
                context: new(
                    authorityScope: new("authority/tests", "tenant/cohesive"),
                    correlationId: new("correlation/typed-fork"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: generated.Document.Metadata.Provenance));
            generatedDecision = ProcessReferenceInterpreter.Activate(
                generated,
                generatedState,
                context,
                EchoRelationHost.Instance);
            var lowLevelDecision = ProcessReferenceInterpreter.Activate(
                lowLevel,
                lowLevelState,
                context,
                EchoRelationHost.Instance);

            Assert.Equal(lowLevelDecision.Disposition, generatedDecision.Disposition);
            Assert.Equal(lowLevelDecision.Emissions, generatedDecision.Emissions);
            Assert.Equal(
                lowLevelDecision.Diagnostics.Select(static item => (item.Code, item.Message)),
                generatedDecision.Diagnostics.Select(static item => (item.Code, item.Message)));
            history.Add(
                $"{activation}: {generatedDecision.Disposition}; "
                + string.Join(" | ", generatedDecision.Diagnostics.Select(static item => item.Message)));
            var generatedJson = JsonSerializer.Serialize(generatedDecision.State, options);
            var lowLevelJson = JsonSerializer.Serialize(lowLevelDecision.State, options);
            Assert.Equal(lowLevelJson, generatedJson);
            generatedState = Assert.IsType<ProcessContinuationState>(
                JsonSerializer.Deserialize<ProcessContinuationState>(generatedJson, options));
            lowLevelState = Assert.IsType<ProcessContinuationState>(
                JsonSerializer.Deserialize<ProcessContinuationState>(lowLevelJson, options));
            Assert.True(ProcessContinuationValidator.Validate(generated, generatedState).IsValid);
            Assert.True(ProcessContinuationValidator.Validate(lowLevel, lowLevelState).IsValid);
            if (generatedDecision.Disposition == ProcessActivationDisposition.Completed)
            {
                break;
            }
        }

        Assert.NotNull(generatedDecision);
        Assert.True(
            generatedDecision.Disposition == ProcessActivationDisposition.Completed,
            string.Join(Environment.NewLine, history));
        Assert.Equal(
            PortableValue.Concrete(
                generated.Definition.Result,
                ObservationValue.FromString("workwork")),
            generatedDecision.State.Terminal.Detail?.Value);
    }

    static void AssertEquivalentSignalWaitRecovery(
        CompiledProcessPlan generated,
        CompiledProcessPlan lowLevel)
    {
        var dueAt = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var continuation = new ProcessContinuationIdentity(new("process-instance/signal-wait"), new("attempt/1"));
        var input = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromObject(new SignalTimerWaitInput(dueAt, "accepted")));
        var generatedState = ProcessReferenceInterpreter.Create(generated, continuation, input);
        var lowLevelState = ProcessReferenceInterpreter.Create(lowLevel, continuation, input);
        var start = Activation(
            generated,
            continuation,
            id: "activation/signal-wait/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: dueAt.AddHours(-1));

        var generatedRegistered = ProcessReferenceInterpreter.Activate(
            generated,
            generatedState,
            start,
            EchoRelationHost.Instance);
        var lowLevelRegistered = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelState,
            start,
            EchoRelationHost.Instance);
        Assert.True(
            generatedRegistered.Disposition == ProcessActivationDisposition.DurableCut,
            $"{generatedRegistered.Disposition}{Environment.NewLine}"
            + string.Join(Environment.NewLine, generatedRegistered.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelRegistered.State, InteractionEnvelopeJsonSerializer.CreateOptions()),
            JsonSerializer.Serialize(generatedRegistered.State, InteractionEnvelopeJsonSerializer.CreateOptions()));

        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var restoredJson = JsonSerializer.Serialize(generatedRegistered.State, options);
        var restored = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(restoredJson, options));
        Assert.True(ProcessContinuationValidator.Validate(generated, restored).IsValid);
        var token = Assert.Single(restored.Tokens);
        var target = new ProcessTokenInteractionTarget(restored.Continuation, token.Id);
        var signalContract = Assert.Single(
                generated.Definition.Nodes
                    .OfType<AwaitMatchProcessNode>())
            .Clauses
            .OfType<ProcessAwaitInteractionClause>()
            .Single()
            .Input
            .Contract;
        var signal = new SignalEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/signal-wait"),
                new ProcessInteractionOrigin(
                    generated.DefinitionReference,
                    new("source/signal-wait"),
                    restored.Continuation,
                    new("activation/source"),
                    token.Id),
                new("correlation/signal-wait"),
                causationId: null,
                new("authority/tests", "tenant/cohesive"),
                new("idempotency/signal-wait"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                generated.Document.Metadata.Provenance),
            GeneratedSignalTimerWaitProcess.Signal,
            PortableValue.Concrete(
                signalContract,
                ObservationValue.FromObject(new Signalled("accepted"))),
            target);
        var inputActivation = new ProcessActivation(
            id: new("activation/signal-wait/input"),
            cause: ProcessActivationCause.Interaction,
            observedAtUtc: dueAt.AddMinutes(-30),
            context: start.Context,
            inputs: [new(target, signal)]);

        var generatedWinner = ProcessReferenceInterpreter.Activate(
            generated,
            restored,
            inputActivation,
            EchoRelationHost.Instance);
        var lowLevelWinner = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelRegistered.State,
            inputActivation,
            EchoRelationHost.Instance);
        Assert.Equal(lowLevelWinner.Disposition, generatedWinner.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelWinner.State, options),
            JsonSerializer.Serialize(generatedWinner.State, options));
        Assert.Equal(
            Owned(Node("await-match-0"), "interaction-clause-Signalled"),
            Assert.Single(generatedWinner.State.Waits).WinnerClause);

        var complete = Activation(
            generated,
            continuation,
            id: "activation/signal-wait/complete",
            cause: ProcessActivationCause.Continue,
            observedAtUtc: dueAt.AddMinutes(-29));
        var generatedCompleted = ProcessReferenceInterpreter.Activate(
            generated,
            generatedWinner.State,
            complete,
            EchoRelationHost.Instance);
        var lowLevelCompleted = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelWinner.State,
            complete,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, generatedCompleted.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelCompleted.State, options),
            JsonSerializer.Serialize(generatedCompleted.State, options));

        var lateSignal = new SignalEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/signal-wait/late"),
                new ProcessInteractionOrigin(
                    generated.DefinitionReference,
                    new("source/signal-wait"),
                    restored.Continuation,
                    new("activation/source"),
                    token.Id),
                new("correlation/signal-wait"),
                causationId: signal.Context.EmissionId,
                new("authority/tests", "tenant/cohesive"),
                new("idempotency/signal-wait/late"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                generated.Document.Metadata.Provenance),
            GeneratedSignalTimerWaitProcess.Signal,
            PortableValue.Concrete(
                signalContract,
                ObservationValue.FromObject(new Signalled("late"))),
            target);
        var lateActivation = new ProcessActivation(
            id: new("activation/signal-wait/late"),
            cause: ProcessActivationCause.Interaction,
            observedAtUtc: dueAt,
            context: complete.Context,
            inputs: [new(target, lateSignal)]);
        var generatedLate = ProcessReferenceInterpreter.Activate(
            generated,
            generatedCompleted.State,
            lateActivation,
            EchoRelationHost.Instance);
        var lowLevelLate = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelCompleted.State,
            lateActivation,
            EchoRelationHost.Instance);
        Assert.Equal(lowLevelLate.Disposition, generatedLate.Disposition);
        Assert.Equal(
            lowLevelLate.InputAdmissions.Select(static receipt =>
                (receipt.Emission, receipt.Disposition, receipt.Reason, receipt.WaitRegistrationId)),
            generatedLate.InputAdmissions.Select(static receipt =>
                (receipt.Emission, receipt.Disposition, receipt.Reason, receipt.WaitRegistrationId)));
        var lateReceipt = Assert.Single(generatedLate.InputAdmissions);
        Assert.Equal(ProcessInputAdmissionDisposition.Observed, lateReceipt.Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Late, lateReceipt.Reason);

        var timerContinuation = new ProcessContinuationIdentity(
            new("process-instance/timer-wait"),
            new("attempt/1"));
        var timerInput = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromObject(new SignalTimerWaitInput(dueAt, "fallback")));
        var generatedTimerState = ProcessReferenceInterpreter.Create(generated, timerContinuation, timerInput);
        var lowLevelTimerState = ProcessReferenceInterpreter.Create(lowLevel, timerContinuation, timerInput);
        var timerStart = Activation(
            generated,
            timerContinuation,
            id: "activation/timer-wait/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: dueAt.AddHours(-1));
        var generatedTimerRegistered = ProcessReferenceInterpreter.Activate(
            generated,
            generatedTimerState,
            timerStart,
            EchoRelationHost.Instance);
        var lowLevelTimerRegistered = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelTimerState,
            timerStart,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.DurableCut, generatedTimerRegistered.Disposition);

        var restoredTimer = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(
                JsonSerializer.Serialize(generatedTimerRegistered.State, options),
                options));
        var timerDue = Activation(
            generated,
            timerContinuation,
            id: "activation/timer-wait/due",
            cause: ProcessActivationCause.Timer,
            observedAtUtc: dueAt);
        var generatedTimerCompleted = ProcessReferenceInterpreter.Activate(
            generated,
            restoredTimer,
            timerDue,
            EchoRelationHost.Instance);
        var lowLevelTimerCompleted = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelTimerRegistered.State,
            timerDue,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, generatedTimerCompleted.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelTimerCompleted.State, options),
            JsonSerializer.Serialize(generatedTimerCompleted.State, options));
        Assert.Equal(
            Owned(Node("await-match-0"), "timer-clause-TimedOut"),
            Assert.Single(generatedTimerCompleted.State.Waits).WinnerClause);
        Assert.Equal(
            PortableValue.Concrete(generated.Definition.Result, ObservationValue.FromString("fallback")),
            generatedTimerCompleted.State.Terminal.Detail?.Value);
    }

    static void AssertEquivalentPartitionRecovery(
        CompiledProcessPlan generated,
        CompiledProcessPlan lowLevel)
    {
        var continuation = new ProcessContinuationIdentity(new("process-instance/partition"), new("attempt/1"));
        var authored = new PartitionProcessInput(
            [
                new("partition/a", "target/a"),
                new("partition/b", "target/a"),
                new("partition/c", "target/b")
            ],
            "generation/1");
        var input = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromObject(authored));
        var activation = Activation(
            generated,
            continuation,
            id: "activation/partition/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: new(2026, 8, 4, 21, 0, 0, TimeSpan.Zero));
        var generatedDecision = ProcessReferenceInterpreter.Activate(
            generated,
            ProcessReferenceInterpreter.Create(generated, continuation, input),
            activation,
            EchoRelationHost.Instance);
        var lowLevelDecision = ProcessReferenceInterpreter.Activate(
            lowLevel,
            ProcessReferenceInterpreter.Create(lowLevel, continuation, input),
            activation,
            EchoRelationHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, generatedDecision.Disposition);
        Assert.Equal(2, generatedDecision.Emissions.Length);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelDecision.State, InteractionEnvelopeJsonSerializer.CreateOptions()),
            JsonSerializer.Serialize(generatedDecision.State, InteractionEnvelopeJsonSerializer.CreateOptions()));
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelDecision.Emissions, InteractionEnvelopeJsonSerializer.CreateOptions()),
            JsonSerializer.Serialize(generatedDecision.Emissions, InteractionEnvelopeJsonSerializer.CreateOptions()));
        var partition = Assert.Single(generatedDecision.State.Partitions);
        Assert.Equal(
            ["partition/a", "partition/b", "partition/c"],
            partition.Work.Select(static work => work.ProgressIdentity));
        Assert.Equal(
            ["partition/a", "partition/c"],
            generatedDecision.State.Children
                .Where(static child => child.Disposition == ProcessChildDisposition.Active)
                .Select(static child => child.ProgressIdentity));
        Assert.Single(
            generatedDecision.State.Children,
            static child => child.Disposition == ProcessChildDisposition.Pending
                            && child.ProgressIdentity == "partition/b");

        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(generatedDecision.State, options);
        var restored = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(json, options));
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.True(ProcessContinuationValidator.Validate(generated, restored).IsValid);

        var emptyContinuation = new ProcessContinuationIdentity(
            new("process-instance/partition-empty"),
            new("attempt/1"));
        var emptyInput = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromObject(new PartitionProcessInput([], "empty")));
        var emptyActivation = Activation(
            generated,
            emptyContinuation,
            id: "activation/partition/empty",
            cause: ProcessActivationCause.Start,
            observedAtUtc: new(2026, 8, 4, 21, 1, 0, TimeSpan.Zero));
        var generatedEmpty = ProcessReferenceInterpreter.Activate(
            generated,
            ProcessReferenceInterpreter.Create(generated, emptyContinuation, emptyInput),
            emptyActivation,
            EchoRelationHost.Instance);
        var lowLevelEmpty = ProcessReferenceInterpreter.Activate(
            lowLevel,
            ProcessReferenceInterpreter.Create(lowLevel, emptyContinuation, emptyInput),
            emptyActivation,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.DurableCut, generatedEmpty.Disposition);
        Assert.Empty(generatedEmpty.Emissions);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelEmpty.State, options),
            JsonSerializer.Serialize(generatedEmpty.State, options));
        var continueActivation = Activation(
            generated,
            emptyContinuation,
            id: "activation/partition/empty-continue",
            cause: ProcessActivationCause.Continue,
            observedAtUtc: new(2026, 8, 4, 21, 2, 0, TimeSpan.Zero));
        var generatedEmptyCompleted = ProcessReferenceInterpreter.Activate(
            generated,
            generatedEmpty.State,
            continueActivation,
            EchoRelationHost.Instance);
        var lowLevelEmptyCompleted = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelEmpty.State,
            continueActivation,
            EchoRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, generatedEmptyCompleted.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelEmptyCompleted.State, options),
            JsonSerializer.Serialize(generatedEmptyCompleted.State, options));
    }

    static void AssertEquivalentChildRecovery(
        CompiledProcessPlan generated,
        CompiledProcessPlan lowLevel)
    {
        var continuation = new ProcessContinuationIdentity(new("process-instance/child"), new("attempt/1"));
        var input = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromString("child-input"));
        var activation = Activation(
            generated,
            continuation,
            id: "activation/child/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: new(2026, 8, 4, 20, 0, 0, TimeSpan.Zero));
        var generatedDecision = ProcessReferenceInterpreter.Activate(
            generated,
            ProcessReferenceInterpreter.Create(generated, continuation, input),
            activation,
            EchoRelationHost.Instance);
        var lowLevelDecision = ProcessReferenceInterpreter.Activate(
            lowLevel,
            ProcessReferenceInterpreter.Create(lowLevel, continuation, input),
            activation,
            EchoRelationHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, generatedDecision.Disposition);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(generatedDecision.Emissions));
        var child = Assert.Single(generatedDecision.State.Children);
        Assert.Equal(GeneratedChildInvocationProcess.Child, child.Process);
        Assert.Equal(GeneratedChildInvocationProcess.Mapping, request.ChildTarget?.OutcomeMapping);
        Assert.Equal(child.Continuation, request.ChildTarget?.Continuation);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelDecision.State, options),
            JsonSerializer.Serialize(generatedDecision.State, options));
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelDecision.Emissions, options),
            JsonSerializer.Serialize(generatedDecision.Emissions, options));

        var json = JsonSerializer.Serialize(generatedDecision.State, options);
        var restored = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(json, options));
        Assert.True(ProcessContinuationValidator.Validate(generated, restored).IsValid);
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
    }

    static void AssertEquivalentRecurrenceRecovery(
        CompiledProcessPlan generated,
        CompiledProcessPlan lowLevel)
    {
        var continuation = new ProcessContinuationIdentity(
            new("process-instance/recurrence"),
            new("attempt/1"));
        var input = PortableValue.Concrete(
            generated.Definition.Input,
            ObservationValue.FromString("poll/42"));
        var generatedState = ProcessReferenceInterpreter.Create(generated, continuation, input);
        var lowLevelState = ProcessReferenceInterpreter.Create(lowLevel, continuation, input);
        var generatedHost = new SequenceRelationHost("pending/1", "approved");
        var lowLevelHost = new SequenceRelationHost("pending/1", "approved");
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var firstActivation = Activation(
            generated,
            continuation,
            id: "activation/recurrence/1",
            cause: ProcessActivationCause.Start,
            observedAtUtc: new(2026, 8, 4, 22, 0, 0, TimeSpan.Zero));
        var generatedFirst = ProcessReferenceInterpreter.Activate(
            generated,
            generatedState,
            firstActivation,
            generatedHost);
        var lowLevelFirst = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelState,
            firstActivation,
            lowLevelHost);

        Assert.Equal(ProcessActivationDisposition.DurableCut, generatedFirst.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelFirst.State, options),
            JsonSerializer.Serialize(generatedFirst.State, options));
        var firstRecurrence = Assert.Single(generatedFirst.State.Recurrences);
        Assert.True(firstRecurrence.Active);
        Assert.Equal(1, firstRecurrence.RepeatCount);
        Assert.Equal(
            ObservationValue.FromString("pending/1"),
            firstRecurrence.LastProgress?.Value);

        var generatedJson = JsonSerializer.Serialize(generatedFirst.State, options);
        var lowLevelJson = JsonSerializer.Serialize(lowLevelFirst.State, options);
        generatedState = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(generatedJson, options));
        lowLevelState = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(lowLevelJson, options));
        Assert.True(ProcessContinuationValidator.Validate(generated, generatedState).IsValid);
        Assert.True(ProcessContinuationValidator.Validate(lowLevel, lowLevelState).IsValid);

        var secondActivation = Activation(
            generated,
            continuation,
            id: "activation/recurrence/2",
            cause: ProcessActivationCause.Continue,
            observedAtUtc: new(2026, 8, 4, 22, 1, 0, TimeSpan.Zero));
        var generatedCompleted = ProcessReferenceInterpreter.Activate(
            generated,
            generatedState,
            secondActivation,
            generatedHost);
        var lowLevelCompleted = ProcessReferenceInterpreter.Activate(
            lowLevel,
            lowLevelState,
            secondActivation,
            lowLevelHost);

        Assert.Equal(ProcessActivationDisposition.Completed, generatedCompleted.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(lowLevelCompleted.State, options),
            JsonSerializer.Serialize(generatedCompleted.State, options));
        Assert.Equal(
            PortableValue.Concrete(
                generated.Definition.Result,
                ObservationValue.FromString("approved")),
            generatedCompleted.State.Terminal.Detail?.Value);
        Assert.False(Assert.Single(generatedCompleted.State.Recurrences).Active);
        Assert.Equal(2, generatedHost.Evaluations.Count);
        Assert.Equal(2, lowLevelHost.Evaluations.Count);
    }

    static ProcessActivation Activation(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        string id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        EmissionId? causationId = null) => new(
        id: new(id),
        cause: cause,
        observedAtUtc: observedAtUtc,
        context: new(
            authorityScope: new("authority/tests", "tenant/cohesive"),
            correlationId: new("correlation/signal-wait"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: plan.Document.Metadata.Provenance,
                    causationId: causationId));

    sealed class EchoRelationHost : IProcessReferenceHost
    {
        public static EchoRelationHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(evaluation.Input);

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class SequenceRelationHost(params string[] observations) : IProcessReferenceHost
    {
        int next;

        internal List<ProcessRelationEvaluation> Evaluations { get; } = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            Evaluations.Add(evaluation);
            var index = Math.Min(next++, observations.Length - 1);
            return ProcessOperationResult.Completed(
                PortableValue.Concrete(
                    evaluation.Input.Contract,
                    ObservationValue.FromString(observations[index])));
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class ResolvingSignalHost : IProcessReferenceHost
    {
        internal List<ProcessSignalTargetResolution> Resolutions { get; } = [];

        internal ProcessTokenInteractionTarget? Target { get; private set; }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution)
        {
            Resolutions.Add(resolution);
            Target = new(resolution.Continuation, resolution.Token);
            return ProcessSignalTargetResult.Resolved(Target);
        }
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location ?? diagnostic.SchemaLocation}: {diagnostic.Message}"));
}

/// <summary>Representative generated Process used by canonical-equivalence tests.</summary>
// <docs:sequential-process>
[GenerateProcessDefinition(nameof(Run))]
public static partial class CustomerQueryProcess
{
    /// <summary>Exact Relation reference used by the generated Process.</summary>
    public static ExecutionDefinitionReference Relation { get; } = new(
        new("relation/customer-query"),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('1', 64)));

    static async ProcessTask<string> Run(
        ProcessContext process,
        string input)
    {
        var queryInput = input;
        var row = await process.Query<string>(Relation, queryInput);
        return row;
    }
}
// </docs:sequential-process>

/// <summary>Semantically identical generated Process containing a non-effectful local.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class CustomerQueryProcessWithPureLocal
{
    static async ProcessTask<string> Run(
        ProcessContext process,
        string input)
    {
        var ignored = input + string.Empty;
        var queryInput = input;
        var row = await process.Query<string>(CustomerQueryProcess.Relation, queryInput);
        return row;
    }
}

/// <summary>Representative generated Process with an authored lifecycle cancellation finalizer.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedCancellableProcess
{
    static readonly Process<
        ProcessCancellationFinalizationInput<string>,
        ProcessCancellationAcknowledgement> Finalizer =
        ProcessAuthoring.Create<
            ProcessCancellationFinalizationInput<string>,
            ProcessCancellationAcknowledgement>(
            new(
                new("process/tests/generated-cancellation-finalizer"),
                new("revision/1"),
                new("return"),
                ProcessRecoveryPolicy.ContinueAttempt,
                new(
                    new("tests.process-computation", "1"),
                    new("tests/ari-469/cancellation-finalizer"),
                    DocumentOrigin.Generated)),
            ProcessCancellationFinalizationContracts.Input(
                new(new ScalarTypeRef(ScalarTypeKind.String))),
            ProcessCancellationFinalizationContracts.Acknowledgement,
            process => process.Return(
                new("return"),
                process.CanonicalValue<ProcessCancellationAcknowledgement>(
                    Expr.Const(ObservationValue.FromObject(
                        new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                        {
                            ["attemptId"] = ObservationValue.FromString("process-attempt/fixture")
                        })),
                    ProcessCancellationFinalizationContracts.Acknowledgement)));

    /// <summary>Exact cancellation-finalizer child invocation protocol.</summary>
    public static ProcessInvocationProtocol<
        ProcessCancellationFinalizationInput<string>,
        ProcessCancellationAcknowledgement> Cancellation { get; } =
        Finalizer.InvocationProtocol(
            new("request/tests/generated-cancellation-finalizer"),
            new("revision/1"),
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(30)),
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-469/cancellation-finalizer-invocation"),
                DocumentOrigin.Generated));

    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        process.OnCancellation(Cancellation, id: new("training/cancel/finalize"));
        return input;
    }
}

/// <summary>Representative generated Process that emits one event and sends one addressed Signal.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedOutboundInteractionProcess
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    /// <summary>Canonical domain-event document used by the generated Process.</summary>
    public static ExecutionDefinitionDocument EventDocument { get; } = InteractionContractDocuments.Create(
        new("event/generated-outbound"),
        new("1"),
        new DomainEventContractDefinition(new(StringContract, new("event/generated-outbound/payload/v1"))),
        Provenance("event"));

    /// <summary>Canonical Signal document used by the generated Process.</summary>
    public static ExecutionDefinitionDocument SignalDocument { get; } = InteractionContractDocuments.Create(
        new("signal/generated-outbound"),
        new("1"),
        new SignalContractDefinition(new(StringContract, new("signal/generated-outbound/payload/v1"))),
        Provenance("signal"));

    /// <summary>Exact typed domain-event reference.</summary>
    public static DomainEventContractReference Event { get; } = new(Reference(EventDocument));

    /// <summary>Exact typed Signal reference.</summary>
    public static SignalContractReference Signal { get; } = new(Reference(SignalDocument));

    static async ProcessTask<string> Run(ProcessContext process, OutboundInteractionInput input)
    {
        await process.EmitEvent(
            Event,
            input.Value,
            id: new("interaction/event"),
            nextRole: "published");
        await process.SendSignal(
            Signal,
            input.Target,
            input.Value,
            id: new("interaction/signal"),
            nextRole: "sent");
        return input.Value;
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance(string role) => new(
        new("tests.process-computation", "1"),
        new($"tests/ari-336/outbound-interactions/{role}"),
        DocumentOrigin.Generated);
}

/// <summary>Portable input to the representative outbound-interaction Process.</summary>
/// <param name="Target">Semantic Signal target resolved by the execution host.</param>
/// <param name="Value">Typed event and Signal payload.</param>
public sealed record OutboundInteractionInput(string Target, string Value);

/// <summary>Representative generated typed Fork with bounded canonical admission.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedTypedForkProcess
{
    /// <summary>Exact audit Relation used by the first branch.</summary>
    public static ExecutionDefinitionReference RecordAudit { get; } = Definition("relation/record-audit", '7');

    /// <summary>Exact notification Relation used by the second branch.</summary>
    public static ExecutionDefinitionReference NotifyOwner { get; } = Definition("relation/notify-owner", '8');

    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        async ProcessTask<string> Audit()
        {
            var value = await process.Query<string>(RecordAudit, input);
            return value;
        }

        async ProcessTask<string> Notify()
        {
            var value = await process.Query<string>(NotifyOwner, input);
            return value;
        }

        var receipts = await process.ForkJoin(
            process.Branch(Notify(), capacityDomain: "external-services"),
            process.Branch(Audit(), capacityDomain: "external-services"),
            admission: ProcessAdmission.Bounded(
                maximumParallelism: 1,
                maximumStartsPerActivation: 1,
                capacityDomains: [ProcessCapacity.Domain("external-services", maximumParallelism: 1)]));
        return receipts.Item1 + receipts.Item2;
    }

    static ExecutionDefinitionReference Definition(string id, char fingerprint) => new(
        new(id),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprint, 64)));
}

/// <summary>Representative generated partial Fork with one typed selected winner.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedAnyForkProcess
{
    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        async ProcessTask<string> Audit()
        {
            var value = await process.Query<string>(GeneratedTypedForkProcess.RecordAudit, input);
            return "audit:" + value;
        }

        async ProcessTask<string> Notify()
        {
            var value = await process.Query<string>(GeneratedTypedForkProcess.NotifyOwner, input);
            return "notify:" + value;
        }

        var winner = await process.ForkAny(
            branches: [Audit(), Notify()],
            policy: ProcessJoin.Any(ProcessJoinCancellationPolicy.CancelRemaining));
        return winner.Branch + ":" + winner.Result;
    }
}

/// <summary>Representative generated RequiredCount Fork selection.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRequiredForkProcess
{
    static async ProcessTask<long> Run(ProcessContext process, string input)
    {
        async ProcessTask<string> Alpha()
        {
            var value = await process.Query<string>(GeneratedTypedForkProcess.RecordAudit, input);
            return "alpha:" + value;
        }

        async ProcessTask<string> Beta()
        {
            var value = await process.Query<string>(GeneratedTypedForkProcess.NotifyOwner, input);
            return "beta:" + value;
        }

        async ProcessTask<string> Gamma()
        {
            var value = await process.Query<string>(CustomerQueryProcess.Relation, input);
            return "gamma:" + value;
        }

        var winners = await process.ForkRequired(
            branches: [Alpha(), Beta(), Gamma()],
            policy: ProcessJoin.Required(
                requiredCount: 2,
                cancellation: ProcessJoinCancellationPolicy.ContinueRemaining));
        return winners.Length;
    }
}

/// <summary>Representative generated durable Timer, multi-outcome Request, and AwaitMatch.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedDurableWaitProcess
{
    static readonly ExecutionDefinitionReference Observe = Definition("relation/observe", '1');
    static readonly RequestContractReference Outbound = new(Definition("request/outbound", '2'));
    static readonly RequestTerminalOutcomeId Completed = new("completed");
    static readonly RequestTerminalOutcomeId Failed = new("failed");
    static readonly DomainEventContractReference Reviewed = new(Definition("event/reviewed", '3'));
    static readonly SignalContractReference Cancelled = new(Definition("signal/cancelled", '4'));
    static readonly RequestContractReference Review = new(Definition("request/review", '5'));
    static readonly ReplyContractReference ReviewReply = new(Definition("reply/review", '6'));

    static async ProcessTask<string> Run(ProcessContext process, DurableWaitInput input)
    {
        await process.Timer(input.FirstDueAt);

        async ProcessTask OnCompleted(string outcome)
        {
            var observed = await process.Query<string>(Observe, outcome);
        }

        async ProcessTask OnFailed(ProcessFailure outcome)
        {
            var observed = await process.Query<string>(Observe, outcome.Message);
        }

        await process.Effect(
            contract: Outbound,
            input: input.Value,
            outcomes:
            [
                process.Outcome<string>(Completed, OnCompleted),
                process.Outcome<ProcessFailure>(Failed, OnFailed)
            ]);

        async ProcessTask OnReviewed(string value)
        {
            var observed = await process.Query<string>(Observe, value);
        }

        async ProcessTask OnCancelled(string value)
        {
            var observed = await process.Query<string>(Observe, value);
        }

        async ProcessTask OnReview(
            string value,
            Cohesive.Processes.Authoring.ProcessRequestObligation request)
        {
            await process.Reply(ReviewReply, request, value);
        }

        async ProcessTask OnDeadline()
        {
            var observed = await process.Query<string>(Observe, input.Value);
        }

        await process.AwaitMatch(
            clauses:
            [
                process.Event<string>(Reviewed, OnReviewed, priority: 20, when: value => value == input.Value),
                process.Signal<string>(Cancelled, OnCancelled, priority: 10),
                process.Request<string>(Review, OnReview, priority: 30),
                process.Deadline(input.SecondDueAt, OnDeadline, priority: 0)
            ],
            arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            lateInput: ProcessAwaitInputDisposition.Observe,
            staleInput: ProcessAwaitInputDisposition.Reject,
            duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
            missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
            retentionHorizon: TimeSpan.FromDays(7));
        return input.Value;
    }

    static ExecutionDefinitionReference Definition(string id, char fingerprint) => new(
        new(id),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprint, 64)));
}

/// <summary>Input to the representative durable-wait computation.</summary>
/// <param name="FirstDueAt">Absolute due instant of the sequential Timer.</param>
/// <param name="SecondDueAt">Absolute due instant competing in AwaitMatch.</param>
/// <param name="Value">Portable payload and final result.</param>
public sealed record DurableWaitInput(DateTimeOffset FirstDueAt, DateTimeOffset SecondDueAt, string Value);

/// <summary>Representative typed failure outcome.</summary>
/// <param name="Message">Portable failure message.</param>
public sealed record ProcessFailure(string Message);

/// <summary>Minimal generated Signal/timer race used by differential recovery tests.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedSignalTimerWaitProcess
{
    /// <summary>Canonical Signal document supplying exact contract evidence.</summary>
    public static ExecutionDefinitionDocument SignalDocument { get; } =
        InteractionContractDocuments.Create(
            new("signal/generated-wait"),
            new("1"),
            new SignalContractDefinition(
                new(
                    new(new DefaultClrTypeRefMapper().Map(typeof(Signalled), null)),
                    new("signal/generated-wait/payload/v1"))),
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-228/signal-timer-wait-contract"),
                DocumentOrigin.Generated));

    /// <summary>Exact Signal reference admitted by the generated AwaitMatch.</summary>
    public static SignalContractReference Signal { get; } = new(
        new(
            SignalDocument.Metadata.DefinitionId,
            SignalDocument.Metadata.RevisionId,
            SignalDocument.Metadata.Fingerprint));

    static async ProcessTask<string> Run(ProcessContext process, SignalTimerWaitInput input)
    {
        var outcome = await process.AwaitMatch<SignalTimerWaitOutcome>(
            clauses:
            [
                process.Signal<Signalled>(Signal, priority: 10),
                process.Deadline<TimedOut>(input.DueAt)
            ],
            arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            lateInput: ProcessAwaitInputDisposition.Observe,
            staleInput: ProcessAwaitInputDisposition.Reject,
            duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
            missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
            retentionHorizon: TimeSpan.FromDays(7));
        switch (outcome)
        {
            case Signalled signalled:
                return signalled.Value;
            case TimedOut _:
                return input.Value;
        }
        return process.Unreachable<string>();
    }
}

/// <summary>Closed source-only result family selected by the generated Signal/timer AwaitMatch.</summary>
public abstract record SignalTimerWaitOutcome;

/// <summary>Admitted typed Signal payload and source-only AwaitMatch result case.</summary>
/// <param name="Value">Value admitted by the exact Signal contract.</param>
public sealed record Signalled(string Value) : SignalTimerWaitOutcome;

/// <summary>Source-only result case selected when the canonical timer clause wins.</summary>
public sealed record TimedOut : SignalTimerWaitOutcome;

/// <summary>Input to the minimal generated Signal/timer race.</summary>
/// <param name="DueAt">Absolute timeout instant.</param>
/// <param name="Value">Final Process result.</param>
public sealed record SignalTimerWaitInput(DateTimeOffset DueAt, string Value);

/// <summary>Representative generated direct child Process invocation.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedChildInvocationProcess
{
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));

    /// <summary>Exact child Process definition used by the representative invocation.</summary>
    public static ExecutionDefinitionReference Child { get; } = Definition("process/child-work", 'a');

    /// <summary>Total child-terminal mapping shared by direct and partitioned invocation.</summary>
    public static ProcessChildOutcomeMapping Mapping { get; } = new(
        new("completed"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    /// <summary>Canonical Request contract used to durably start and join the child.</summary>
    public static ExecutionDefinitionDocument RequestDocument { get; } =
        InteractionContractDocuments.Create(
            new("interaction/request/child-work"),
            new("1"),
            new RequestContractDefinition(
                new(Text, new("child-work/input/v1")),
                new(
                    [
                        new RequestResultDefinition(
                            Mapping.Completed,
                            new(Text, new("child-work/completed/v1"))),
                        new RequestFailureDefinition(
                            Mapping.Failed,
                            new(Text, new("child-work/failed/v1"))),
                        new RequestFailureDefinition(
                            Mapping.Cancelled,
                            new(Text, new("child-work/cancelled/v1"))),
                        new RequestFailureDefinition(
                            Mapping.Terminated,
                            new(Text, new("child-work/terminated/v1")))
                    ],
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Observe,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.ReconcileBeforeRetry,
                    RequestResolutionSemantics.Reconcile,
                    RequestResolutionSemantics.Reconcile,
                    TimeSpan.FromDays(30))),
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-229/child-request"),
                DocumentOrigin.Generated));

    /// <summary>Exact typed reference to <see cref="RequestDocument"/>.</summary>
    public static RequestContractReference Request { get; } = new(
        new(
            RequestDocument.Metadata.DefinitionId,
            RequestDocument.Metadata.RevisionId,
            RequestDocument.Metadata.Fingerprint));

    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        async ProcessTask Completed(string result) { }
        async ProcessTask Failed(string failure) { }
        async ProcessTask Cancelled(string cancellation) { }
        async ProcessTask Terminated(string termination) { }

        await process.InvokeProcess(
            process: Child,
            contract: Request,
            outcomeMapping: Mapping,
            input: input,
            purpose: ProcessChildPurpose.Work,
            cancellation: ProcessChildCancellationPolicy.Propagate,
            outcomes:
            [
                process.Outcome<string>(Mapping.Completed, Completed),
                process.Outcome<string>(Mapping.Failed, Failed),
                process.Outcome<string>(Mapping.Cancelled, Cancelled),
                process.Outcome<string>(Mapping.Terminated, Terminated)
            ]);
        return input;
    }

    static ExecutionDefinitionReference Definition(string id, char fingerprint) => new(
        new(id),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprint, 64)));
}

/// <summary>Canonical child and derived typed invocation protocol used by typed/raw differential authoring tests.</summary>
public static class GeneratedTypedChildInvocationProtocol
{
    /// <summary>Exact canonical child Process.</summary>
    public static Process<string, string> Child { get; } = ProcessAuthoring.Create<string, string>(
        new(
            new("process/tests/typed-invoked-child"),
            new("1"),
            new("return"),
            ProcessRecoveryPolicy.ContinueAttempt,
            Provenance("child")),
        process => process.Return(new("return"), process.Input.Value));

    /// <summary>Typed exact Request/Reply protocol derived from <see cref="Child"/>.</summary>
    public static ProcessInvocationProtocol<string, string> Protocol { get; } = Child.InvocationProtocol(
        new("request/tests/typed-invoked-child"),
        new("1"),
        ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(30)),
        Provenance("protocol"));

    static ExecutionProvenance Provenance(string role) => new(
        new("tests.process-computation", "1"),
        new($"tests/ari-367/typed-invocation/{role}"),
        DocumentOrigin.Generated);
}

/// <summary>Representative typed child invocation with total semantic handlers.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedTypedChildInvocationProcess
{
    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        await process.InvokeProcess(
            protocol: GeneratedTypedChildInvocationProtocol.Protocol,
            input: input,
            purpose: ProcessChildPurpose.Work,
            cancellation: ProcessChildCancellationPolicy.Propagate,
            completed: Completed,
            failed: Failed,
            cancelled: Cancelled,
            terminated: Terminated,
            id: new("invoke-child"));
        return input;

        async ProcessTask Completed(string result) { }
        async ProcessTask Failed(ProcessChildFailure failure) { }
        async ProcessTask Cancelled() { }
        async ProcessTask Terminated() { }
    }
}

/// <summary>Raw exact-reference equivalent of <see cref="GeneratedTypedChildInvocationProcess"/>.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRawProtocolChildInvocationProcess
{
    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        await process.InvokeProcess(
            process: GeneratedTypedChildInvocationProtocol.Protocol.Process.Reference,
            contract: GeneratedTypedChildInvocationProtocol.Protocol.Request,
            outcomeMapping: GeneratedTypedChildInvocationProtocol.Protocol.OutcomeMapping,
            input: input,
            purpose: ProcessChildPurpose.Work,
            cancellation: ProcessChildCancellationPolicy.Propagate,
            outcomes:
            [
                process.Outcome<string>(
                    GeneratedTypedChildInvocationProtocol.Protocol.OutcomeMapping.Completed,
                    Completed),
                process.Outcome<ProcessChildFailure>(
                    GeneratedTypedChildInvocationProtocol.Protocol.OutcomeMapping.Failed,
                    Failed),
                process.Outcome(
                    GeneratedTypedChildInvocationProtocol.Protocol.OutcomeMapping.Cancelled,
                    Cancelled),
                process.Outcome(
                    GeneratedTypedChildInvocationProtocol.Protocol.OutcomeMapping.Terminated,
                    Terminated)
            ],
            id: new("invoke-child"));
        return input;

        async ProcessTask Completed(string result) { }
        async ProcessTask Failed(ProcessChildFailure failure) { }
        async ProcessTask Cancelled() { }
        async ProcessTask Terminated() { }
    }
}

/// <summary>Canonical Request protocol with a source-only closed outcome family for typed Effect tests.</summary>
public static class GeneratedTypedRequestEffectProtocol
{
    /// <summary>Typed canonical protocol consumed by generated typed and raw Process authoring.</summary>
    public static RequestProtocol<TrainingSubmission, TrainingSubmissionOutcome, TrainingSubmissionCases> Protocol { get; } =
        InteractionContractAuthoring.CreateRequestProtocol<
            TrainingSubmission,
            TrainingSubmissionOutcome,
            TrainingSubmissionCases>(
            new("request/tests/training-submission"),
            new("1"),
            new("request/tests/training-submission/payload/v1"),
            outcomes => new(
                Accepted: outcomes.Result<TrainingSubmissionAccepted, TrainingAccepted>(
                    new("accepted"),
                    new("request/tests/training-submission/accepted/v1")),
                Rejected: outcomes.Failure<TrainingSubmissionRejected, TrainingFailure>(
                    new("rejected"),
                    new("request/tests/training-submission/rejected/v1")),
                TimedOut: outcomes.Timeout<TrainingSubmissionTimedOut, TrainingFailure>(
                    new("timed-out"),
                    new("request/tests/training-submission/timed-out/v1"))),
            new(
                RequestOptionalTerminalSemantics.TerminalOutcome,
                RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Observe,
                RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition,
                RequestRetrySemantics.ReconcileBeforeRetry,
                RequestResolutionSemantics.Reconcile,
                RequestResolutionSemantics.Reconcile,
                TimeSpan.FromDays(30)),
            new(
                new("tests.process-computation", "1"),
                new("tests/ari-423/training-submission-protocol"),
                DocumentOrigin.Generated));
}

/// <summary>Representative typed protocol Effect consumed through an exhaustive C# switch.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedTypedRequestEffectProcess
{
    static async ProcessTask<string> Run(ProcessContext process, TrainingSubmission input)
    {
        var outcome = await process.Effect(
            GeneratedTypedRequestEffectProtocol.Protocol,
            input,
            id: new("submission/provider"));
        switch (outcome)
        {
            case TrainingSubmissionAccepted(var accepted):
                return accepted.SubmissionId;
            case TrainingSubmissionRejected(var failure):
                return failure.Reason;
            case TrainingSubmissionTimedOut(var failure):
                return failure.Reason;
        }
        return process.Unreachable<string>();
    }
}

/// <summary>Raw exact-reference equivalent of <see cref="GeneratedTypedRequestEffectProcess"/>.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRawRequestEffectProcess
{
    static async ProcessTask<string> Run(ProcessContext process, TrainingSubmission input)
    {
        await process.Effect(
            GeneratedTypedRequestEffectProtocol.Protocol.Request,
            input,
            outcomes:
            [
                process.Outcome<TrainingAccepted>(
                    GeneratedTypedRequestEffectProtocol.Protocol.Outcomes.Accepted.Id,
                    TrainingSubmissionAccepted,
                    id: ProcessAuthoringIdentities.NodeForRequestOutcome(
                        new("submission/provider"),
                        GeneratedTypedRequestEffectProtocol.Protocol.Outcomes.Accepted.Id)),
                process.Outcome<TrainingFailure>(
                    GeneratedTypedRequestEffectProtocol.Protocol.Outcomes.Rejected.Id,
                    TrainingSubmissionRejected,
                    id: ProcessAuthoringIdentities.NodeForRequestOutcome(
                        new("submission/provider"),
                        GeneratedTypedRequestEffectProtocol.Protocol.Outcomes.Rejected.Id)),
                process.Outcome<TrainingFailure>(
                    GeneratedTypedRequestEffectProtocol.Protocol.Outcomes.TimedOut.Id,
                    TrainingSubmissionTimedOut,
                    id: ProcessAuthoringIdentities.NodeForRequestOutcome(
                        new("submission/provider"),
                        GeneratedTypedRequestEffectProtocol.Protocol.Outcomes.TimedOut.Id))
            ],
            id: new("submission/provider"));
        return process.Unreachable<string>();

        async ProcessTask TrainingSubmissionAccepted(TrainingAccepted accepted)
        {
            await process.Succeed(
                accepted.SubmissionId,
                id: ProcessAuthoringIdentities.NodeFor(new(
                    ["body", "request-0", "outcome-TrainingSubmissionAccepted", "return-0"])));
        }

        async ProcessTask TrainingSubmissionRejected(TrainingFailure failure)
        {
            await process.Succeed(
                failure.Reason,
                id: ProcessAuthoringIdentities.NodeFor(new(
                    ["body", "request-0", "outcome-TrainingSubmissionRejected", "return-0"])));
        }

        async ProcessTask TrainingSubmissionTimedOut(TrainingFailure failure)
        {
            await process.Succeed(
                failure.Reason,
                id: ProcessAuthoringIdentities.NodeFor(new(
                    ["body", "request-0", "outcome-TrainingSubmissionTimedOut", "return-0"])));
        }
    }
}

/// <summary>Portable Request payload used by typed Effect fixtures.</summary>
/// <param name="DatasetId">Dataset submitted to an external training provider.</param>
public sealed record TrainingSubmission(string DatasetId);

/// <summary>Portable successful provider-submission result.</summary>
/// <param name="SubmissionId">Provider-owned submission identity.</param>
public sealed record TrainingAccepted(string SubmissionId);

/// <summary>Portable failed or timed-out provider-submission evidence.</summary>
/// <param name="Reason">Provider or timeout failure reason.</param>
public sealed record TrainingFailure(string Reason);

/// <summary>Closed source-only result family selected by the training-submission Request protocol.</summary>
public abstract record TrainingSubmissionOutcome;

/// <summary>Source-only successful result case.</summary>
/// <param name="Payload">Canonical successful outcome payload.</param>
public sealed record TrainingSubmissionAccepted(TrainingAccepted Payload) : TrainingSubmissionOutcome;

/// <summary>Source-only rejected result case.</summary>
/// <param name="Payload">Canonical failure outcome payload.</param>
public sealed record TrainingSubmissionRejected(TrainingFailure Payload) : TrainingSubmissionOutcome;

/// <summary>Source-only timed-out result case distinct from rejection despite sharing its payload type.</summary>
/// <param name="Payload">Canonical timeout outcome payload.</param>
public sealed record TrainingSubmissionTimedOut(TrainingFailure Payload) : TrainingSubmissionOutcome;

/// <summary>Named protocol-owned case descriptors exposed for analyzer and handler projection.</summary>
/// <param name="Accepted">Successful terminal result case.</param>
/// <param name="Rejected">Rejected terminal failure case.</param>
/// <param name="TimedOut">Timed-out terminal case.</param>
public sealed record TrainingSubmissionCases(
    RequestProtocolCase<TrainingSubmissionAccepted, TrainingAccepted> Accepted,
    RequestProtocolCase<TrainingSubmissionRejected, TrainingFailure> Rejected,
    RequestProtocolCase<TrainingSubmissionTimedOut, TrainingFailure> TimedOut);

/// <summary>Representative generated child Process invocation authored as compensation work.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedCompensationProcess
{
    /// <summary>Exact compensating child Process definition.</summary>
    public static ExecutionDefinitionReference Child { get; } = new(
        new("process/undo-reservation"),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('d', 64)));

    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        async ProcessTask Completed(string result) { }
        async ProcessTask Failed(string failure) { }
        async ProcessTask Cancelled(string cancellation) { }
        async ProcessTask Terminated(string termination) { }

        await process.InvokeProcess(
            process: Child,
            contract: GeneratedChildInvocationProcess.Request,
            outcomeMapping: GeneratedChildInvocationProcess.Mapping,
            input: input,
            purpose: ProcessChildPurpose.Compensation,
            cancellation: ProcessChildCancellationPolicy.Propagate,
            outcomes:
            [
                process.Outcome<string>(GeneratedChildInvocationProcess.Mapping.Completed, Completed),
                process.Outcome<string>(GeneratedChildInvocationProcess.Mapping.Failed, Failed),
                process.Outcome<string>(GeneratedChildInvocationProcess.Mapping.Cancelled, Cancelled),
                process.Outcome<string>(GeneratedChildInvocationProcess.Mapping.Terminated, Terminated)
            ]);
        return input;
    }
}

/// <summary>Representative generated finite bounded partition work.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedPartitionProcess
{
    /// <summary>Exact child Process used for every partition.</summary>
    public static ExecutionDefinitionReference Child { get; } = new(
        new("process/partition-child"),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('b', 64)));

    static async ProcessTask<string> Run(ProcessContext process, PartitionProcessInput input)
    {
        async ProcessTask Failed() { }

        await process.ForEachPartition<PartitionItem, string>(
            partitions: input.Partitions,
            progressIdentity: partition => partition.Id,
            process: Child,
            contract: GeneratedChildInvocationProcess.Request,
            outcomeMapping: GeneratedChildInvocationProcess.Mapping,
            childInput: partition => partition.Id + ":" + input.Value,
            limits: new ProcessWorkLimits(
                maximumItems: 3,
                maximumStartsPerActivation: 2,
                maximumParallelism: 2),
            failure: ProcessPartitionFailurePolicy.FailFast,
            capacityIdentity: partition => partition.Target,
            capacityDomains:
            [
                new("target/a", maximumParallelism: 1),
                new("target/b", maximumParallelism: 1)
            ],
            cancellation: ProcessChildCancellationPolicy.Propagate,
            failed: Failed);
        return input.Value;
    }
}

/// <summary>One portable partition in the generated bounded-work example.</summary>
/// <param name="Id">Stable progress identity.</param>
/// <param name="Target">Declared capacity-domain identity.</param>
public sealed record PartitionItem(string Id, string Target);

/// <summary>Input to the generated bounded-work example.</summary>
/// <param name="Partitions">Finite partition collection.</param>
/// <param name="Value">Value fused into every child input and returned after successful settlement.</param>
public sealed record PartitionProcessInput(ImmutableArray<PartitionItem> Partitions, string Value);

/// <summary>Representative generated finite recurrence across durable activations.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRecurrenceProcess
{
    /// <summary>Exact Relation used by each polling occurrence.</summary>
    public static ExecutionDefinitionReference Poll { get; } = new(
        new("relation/poll-status"),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('c', 64)));

    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        async ProcessTask<string> PollOnce()
        {
            var observation = await process.Query<string>(Poll, input);
            return observation;
        }

        async ProcessTask Exhausted() { }
        async ProcessTask Stalled() { }

        var observation = await process.RepeatAcrossActivation(
            occurrence: PollOnce(),
            continueWhen: status => status != "approved",
            progress: status => status,
            policy: new ProcessRecurrencePolicy(
                maximumOccurrences: 3,
                maximumUnchangedProgressOccurrences: 1),
            exhausted: Exhausted,
            stalled: Stalled);
        return observation;
    }
}

/// <summary>Baseline convention-identity computation used for stability comparison.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedIdentityBaselineProcess
{
    /// <summary>First exact Relation reference.</summary>
    public static ExecutionDefinitionReference Alpha { get; } = Definition("relation/identity-alpha", 'e');

    /// <summary>Second exact Relation reference.</summary>
    public static ExecutionDefinitionReference Beta { get; } = Definition("relation/identity-beta", 'f');

    static async ProcessTask<string> Run(ProcessContext process, IdentityProcessInput input)
    {
        var alpha = await process.Query<string>(Alpha, input.Value);
        var beta = await process.Query<string>(Beta, input.Value);
        return alpha + beta;
    }

    static ExecutionDefinitionReference Definition(string id, char fingerprint) => new(
        new(id),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprint, 64)));
}

/// <summary>Equivalent independent queries authored in the opposite order.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedIdentityReorderedProcess
{
    static async ProcessTask<string> Run(ProcessContext process, IdentityProcessInput input)
    {
        var beta = await process.Query<string>(GeneratedIdentityBaselineProcess.Beta, input.Value);
        var alpha = await process.Query<string>(GeneratedIdentityBaselineProcess.Alpha, input.Value);
        return alpha + beta;
    }
}

/// <summary>Baseline queries with a heterogeneous Timer inserted between them.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedIdentityInsertedProcess
{
    static async ProcessTask<string> Run(ProcessContext process, IdentityProcessInput input)
    {
        var alpha = await process.Query<string>(GeneratedIdentityBaselineProcess.Alpha, input.Value);
        await process.Timer(input.DueAt);
        var beta = await process.Query<string>(GeneratedIdentityBaselineProcess.Beta, input.Value);
        return alpha + beta;
    }
}

/// <summary>Input to convention-identity stability fixtures.</summary>
/// <param name="Value">Portable query input.</param>
/// <param name="DueAt">Absolute due instant for the heterogeneous insertion fixture.</param>
public sealed record IdentityProcessInput(string Value, DateTimeOffset DueAt);

/// <summary>Representative explicit nested Choice/Match policy computation.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedDecisionProcess
{
    static async ProcessTask<string> Run(ProcessContext process, DecisionProcessInput input)
    {
        async ProcessTask Wait()
        {
            await process.Timer(input.DueAt, id: new("decision/timer"));
        }

        async ProcessTask Continue()
        {
        }

        async ProcessTask Fast()
        {
            await process.Match(
                value: input.State,
                selection: CaseSelection.OrderedFirstMatch,
                completeness: BranchCompleteness.Fallback,
                cases:
                [
                    process.Case("wait", Wait)
                ],
                fallback: Continue,
                id: new("decision/state"));
        }

        async ProcessTask Other()
        {
        }

        await process.Choice(
            selection: CaseSelection.OrderedFirstMatch,
            completeness: BranchCompleteness.Fallback,
            cases:
            [
                process.When(input.Category == "fast", Fast, id: new("decision/category/fast"))
            ],
            fallback: Other,
            id: new("decision/category"));
        return input.State;
    }
}

/// <summary>
/// Semantically equivalent decision computation after a syntax transform moves local functions below the terminal
/// return and makes one untyped branch's fallthrough explicit.
/// </summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedDecisionProcessWithTrailingBranchReturn
{
    static async ProcessTask<string> Run(ProcessContext process, DecisionProcessInput input)
    {
        await process.Choice(
            selection: CaseSelection.OrderedFirstMatch,
            completeness: BranchCompleteness.Fallback,
            cases:
            [
                process.When(input.Category == "fast", Fast, id: new("decision/category/fast"))
            ],
            fallback: Other,
            id: new("decision/category"));
        return input.State;

        async ProcessTask Wait()
        {
            await process.Timer(input.DueAt, id: new("decision/timer"));
            return;
        }

        async ProcessTask Continue()
        {
        }

        async ProcessTask Fast()
        {
            await process.Match(
                value: input.State,
                selection: CaseSelection.OrderedFirstMatch,
                completeness: BranchCompleteness.Fallback,
                cases:
                [
                    process.Case("wait", Wait)
                ],
                fallback: Continue,
                id: new("decision/state"));
        }

        async ProcessTask Other()
        {
        }
    }
}

/// <summary>Input to the explicit nested Choice/Match computation.</summary>
/// <param name="Category">Outer predicate-selection value.</param>
/// <param name="State">Inner exact-match value and terminal result.</param>
/// <param name="DueAt">Absolute due instant selected by the nested wait arm.</param>
public sealed record DecisionProcessInput(string Category, string State, DateTimeOffset DueAt);

/// <summary>Representative human-facing Process covering sequential, branching, and parallel authoring.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class ApproveCustomerProcess
{
    static readonly ExecutionDefinitionReference CustomerByEmail = Definition("relation/customer-by-email", '1');
    static readonly ExecutionDefinitionReference CustomerById = Definition("relation/customer-by-id", '2');
    static readonly ExecutionDefinitionReference ApproveCustomer = Definition("transition/approve-customer", '3');
    static readonly RequestContractReference SendWelcome = new(Definition("request/send-welcome", '4'));
    static readonly RequestContractReference RecordAudit = new(Definition("request/record-audit", '5'));
    static readonly RequestContractReference NotifyOwner = new(Definition("request/notify-owner", '6'));
    static readonly RequestContractReference RequestDocumentReview = new(Definition("request/document-review", '7'));
    static readonly DomainEventContractReference DocumentReviewSubmitted = new(Definition("event/document-review-submitted", '8'));
    static readonly DomainEventContractReference ApprovalWithdrawn = new(Definition("event/approval-withdrawn", '9'));
    static readonly RequestTerminalOutcomeId Completed = new("completed");

    static async ProcessTask<ApproveCustomerResult> Run(
        ProcessContext process,
        ApproveCustomerInput input)
    {
        var lookup = new CustomerLookup(input.Email);
        var customerId = await process.Query<CustomerId>(CustomerByEmail, lookup);
        var customer = await process.Read<Customer>(CustomerById, customerId);

        if (customer.Status == "Suspended")
        {
            return new(
                customer.Id,
                "rejected",
                DeliveryId: null,
                AuditReceiptId: null,
                NotificationReceiptId: null);
        }

        var reviewTask = await process.Effect<DocumentReviewTask>(
            RequestDocumentReview,
            Completed,
            new DocumentReviewRequest(customer.Id, input.Reason));
        var review = await process.AwaitMatch<CustomerReviewOutcome>(
            clauses:
            [
                process.Event<DocumentReviewSubmitted>(
                    DocumentReviewSubmitted,
                    priority: 10,
                    when: submitted => submitted.TaskId == reviewTask.Id),
                process.Event<CustomerApprovalWithdrawn>(
                    ApprovalWithdrawn,
                    priority: 20,
                    when: withdrawn => withdrawn.CustomerId == customer.Id),
                process.Deadline<DocumentReviewTimedOut>(reviewTask.DueAt)
            ],
            arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            lateInput: ProcessAwaitInputDisposition.Observe,
            staleInput: ProcessAwaitInputDisposition.Reject,
            duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
            missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
            retentionHorizon: TimeSpan.FromDays(30));
        switch (review)
        {
            case DocumentReviewTimedOut _:
                return new(
                    customer.Id,
                    "review-timed-out",
                    DeliveryId: null,
                    AuditReceiptId: null,
                    NotificationReceiptId: null);
            case CustomerApprovalWithdrawn _:
                return new(
                    customer.Id,
                    "withdrawn",
                    DeliveryId: null,
                    AuditReceiptId: null,
                    NotificationReceiptId: null);
            case DocumentReviewSubmitted { Decision: var decision }:
                if (decision != "approved")
                {
                    return new(
                        customer.Id,
                        "rejected",
                        DeliveryId: null,
                        AuditReceiptId: null,
                        NotificationReceiptId: null);
                }
                break;
        }

        var approval = await process.Transition<Approval>(
            ApproveCustomer,
            customer.Id,
            new ApproveCustomerTransitionInput(input.Reason));
        var delivery = await process.Effect<Delivery>(
            SendWelcome,
            Completed,
            new WelcomeMessage(customer.Email, "Welcome " + approval.DisplayName));

        async ProcessTask<OperationReceipt> Audit()
        {
            var receipt = await process.Effect<OperationReceipt>(
                RecordAudit,
                Completed,
                new AuditMessage(customer.Id, approval.DisplayName));
            return receipt;
        }

        async ProcessTask<OperationReceipt> Notify()
        {
            var receipt = await process.Effect<OperationReceipt>(
                NotifyOwner,
                Completed,
                new OwnerNotification(customer.Id, delivery.Id));
            return receipt;
        }

        var (auditReceipt, notificationReceipt) = await process.ForkJoin(Audit(), Notify());
        switch (delivery.Status)
        {
            case "sent":
                return new(
                    customer.Id,
                    "approved",
                    delivery.Id,
                    auditReceipt.Id,
                    notificationReceipt.Id);
            default:
                return new(
                    customer.Id,
                    "pending",
                    delivery.Id,
                    auditReceipt.Id,
                    notificationReceipt.Id);
        }
    }

    static ExecutionDefinitionReference Definition(string id, char fingerprint) => new(
        new(id),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprint, 64)));
}

/// <summary>Input to the representative customer-approval Process.</summary>
/// <param name="Email">Customer lookup email.</param>
/// <param name="Reason">Approval reason.</param>
public sealed record ApproveCustomerInput(string Email, string Reason);

/// <summary>Customer lookup relation input.</summary>
/// <param name="Email">Customer email.</param>
public sealed record CustomerLookup(string Email);

/// <summary>Customer identity returned by lookup.</summary>
/// <param name="Value">Stable customer identity.</param>
public sealed record CustomerId(string Value);

/// <summary>Customer entity projection used by the Process.</summary>
/// <param name="Id">Stable customer identity.</param>
/// <param name="Status">Current customer status.</param>
/// <param name="Email">Customer email.</param>
public sealed record Customer(string Id, string Status, string Email);

/// <summary>Approval Transition input.</summary>
/// <param name="Reason">Approval reason.</param>
public sealed record ApproveCustomerTransitionInput(string Reason);

/// <summary>Request payload used to create one durable human document-review task.</summary>
/// <param name="CustomerId">Customer whose submitted evidence requires review.</param>
/// <param name="Reason">Authored reason supplied to the reviewer.</param>
public sealed record DocumentReviewRequest(string CustomerId, string Reason);

/// <summary>Stable reference returned after the human document-review task is durably created.</summary>
/// <param name="Id">Stable review-task identity targeted by the completion interaction.</param>
/// <param name="DueAt">Absolute deadline participating in canonical AwaitMatch arbitration.</param>
public sealed record DocumentReviewTask(string Id, DateTimeOffset DueAt);

/// <summary>Closed source-only result family for customer document review.</summary>
public abstract record CustomerReviewOutcome;

/// <summary>Typed review-completion event and source-only successful AwaitMatch case.</summary>
/// <param name="TaskId">Exact review-task identity completed by this event.</param>
/// <param name="Decision">Portable review disposition.</param>
/// <param name="ReviewerId">Stable reviewer identity retained by downstream work.</param>
public sealed record DocumentReviewSubmitted(string TaskId, string Decision, string ReviewerId)
    : CustomerReviewOutcome;

/// <summary>Typed withdrawal event and source-only AwaitMatch result case.</summary>
/// <param name="CustomerId">Customer whose in-flight approval was withdrawn.</param>
public sealed record CustomerApprovalWithdrawn(string CustomerId) : CustomerReviewOutcome;

/// <summary>Source-only result case selected when the document-review deadline wins.</summary>
public sealed record DocumentReviewTimedOut : CustomerReviewOutcome;

/// <summary>Approval Transition result.</summary>
/// <param name="DisplayName">Customer display name.</param>
public sealed record Approval(string DisplayName);

/// <summary>Welcome-message Request payload.</summary>
/// <param name="Email">Delivery email.</param>
/// <param name="Subject">Welcome-message subject.</param>
public sealed record WelcomeMessage(string Email, string Subject);

/// <summary>Welcome-message delivery result.</summary>
/// <param name="Id">Delivery identity.</param>
/// <param name="Status">Delivery status.</param>
public sealed record Delivery(string Id, string Status);

/// <summary>Audit Request payload.</summary>
/// <param name="CustomerId">Customer identity.</param>
/// <param name="DisplayName">Approved customer display name.</param>
public sealed record AuditMessage(string CustomerId, string DisplayName);

/// <summary>Owner-notification Request payload.</summary>
/// <param name="CustomerId">Customer identity.</param>
/// <param name="DeliveryId">Welcome delivery identity.</param>
public sealed record OwnerNotification(string CustomerId, string DeliveryId);

/// <summary>Result of an auxiliary parallel Request.</summary>
/// <param name="Id">Operation receipt identity.</param>
public sealed record OperationReceipt(string Id);

/// <summary>Terminal result of the representative customer-approval Process.</summary>
/// <param name="CustomerId">Customer identity.</param>
/// <param name="Disposition">Approval disposition.</param>
/// <param name="DeliveryId">Optional welcome delivery identity.</param>
/// <param name="AuditReceiptId">Optional audit operation receipt identity.</param>
/// <param name="NotificationReceiptId">Optional owner-notification operation receipt identity.</param>
public sealed record ApproveCustomerResult(
    string CustomerId,
    string Disposition,
    string? DeliveryId,
    string? AuditReceiptId,
    string? NotificationReceiptId);
