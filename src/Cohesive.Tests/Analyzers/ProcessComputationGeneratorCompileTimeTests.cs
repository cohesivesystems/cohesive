using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Analyzers;
using Cohesive.Processes.Authoring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cohesive.Tests.Analyzers;

/// <summary>Compile-time coverage for C# Process computation-expression generation.</summary>
public sealed class ProcessComputationGeneratorCompileTimeTests
{
    [Fact]
    public void Generator_LowersNaturalAwaitFlowAndFusesPureLocals()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ApproveCustomerProcess
                     {
                         private static ExecutionDefinitionReference CustomerByEmail => null!;
                         private static ExecutionDefinitionReference CustomerById => null!;
                         private static ExecutionDefinitionReference ApproveCustomer => null!;
                         private static RequestContractReference SendWelcome => null!;
                         private static RequestTerminalOutcomeId WelcomeSent => new("sent");

                         private static async ProcessTask<ApproveCustomerResult> Run(
                             ProcessContext process,
                             ApproveCustomerInput input)
                         {
                             var lookup = new CustomerLookup(input.Email);
                             var normalizedLookup = new CustomerLookup(lookup.Email);
                             var customerQuery = CustomerByEmail;
                             var customerId = await process.Query<CustomerId>(customerQuery, normalizedLookup);
                             var customer = await process.Read<Customer>(CustomerById, customerId);

                             if (customer.Status == "Suspended")
                                 return new ApproveCustomerResult(customer.Id, "rejected");

                             var transitionInput = new ApproveTransitionInput(input.Reason);
                             var approval = await process.Transition<Approval>(
                                 ApproveCustomer,
                                 customer.Id,
                                 transitionInput);
                             var subject = "Welcome " + approval.DisplayName;
                             var message = new WelcomeMessage(customer.Email, subject);
                             var delivery = await process.Effect<Delivery>(
                                 SendWelcome,
                                 WelcomeSent,
                                 message);
                             return new ApproveCustomerResult(customer.Id, delivery.Id);
                         }
                     }

