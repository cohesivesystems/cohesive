using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Api;

/// <summary>Portable lowered API result; no CLR type survives.</summary>
/// <param name="Id">Stable result identity.</param><param name="Kind">Semantic result kind.</param>
/// <param name="Body">Body contract, or null for no body.</param><param name="IsPrimary">Primary result selection.</param>
/// <param name="Http">Optional status and content-type projection.</param>
public sealed record ApiContractResult(string Id, ApiResultKind Kind, TypeRef? Body, bool IsPrimary, ApiHttpResultBinding? Http);

/// <summary>Portable HTTP parameter declaration.</summary>
/// <param name="Name">Wire name.</param><param name="Source">Binding location.</param>
/// <param name="Type">Portable value type.</param><param name="IsOptional">Whether omission is allowed.</param>
public sealed record ApiContractParameter(string Name, HttpParameterSource Source, TypeRef Type, bool IsOptional);

/// <summary>Portable HTTP binding, preserving separate body and query DTO contracts.</summary>
/// <param name="Method">Method.</param><param name="Route">Route template.</param><param name="Parameters">Ordered bindings.</param>
/// <param name="Body">Body contract.</param><param name="Query">Query DTO contract.</param>
public sealed record ApiContractHttp(string Method, string Route, ImmutableArray<ApiContractParameter> Parameters, TypeRef? Body, TypeRef? Query);

/// <summary>Canonical API contract lowered from source using an explicit serializer profile. Runtime handlers remain outside IR.</summary>
/// <param name="Id">Endpoint identity.</param><param name="Kind">Operation kind.</param>
/// <param name="SerializerProfile">Producer-assigned version of effective serialization policy.</param>
/// <param name="CoverageGaps">Unsupported serializer features, retained as explicit evidence for comparison.</param>
/// <param name="Types">Original core shape/type model; absent when no type could be structurally lowered.</param><param name="Request">Request envelope contract.</param>
/// <param name="Results">All result variants.</param><param name="Http">Optional HTTP binding.</param>
/// <param name="ScopePolicies">Original scope policy declarations.</param><param name="AuthorizationRequirements">Original authorization requirements.</param>
/// <param name="Entity">Optional owning entity identity.</param>
/// <param name="SemanticReferences">Original semantic attribution.</param><param name="ResultOf">Originating endpoint for polling.</param>
/// <param name="Execution">Optional exact execution binding.</param><param name="Service">Optional exact service binding.</param>
/// <param name="ServiceOperation">Selected operation in the service, if any.</param>
public sealed record ApiContractDefinition(ApiEndpointId Id, ApiOperationKind Kind, string SerializerProfile, ImmutableArray<string> CoverageGaps, ShapeGraph? Types,
    TypeRef? Request, ImmutableArray<ApiContractResult> Results, ApiContractHttp? Http,
    ImmutableArray<ApiScopePolicy> ScopePolicies, ImmutableArray<ApiAuthorizationRequirement> AuthorizationRequirements,
    ImmutableArray<ApiSemanticReference> SemanticReferences, EntityTypeName? Entity, ApiEndpointId? ResultOf,
    ExecutionDefinitionReference? Execution, ExecutionDefinitionReference? Service, string? ServiceOperation);

/// <summary>Versioned, fingerprinted portable API contract. Always derived from authoring source.</summary>
/// <param name="SchemaVersion">Wire version.</param><param name="Definition">Lowered semantic contract.</param><param name="Fingerprint">Verified content evidence.</param>
public sealed record ApiContractDocument(string SchemaVersion, ApiContractDefinition Definition, ExecutionDefinitionFingerprint Fingerprint)
{
    /// <summary>Current wire and canonical fingerprint profile.</summary>
    public const string Schema = "cohesive-api-contract/v1";
    internal static readonly JsonSerializerOptions Options = RelationQueryJsonSerializer.CreateOptions();

