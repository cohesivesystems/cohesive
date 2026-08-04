using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
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
        var generated = GeneratedCustomerQueryProcess.Define(Metadata());
        var query = ProcessAuthoringIdentities.NodeFor(new(["body", "query-row"]));
        var returned = ProcessAuthoringIdentities.NodeFor(new(["body", "return-1"]));
        var lowLevel = ProcessAuthoring.Create<string, string>(
            Metadata().WithEntry(query),
            process =>
            {
                var output = process.Output<string>(query, "result");

                process.EvaluateRelation(
                    query,
                    GeneratedCustomerQueryProcess.Relation,
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
    public void TypedForkComputation_IsByteEquivalentToCanonicalBuilderAuthoring()
    {
        var fork = Node("fork-0");
        var join = Node("fork-0", "join-1");
        var auditBranch = Node("fork-0", "branch-Audit");
        var auditQuery = Node("fork-0", "branch-Audit", "query-value");
        var notifyBranch = Node("fork-0", "branch-Notify");
        var notifyQuery = Node("fork-0", "branch-Notify", "query-value");
        var returned = Node("return-8");
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
        var original = GeneratedCustomerQueryProcess.Define(Metadata());
        var withPureLocal = GeneratedCustomerQueryProcessWithPureLocal.Define(Metadata());

        Assert.Equal(original.Definition, withPureLocal.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(original.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(withPureLocal.Document));
    }

    [Fact]
    public void GeneratedComputation_HonorsMatchingExplicitEntryAndRejectsConflict()
    {
        var entry = ProcessAuthoringIdentities.NodeFor(new(["body", "query-row"]));

        var generated = GeneratedCustomerQueryProcess.Define(Metadata().WithEntry(entry));

        Assert.Equal(entry, generated.Definition.Entry);
        Assert.Throws<ArgumentException>(() =>
            GeneratedCustomerQueryProcess.Define(Metadata().WithEntry(new("conflicting-entry"))));
    }

    [Fact]
    public void GeneratedDocument_StrictlyRestoresWithoutHostLanguageState()
    {
        var generated = GeneratedCustomerQueryProcess.Define(Metadata());
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
        Assert.Equal(3, generated.Definition.Nodes.OfType<RequestProcessNode>().Count());
        Assert.Single(generated.Definition.Nodes.OfType<ChoiceProcessNode>());
        Assert.Single(generated.Definition.Nodes.OfType<MatchProcessNode>());
        Assert.Equal(3, generated.Definition.Nodes.OfType<ReturnProcessNode>().Count());

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

    static ExecutionNodeId Node(params string[] path) =>
        ProcessAuthoringIdentities.NodeFor(new(["body", .. path]));

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
                break;
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

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location ?? diagnostic.SchemaLocation}: {diagnostic.Message}"));
}

/// <summary>Representative generated Process used by canonical-equivalence tests.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedCustomerQueryProcess
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

/// <summary>Semantically identical generated Process containing a non-effectful local.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedCustomerQueryProcessWithPureLocal
{
    static async ProcessTask<string> Run(
        ProcessContext process,
        string input)
    {
        var ignored = input + string.Empty;
        var queryInput = input;
        var row = await process.Query<string>(GeneratedCustomerQueryProcess.Relation, queryInput);
        return row;
    }
}

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
