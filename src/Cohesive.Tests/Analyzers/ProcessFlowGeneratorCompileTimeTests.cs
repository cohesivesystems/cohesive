using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Analyzers;
using Cohesive.Processes.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Tests.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cohesive.Tests.Analyzers;

/// <summary>
/// Compile-time coverage for async-style process authoring generation.
/// </summary>
public sealed class ProcessFlowGeneratorCompileTimeTests
{
    [Fact]
    public void ProcessFlowGenerator_EmitsExpectedLowering()
    {
        var source = """
                     using System;
                     using Cohesive.Processes.Model;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;
                     [GenerateProcessDefinition(nameof(Build))]
                     public partial class GreetingProcess : IProcessDefinition<string, string>
                     {
                         private async ProcessTask<string> Build(ProcessAuthoringContext<string, string> flow, string name)
                         {
                             var greeting = await flow.Request(new FormatGreetingRequest(name));
                             return flow.Return($"{name}:{greeting}");
                         }
                     }

                     public sealed record FormatGreetingRequest(string Name) : IEffectRequest<string>
                     {
                         public static string RequestName => "FormatGreeting";
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation);
        var generatedText = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static sourceText => sourceText.SourceText.ToString()));

        Assert.Contains("global::Cohesive.Processes.Model.ProcessDefinition? __BuildDefinitionDefaultDefinition;", generatedText);
        Assert.Contains("private global::Cohesive.Processes.Model.ProcessDefinition BuildDefinition(", generatedText);
        Assert.Contains("public global::Cohesive.Processes.Model.TypedProcessDefinition<", generatedText);
        Assert.Contains("Define(string? processName = null)", generatedText);
        Assert.DoesNotContain("public static global::Cohesive.Processes.Model.ProcessDefinition BuildDefinition(", generatedText);
        Assert.DoesNotContain("public static global::Cohesive.Processes.Model.TypedProcessDefinition<", generatedText);
        Assert.Contains("var __normalizedProcessName = string.IsNullOrWhiteSpace(processName) ? null : processName;", generatedText);
        Assert.Contains("const string DEFAULT_PROCESS_NAME = \"Greeting\";", generatedText);
        Assert.Contains("var __builder = new global::Cohesive.Processes.Model.ProcessDefinitionBuilder(processNameOverride ?? DEFAULT_PROCESS_NAME);", generatedText);
        Assert.Contains("__builder.AddEffectRequestNode<", generatedText);
        Assert.Contains("name: \"greeting\"", generatedText);
        Assert.Contains("requestExpression: context => new FormatGreetingRequest(context.RequireParameter<", generatedText);
        Assert.Contains("(\"name\")", generatedText);
        Assert.Contains("resultVariable: \"greeting\"", generatedText);
        Assert.Contains("nextNode: \"end\"", generatedText);
        Assert.Contains("__builder.AddEndNode<", generatedText);
        Assert.Contains("resultExpression: context => $\"{context.RequireParameter<", generatedText);
        Assert.Contains(":{context.RequireVariable<", generatedText);
        Assert.Contains("return new global::Cohesive.Processes.Model.TypedProcessDefinition<", generatedText);
        Assert.Contains("global::Cohesive.Processes.Model.ProcessDefinition __definition = __BuildDefinitionDefaultDefinition ??= BuildDefinition();", generatedText);
        Assert.Contains("return __builder.Build();", generatedText);
    }

    [Fact]
    public void ProcessFlowGenerator_ReportsDiagnostic_WhenContainingTypeIsNotPartial()
    {
        var source = """
                     using Cohesive.Processes.Model;

                     namespace Sample;
                     [GenerateProcessDefinition(nameof(Build))]
                     public static class InvalidProcess
                     {
                         private static async ProcessTask<int> Build(ProcessAuthoringContext<int, int> flow)
                         {
                             var value = await flow.Input();
                             return flow.Return(value);
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation);
        var diagnostics = runResult.Results.SelectMany(static result => result.Diagnostics).ToArray();

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "COHPF001");
    }

