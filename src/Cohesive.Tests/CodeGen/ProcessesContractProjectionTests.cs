using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Adapters.TypeScript;
using Cohesive.CodeGen;
using Cohesive.CodeGen.Cli;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Contracts;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;

namespace Cohesive.Tests.CodeGen;

public sealed class ProcessesContractProjectionTests
{
    [Fact]
    public void ProcessesContracts_ProjectExactDocumentAndClosedConstructInventories()
    {
        var graph = LoadWireContractGraph();

        Assert.Contains(graph.Shapes, shape =>
            shape.Id.Value.EndsWith(nameof(ProcessDefinition), StringComparison.Ordinal));
        Assert.Contains(graph.Shapes, shape =>
            shape.Id.Value.EndsWith(nameof(ExecutionDefinitionDocument), StringComparison.Ordinal));
        Assert.Contains(graph.Shapes, shape =>
            shape.Id.Value.EndsWith(nameof(ProcessExecutionTraceArtifact), StringComparison.Ordinal));

        var processNodes = AssertUnion(graph, nameof(ProcessNode), ProcessWireNames.NodeDiscriminator);
        var awaitClauses = AssertUnion(
            graph,
            nameof(ProcessAwaitClause),
            ProcessWireNames.AwaitClauseDiscriminator);

        var declaredNodeKinds = typeof(ProcessNode)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(GetStringDiscriminator)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var catalogNodeKinds = ProcessNodeConstructCatalog.DeclaredRequirements
            .Select(static requirement => requirement.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var projectedNodeKinds = processNodes.Cases
            .Select(static unionCase => unionCase.DiscriminatorValue)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(19, declaredNodeKinds.Length);
        Assert.Equal(declaredNodeKinds, catalogNodeKinds);
        Assert.Equal(declaredNodeKinds, projectedNodeKinds);

        var declaredClauseKinds = typeof(ProcessAwaitClause)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(GetStringDiscriminator)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var projectedClauseKinds = awaitClauses.Cases
            .Select(static unionCase => unionCase.DiscriminatorValue)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, declaredClauseKinds.Length);
        Assert.Equal(declaredClauseKinds, projectedClauseKinds);

        var text = EmitProcessesContracts(graph);
        Assert.Contains("export interface ProcessDefinition", text, StringComparison.Ordinal);
        Assert.Contains("nodes: ProcessNode[];", text, StringComparison.Ordinal);
        Assert.Contains("export type ProcessNode =", text, StringComparison.Ordinal);
        Assert.Contains("readonly $node: 'invokeTransition';", text, StringComparison.Ordinal);
        Assert.Contains("readonly $node: 'cancellationFinalizer';", text, StringComparison.Ordinal);
        Assert.Contains("readonly $node: 'return';", text, StringComparison.Ordinal);
        Assert.Contains("export type ProcessAwaitClause =", text, StringComparison.Ordinal);
        Assert.Contains("readonly $clause: 'interaction';", text, StringComparison.Ordinal);
        Assert.Contains("readonly $clause: 'timer';", text, StringComparison.Ordinal);
        Assert.Contains("export const canonicalProcessNodeKinds = [", text, StringComparison.Ordinal);
        Assert.Contains(
            "] as const satisfies readonly ProcessNode[\"$node\"][];",
            text,
            StringComparison.Ordinal);
        Assert.Contains("export const canonicalProcessAwaitClauseKinds = [", text, StringComparison.Ordinal);
        Assert.Contains(
            "] as const satisfies readonly ProcessAwaitClause[\"$clause\"][];",
            text,
            StringComparison.Ordinal);
        Assert.Contains("export interface ExecutionDefinitionDocument", text, StringComparison.Ordinal);
        Assert.Contains("export interface ProcessExecutionTraceArtifact", text, StringComparison.Ordinal);
        Assert.Contains("export interface NormalizedExecutionTraceEvent", text, StringComparison.Ordinal);
        Assert.Contains("processOccurrence?: ProcessTraceOccurrenceEvidence | null;", text, StringComparison.Ordinal);
        Assert.Contains("requestOutcome?: RequestTerminalOutcomeId | null;", text, StringComparison.Ordinal);
        Assert.Contains("export interface ProcessTraceOccurrenceEvidence", text, StringComparison.Ordinal);
        Assert.Contains("continuation?: ProcessContinuationIdentity | null;", text, StringComparison.Ordinal);
        Assert.Contains(
            "export type ExecutionTraceEvidenceDisclosure = 'Unknown' | 'Disclosed' | 'Redacted' | 'Unavailable' | 'Unsupported';",
            text,
            StringComparison.Ordinal);
        Assert.Contains("definition: unknown;", text, StringComparison.Ordinal);
        Assert.Contains("sourceMap: ExecutionSourceMap;", text, StringComparison.Ordinal);
        Assert.Contains("import type {", text, StringComparison.Ordinal);
        Assert.Contains("ValueContract", text, StringComparison.Ordinal);
        Assert.Contains("} from '@cohesivesystems/relations';", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export interface ValueContract", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessesTypeScriptArtifact_IsCurrent()
    {
        var expected = EmitProcessesContracts(LoadWireContractGraph());
        var path = Path.Combine(
            FindRepoRoot(),
            "src",
            "frontend",
            "processes",
            "src",
            "generated",
            "processes.shapes.generated.ts");

        Assert.True(File.Exists(path), $"Missing generated Process artifact '{path}'.");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    static ShapeGraph LoadWireContractGraph() => ContractsAssemblyShapeGraphLoader.Load(
        typeof(ProcessesContractsDefinition).Assembly.Location,
        moduleName: "processes",
        jsonSerializerOptions: ExecutionDefinitionJsonSerializer.CreateOptions());

    static string EmitProcessesContracts(ShapeGraph graph)
    {
        var emission = new TypeScriptShapeEmitter(new TypeScriptEmitterOptions
        {
            FileName = "processes.shapes.generated.ts",
            NewLine = "\n",
            EmitAutoGeneratedHeader = true,
            ExternalTypeModules =
            [
                new TypeScriptExternalTypeModule
                {
                    TypeIdPrefix = "clr:type:Cohesive.Model.",
                    ShapeIdPrefix = "clr:shape:Cohesive.Model.",
                    ImportPath = "@cohesivesystems/relations"
                }
            ],
            UnionDiscriminatorCatalogs =
            [
                new()
                {
                    UnionTypeName = nameof(ProcessNode),
                    ExportName = "canonicalProcessNodeKinds"
                },
                new()
                {
                    UnionTypeName = nameof(ProcessAwaitClause),
                    ExportName = "canonicalProcessAwaitClauseKinds"
                }
            ]
        }).Emit(new ShapeCodeGenerationRequest(graph));

        return Assert.Single(emission.Documents).Text;
    }

    static TypeDefinition.Union AssertUnion(ShapeGraph graph, string name, string discriminator)
    {
        var union = Assert.IsType<TypeDefinition.Union>(
            Assert.Single(graph.NamedTypes, type => type.Name == name));
        Assert.Equal(discriminator, union.Discriminator.FieldName);
        Assert.NotEmpty(union.Cases);
        return union;
    }

    static string GetStringDiscriminator(JsonDerivedTypeAttribute attribute) =>
        Assert.IsType<string>(attribute.TypeDiscriminator);

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cohesive.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
