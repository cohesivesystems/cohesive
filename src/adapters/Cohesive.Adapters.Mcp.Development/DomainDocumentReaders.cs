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
            validation, true, [], false); // Shape, entity and catalog dependency indexing is not yet covered.
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