    [Fact]
    public void ProcessFlowGenerator_LowersReadQueryComputeAndTransitionSteps()
    {
        var projectionProcess = new CustomerProjectionProcess().Define().Definition;
        Assert.IsType<ExecuteEntityReadNode>(projectionProcess.Nodes["customer"]);
        Assert.IsType<ExecuteEntityQueryNode>(projectionProcess.Nodes["profiles"]);
        Assert.IsType<ComputeValueNode>(projectionProcess.Nodes["profile"]);
        Assert.IsType<ComputeValueNode>(projectionProcess.Nodes["updatedName"]);
        Assert.IsType<ExecuteEntityTransitionNode>(projectionProcess.Nodes["rename"]);

        var batchProcess = new CounterBatchProcess().Define().Definition;
        Assert.IsType<ExecuteEntityTransitionNode>(batchProcess.Nodes["results"]);
    }

    [Fact]
    public void ProcessFlowGenerator_LowersDirectTransitionOverload()
    {
        var source = """
                     using System;
                     using Cohesive.Processes.Model;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;
                     [GenerateProcessDefinition(nameof(Build))]
                     public abstract partial class CounterProcess : IProcessDefinition<string, long>
                     {
                         static readonly CounterEntity Counter = CounterEntity.Instance;

                         private async ProcessTask<long> Build(ProcessAuthoringContext<string, long> flow, string entityId)
                         {
                             var result = await flow.Transition(
                                 entityId: entityId,
                                 transition: Counter.Increment,
                                 input: new CounterEntity.IncrementInput(2));
                             return flow.Return(result.NewVersion);
                         }
                     }

                     public sealed class CounterEntity : Entity<CounterEntity>
                     {
                         public sealed record IncrementInput(int Amount);

                         public CounterEntity()
                         {
                             Id = WriteOnceField<string>(nameof(Id));
                             Value = MutableField<int>(nameof(Value));
                             Increment = Transition<IncrementInput>(nameof(Increment), t => t
                                 .Set(counter => counter.Value, (counter, input) => counter.Value + input.Amount));
                         }

                         public static CounterEntity Instance { get; } = new();

                         public Field<string> Id { get; }
                         public Field<int> Value { get; }
                         public Transition<CounterEntity, IncrementInput> Increment { get; }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation);
        var generatedText = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static sourceText => sourceText.SourceText.ToString()));

        Assert.DoesNotContain(runResult.Results.SelectMany(static result => result.Diagnostics), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.AddEntityTransitionNode(", generatedText);
        Assert.Contains("global::Cohesive.Processes.Model.ProcessEntityTransition.For(entityId:", generatedText);
        Assert.Contains("transition: Counter.Increment", generatedText);
        Assert.Contains("input: new CounterEntity.IncrementInput(2)", generatedText);
    }

    [Fact]
    public void ProcessFlowGenerator_LowersTimerAndPollHelpers()
    {
        var source = """
                     using System;
                     using Cohesive.Processes.Model;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;
                     [GenerateProcessDefinition(nameof(Build))]
                     public abstract partial class PollingProcess : IProcessDefinition<string, PollResult>
                     {
                         private async ProcessTask<PollResult> Build(ProcessAuthoringContext<string, PollResult> flow, string jobId)
                         {
                             var timer = await flow.Timer(TimeSpan.FromSeconds(5));
                             var status = await flow.Poll(
                                 request: new GetStatusRequest(jobId),
                                 isCompleted: static value => value.IsDone,
                                 interval: TimeSpan.FromSeconds(10),
                                 timeout: TimeSpan.FromMinutes(2),
                                 timeoutResult: new StatusResult(true, "timeout"));
                             return flow.Return(new PollResult(timer.Key, status.State));
                         }
                     }

                     public sealed record GetStatusRequest(string JobId) : IEffectRequest<StatusResult>
                     {
                         public static string RequestName => "GetStatus";
                     }

                     public sealed record StatusResult(bool IsDone, string State);
                     public sealed record PollResult(string TimerKey, string State);
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation);
        var generatedText = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static sourceText => sourceText.SourceText.ToString()));

        Assert.DoesNotContain(runResult.Results.SelectMany(static result => result.Diagnostics), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.AddWaitNode(name: \"timer\"", generatedText);
        Assert.Contains("__builder.AddEffectRequestNode<", generatedText);
        Assert.Contains("name: \"status\"", generatedText);
        Assert.Contains("__builder.AddBranchingNode(name: (\"status\") + \"__terminal\"", generatedText);
        Assert.Contains("__builder.AddWaitNode(name: (\"status\") + \"__wait\"", generatedText);
        Assert.Contains("new global::System.Func<", generatedText);
        Assert.Contains("static value => value!.IsDone", generatedText);
    }

    [Fact]
    public void ProcessFlowGenerator_LowersEarlyReturnBranch()
    {
        var source = """
                     using Cohesive.Processes.Model;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;
                     [GenerateProcessDefinition(nameof(Build))]
                     public partial class EarlyReturnProcess : IProcessDefinition<string, string>
                     {
                         private async ProcessTask<string> Build(ProcessAuthoringContext<string, string> flow, string input)
                         {
                             if (input == "skip")
                             {
                                 return flow.Return("skipped");
                             }

                             var continued = await flow.Compute(input + ":continued");
                             return flow.Return(continued);
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation);
        var generatedText = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static sourceText => sourceText.SourceText.ToString()));

        Assert.DoesNotContain(runResult.Results.SelectMany(static result => result.Diagnostics), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.AddBranchingNode(name: \"__branch_0\"", generatedText);
        Assert.Contains("== \"skip\"", generatedText);
        Assert.Contains("\"__return_0\"", generatedText);
        Assert.Contains("__builder.AddEndNode<", generatedText);
        Assert.Contains("name: \"__return_0\"", generatedText);
        Assert.Contains("__builder.AddComputeNode(name: \"continued\"", generatedText);
        Assert.Contains("elseNode: \"continued\"", generatedText);
    }

    [Fact]
    public void ProcessFlowGenerator_LowersIfElseBranchWithSharedContinuation()
    {
        var source = """
                     using Cohesive.Processes.Model;
                     using Cohesive.Transitions.Authoring;

                     namespace Sample;
                     [GenerateProcessDefinition(nameof(Build))]
                     public partial class IfElseProcess : IProcessDefinition<string, string>
                     {
                         private async ProcessTask<string> Build(ProcessAuthoringContext<string, string> flow, string input)
                         {
                             if (input == "left")
                             {
                                 var leftValue = await flow.Compute("left");
                             }
                             else
                             {
                                 var rightValue = await flow.Compute("right");
                             }

                             var finalValue = await flow.Compute(input + ":done");
                             return flow.Return(finalValue);
                         }
                     }
                     """;

        var compilation = CreateCompilation(source);
        var runResult = RunGenerator(compilation);
        var generatedText = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static sourceText => sourceText.SourceText.ToString()));

        Assert.DoesNotContain(runResult.Results.SelectMany(static result => result.Diagnostics), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("__builder.AddBranchingNode(name: \"__branch_0\"", generatedText);
        Assert.Contains("== \"left\"", generatedText);
        Assert.Contains("elseNode: \"rightValue\"", generatedText);
        Assert.Contains("__builder.AddComputeNode(name: \"leftValue\"", generatedText);
        Assert.Contains("resultVariable: \"leftValue\", nextNode: \"finalValue\"", generatedText);
        Assert.Contains("__builder.AddComputeNode(name: \"rightValue\"", generatedText);
        Assert.Contains("resultVariable: \"rightValue\", nextNode: \"finalValue\"", generatedText);
        Assert.Contains("__builder.AddComputeNode(name: \"finalValue\"", generatedText);
        Assert.Contains("nextNode: \"end\"", generatedText);
        Assert.Contains("__builder.SetEntryNode(\"__branch_0\")", generatedText);
    }

    static GeneratorDriverRunResult RunGenerator(CSharpCompilation compilation)
    {
        IIncrementalGenerator generator = new ProcessFlowSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    static CSharpCompilation CreateCompilation(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        return CSharpCompilation.Create(
            assemblyName: "ProcessFlowCompileTimeTests",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: compilationOptions);
    }

    static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var assemblies =
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(static assembly => !assembly.IsDynamic)
                .Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location))
                .Where(static assembly =>
                    assembly.GetName().Name is "System.Private.CoreLib"
                        or "System.Runtime"
                        or "netstandard"
                        or "System.Console"
                        or "System.Linq"
                        or "System.Collections"
                        or "System.ObjectModel"
                        or "System.Runtime.Extensions")
                .Append(typeof(Binder).Assembly)
                .Append(typeof(ProcessFlowSourceGenerator).Assembly)
                .Append(typeof(GenerateProcessDefinitionAttribute).Assembly)
                .Append(typeof(IEffectRequest<>).Assembly)
                .Distinct()
                .Select(static assembly => MetadataReference.CreateFromFile(assembly.Location));

        return [..assemblies];
    }
}