                     public sealed record ApproveCustomerInput(string Email, string Reason);
                     public sealed record CustomerLookup(string Email);
                     public sealed record CustomerId(string Value);
                     public sealed record Customer(string Id, string Status, string Email);
                     public sealed record ApproveTransitionInput(string Reason);
                     public sealed record Approval(string DisplayName);
                     public sealed record WelcomeMessage(string Email, string Subject);
                     public sealed record Delivery(string Id);
                     public sealed record ApproveCustomerResult(string CustomerId, string Outcome);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static result => result.SourceText.ToString()));
        var errors = outputCompilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        Assert.Empty(errors);
        Assert.Contains("ProcessAuthoring.Create<", generated);
        Assert.Contains("__builder.EvaluateRelation", generated);
        Assert.Contains("__builder.InvokeTransition", generated);
        Assert.Contains("__builder.Request", generated);
        Assert.Contains("__builder.Choice", generated);
        Assert.Contains("ExprFunctionNames.Object", generated);
        Assert.Contains("ExprFunctionNames.Concat", generated);
        Assert.DoesNotContain("Compute", generated);
        Assert.DoesNotContain("System.Func", generated);
        Assert.DoesNotContain("async", generated);
    }

    [Fact]
    public void Generator_LowersTypedOutboundInteractionsWithoutCallbacks()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class OutboundInteractionProcess
                     {
                         private static DomainEventContractReference Event => null!;
                         private static SignalContractReference Signal => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             await process.EmitEvent(
                                 Event,
                                 input.Value,
                                 nextRole: "published");
                             await process.SendSignal(
                                 Signal,
                                 input.Target,
                                 input.Value,
                                 nextRole: "sent");
                             return input.Value;
                         }
                     }

                     public sealed record Input(string Target, string Value);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.EmitEvent", generated);
        Assert.Contains("__builder.SendSignal", generated);
        Assert.Contains("emit-event-0", generated);
        Assert.Contains("send-signal-0", generated);
        Assert.Contains("\"published\"", generated);
        Assert.Contains("\"sent\"", generated);
        Assert.DoesNotContain("System.Func", generated);
        Assert.DoesNotContain("async", generated);
    }

    [Fact]
    public void Generator_RejectsRuntimeBoundInteractionContractsAtTheCallSite()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class RuntimeContractProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             await process.EmitEvent(input.Contract, input.Value);
                             return input.Value;
                         }
                     }

                     public sealed record Input(DomainEventContractReference Contract, string Value);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("exact semantic arguments", diagnostic.GetMessage());
        Assert.Equal(11, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsNonPortableSignalTargetsAtTheCallSite()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class NonPortableSignalTargetProcess
                     {
                         private static SignalContractReference Signal => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             await process.SendSignal(Signal, Guid.NewGuid(), input);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC004");

        Assert.Contains("Guid.NewGuid", diagnostic.GetMessage());
        Assert.Equal(14, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsMissingSignalTargetSemanticsAtTheArgument()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class MissingSignalTargetProcess
                     {
                         private static SignalContractReference Signal => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             await process.SendSignal(Signal, (string?)null, input);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("non-null semantic target", diagnostic.GetMessage());
        Assert.Equal(13, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_LowersTopLevelCancellationFinalizerDeclaration()
    {
        var compilation = CreateCompilation(CancellationFinalizerSource("""
            process.OnCancellation(Protocol, id: new("cancel/finalize"));
            """));
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.OnCancellation", generated);
        Assert.Contains("cancel/finalize", generated);
        Assert.DoesNotContain("System.Func", generated);
        Assert.Contains("ValueContract inputContract", generated);
        Assert.Contains("ValueContract resultContract", generated);
        Assert.Contains("inputContract: inputContract", generated);
        Assert.Contains("resultContract: resultContract!", generated);
    }

    [Fact]
    public void Generator_RejectsNestedCancellationFinalizerDeclaration()
    {
        var compilation = CreateCompilation(CancellationFinalizerSource("""
            if (input.Length > 0)
            {
                process.OnCancellation(Protocol);
            }
            """));
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("root computation body", diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_RejectsDuplicateCancellationFinalizerDeclarations()
    {
        var compilation = CreateCompilation(CancellationFinalizerSource("""
            process.OnCancellation(Protocol, id: new("cancel/first"));
            process.OnCancellation(Protocol, id: new("cancel/second"));
            """));
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("only one cancellation finalizer", diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_RejectsCancellationFinalizerForDifferentRootInput()
    {
        var source = CancellationFinalizerSource(
            "process.OnCancellation(WrongProtocol);",
            """
            private static ProcessInvocationProtocol<ProcessCancellationFinalizationInput<int>, ProcessCancellationAcknowledgement> WrongProtocol => null!;
            """);
        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("exact root Process input type", diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_ReportsUnsupportedPureComputationAtItsSource()
    {
        var source = """
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             var value = input.ToUpperInvariant();
                             return value;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC004");

        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("ToUpperInvariant", diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_LowersExactSwitchToCanonicalMatch()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class StatusProcess
                     {
                         private static ExecutionDefinitionReference StatusQuery => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             var status = await process.Query<string>(StatusQuery, input, id: new("status-query"));
                             switch (status)
                             {
                                 case "ready":
                                     return "accepted";
                                 case "blocked":
                                     return "rejected";
                                 default:
                                     return "pending";
                             }
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.Match", generated);
        Assert.Contains("new(\"status-query\")", generated);
        Assert.Contains("pattern: \"ready\"", generated);
        Assert.Contains("pattern: \"blocked\"", generated);
        Assert.Contains("BranchCompleteness.Fallback", generated);
    }

    [Fact]
    public void Generator_LowersExplicitChoiceAndMatchPoliciesWithoutCallbacks()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ExplicitDecisionProcess
                     {
                         private static ExecutionDefinitionReference StatusQuery => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, DecisionInput input)
                         {
                             async ProcessTask Escalate()
                             {
                                 await process.Timer(input.DueAt);
                             }

                             async ProcessTask Continue()
                             {
                                 await process.Timer(input.DueAt);
                             }

                             async ProcessTask Otherwise()
                             {
                                 await process.Timer(input.DueAt);
                             }

                             var status = await process.Query<string>(StatusQuery, input.Status, id: new("status"));
                             await process.Choice(
                                 selection: CaseSelection.OrderedFirstMatch,
                                 completeness: BranchCompleteness.Fallback,
                                 cases:
                                 [
                                     process.When(status == "blocked", Escalate, id: new("choice/blocked")),
                                     process.When(status == "ready", Continue)
                                 ],
                                 fallback: Otherwise,
                                 id: new("status-choice"));
                             await process.Match(
                                 value: status,
                                 selection: CaseSelection.OrderedFirstMatch,
                                 completeness: BranchCompleteness.Fallback,
                                 cases:
                                 [
                                     process.Case("blocked", Escalate, id: new("match/blocked")),
                                     process.Case("ready", Continue)
                                 ],
                                 fallback: Otherwise,
                                 id: new("status-match"));
                             return status;
                         }
                     }

                     public sealed record DecisionInput(string Status, DateTimeOffset DueAt);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.Choice(id:", generated);
        Assert.Contains("new(\"status-choice\")", generated);
        Assert.Contains("selection: (CaseSelection.OrderedFirstMatch)", generated);
        Assert.Contains("completeness: (BranchCompleteness.Fallback)", generated);
        Assert.Contains("__builder.Match(id:", generated);
        Assert.Contains("new(\"status-match\")", generated);
        Assert.Contains("matchedValue:", generated);
        Assert.Contains("pattern: (\"blocked\")", generated);
        Assert.Contains("new(\"choice/blocked\")", generated);
        Assert.Contains("new(\"match/blocked\")", generated);
        Assert.DoesNotContain("ProcessChoiceArm", generated);
        Assert.DoesNotContain("ProcessMatchArm", generated);
        Assert.DoesNotContain("ProcessBranch", generated);
        Assert.DoesNotContain("System.Func", generated);
    }

    [Theory]
    [InlineData("BranchCompleteness.Fallback", "null", "fallback completeness requires a named fallback branch")]
    [InlineData("BranchCompleteness.Exhaustive", "Otherwise", "exhaustive completeness cannot declare a fallback branch")]
    public void Generator_RejectsChoicePolicyAndFallbackMismatch(
        string completeness,
        string fallback,
        string expected)
    {
        var source = $$"""
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidChoiceProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Selected()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch);
                             }
                             async ProcessTask Otherwise()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch);
                             }
                             await process.Choice(
                                 selection: CaseSelection.OrderedFirstMatch,
                                 completeness: {{completeness}},
                                 cases: [process.When(input == "selected", Selected)],
                                 fallback: {{fallback}});
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains(expected, diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("CaseSelection.Unspecified", "BranchCompleteness.Fallback")]
    [InlineData("CaseSelection.OrderedFirstMatch", "BranchCompleteness.Unspecified")]
    public void Generator_RejectsUnspecifiedExplicitDecisionPolicies(
        string selection,
        string completeness)
    {
        var source = $$"""
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidPolicyProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Selected()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch);
                             }
                             async ProcessTask Otherwise()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch);
                             }
                             await process.Choice(
                                 selection: {{selection}},
                                 completeness: {{completeness}},
                                 cases: [process.When(input == "selected", Selected)],
                                 fallback: Otherwise);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("cannot be Unspecified", diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_ReportsDuplicateExactIdentityAtTheSecondDeclaration()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class DuplicateIdentityProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask First()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch, id: new("timer/first"));
                             }
                             async ProcessTask Second()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch, id: new("timer/second"));
                             }
                             await process.Choice(
                                 selection: CaseSelection.OrderedFirstMatch,
                                 completeness: BranchCompleteness.Exhaustive,
                                 cases:
                                 [
                                     process.When(input == "first", First, id: new("case/duplicate")),
                                     process.When(input != "first", Second, id: new("case/duplicate"))
                                 ]);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC007");

        Assert.Contains("case/duplicate", diagnostic.GetMessage());
        Assert.Equal(26, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsRuntimeDerivedExactMatchPattern()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class RuntimePatternProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, PatternInput input)
                         {
                             async ProcessTask Selected()
                             {
                                 await process.Timer(input.DueAt);
                             }
                             await process.Match(
                                 value: input.Value,
                                 selection: CaseSelection.OrderedFirstMatch,
                                 completeness: BranchCompleteness.Exhaustive,
                                 cases: [process.Case(input.Value, Selected)]);
                             return input.Value;
                         }
                     }

                     public sealed record PatternInput(string Value, DateTimeOffset DueAt);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("cannot depend on runtime bindings", diagnostic.GetMessage());
        Assert.Equal(20, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_LowersLocalFunctionBranchesToCanonicalForkAndJoin()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ParallelNotificationProcess
                     {
                         private static RequestContractReference SendEmail => null!;
                         private static RequestContractReference RecordAudit => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Notify()
                             {
                                 var delivery = await process.Effect<string>(SendEmail, Completed, input);
                             }

                             async ProcessTask Audit()
                             {
                                 var receipt = await process.Effect<string>(RecordAudit, Completed, input);
                             }

                             await process.ForkJoin(new ExecutionNodeId("notifications"), Notify(), Audit());
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.Fork", generated);
        Assert.Contains("__builder.Join", generated);
        Assert.Equal(2, Count(generated, "__builder.Request(id:"));
        Assert.Contains("ProcessJoinMode.All", generated);
        Assert.Contains("ProcessJoinFailurePolicy.FailFast", generated);
        Assert.Contains("new ExecutionNodeId(\"notifications\")", generated);
        Assert.DoesNotContain("Notify()", generated);
        Assert.DoesNotContain("Audit()", generated);
        Assert.DoesNotContain("ProcessTaskMethodBuilder", generated);
    }

    [Fact]
    public void Generator_LowersTypedForkResultsAndAdmissionWithoutPersistingTupleSyntax()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ParallelReceiptProcess
                     {
                         private static RequestContractReference RecordAudit => null!;
                         private static RequestContractReference NotifyOwner => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<Receipt> Audit()
                             {
                                 var receipt = await process.Effect<Receipt>(RecordAudit, Completed, input);
                                 return new Receipt("audit-" + receipt.Id);
                             }

                             async ProcessTask<Receipt> Notify()
                             {
                                 var receipt = await process.Effect<Receipt>(NotifyOwner, Completed, input);
                                 return new Receipt("notify-" + receipt.Id);
                             }

                             var (auditReceipt, notifyReceipt) = await process.ForkJoin(
                                 process.Branch(Audit(), capacityDomain: "outbound"),
                                 Notify(),
                                 admission: ProcessAdmission.Bounded(
                                     maximumParallelism: 1,
                                     maximumStartsPerActivation: 1,
                                     capacityDomains: [ProcessCapacity.Domain("outbound", maximumParallelism: 1)]),
                                 id: new("parallel-receipts"));
                             return auditReceipt.Id + notifyReceipt.Id;
                         }
                     }

                     public sealed record Receipt(string Id);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("new global::Cohesive.Processes.IR.ProcessWorkLimits", generated);
        Assert.Contains("ProcessCapacity.Domain(\"outbound\", maximumParallelism: 1)", generated);
        Assert.Contains("capacityDomain: (\"outbound\")", generated);
        Assert.Contains("new(\"parallel-receipts\")", generated);
        Assert.Equal(2, Count(generated, "FieldPathSegment.ForField(\"Id\")"));
        Assert.DoesNotContain("ValueTuple", generated);
        Assert.DoesNotContain("auditReceipt", generated);
        Assert.DoesNotContain("notifyReceipt", generated);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Generator_LowersEverySupportedTypedForkArity(int arity)
    {
        var source = TypedForkSource(arity);

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Equal(arity, Count(generated, "__builder.Request(id:"));
        Assert.Contains($"__fork_branch_{arity - 1}_", generated);
        Assert.DoesNotContain("ValueTuple", generated);
    }

    [Fact]
    public void Generator_ReportsTypedForkBranchWithoutOneFinalResultExpression()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidTypedForkProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<string> ConditionalBranch()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 if (input == "special")
                                     return value;
                                 return value;
                             }

                             async ProcessTask<string> FinalBranch()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value;
                             }

                             var (first, second) = await process.ForkJoin(ConditionalBranch(), FinalBranch());
                             return first + second;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("has ambiguous result paths", diagnostic.GetMessage());
        Assert.Equal(14, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_ReportsMissingTypedForkBranchResult()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class MissingBranchResultProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<string> Missing()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                             }

                             async ProcessTask<string> Complete()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value;
                             }

                             var results = await process.ForkJoin(Missing(), Complete());
                             return results.Item2;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("has no portable result expression", diagnostic.GetMessage());
        Assert.Equal("/samples/ApproveCustomerProcess.cs", diagnostic.Location.SourceTree?.FilePath);
    }

    [Fact]
    public void Generator_ReportsPathPartialTypedForkBranchResult()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class PartialBranchResultProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<string> Partial()
                             {
                                 var first = await process.Effect<string>(Request, Completed, input);
                                 if (input == "return")
                                     return first;
                                 var second = await process.Effect<string>(Request, Completed, input);
                             }

                             async ProcessTask<string> Complete()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value;
                             }

                             var results = await process.ForkJoin(Partial(), Complete());
                             return results.Item2;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("has a path-partial result", diagnostic.GetMessage());
        Assert.Equal(18, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_ReportsRuntimeDependentForkCapacityAtTheAnnotation()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidCapacityProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<string> First()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value;
                             }

                             async ProcessTask<string> Second()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value;
                             }

                             var (first, second) = await process.ForkJoin(
                                 process.Branch(First(), capacityDomain: input),
                                 Second(),
                                 admission: ProcessAdmission.Bounded(
                                     maximumParallelism: 1,
                                     capacityDomains: [ProcessCapacity.Domain("fixed", maximumParallelism: 1)]));
                             return first + second;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("capacity domains", diagnostic.GetMessage());
        Assert.Equal(27, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_ReportsUnusedNonPortableTypedBranchResult()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidBranchResultProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<string> First()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value.ToUpperInvariant();
                             }

                             async ProcessTask<string> Second()
                             {
                                 var value = await process.Effect<string>(Request, Completed, input);
                                 return value;
                             }

                             var ignored = await process.ForkJoin(First(), Second());
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC004");

        Assert.Contains("branch 'First' result is not portable", diagnostic.GetMessage());
        Assert.Contains("ToUpperInvariant", diagnostic.GetMessage());
        Assert.Equal(17, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_LowersDurableTimerRequestAndAwaitMatchWithoutCallbacks()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class DurableWaitProcess
                     {
                         private static RequestContractReference Outbound => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");
                         private static SignalContractReference Signal => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             await process.Timer(input.DueAt);

                             async ProcessTask CompletedBranch(string value) { }
                             await process.Effect(
                                 Outbound,
                                 input.Value,
                                 [process.Outcome<string>(Completed, CompletedBranch)]);

                             async ProcessTask Signalled(string value) { }
                             async ProcessTask TimedOut() { }
                             await process.AwaitMatch(
                                 clauses:
                                 [
                                     process.Signal<string>(Signal, Signalled, priority: 10, when: value => value == input.Value),
                                     process.Deadline(input.DueAt, TimedOut)
                                 ],
                                 arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                                 lateInput: ProcessAwaitInputDisposition.Observe,
                                 staleInput: ProcessAwaitInputDisposition.Reject,
                                 duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
                                 missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
                                 retentionHorizon: TimeSpan.FromDays(7));
                             return input.Value;
                         }
                     }

                     public sealed record Input(DateTimeOffset DueAt, string Value);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.Timer", generated);
        Assert.Contains("__builder.Request", generated);
        Assert.Contains("__builder.AwaitMatch", generated);
        Assert.Contains("__builder.AwaitInteractionClause", generated);
        Assert.Contains("__builder.AwaitTimerClause", generated);
        Assert.DoesNotContain("CompletedBranch(", generated);
        Assert.DoesNotContain("Signalled(", generated);
        Assert.DoesNotContain("TimedOut(", generated);
        Assert.DoesNotContain("System.Func", generated);
        Assert.DoesNotContain("callback", generated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generator_FusesTypedAwaitMatchSwitchIntoCanonicalClauseContinuations()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     public abstract record ReviewOutcome;
                     public sealed record ReviewSubmitted(string Decision) : ReviewOutcome;
                     public sealed record ReviewTimedOut : ReviewOutcome;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ReviewProcess
                     {
                         private static SignalContractReference Submitted => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             var review = await process.AwaitMatch<ReviewOutcome>(
                                 clauses:
                                 [
                                     process.Signal<ReviewSubmitted>(Submitted, priority: 10),
                                     process.Deadline<ReviewTimedOut>(input.DueAt)
                                 ],
                                 arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                                 lateInput: ProcessAwaitInputDisposition.Observe,
                                 staleInput: ProcessAwaitInputDisposition.Reject,
                                 duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
                                 missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
                                 retentionHorizon: TimeSpan.FromDays(7));
                             switch (review)
                             {
                                 case ReviewSubmitted { Decision: var decision }:
                                     return decision;
                                 case ReviewTimedOut _:
                                     return input.Fallback;
                             }
                             return process.Unreachable<string>();
                         }
                     }

                     public sealed record Input(DateTimeOffset DueAt, string Fallback);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.AwaitMatch", generated);
        Assert.Contains("__builder.AwaitInteractionClause", generated);
        Assert.Contains("__builder.AwaitTimerClause", generated);
        Assert.Contains("__builder.Output<global::Sample.ReviewSubmitted>", generated);
        Assert.DoesNotContain("ReviewOutcome", generated);
        Assert.DoesNotContain("ValueTuple", generated);
        Assert.DoesNotContain("callback", generated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generator_RejectsNonExhaustiveTypedAwaitMatchAtTheSwitch()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     public abstract record ReviewOutcome;
                     public sealed record ReviewSubmitted(string Decision) : ReviewOutcome;
                     public sealed record ReviewTimedOut : ReviewOutcome;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ReviewProcess
                     {
                         private static SignalContractReference Submitted => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             var review = await process.AwaitMatch<ReviewOutcome>(
                                 clauses:
                                 [
                                     process.Signal<ReviewSubmitted>(Submitted),
                                     process.Deadline<ReviewTimedOut>(input.DueAt)
                                 ],
                                 arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                                 lateInput: ProcessAwaitInputDisposition.Observe,
                                 staleInput: ProcessAwaitInputDisposition.Reject,
                                 duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
                                 missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
                                 retentionHorizon: TimeSpan.FromDays(7));
                             switch (review)
                             {
                                 case ReviewSubmitted submitted:
                                     return submitted.Decision;
                             }
                             return input.Fallback;
                         }
                     }

                     public sealed record Input(DateTimeOffset DueAt, string Fallback);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("typed AwaitMatch switch is not exhaustive", diagnostic.GetMessage());
        Assert.Contains("ReviewTimedOut", diagnostic.GetMessage());
        Assert.Equal(31, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_FusesTypedEffectSwitchIntoCanonicalRequestOutcomes()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     public sealed record Submission(string DatasetId);
                     public sealed record AcceptedPayload(string SubmissionId);
                     public sealed record FailurePayload(string Reason);
                     public abstract record SubmissionOutcome;
                     public sealed record Accepted(AcceptedPayload Payload) : SubmissionOutcome;
                     public sealed record Rejected(FailurePayload Payload) : SubmissionOutcome;
                     public sealed record SubmissionCases(
                         RequestProtocolCase<Accepted, AcceptedPayload> Accepted,
                         RequestProtocolCase<Rejected, FailurePayload> Rejected);

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class SubmissionProcess
                     {
                         private static RequestProtocol<Submission, SubmissionOutcome, SubmissionCases> Protocol => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Submission input)
                         {
                             var outcome = await process.Effect(Protocol, input);
                             switch (outcome)
                             {
                                 case Accepted(var accepted):
                                     return accepted.SubmissionId;
                                 case Rejected(var failure):
                                     return failure.Reason;
                             }
                             return process.Unreachable<string>();
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.Request", generated);
        Assert.Contains("CaseFor<global::Sample.Accepted>().Id", generated);
        Assert.Contains("CaseFor<global::Sample.Rejected>().Id", generated);
        Assert.Contains("Output<global::Sample.AcceptedPayload>", generated);
        Assert.Contains("Output<global::Sample.FailurePayload>", generated);
        Assert.Contains("NodeForRequestOutcome", generated);
        Assert.DoesNotContain("SubmissionOutcome", generated);
        Assert.DoesNotContain("callback", generated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generator_RejectsNonExhaustiveTypedEffectAtTheSwitch()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     public sealed record Submission(string DatasetId);
                     public sealed record AcceptedPayload(string SubmissionId);
                     public sealed record FailurePayload(string Reason);
                     public abstract record SubmissionOutcome;
                     public sealed record Accepted(AcceptedPayload Payload) : SubmissionOutcome;
                     public sealed record Rejected(FailurePayload Payload) : SubmissionOutcome;
                     public sealed record SubmissionCases(
                         RequestProtocolCase<Accepted, AcceptedPayload> Accepted,
                         RequestProtocolCase<Rejected, FailurePayload> Rejected);

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class SubmissionProcess
                     {
                         private static RequestProtocol<Submission, SubmissionOutcome, SubmissionCases> Protocol => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Submission input)
                         {
                             var outcome = await process.Effect(Protocol, input);
                             switch (outcome)
                             {
                                 case Accepted(var accepted):
                                     return accepted.SubmissionId;
                             }
                             return process.Unreachable<string>();
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("typed Effect switch is not exhaustive", diagnostic.GetMessage());
        Assert.Contains("Rejected", diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_RejectsDuplicateTypedEffectCaseAtTheSecondCase()
    {
        var compilation = CreateCompilation(TypedEffectSource("""
            case Accepted(var accepted):
                return accepted.SubmissionId;
            case Accepted(var duplicate):
                return duplicate.SubmissionId;
            case Rejected(var failure):
                return failure.Reason;
            """));
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("Accepted", diagnostic.GetMessage());
        Assert.Contains("handled more than once", diagnostic.GetMessage());
        Assert.Contains("case Accepted(var duplicate)", diagnostic.Location.SourceTree!.GetText()
            .GetSubText(diagnostic.Location.SourceSpan).ToString());
    }

    [Fact]
    public void Generator_RejectsDefaultFallbackOnOtherwiseExhaustiveTypedEffect()
    {
        var compilation = CreateCompilation(TypedEffectSource("""
            case Accepted(var accepted):
                return accepted.SubmissionId;
            case Rejected(var failure):
                return failure.Reason;
            default:
                return input.DatasetId;
            """));
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("unguarded type case", diagnostic.GetMessage());
        Assert.Contains("default", diagnostic.Location.SourceTree!.GetText()
            .GetSubText(diagnostic.Location.SourceSpan).ToString());
    }

    [Fact]
    public void Generator_RejectsCallbackLambdaForDurableOutcomeAtItsSource()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class CallbackProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             await process.Effect(
                                 Request,
                                 input,
                                 [process.Outcome<string>(Completed, async value => { })]);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("must name a unique local async ProcessTask function", diagnostic.GetMessage());
        Assert.Equal(17, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsPartialJoinTupleDeconstructionAtItsSource()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class PartialJoinProcess
                     {
                         private static ExecutionDefinitionReference Query => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask<string> First()
                             {
                                 var value = await process.Query<string>(Query, input);
                                 return value;
                             }
                             async ProcessTask<string> Second()
                             {
                                 var value = await process.Query<string>(Query, input);
                                 return value;
                             }
                             var (first, second) = await process.ForkAny(
                                 branches: [First(), Second()],
                                 policy: ProcessJoin.Any(ProcessJoinCancellationPolicy.CancelRemaining));
                             return first + second;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("cannot be deconstructed", diagnostic.GetMessage());
        Assert.Equal(24, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsHostTaskRaceAtItsSource()
    {
        var source = """
                     using System.Threading.Tasks;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class HostRaceProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             await Task.WhenAny(Task.CompletedTask, Task.CompletedTask);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("ProcessContext", diagnostic.GetMessage());
        Assert.Equal(11, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_LowersChildAndBoundedPartitionWithoutCallbacksOrEnumerationState()
    {
        var source = """
                     using System;
                     using System.Collections.Immutable;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ChildWorkProcess
                     {
                         private static ExecutionDefinitionReference Child => null!;
                         private static ExecutionDefinitionReference Observe => null!;
                         private static RequestContractReference Request => null!;
                         private static ProcessChildOutcomeMapping Mapping => new(
                             new("completed"), new("failed"), new("cancelled"), new("terminated"));

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             async ProcessTask Completed(string value)
                             {
                                 var observed = await process.Query<string>(Observe, value);
                             }
                             async ProcessTask Failed(string value) { }
                             async ProcessTask Cancelled(string value) { }
                             async ProcessTask Terminated(string value) { }
                             await process.InvokeProcess(
                                 process: Child,
                                 contract: Request,
                                 outcomeMapping: Mapping,
                                 input: input.Value,
                                 purpose: ProcessChildPurpose.Work,
                                 cancellation: ProcessChildCancellationPolicy.Propagate,
                                 outcomes:
                                 [
                                     process.Outcome<string>(Mapping.Completed, Completed),
                                     process.Outcome<string>(Mapping.Failed, Failed),
                                     process.Outcome<string>(Mapping.Cancelled, Cancelled),
                                     process.Outcome<string>(Mapping.Terminated, Terminated)
                                 ]);

                             async ProcessTask PartitionFailed() { }
                             await process.ForEachPartition<string, string>(
                                 partitions: input.Partitions,
                                 progressIdentity: partition => partition,
                                 process: Child,
                                 contract: Request,
                                 outcomeMapping: Mapping,
                                 childInput: partition => partition + ":" + input.Value,
                                 limits: new ProcessWorkLimits(
                                     maximumItems: 10,
                                     maximumStartsPerActivation: 2,
                                     maximumParallelism: 2),
                                 failure: ProcessPartitionFailurePolicy.FailFast,
                                 capacityIdentity: partition => "target/a",
                                 capacityDomains: [new("target/a", maximumParallelism: 1)],
                                 cancellation: ProcessChildCancellationPolicy.Propagate,
                                 failed: PartitionFailed);
                             return input.Value;
                         }
                     }

                     public sealed record Input(string[] Partitions, string Value);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.InvokeProcess", generated);
        Assert.Contains("__builder.ForEachPartition", generated);
        Assert.Contains("maximumStartsPerActivation: 2", generated);
        Assert.Contains("role: \"partition\"", generated);
        Assert.DoesNotContain("Completed(", generated);
        Assert.DoesNotContain("PartitionFailed(", generated);
        Assert.DoesNotContain("System.Func", generated);
        Assert.DoesNotContain("IEnumerable", generated);
    }

    [Fact]
    public void Generator_LowersTypedChildProtocolAndSemanticHandlersToExistingCanonicalNode()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.Execution;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedChildProcess
                     {
                         private static ProcessInvocationProtocol<string, Result> Protocol => null!;

                         private static async ProcessTask<Result> Run(ProcessContext process, string input)
                         {
                             await process.InvokeProcess(
                                 protocol: Protocol,
                                 input: input,
                                 purpose: ProcessChildPurpose.Work,
                                 cancellation: ProcessChildCancellationPolicy.Propagate,
                                 completed: Completed,
                                 failed: Failed,
                                 cancelled: Cancelled,
                                 terminated: Terminated,
                                 id: new("invoke-child"));
                             return new Result(input);

                             async ProcessTask Completed(Result result) { }
                             async ProcessTask Failed(ProcessChildFailure failure) { }
                             async ProcessTask Cancelled() { }
                             async ProcessTask Terminated() { }
                         }
                     }

                     public sealed record Result(string Value);
                     """;

        var compilation = CreateCompilation(source);
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();
        Assert.Contains("__builder.InvokeProcess", generated);
        Assert.Equal(1, Count(generated, "var __protocol_"));
        Assert.Equal(1, Count(generated, "(Protocol)"));
        Assert.Contains(".Process.Reference", generated);
        Assert.Contains(".Request", generated);
        Assert.Contains(".OutcomeMapping.Completed", generated);
        Assert.Contains(".OutcomeMapping.Terminated", generated);
        Assert.DoesNotContain("Completed(", generated);
    }

    [Fact]
    public void Generator_LowersSharedTypedChildHandlersPerInvocationOccurrence()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.Execution;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class SharedTypedChildHandlersProcess
                     {
                         private static ProcessInvocationProtocol<string, Result> Protocol => null!;

                         private static async ProcessTask<Result> Run(ProcessContext process, Input input)
                         {
                             if (input.UseFirst)
                             {
                                 await process.InvokeProcess(
                                     protocol: Protocol,
                                     input: input.Value,
                                     purpose: ProcessChildPurpose.Reconciliation,
                                     cancellation: ProcessChildCancellationPolicy.Propagate,
                                     completed: Completed,
                                     failed: Failed,
                                     cancelled: Cancelled,
                                     terminated: Terminated,
                                     id: new("invoke-first"));
                             }
                             else
                             {
                                 await process.InvokeProcess(
                                     protocol: Protocol,
                                     input: input.Value,
                                     purpose: ProcessChildPurpose.Reconciliation,
                                     cancellation: ProcessChildCancellationPolicy.Propagate,
                                     completed: Completed,
                                     failed: Failed,
                                     cancelled: Cancelled,
                                     terminated: Terminated,
                                     id: new("invoke-second"));
                             }

                             return process.Unreachable<Result>();

                             async ProcessTask Completed(Result result)
                             {
                                 var completed = result;
                                 await process.Succeed(completed);
                             }

                             async ProcessTask Failed(ProcessChildFailure failure)
                             {
                                 var rejected = new Result("failed");
                                 await process.Terminate(rejected);
                             }

                             async ProcessTask Cancelled()
                             {
                                 await process.Terminate(new Result("cancelled"));
                             }

                             async ProcessTask Terminated()
                             {
                                 await process.Terminate(new Result("terminated"));
                             }
                         }
                     }

                     public sealed record Input(string Value, bool UseFirst);
                     public sealed record Result(string Value);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Diagnostics);
        Assert.All(runResult.Results, static result => Assert.Null(result.Exception));
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Equal(2, Count(generated, "__builder.InvokeProcess"));
        Assert.Equal(4, Count(generated, "__builder.Output<"));
        Assert.All(
            Enumerable.Range(0, 4),
            index => Assert.Contains($"output: __authored_output_{index}", generated));
        Assert.DoesNotContain("Completed(", generated);

        var replay = RunGenerator(CreateCompilation(source), out _);
        var replayed = Assert.Single(replay.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();
        Assert.Equal(generated, replayed);
    }

    [Fact]
    public void Generator_LowersSharedTypedChildHandlersAcrossExhaustiveEffectCases()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.Execution;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     public sealed record Submission(string DatasetId);
                     public sealed record AcceptedPayload(string Value);
                     public sealed record RejectedPayload(string Value);
                     public abstract record SubmissionOutcome;
                     public sealed record Accepted(AcceptedPayload Payload) : SubmissionOutcome;
                     public sealed record Rejected(RejectedPayload Payload) : SubmissionOutcome;
                     public sealed record SubmissionCases(
                         RequestProtocolCase<Accepted, AcceptedPayload> Accepted,
                         RequestProtocolCase<Rejected, RejectedPayload> Rejected);

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class SharedEffectChildHandlersProcess
                     {
                         private static RequestProtocol<Submission, SubmissionOutcome, SubmissionCases> Provider => null!;
                         private static ProcessInvocationProtocol<string, string> Child => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, Submission input)
                         {
                             var outcome = await process.Effect(Provider, input, id: new("provider"));
                             switch (outcome)
                             {
                                 case Accepted(var accepted):
                                     await process.InvokeProcess(
                                         protocol: Child,
                                         input: accepted.Value,
                                         purpose: ProcessChildPurpose.Reconciliation,
                                         cancellation: ProcessChildCancellationPolicy.Propagate,
                                         completed: Completed,
                                         failed: Failed,
                                         cancelled: Cancelled,
                                         terminated: Terminated,
                                         id: new("accepted-child"));
                                     break;

                                 case Rejected(var rejected):
                                     await process.InvokeProcess(
                                         protocol: Child,
                                         input: rejected.Value,
                                         purpose: ProcessChildPurpose.Reconciliation,
                                         cancellation: ProcessChildCancellationPolicy.Propagate,
                                         completed: Completed,
                                         failed: Failed,
                                         cancelled: Cancelled,
                                         terminated: Terminated,
                                         id: new("rejected-child"));
                                     break;
                             }

                             return process.Unreachable<string>();

                             async ProcessTask Completed(string result)
                             {
                                 var completed = result;
                                 await process.Succeed(completed);
                             }

                             async ProcessTask Failed(ProcessChildFailure failure)
                             {
                                 var rejected = "failed";
                                 await process.Terminate(rejected);
                             }

                             async ProcessTask Cancelled()
                             {
                                 await process.Terminate("cancelled");
                             }

                             async ProcessTask Terminated()
                             {
                                 await process.Terminate("terminated");
                             }
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Diagnostics);
        Assert.All(runResult.Results, static result => Assert.Null(result.Exception));
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Equal(2, Count(generated, "__builder.InvokeProcess"));
        Assert.Equal(6, Count(generated, "__builder.Output<"));
        Assert.Contains("CaseFor<global::Sample.Accepted>().Id", generated);
        Assert.Contains("CaseFor<global::Sample.Rejected>().Id", generated);
    }

    [Fact]
    public void Generator_RejectsDuplicateTypedChildSemanticHandlers()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.Execution;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedChildProcess
                     {
                         private static ProcessInvocationProtocol<string, string> Protocol => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Completed(string result) { }
                             async ProcessTask Failed(ProcessChildFailure failure) { }
                             async ProcessTask Cancelled() { }
                             await process.InvokeProcess(
                                 Protocol,
                                 input,
                                 ProcessChildPurpose.Work,
                                 ProcessChildCancellationPolicy.Propagate,
                                 Completed,
                                 Failed,
                                 Cancelled,
                                 Cancelled);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("unique local", diagnostic.GetMessage());
    }

    [Fact]
    public void CSharpRejectsTypedChildProtocolInputMismatch()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedChildProcess
                     {
                         private static ProcessInvocationProtocol<Input, string> Protocol => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Branch(string result) { }
                             await process.InvokeProcess(
                                 Protocol,
                                 input,
                                 ProcessChildPurpose.Work,
                                 ProcessChildCancellationPolicy.Propagate,
                                 Branch,
                                 Branch,
                                 Branch,
                                 Branch);
                             return input;
                         }
                     }

                     public sealed record Input(string Value);
                     """;

        var compilation = CreateCompilation(source);
        RunGenerator(compilation, out var outputCompilation);

        Assert.Contains(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0411"
                || diagnostic.Id == "CS1503");
    }

    [Fact]
    public void Generator_LowersTypedRelationHandleToItsExactReference()
    {
        var source = """
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Relations.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedRelationProcess
                     {
                         private static Relation<Input, Result> Normalize => null!;

                         private static async ProcessTask<Result> Run(ProcessContext process, Input input)
                         {
                             var result = await process.Query(Normalize, input);
                             return result;
                         }
                     }

                     public sealed record Input(string Value);
                     public sealed record Result(string Value);
                     """;

        var compilation = CreateCompilation(source);
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Contains("__builder.EvaluateRelation", generated);
        Assert.Contains("(Normalize).Reference", generated);
    }

    [Fact]
    public void CSharpRejectsTypedRelationInputMismatch()
    {
        var source = """
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Relations.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedRelationProcess
                     {
                         private static Relation<ExpectedInput, Result> Normalize => null!;

                         private static async ProcessTask<Result> Run(ProcessContext process, ActualInput input)
                         {
                             var result = await process.Query(Normalize, input);
                             return result;
                         }
                     }

                     public sealed record ExpectedInput(string Value);
                     public sealed record ActualInput(string Value);
                     public sealed record Result(string Value);
                     """;

        var compilation = CreateCompilation(source);
        RunGenerator(compilation, out var outputCompilation);

        Assert.Contains(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0411"
                || diagnostic.Id == "CS1503");
    }

    [Fact]
    public void Generator_LowersTypedHostedQueryHandleToItsExactReference()
    {
        var source = """
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Relations.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedHostedQueryProcess
                     {
                         private static HostedQuery<Input, Result> Acquire => null!;

                         private static async ProcessTask<Result> Run(ProcessContext process, Input input)
                         {
                             var result = await process.Read(Acquire, input);
                             return result;
                         }
                     }

                     public sealed record Input(string Value);
                     public sealed record Result(string Value);
                     """;

        var compilation = CreateCompilation(source);
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Contains("__builder.EvaluateRelation", generated);
        Assert.Contains("(Acquire).Reference", generated);
    }

    [Fact]
    public void CSharpRejectsTypedHostedQueryInputMismatch()
    {
        var source = """
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Relations.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedHostedQueryProcess
                     {
                         private static HostedQuery<ExpectedInput, Result> Acquire => null!;

                         private static async ProcessTask<Result> Run(ProcessContext process, ActualInput input)
                         {
                             var result = await process.Read(Acquire, input);
                             return result;
                         }
                     }

                     public sealed record ExpectedInput(string Value);
                     public sealed record ActualInput(string Value);
                     public sealed record Result(string Value);
                     """;

        var compilation = CreateCompilation(source);
        RunGenerator(compilation, out var outputCompilation);

        Assert.Contains(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0411"
                || diagnostic.Id == "CS1503");
    }

    [Fact]
    public void Generator_LowersTypedTransitionHandleToItsExactReference()
    {
        var source = """
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedTransitionProcess
                     {
                         private static Transition<ReviewEntity, TransitionInput, TransitionOutcome> Approve => null!;

                         private static async ProcessTask<TransitionOutcome> Run(ProcessContext process, TransitionInput input)
                         {
                             var outcome = await process.Transition(Approve, "review-1", input);
                             await process.Transition(Approve, "review-2", input);
                             return outcome;
                         }
                     }

                     public abstract class ReviewEntity : Entity
                     {
                     }
                     public sealed record TransitionInput(string Reason);
                     public sealed record TransitionOutcome(string Status);
                     """;

        var compilation = CreateCompilation(source);
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Equal(2, Count(generated, "__builder.InvokeTransition"));
        Assert.Equal(2, Count(generated, "(Approve).Reference"));
    }

    [Fact]
    public void CSharpRejectsTypedTransitionInputMismatch()
    {
        var source = """
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedTransitionProcess
                     {
                         private static Transition<ReviewEntity, ExpectedInput, TransitionOutcome> Approve => null!;

                         private static async ProcessTask<TransitionOutcome> Run(ProcessContext process, ActualInput input)
                         {
                             var outcome = await process.Transition(Approve, "review-1", input);
                             return outcome;
                         }
                     }

                     public abstract class ReviewEntity : Entity
                     {
                     }
                     public sealed record ExpectedInput(string Reason);
                     public sealed record ActualInput(string Reason);
                     public sealed record TransitionOutcome(string Status);
                     """;

        var compilation = CreateCompilation(source);
        RunGenerator(compilation, out var outputCompilation);

        Assert.Contains(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0411"
                || diagnostic.Id == "CS1503");
    }

    [Fact]
    public void CSharpRequiresEveryTypedChildSemanticHandler()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.Execution;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class TypedChildProcess
                     {
                         private static ProcessInvocationProtocol<string, string> Protocol => null!;

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Completed(string result) { }
                             async ProcessTask Failed(ProcessChildFailure failure) { }
                             async ProcessTask Cancelled() { }
                             await process.InvokeProcess(
                                 protocol: Protocol,
                                 input: input,
                                 purpose: ProcessChildPurpose.Work,
                                 cancellation: ProcessChildCancellationPolicy.Propagate,
                                 completed: Completed,
                                 failed: Failed,
                                 cancelled: Cancelled);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);

        Assert.Contains(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS7036");
    }

    [Fact]
    public void Generator_RejectsPartitionProjectionMethodGroupAtItsSource()
    {
        var source = """
                     using System;
                     using System.Collections.Immutable;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ChildWorkProcess
                     {
                         private static ExecutionDefinitionReference Child => null!;
                         private static RequestContractReference Request => null!;
                         private static ProcessChildOutcomeMapping Mapping => new(
                             new("completed"), new("failed"), new("cancelled"), new("terminated"));
                         private static string Progress(string partition) => partition;

                         private static async ProcessTask<string> Run(ProcessContext process, string[] input)
                         {
                             async ProcessTask Failed() { }
                             await process.ForEachPartition<string, string>(
                                 partitions: input,
                                 progressIdentity: Progress,
                                 process: Child,
                                 contract: Request,
                                 outcomeMapping: Mapping,
                                 childInput: partition => partition,
                                 limits: new ProcessWorkLimits(10, 2, 2),
                                 failure: ProcessPartitionFailurePolicy.FailFast,
                                 capacityIdentity: null,
                                 capacityDomains: [],
                                 cancellation: ProcessChildCancellationPolicy.Propagate,
                                 failed: Failed);
                             return "done";
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("inline pure lambda", diagnostic.GetMessage());
        Assert.Equal(23, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsIncompleteChildTerminalMappingAtItsSource()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class ChildProcess
                     {
                         private static ExecutionDefinitionReference Child => null!;
                         private static RequestContractReference Request => null!;
                         private static ProcessChildOutcomeMapping Mapping => new(
                             new("completed"), new("failed"), new("cancelled"), new("terminated"));

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Completed(string value) { }
                             async ProcessTask Failed(string value) { }
                             async ProcessTask Cancelled(string value) { }
                             await process.InvokeProcess(
                                 Child,
                                 Request,
                                 Mapping,
                                 input,
                                 ProcessChildPurpose.Work,
                                 ProcessChildCancellationPolicy.Propagate,
                                 [
                                     process.Outcome<string>(Mapping.Completed, Completed),
                                     process.Outcome<string>(Mapping.Failed, Failed),
                                     process.Outcome<string>(Mapping.Cancelled, Cancelled)
                                 ]);
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("exactly one branch", diagnostic.GetMessage());
        Assert.Equal(20, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsRuntimeDerivedPartitionCapacityPolicyAtItsSource()
    {
        var source = """
                     using System.Collections.Immutable;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class PartitionProcess
                     {
                         private static ExecutionDefinitionReference Child => null!;
                         private static RequestContractReference Request => null!;
                         private static ProcessChildOutcomeMapping Mapping => new(
                             new("completed"), new("failed"), new("cancelled"), new("terminated"));

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             async ProcessTask Failed() { }
                             await process.ForEachPartition<string, string>(
                                 partitions: input.Partitions,
                                 progressIdentity: partition => partition,
                                 process: Child,
                                 contract: Request,
                                 outcomeMapping: Mapping,
                                 childInput: partition => partition,
                                 limits: new ProcessWorkLimits(10, 2, 2),
                                 failure: ProcessPartitionFailurePolicy.FailFast,
                                 capacityIdentity: partition => "target/a",
                                 capacityDomains: input.CapacityDomains,
                                 cancellation: ProcessChildCancellationPolicy.Propagate,
                                 failed: Failed);
                             return input.Value;
                         }
                     }

                     public sealed record Input(
                         string[] Partitions,
                         ImmutableArray<ProcessCapacityDomainLimit> CapacityDomains,
                         string Value);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("cannot depend on runtime bindings", diagnostic.GetMessage());
        Assert.Equal(29, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_LowersBoundedRecurrenceAndCompensationWithoutHostLoopState()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class PollingProcess
                     {
                         private static RequestContractReference PollRequest => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");
                         private static ExecutionDefinitionReference Compensation => null!;
                         private static RequestContractReference CompensationRequest => null!;
                         private static ProcessChildOutcomeMapping Mapping => new(
                             new("completed"), new("failed"), new("cancelled"), new("terminated"));

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             async ProcessTask<PollResult> Poll()
                             {
                                 await process.Timer(input.DueAt);
                                 var observation = await process.Effect<string>(PollRequest, Completed, input.Value);
                                 return new(observation, observation);
                             }
                             async ProcessTask Compensated(string value) { }
                             async ProcessTask CompensationFailed(string value) { }
                             async ProcessTask CompensationCancelled(string value) { }
                             async ProcessTask CompensationTerminated(string value) { }
                             async ProcessTask Exhausted()
                             {
                                 await process.InvokeProcess(
                                     process: Compensation,
                                     contract: CompensationRequest,
                                     outcomeMapping: Mapping,
                                     input: input.Value,
                                     purpose: ProcessChildPurpose.Compensation,
                                     cancellation: ProcessChildCancellationPolicy.Propagate,
                                     outcomes:
                                     [
                                         process.Outcome<string>(Mapping.Completed, Compensated),
                                         process.Outcome<string>(Mapping.Failed, CompensationFailed),
                                         process.Outcome<string>(Mapping.Cancelled, CompensationCancelled),
                                         process.Outcome<string>(Mapping.Terminated, CompensationTerminated)
                                     ]);
                             }
                             async ProcessTask Stalled() { }

                             var final = await process.RepeatAcrossActivation(
                                 occurrence: Poll(),
                                 continueWhen: observation => observation.Status == "pending",
                                 progress: observation => observation.Version,
                                 policy: new ProcessRecurrencePolicy(
                                     maximumOccurrences: 5,
                                     maximumUnchangedProgressOccurrences: 2),
                                 exhausted: Exhausted,
                                 stalled: Stalled);
                             return final.Status;
                         }
                     }

                     public sealed record Input(string Value, DateTimeOffset DueAt);
                     public sealed record PollResult(string Status, string Version);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out var outputCompilation);
        var generated = Assert.Single(runResult.Results.SelectMany(static result => result.GeneratedSources))
            .SourceText
            .ToString();

        Assert.Empty(runResult.Results.SelectMany(static result => result.Diagnostics));
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.RepeatAcrossActivation", generated);
        Assert.Contains("__builder.Timer", generated);
        Assert.Contains("__builder.InvokeProcess", generated);
        Assert.Contains("ProcessChildPurpose.Compensation", generated);
        Assert.Contains("maximumOccurrences: 5", generated);
        Assert.Contains("role: \"repeat\"", generated);
        Assert.DoesNotContain("System.Func", generated);
        Assert.DoesNotContain("ProcessProjection", generated);
    }

    [Fact]
    public void Generator_RejectsRuntimeDerivedRecurrencePolicyAtItsSource()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class PollingProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, Input input)
                         {
                             async ProcessTask<string> Poll()
                             {
                                 var observation = await process.Effect<string>(Request, Completed, input.Value);
                                 return observation;
                             }
                             async ProcessTask Exhausted() { }
                             async ProcessTask Stalled() { }
                             var final = await process.RepeatAcrossActivation(
                                 occurrence: Poll(),
                                 continueWhen: observation => observation == "pending",
                                 progress: observation => observation,
                                 policy: input.Policy,
                                 exhausted: Exhausted,
                                 stalled: Stalled);
                             return final;
                         }
                     }

                     public sealed record Input(string Value, ProcessRecurrencePolicy Policy);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("cannot depend on runtime bindings", diagnostic.GetMessage());
        Assert.Equal(26, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsHostForeachEnumerationAtItsSource()
    {
        var source = """
                     using System;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class HostEnumerationProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string[] input)
                         {
                             foreach (var partition in input)
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch);
                             }
                             return "done";
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("host loops are not supported", diagnostic.GetMessage());
        Assert.Equal(11, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsMutableRecurrenceStateAtItsSource()
    {
        var source = """
                     using System;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class MutableProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             var progress = input;
                             progress = input + "/next";
                             await process.Timer(DateTimeOffset.UnixEpoch);
                             return progress;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("mutable Process-local state is not supported", diagnostic.GetMessage());
        Assert.Equal(12, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsRecursiveProcessLocalCallsAtTheirSource()
    {
        var source = """
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;
                     using Cohesive.Processes.IR;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class RecursiveProcess
                     {
                         private static RequestContractReference Request => null!;
                         private static RequestTerminalOutcomeId Completed => new("completed");

                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Recurse()
                             {
                                 await Recurse();
                             }
                             async ProcessTask<string> Poll()
                             {
                                 await Recurse();
                                 var observation = await process.Effect<string>(Request, Completed, input);
                                 return observation;
                             }
                             async ProcessTask Exhausted() { }
                             async ProcessTask Stalled() { }
                             var final = await process.RepeatAcrossActivation(
                                 Poll(),
                                 observation => observation == "pending",
                                 observation => observation,
                                 new ProcessRecurrencePolicy(3, 1),
                                 Exhausted,
                                 Stalled);
                             return final;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("recursive local Process calls are not supported", diagnostic.GetMessage());
        Assert.Equal(21, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public void Generator_RejectsExecutableFlowAfterTrailingBareBranchReturn()
    {
        var source = """
                     using System;
                     using Cohesive.Execution;
                     using Cohesive.Processes.Authoring;

                     namespace Sample;

                     [GenerateProcessDefinition(nameof(Run))]
                     public static partial class InvalidTrailingReturnProcess
                     {
                         private static async ProcessTask<string> Run(ProcessContext process, string input)
                         {
                             async ProcessTask Selected()
                             {
                                 await process.Timer(DateTimeOffset.UnixEpoch);
                                 return;
                                 await process.Timer(DateTimeOffset.UnixEpoch.AddMinutes(1));
                             }
                             async ProcessTask Fallback()
                             {
                             }
                             await process.Choice(
                                 selection: CaseSelection.OrderedFirstMatch,
                                 completeness: BranchCompleteness.Fallback,
                                 cases: [process.When(input == "selected", Selected)],
                                 fallback: Fallback,
                                 id: new("decision/category"));
                             return input;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation, out _);
        var diagnostic = Assert.Single(
            runResult.Results.SelectMany(static result => result.Diagnostics),
            static diagnostic => diagnostic.Id == "COHPC003");

        Assert.Contains("statements after an unconditional return are not supported", diagnostic.GetMessage());
        Assert.Equal(16, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    static GeneratorDriverRunResult RunGenerator(
        CSharpCompilation compilation,
        out Compilation outputCompilation)
    {
        IIncrementalGenerator generator = new ProcessComputationSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out outputCompilation,
            out _);
        return driver.GetRunResult();
    }

    static CSharpCompilation CreateCompilation(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            parseOptions,
            path: "/samples/ApproveCustomerProcess.cs");
        return CSharpCompilation.Create(
            assemblyName: "ProcessComputationCompileTimeTests",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    static string TypedForkSource(int arity)
    {
        var branches = string.Join(
            ", ",
            Enumerable.Range(1, arity).Select(static index => $"Branch{index}()"));
        var functions = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, arity).Select(static index => $$"""
                async ProcessTask<string> Branch{{index}}()
                {
                    var value = await process.Effect<string>(Request, Completed, input);
                    return value;
                }
                """));

        return $$"""
                 using Cohesive.Execution;
                 using Cohesive.Processes.Authoring;

                 namespace Sample;

                 [GenerateProcessDefinition(nameof(Run))]
                 public static partial class TypedForkArity{{arity}}Process
                 {
                     private static RequestContractReference Request => null!;
                     private static RequestTerminalOutcomeId Completed => new("completed");

                     private static async ProcessTask<string> Run(ProcessContext process, string input)
                     {
                         var results = await process.ForkJoin({{branches}});
                         return results.Item{{arity}};

                         {{functions}}
                     }
                 }
                 """;
    }

    static string TypedEffectSource(string switchSections) => $$"""
        using Cohesive.Execution;
        using Cohesive.Processes.Authoring;

        namespace Sample;

        public sealed record Submission(string DatasetId);
        public sealed record AcceptedPayload(string SubmissionId);
        public sealed record FailurePayload(string Reason);
        public abstract record SubmissionOutcome;
        public sealed record Accepted(AcceptedPayload Payload) : SubmissionOutcome;
        public sealed record Rejected(FailurePayload Payload) : SubmissionOutcome;
        public sealed record SubmissionCases(
            RequestProtocolCase<Accepted, AcceptedPayload> Accepted,
            RequestProtocolCase<Rejected, FailurePayload> Rejected);

        [GenerateProcessDefinition(nameof(Run))]
        public static partial class SubmissionProcess
        {
            private static RequestProtocol<Submission, SubmissionOutcome, SubmissionCases> Protocol => null!;

            private static async ProcessTask<string> Run(ProcessContext process, Submission input)
            {
                var outcome = await process.Effect(Protocol, input);
                switch (outcome)
                {
        {{switchSections}}
                }
                return process.Unreachable<string>();
            }
        }
        """;

    static string CancellationFinalizerSource(string declaration, string additionalMembers = "") => $$"""
        using Cohesive.Execution;
        using Cohesive.Processes.Authoring;
        using Cohesive.Processes.IR;

        namespace Sample;

        [GenerateProcessDefinition(nameof(Run))]
        public static partial class CancellableProcess
        {
            private static ProcessInvocationProtocol<ProcessCancellationFinalizationInput<string>, ProcessCancellationAcknowledgement> Protocol => null!;
            {{additionalMembers}}

            private static async ProcessTask<string> Run(ProcessContext process, string input)
            {
                {{declaration}}
                return input;
            }
        }
        """;

    static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic)
            .Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location))
            .Where(static assembly =>
                assembly.GetName().Name is "System.Private.CoreLib"
                    or "System.Runtime"
                    or "netstandard"
                    or "System.Console"
                    or "System.Linq"
                    or "System.Collections"
                    or "System.Collections.Immutable"
                    or "System.ObjectModel"
                    or "System.Runtime.Extensions")
            .Append(typeof(Binder).Assembly)
            .Append(typeof(Cohesive.Execution.ExecutionDefinitionReference).Assembly)
            .Append(typeof(ProcessContext).Assembly)
            .Append(typeof(Cohesive.Relations.Authoring.Relation<,>).Assembly)
            .Append(typeof(Cohesive.Transitions.Authoring.Transition<,,>).Assembly)
            .Append(typeof(ProcessComputationSourceGenerator).Assembly)
            .Distinct()
            .Select(static assembly => MetadataReference.CreateFromFile(assembly.Location));
        return [.. assemblies];
    }

    static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }
}
