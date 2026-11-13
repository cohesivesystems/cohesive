using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Mcp.Development;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Semantics.Tests;

public sealed class ProcessRecoveryWorkspaceTests
{
    [Fact]
    public void RecoveryInspectionRetainsSourcePolicyGapsAndIntegrity()
    {
        var root = Path.Combine(Path.GetTempPath(), "recovery-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "source.cs"), "source");
            ValueContract contract = new(new ScalarTypeRef(ScalarTypeKind.String));
            var document = ProcessDefinitionDocuments.Create(new("process/recovery"), new("1"),
                new(contract, contract, new("return"), [new ReturnProcessNode(new("return"), Expr.Const("done"))], ProcessRecoveryPolicy.ContinueAttempt),
                new(new("tests", "1"), new("tests/recovery"), DocumentOrigin.Generated));
            var plan = ProcessStaticCompiler.Compile(document, new(), new(requiredGuarantees: ProcessRecoveryRequirements.WorkerTakeover)).Plan!;
            var projected = ProcessRecoveryWorkspaceProjection.Export(plan, ProcessRecoveryRequirements.Unavailable);
            void Export(JsonElement projection) => SemanticWorkspaceExport.WriteDocuments(root, "bundle.json", ["source.cs"], "tests", _ =>
                [JsonSerializer.Deserialize<JsonElement>(ExecutionDefinitionJsonSerializer.Serialize(document)), projection]);
            Export(projected);
            var workspace = DevelopmentMcpServer.CreateDefaultWorkspace(root, "bundle.json");
            var snapshot = workspace.Describe();
            var subject = workspace.Resolve("recovery:", snapshot.SnapshotId).Result.Items.Single();
            var inspected = workspace.Inspect(subject.Id, snapshot.SnapshotId);
            Assert.Contains("transitionEffectFencing", JsonSerializer.Serialize(inspected));
            Assert.Contains("unavailable", JsonSerializer.Serialize(inspected), StringComparison.OrdinalIgnoreCase);
            Assert.Single(workspace.References("process/recovery", snapshot.SnapshotId).Result.References.Items);
            var tampered = JsonNode.Parse(projected.GetRawText())!;
            tampered["payload"]!["profile"] = "fabricated";
            Export(JsonSerializer.SerializeToElement(tampered));
            Assert.Throws<JsonException>(() => workspace.Describe());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
