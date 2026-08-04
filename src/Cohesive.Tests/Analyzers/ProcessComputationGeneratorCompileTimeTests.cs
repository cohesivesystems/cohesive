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
