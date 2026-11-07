using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Application;

/// <summary>Fingerprint-protected endpoint inspection artifact, not a complete portable API IR.</summary>
/// <param name="SchemaVersion">Projection wire version.</param>
/// <param name="Endpoint">Existing endpoint/binding projection; excludes CLR types and callbacks.</param>
/// <param name="Fingerprint">Digest of this projection, not API compatibility evidence.</param>
public sealed record ApplicationEndpointDocument(string SchemaVersion, ApplicationEndpointExplanation Endpoint,
    ExecutionDefinitionFingerprint Fingerprint)
{
    /// <summary>Current projection schema and fingerprint scope.</summary>
    public const string Schema = "cohesive.application.endpoint-inspection/v1";
    static readonly JsonSerializerOptions Options = RelationQueryJsonSerializer.CreateOptions();

    /// <summary>Captures an existing endpoint projection with deterministic integrity evidence.</summary>
    /// <param name="endpoint">Derived endpoint projection.</param>
    /// <returns>A fingerprinted inspection document.</returns>
    /// <exception cref="ArgumentNullException">The endpoint is null.</exception>
    public static ApplicationEndpointDocument Create(ApplicationEndpointExplanation endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new(Schema, endpoint, Digest(endpoint));
    }

    /// <summary>Reads and verifies a derived projection.</summary>
    /// <param name="json">Original document JSON.</param>
    /// <returns>The verified endpoint projection.</returns>
    /// <exception cref="JsonException">The schema, projection or fingerprint is invalid.</exception>
    public static ApplicationEndpointDocument Read(JsonElement json)
    {
        if (StrictDocumentJson.TryFindDuplicateProperty(json, "", out var duplicate))
            throw new JsonException($"Duplicate endpoint inspection property at '{duplicate}'.");
        var document = json.Deserialize<ApplicationEndpointDocument>(Options);
        if (document is null || document.SchemaVersion != Schema || document.Endpoint is null
            || document.Fingerprint != Digest(document.Endpoint)) throw new JsonException("Invalid endpoint inspection integrity.");
        return document;
    }

    /// <summary>Serializes this original inspection artifact.</summary>
    /// <returns>Independently owned JSON.</returns>
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Options);

    static ExecutionDefinitionFingerprint Digest(ApplicationEndpointExplanation endpoint) => new("sha256", Schema,
        Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(endpoint, Options)));
}
