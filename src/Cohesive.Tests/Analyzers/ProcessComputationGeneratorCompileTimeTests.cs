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
