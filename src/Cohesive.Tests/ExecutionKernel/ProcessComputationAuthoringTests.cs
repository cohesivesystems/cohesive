using System.Text;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
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
            return new(customer.Id, "rejected", DeliveryId: null);
        }

        var approval = await process.Transition<Approval>(
            ApproveCustomer,
            customer.Id,
            new ApproveCustomerTransitionInput(input.Reason));
        var delivery = await process.Effect<Delivery>(
            SendWelcome,
            Completed,
            new WelcomeMessage(customer.Email, "Welcome " + approval.DisplayName));

        async ProcessTask Audit()
        {
            var receipt = await process.Effect<OperationReceipt>(
                RecordAudit,
                Completed,
                new AuditMessage(customer.Id, approval.DisplayName));
        }

        async ProcessTask Notify()
        {
            var receipt = await process.Effect<OperationReceipt>(
                NotifyOwner,
                Completed,
                new OwnerNotification(customer.Id, delivery.Id));
        }

        await process.ForkJoin(Audit(), Notify());
        switch (delivery.Status)
        {
            case "sent":
                return new(customer.Id, "approved", delivery.Id);
            default:
                return new(customer.Id, "pending", delivery.Id);
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
public sealed record ApproveCustomerResult(string CustomerId, string Disposition, string? DeliveryId);
