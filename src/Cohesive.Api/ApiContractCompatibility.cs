using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Api;

/// <summary>Direction of a client/server contract comparison.</summary>
public enum ApiCompatibilityDirection
{
    /// <summary>Existing clients calling the proposed server.</summary>
    ExistingClientToProposedServer,
    /// <summary>Proposed clients calling the existing server.</summary>
    ProposedClientToExistingServer
}
/// <summary>Compatibility under the explicitly selected interpretation.</summary>
public enum ApiCompatibilityVerdict
{
    /// <summary>All inspected declarations satisfy the policy.</summary>
    Compatible,
    /// <summary>At least one supported rule identifies a breaking change.</summary>
    Breaking,
    /// <summary>No definite break, but evidence or interpretation is incomplete.</summary>
    Unknown
}
/// <summary>Directional declaration evidence, not runtime behavioral equivalence.</summary>
/// <param name="Policy">Versioned interpretation.</param><param name="Direction">Client/server direction.</param>
/// <param name="Verdict">Overall result; breaking takes precedence over unknown.</param><param name="Diagnostics">Structured path-specific findings.</param>
public sealed record ApiCompatibilityResult(string Policy, ApiCompatibilityDirection Direction, ApiCompatibilityVerdict Verdict,
    ImmutableArray<DocumentValidationDiagnostic> Diagnostics);

/// <summary>Conservative HTTP/JSON compatibility interpretation over original portable type contracts.</summary>
public static class ApiContractCompatibility
{
    /// <summary>Policy assumes JSON consumers ignore extra fields, exact scalar formats and declaration-level authorization.</summary>
    public const string Policy = "http-json-tolerant-readers/v1";

    /// <summary>Checks input acceptance contravariantly and output acceptance covariantly.</summary>
    /// <param name="baseline">Existing verified contract.</param><param name="proposed">Proposed verified contract.</param>
    /// <param name="direction">Client/server direction.</param><returns>Bounded findings with explicit unknown coverage.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Direction is unsupported.</exception>
    /// <exception cref="JsonException">Either document fails integrity validation.</exception>
    public static ApiCompatibilityResult Compare(ApiContractDocument baseline, ApiContractDocument proposed,
        ApiCompatibilityDirection direction = ApiCompatibilityDirection.ExistingClientToProposedServer)
    {
        return CompareDocuments(baseline.ToJson(), proposed.ToJson(), direction);
    }

