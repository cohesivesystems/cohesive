using Cohesive.Transitions.Model;
using Cohesive.Api;
using Cohesive.Model;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Application;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;
using Cohesive.Semantics;

namespace Cohesive.Adapters.Mcp.Development;

// Domain dispatch stays outside the protocol-independent workspace. Original serializers own integrity.
internal static class DomainDocumentReaders
{
    internal static readonly IReadOnlyDictionary<string, Func<JsonElement, WorkspaceDocument>> Readers =
        new Dictionary<string, Func<JsonElement, WorkspaceDocument>>
        {
            [EntityStateDocument.Schema] = Entity,
            [ShapeGraphDocument.CurrentSchemaVersion] = ShapeWorkspaceProjection.Read,
            [RelationshipCatalogDocument.CurrentSchemaVersion] = Relationships,
            [ApiContractDocument.Schema] = ApiContract,
            [RelationQueryDocument.CurrentSchemaVersion] = Relation,
            [ApplicationEndpointDocument.Schema] = Endpoint
        };

    static WorkspaceDocument Relation(JsonElement json)
    {
        var validation = RelationQueryJsonSerializer.TryDeserialize(json.GetRawText(), out var document);
        if (document is null) throw new JsonException(string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
        var id = document.Definition switch
        {
            QueryDefinition query => query.Id.Value,
            RelationDefinition relation => relation.Id.Value,
            _ => throw new JsonException("Unknown relation definition kind.")
        };
        var fingerprint = document.DefinitionFingerprint;
        return new(new(new(id), null, new(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value)),
            "relation-query", document.Metadata?.Name ?? (document.Definition is QueryDefinition named ? named.Name.Value : id),
            JsonSerializer.Deserialize<JsonElement>(RelationQueryJsonSerializer.Serialize(document)),
            string.IsNullOrWhiteSpace(document.Metadata?.SourceUri) ? null : new(document.Metadata.SourceUri), null,
            validation, true,
            [.. document.Definition.GetShapeDependencies().Select(s => ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.ShapeId(s))),
             .. document.Definition.GetRelationshipDependencies().Select(r => ShapeWorkspaceProjection.Identity("relationship:" + Uri.EscapeDataString(r.Value)))],
            false); // Shape-granular: field expression and external function dependencies remain incomplete.
    }

    static WorkspaceDocument Entity(JsonElement json)
    {
        var entity = EntityStateDocument.Read(json);
        var validation = EntityShapeGraphValidator.Validate(entity);
        var state = entity.ShapeGraph?.Shape ?? entity.StateShape.QualifiedId;
        var profile = EntityStateDocument.Schema;
        var content = new { entity.Name, entity.Shape, entity.Invariants, State = state, Graph = entity.ShapeGraph?.Document.Graph };
        return new(new(new(ShapeWorkspaceProjection.EntityId(entity.Name)), null, new("sha256", profile,
            Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(content, ApiOptions)))), "entity-state", entity.Name.Value,
            json.Clone(), null, null, validation, true, [ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.ShapeId(state))], false);
    }

    static WorkspaceDocument Relationships(JsonElement json)
    {
        var validation = RelationshipCatalogJsonSerializer.TryDeserialize(json.GetRawText(), out var document);
        if (document is null) throw new JsonException("Invalid relationship catalog.");
        if (document.Catalog.Relationships.Length >= SemanticWorkspace.MaximumSubjects) throw new JsonException("Relationship catalog exceeds the workspace subject limit.");
        var fingerprint = document.CatalogFingerprint;
        var members = document.Catalog.Relationships.Select(relationship => new WorkspaceDocument(
            new(new("relationship:" + Uri.EscapeDataString(relationship.Id.Value)), null,
                new("sha256", "relationship-inspection/v1", Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(relationship, ApiOptions)))),
            "relationship", relationship.Id.Value, JsonSerializer.SerializeToElement(relationship, ApiOptions), null, null, validation, true,
            [ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.ShapeId(relationship.SourceShape)),
             ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.ShapeId(relationship.TargetShape)),
             .. relationship.SourceReference.Segments[0].TryGetFieldIdentity(out var field)
                ? new[] { ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.FieldId(relationship.SourceShape, field)) } : []], false)).ToImmutableArray();
        return new(new(new("relationship-catalog:" + fingerprint.Value), null, new(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value)),
            "relationship-catalog", document.Metadata.Name, json.Clone(), null, null, validation, true,
            [.. members.Select(m => m.Reference)], false) { Members = members };
    }

    static readonly JsonSerializerOptions ApiOptions = RelationQueryJsonSerializer.CreateOptions();
    static WorkspaceDocument ApiContract(JsonElement json)
    {
        var document = ApiContractDocument.Read(json);
        var definition = document.Definition;
        var shapes = definition.Types is { } types ? ShapeWorkspaceProjection.Read(JsonSerializer.SerializeToElement(ShapeGraphDocument.FromGraph(types), ApiOptions)) : null;
        var dependencies = ImmutableArray.CreateBuilder<WorkspaceDocumentReference>();
        if (definition.Entity is { } entity) dependencies.Add(ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.EntityId(entity)));
        if (definition.Execution is { } execution) dependencies.Add(WorkspaceDocumentReference.FromExecution(execution));
        if (definition.Service is { } service) dependencies.Add(WorkspaceDocumentReference.FromExecution(service));
        if (definition.ResultOf is { } resultOf) dependencies.Add(ShapeWorkspaceProjection.Identity("api:" + resultOf.Value));
        if (definition.Types is { } graph) dependencies.AddRange(graph.Shapes.Select(s => ShapeWorkspaceProjection.Identity(ShapeWorkspaceProjection.ShapeId(graph.Qualify(s.Id)))));
        return new(new(new("api:" + definition.Id.Value), null, document.Fingerprint), "api-contract", definition.Http?.Route,
            document.ToJson(), null, null, new DocumentValidationResult([]), true, dependencies.ToImmutable(), false) { Members = shapes is null ? [] : [shapes] };
    }

    static WorkspaceDocument Endpoint(JsonElement json)
    {
        var document = ApplicationEndpointDocument.Read(json);
        var endpoint = document.Endpoint;
        var dependencies = ImmutableArray.CreateBuilder<WorkspaceDocumentReference>();
        if (endpoint.Execution is { } execution) dependencies.Add(WorkspaceDocumentReference.FromExecution(execution));
        if (endpoint.Service is { } service) dependencies.Add(WorkspaceDocumentReference.FromExecution(service.Definition));
        if (endpoint.ResultOf is { } resultOf) dependencies.Add(new(new("api:" + resultOf.Value), null, null));
        return new(new(new("api:" + endpoint.Id.Value), null, document.Fingerprint), "api-endpoint-inspection",
            endpoint.Http?.Route, document.ToJson(), null, null, new DocumentValidationResult([]), false,
            dependencies.ToImmutable(), false); // Deliberately partial API surface; see the owning projection contract.
    }
}
