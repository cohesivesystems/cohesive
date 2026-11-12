using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Semantics;

namespace Cohesive.Adapters.Mcp.Development;

/// <summary>Inspectable, integrity-bound projections of compiler-owned recovery assessments.</summary>
/// <remarks>These are derived inspection artifacts, not executable authoring authority. Capability assertions
/// require physical binding validation and conformance testing; an exported report cannot prove runtime behavior.</remarks>
public static class ProcessRecoveryWorkspaceProjection
{
    /// <summary>Version of the workspace inspection projection.</summary>
    public const string Schema = "cohesive.process-recovery-assessment/v1";
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Projects the existing requirement matcher for an exact compiled plan and target profile.</summary>
    /// <param name="plan">Lowered source with explicit compilation guarantees.</param>
    /// <param name="profile">Adapter-owned target evidence.</param>
    /// <returns>Independent JSON for export through SemanticWorkspaceExport.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static JsonElement Export(CompiledProcessPlan plan, ProcessInterpreterCapabilityProfile profile)
    {
        var report = ProcessRecoveryRequirements.Assess(plan, profile);
        var payload = new Payload(plan.DefinitionReference, plan.Document.Metadata.Provenance.Source, plan.Options, profile.Id.Value,
            JsonSerializer.SerializeToElement(report, Options),
            "Recovery-only assessment. Other constructs, physical wiring and runtime conformance are not proven by this artifact.");
        return JsonSerializer.SerializeToElement(new Document(Schema, payload, Hash(payload)), Options);
    }

    internal static WorkspaceDocument Read(JsonElement json)
    {
        var document = json.Deserialize<Document>(Options) ?? throw new JsonException("Recovery assessment is absent.");
        if (document.SchemaVersion != Schema || document.Payload is null
            || document.Fingerprint != Hash(document.Payload))
            throw new JsonException("Recovery assessment schema or integrity mismatch.");
        var payload = document.Payload;
        return new(new(new("recovery:" + Uri.EscapeDataString(payload.Definition.DefinitionId.Value) + "/" + Uri.EscapeDataString(payload.Profile)),
            payload.Definition.RevisionId, new("sha256", Schema, document.Fingerprint)),
            "process-recovery-assessment", payload.Profile, json.Clone(), payload.Source, null,
            DocumentValidationResult.FromDiagnostics([]), false,
            [WorkspaceDocumentReference.FromExecution(payload.Definition)], false);
    }

    static string Hash(Payload payload) => Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(payload, Options));
    sealed record Payload(ExecutionDefinitionReference Definition, ExecutionSourceProvenance? Source, ProcessCompilationOptions Compilation,
        string Profile, JsonElement Report, string Limitations);
    sealed record Document(string SchemaVersion, Payload Payload, string Fingerprint);
}