    /// <summary>Compares verified portable JSON, including endpoint additions and removals.</summary>
    /// <param name="baseline">Existing document or null for an addition.</param><param name="proposed">Proposed document or null for a removal.</param>
    /// <param name="direction">Which contract describes the client.</param><returns>Directional compatibility evidence.</returns>
    /// <exception cref="ArgumentException">Neither snapshot has a document or direction is invalid.</exception>
    /// <exception cref="JsonException">A document has an unsupported schema or invalid integrity.</exception>
    public static ApiCompatibilityResult CompareDocuments(JsonElement? baseline, JsonElement? proposed,
        ApiCompatibilityDirection direction = ApiCompatibilityDirection.ExistingClientToProposedServer)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (baseline is null && proposed is null) throw new ArgumentException("At least one endpoint contract is required.");
        var old = baseline is { } oldJson ? ApiContractDocument.Read(oldJson) : null;
        var next = proposed is { } nextJson ? ApiContractDocument.Read(nextJson) : null;
        if (old is null || next is null)
        {
            var clientPresent = direction == ApiCompatibilityDirection.ExistingClientToProposedServer ? old is not null : next is not null;
            return new(Policy, direction, clientPresent ? ApiCompatibilityVerdict.Breaking : ApiCompatibilityVerdict.Compatible,
                clientPresent ? [new("api.compatibility.endpointRemoved", DiagnosticSeverity.Error, "The server does not expose the client's endpoint.", "/id")] : []);
        }
        var client = (direction == ApiCompatibilityDirection.ExistingClientToProposedServer ? old : next).Definition;
        var server = (direction == ApiCompatibilityDirection.ExistingClientToProposedServer ? next : old).Definition;
        var findings = new List<DocumentValidationDiagnostic>();
        var work = 0;
        var breakingFound = false;
        var unknownFound = false;
        var omitted = false;
        void Finding(string code, string path, string message, bool breaking = true)
        {
            breakingFound |= breaking;
            unknownFound |= !breaking;
            if (findings.Count < 99) findings.Add(new(code, breaking ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, message, path));
            else omitted = true;
        }
        void Unknown(string path, string message) => Finding("api.compatibility.unknown", path, message, breaking: false);
        bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, ApiContractDocument.Options) == JsonSerializer.Serialize(b, ApiContractDocument.Options);
        foreach (var gap in client.CoverageGaps.Union(server.CoverageGaps, StringComparer.Ordinal)) Unknown("/serializerProfile", gap);
        if (client.Id != server.Id) Finding("api.compatibility.identity", "/id", "Endpoint identity differs.");
        if (client.Entity != server.Entity) Unknown("/entity", "Owning entity authority differs.");
        if (client.Kind != server.Kind) Finding("api.compatibility.operation", "/kind", "Operation semantics differ.");
        if (client.SerializerProfile != server.SerializerProfile) Unknown("/serializerProfile", "Serialization profiles differ; wire equivalence is unproven.");
        if (!Same(client.ScopePolicies, server.ScopePolicies) || !client.AuthorizationRequirements.Select(r => r.Id).SequenceEqual(server.AuthorizationRequirements.Select(r => r.Id)))
            Unknown("/authorizationRequirements", "Scope or authorization changed; credential and access compatibility requires review.");
        if (client.ResultOf != server.ResultOf) Finding("api.compatibility.resultOf", "/resultOf", "Polling ownership differs.");
        if (!client.SemanticReferences.Select(r => (r.Authority, r.SchemaVersion, r.Path)).SequenceEqual(server.SemanticReferences.Select(r => (r.Authority, r.SchemaVersion, r.Path)))) Unknown("/semanticReferences", "Referenced semantics differ.");
        if (client.Http is not { } ch || server.Http is not { } sh) Unknown("/http", "This policy requires HTTP bindings on both contracts.");
        else
        {
            if (ch.Method != sh.Method || ch.Route != sh.Route) Finding("api.compatibility.route", "/http", "Method or route changed.");
            foreach (var parameter in sh.Parameters)
            {
                var previous = ch.Parameters.SingleOrDefault(p => p.Source == parameter.Source && p.Name == parameter.Name);
                var path = "/http/parameters/" + parameter.Source + "/" + Escape(parameter.Name);
                if (previous is null) { if (!parameter.IsOptional) Finding("api.compatibility.requiredParameter", path, "Server requires a parameter the client does not supply."); continue; }
                if (previous.IsOptional && !parameter.IsOptional) Finding("api.compatibility.requiredParameter", path, "Server no longer accepts omission.");
                Accept(previous.Type, client.Types, parameter.Type, server.Types, path, 0);
            }
            if (ch.Parameters.Any(p => !sh.Parameters.Any(q => p.Source == q.Source && p.Name == q.Name)))
                Unknown("/http/parameters", "Removed parameter bindings may change behavior.");
            Accept(ch.Body, client.Types, sh.Body, server.Types, "/http/body", 0);
            if (ch.Query is not null || sh.Query is not null) Unknown("/http/query", "Query DTO binding conventions are not interpreted by this policy.");
        }
        foreach (var result in server.Results)
        {
            var expected = client.Results.SingleOrDefault(r => r.Id == result.Id);
            var path = "/results/" + Escape(result.Id);
            if (expected is null) { Finding("api.compatibility.resultAdded", path, "Server may produce a result unknown to this client."); continue; }
            if (expected.Kind != result.Kind || expected.IsPrimary != result.IsPrimary || !Same(expected.Http, result.Http))
                Finding("api.compatibility.resultBinding", path, "Result category, primary selection, status or content type differs.");
            Accept(result.Body, server.Types, expected.Body, client.Types, path + "/body", 0);
        }
        if (client.Results.Any(r => !server.Results.Any(s => s.Id == r.Id)))
            Unknown("/results", "Removed result variants may alter behavioral guarantees.");
        if (omitted) findings.Add(new("api.compatibility.truncated", DiagnosticSeverity.Warning, "Further findings may be omitted; overall verdict includes every visited finding.", "/"));
        var diagnostics = DocumentValidationDiagnostics.Normalize([.. findings]);
        return new(Policy, direction, breakingFound ? ApiCompatibilityVerdict.Breaking
            : unknownFound ? ApiCompatibilityVerdict.Unknown : ApiCompatibilityVerdict.Compatible, diagnostics);

        void Accept(TypeRef? produced, ShapeGraph? producerGraph, TypeRef? accepted, ShapeGraph? consumerGraph, string path, int depth)
        {
            if (++work > 4096 || depth > 32) { Unknown(path, "Type traversal bound reached (4096 visits, depth 32)."); return; }
            if (produced is null || accepted is null)
            {
                if (produced != accepted) Finding("api.compatibility.body", path, "Body presence differs.");
                return;
            }
            var pf = Fields(produced, producerGraph, path);
            var af = Fields(accepted, consumerGraph, path);
            if (pf is { } source && af is { } target)
            {
                var sourceByName = source.ToDictionary(f => f.Name, StringComparer.Ordinal);
                foreach (var field in target)
                {
                    if (work >= 4096) { Unknown(path, "Type traversal budget exhausted."); break; }
                    var location = path + "/" + Escape(field.Name);
                    var supplied = sourceByName.GetValueOrDefault(field.Name);
                    if (supplied is null)
                    {
                        if (field.Presence == FieldPresence.Required) Finding("api.compatibility.requiredField", location, "Producer does not guarantee a required field.");
                        continue;
                    }
                    if (supplied.Presence == FieldPresence.Optional && field.Presence == FieldPresence.Required)
                        Finding("api.compatibility.presence", location, "Producer may omit a required field.");
                    if (supplied.Nullability == FieldNullability.Nullable && field.Nullability == FieldNullability.NonNullable)
                        Finding("api.compatibility.nullability", location, "Producer may emit null where the consumer forbids it.");
                    if (supplied.Cardinality != field.Cardinality) Finding("api.compatibility.cardinality", location, "Field cardinality differs.");
                    if (supplied.Annotations.Count > 0 || field.Annotations.Count > 0)
                        Unknown(location, "Field serialization annotations require an additional interpretation.");
                    Accept(supplied.Type, producerGraph, field.Type, consumerGraph, location, depth + 1);
                }
            }
            else if (produced is ArrayTypeRef pa && accepted is ArrayTypeRef aa) Accept(pa.ElementType, producerGraph, aa.ElementType, consumerGraph, path + "/*", depth + 1);
            else if (produced is ScalarTypeRef ps && accepted is ScalarTypeRef ac)
            {
                if (ps != ac) Finding("api.compatibility.scalar", path, "Scalar kind or format differs; no numeric coercion is assumed.");
            }
            else if (NamedEnum(produced, producerGraph) is { } sourceEnum && NamedEnum(accepted, consumerGraph) is { } targetEnum)
            {
                // Unchanged closed wire catalogs under the same profile are comparable. Evolving numeric/string
                // converter conventions require a dedicated interpretation; do not guess from CLR member names.
                if (!Same(sourceEnum, targetEnum) || sourceEnum.Annotations.ContainsKey(new(SystemTextJsonShapeAnnotations.Representation)))
                    Unknown(path, "Enum wire catalog or converter representation requires review.");
            }
            else if (produced is EnumTypeRef pe && accepted is EnumTypeRef ae)
            {
                if (pe.Members.Except(ae.Members).Any()) Finding("api.compatibility.enum", path, "Producer may emit enum members unknown to the consumer.");
            }
            else Unknown(path, "Type family is opaque, unresolved or unsupported by this policy.");
        }
        static TypeDefinition.Enum? NamedEnum(TypeRef type, ShapeGraph? graph) => type is NamedTypeRef named && graph is not null
            && graph.TryGetType(named.TypeId, out var definition) ? definition as TypeDefinition.Enum : null;
        ImmutableArray<ObjectFieldTypeDef>? Fields(TypeRef type, ShapeGraph? graph, string path)
        {
            if (type is ObjectTypeRef obj) return obj.Fields;
            if (type is NamedTypeRef named && graph is not null && graph.TryGetType(named.TypeId, out var definition) && definition is TypeDefinition.Structural structural)
            {
                if (structural.Annotations.ContainsKey(new(SystemTextJsonShapeAnnotations.Representation))
                    || structural.Annotations.ContainsKey(new(SystemTextJsonShapeAnnotations.Dictionary)))
                    Unknown(path, "Structural wire representation requires an additional interpretation.");
                if (!structural.Constraints.IsEmpty || structural.Fields.Any(f => !f.Constraints.IsEmpty)) Unknown(path, "Constraints require a separate implication interpretation.");
                return [.. structural.Fields.Select(f => new ObjectFieldTypeDef(f.Name.Value, f.Type, f.Cardinality, f.Presence, f.Nullability, f.Annotations))];
            }
            return null;
        }
    }
    static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
}