    /// <summary>Lowers an authored endpoint with explicit effective JSON serialization policy.</summary>
    /// <param name="operation">Authoritative source declaration.</param><param name="jsonOptions">Effective serializer options; no options or converters survive lowering.</param>
    /// <param name="serializerProfile">Exact producer-owned serialization convention version.</param>
    /// <param name="execution">Optional admitted execution binding.</param><param name="service">Optional admitted service binding.</param>
    /// <param name="serviceOperation">Selected operation identity when a service is supplied.</param>
    /// <param name="wireMetadata">Optional adapter-owned interpretation of known wire types; retained only as lowered metadata. Include its version in the serializer profile.</param>
    /// <returns>A deterministic portable contract retaining all supported source declarations.</returns>
    /// <exception cref="ArgumentException">The profile is empty or source declarations conflict.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="NotSupportedException">Serializer metadata cannot be lowered safely.</exception>
    /// <exception cref="InvalidOperationException">Lowered wire types declare conflicting identities.</exception>
    public static ApiContractDocument Lower(ApiOperation operation, JsonSerializerOptions jsonOptions, string serializerProfile,
        ExecutionDefinitionReference? execution = null, ExecutionDefinitionReference? service = null, string? serviceOperation = null,
        IClrShapeMetadataProvider? wireMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializerProfile);
        if ((service is null) != string.IsNullOrWhiteSpace(serviceOperation)) throw new ArgumentException("A service and selected operation must be supplied together.");
        var effective = new JsonSerializerOptions(jsonOptions);
        effective.MakeReadOnly(populateMissingResolver: true);
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        if (effective.Converters.Count > 0 || effective.ReferenceHandler is not null || effective.TypeInfoResolver is not System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver)
            gaps.Add("Custom converters, reference handling or custom serializer metadata require separate conformance evidence.");
        if (effective.DefaultIgnoreCondition != System.Text.Json.Serialization.JsonIgnoreCondition.Never || effective.IncludeFields
            || effective.IgnoreReadOnlyFields || effective.IgnoreReadOnlyProperties || effective.UnmappedMemberHandling == System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            || (effective.NumberHandling & ~System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString) != 0)
            gaps.Add("Effective field omission, unknown-member or numeric encoding policy exceeds the tolerant-reader interpretation.");
        var types = new[] { operation.RequestType }.Concat(operation.Results.Select(r => r.BodyType))
            .Concat(operation.Http?.Parameters.Select(p => p.Type) ?? [])
            .Concat(new[] { operation.Http?.Body?.BodyType, operation.Http?.Query?.QueryType }.OfType<Type>())
            .Where(t => t != typeof(void)).Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        // Core lowering remains the only reflection/serializer interpreter. Isolate unsupported roots so one
        // opaque request envelope does not erase otherwise known result contracts.
        var graphId = new GraphId("api-contract:" + operation.Id.Value);
        var shapes = new Dictionary<ShapeId, Shape>();
        var namedTypes = new Dictionary<TypeId, TypeDefinition>();
        var mappings = new Dictionary<Type, TypeRef>();
        foreach (var type in types)
        {
            // A target-owned wire root must be lowered before CLR discovery walks converter internals.
            if (wireMetadata?.GetMetadata(ClrShapeMetadataContext.ForType(type)) is { TypeRef: { } wireType } metadata)
            {
                mappings[type] = wireType;
                foreach (var named in metadata.NamedTypes)
                {
                    if (namedTypes.TryGetValue(named.Id, out var prior) && prior != named)
                        throw new InvalidOperationException("Conflicting lowered wire type identity.");
                    namedTypes[named.Id] = named;
                }
                continue;
            }
            var envelope = typeof(ContractValue<>).MakeGenericType(type);
            ClrShapeGraphBuildResult lowered;
            try
            {
                var builder = new ClrShapeGraphBuilder().UsePublicJsonContracts(effective)
                    .AddMetadataProvider(new ApiJsonWireMetadata(effective));
                if (wireMetadata is not null) builder.AddMetadataProvider(wireMetadata);
                lowered = builder.AddShape(envelope).BuildResult(graphId);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                mappings[type] = new OpaqueRuntimeTypeRef(type.FullName ?? type.Name,
                    new TypeInferenceDiagnostic("api.contract.unsupported", error.Message));
                continue;
            }
            foreach (var (clr, id) in lowered.TypeIds)
            {
                if (lowered.Graph.TryGetType(id, out var named) && named is TypeDefinition.Structural)
                {
                    var info = effective.GetTypeInfo(clr);
                    if ((info.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object
                            && !SingleValueWrapperJsonConverter.ScalarOnly.CanConvert(clr))
                        || info.Properties.Any(p => p.Get is not null && (p.ShouldSerialize is not null || p.CustomConverter is not null || p.IsExtensionData)))
                        gaps.Add("Conditional or custom JSON representation of " + id.Value + " is not proven by the structural graph.");
                }
            }
            foreach (var shape in lowered.Graph.Shapes)
            {
                if (shapes.TryGetValue(shape.Id, out var prior) && prior != shape) throw new InvalidOperationException("Conflicting lowered shape identity.");
                shapes[shape.Id] = shape;
            }
            foreach (var named in lowered.Graph.NamedTypes)
            {
                if (namedTypes.TryGetValue(named.Id, out var prior) && prior != named) throw new InvalidOperationException("Conflicting lowered type identity.");
                namedTypes[named.Id] = named;
            }
            mappings[type] = ValueContract.FromField(lowered.Graph.GetShape(lowered.ShapeIds[envelope]).Fields.Single()).GetEffectiveType()!;
        }
        var graph = shapes.Count == 0 && namedTypes.Count == 0 ? null : new ShapeGraph(graphId, [.. shapes.Values.OrderBy(s => s.Id.Value, StringComparer.Ordinal)],
            [.. namedTypes.Values.OrderBy(t => t.Id.Value, StringComparer.Ordinal)]);
        TypeRef? Map(Type? type) => type is null || type == typeof(void) ? null : mappings[type];
        var http = operation.Http is { } h ? new ApiContractHttp(h.Method, h.Route,
            [.. h.Parameters.OrderBy(p => p.Source).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p => new ApiContractParameter(p.Name, p.Source, Map(p.Type)!, p.IsOptional))],
            Map(h.Body?.BodyType), Map(h.Query?.QueryType)) : null;
        var definition = new ApiContractDefinition(operation.Id, operation.Kind, serializerProfile, [.. gaps], graph,
            Map(operation.RequestType), [.. operation.Results.OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new ApiContractResult(r.Id, r.Kind, Map(r.BodyType), r.IsPrimary, r.Http))], http,
            [.. operation.ScopePolicies], [.. operation.AuthorizationRequirements], [.. operation.SemanticReferences],
            operation.Entity, operation.ResultOf, execution ?? operation.TransitionReference, service, serviceOperation);
        return new(Schema, definition, Digest(definition));
    }

    /// <summary>Reads a portable contract and verifies shared canonical integrity and shape validation.</summary>
    /// <param name="json">Original document JSON.</param><returns>Verified derived contract.</returns>
    /// <exception cref="JsonException">Wire structure, shape validation or fingerprint is invalid.</exception>
    public static ApiContractDocument Read(JsonElement json)
    {
        if (StrictDocumentJson.TryFindDuplicateProperty(json, "", out var duplicate)) throw new JsonException($"Duplicate API property '{duplicate}'.");
        var document = json.Deserialize<ApiContractDocument>(Options);
        if (document is null || document.SchemaVersion != Schema || document.Definition is not { } definition
            || string.IsNullOrWhiteSpace(definition.Id.Value) || string.IsNullOrWhiteSpace(definition.SerializerProfile)
            || definition.CoverageGaps.IsDefault || !Enum.IsDefined(definition.Kind)
            || (definition.Service is null) != string.IsNullOrWhiteSpace(definition.ServiceOperation)
            || definition.ScopePolicies.IsDefault || definition.AuthorizationRequirements.IsDefault || definition.SemanticReferences.IsDefault
            || definition.Results.IsDefaultOrEmpty
            || definition.Results.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || !Enum.IsDefined(r.Kind))
            || definition.Results.Count(r => r.IsPrimary) != 1
            || definition.Results.Select(r => r.Id).Distinct().Count() != definition.Results.Length
            || definition.Http is { } http && (http.Parameters.IsDefault || string.IsNullOrWhiteSpace(http.Method) || string.IsNullOrWhiteSpace(http.Route)
                || http.Parameters.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Type is null || !Enum.IsDefined(p.Source))
                || http.Parameters.Select(p => (p.Name, p.Source)).Distinct().Count() != http.Parameters.Length)
            || definition.Types is { } graph && !ShapeGraphDocumentSemanticValidator.Validate(ShapeGraphDocument.FromGraph(graph)).IsValid
            || document.Fingerprint != Digest(definition)) throw new JsonException("Invalid portable API contract or fingerprint.");
        return document;
    }

    /// <summary>Serializes the original portable contract.</summary><returns>Independently owned JSON.</returns>
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Options);
    static ExecutionDefinitionFingerprint Digest(ApiContractDefinition definition) => new("sha256", Schema,
        Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(definition with
        {
            SemanticReferences = [.. definition.SemanticReferences.Select(r => new ApiSemanticReference(r.Authority, r.SchemaVersion, r.Path))],
            AuthorizationRequirements = [.. definition.AuthorizationRequirements.Select(r => new ApiAuthorizationRequirement(r.Id))]
        }, Options)));
    sealed record ContractValue<T>(T Value);
}
