using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Migrations;

/// <summary>A direct target field interpreted as a total forward path ending in a scalar field.</summary>
/// <param name="Target">Field in the domain extent.</param>
/// <param name="Hops">Ordered codomain relationships, empty for a root field.</param>
/// <param name="Source">Field on the final codomain extent.</param>
public sealed record SchemaMappingField(FieldName Target, ImmutableArray<RelationshipId> Hops, FieldName Source);

/// <summary>A versioned single-extent mapping with embedded immutable schema and relationship snapshots.</summary>
public sealed class SchemaMappingDocument
{
    /// <summary>Wire and lowering profile for required forward paths and identity-preserving pullbacks.</summary>
    public const string CurrentVersion = "schema-mapping/v1";
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();
    readonly FieldName domainKey;
    readonly FieldName codomainKey;

    /// <summary>Restores and validates the mapping against its pinned contracts.</summary>
    /// <param name="version">Supported mapping profile.</param>
    /// <param name="id">Stable extent mapping identity.</param>
    /// <param name="domain">Summary extent within the exact destination graph.</param>
    /// <param name="codomain">Root extent within the exact operational graph.</param>
    /// <param name="relationships">Pinned relationship catalog within the operational graph.</param>
    /// <param name="fields">Explicit field interpretations, excluding the derived identity.</param>
    /// <exception cref="ArgumentNullException">An identity, graph, or catalog is missing.</exception>
    /// <exception cref="ArgumentException">A field list, graph, or catalog is malformed.</exception>
    /// <exception cref="InvalidOperationException">A mapping obligation is unsupported or unsatisfied.</exception>
    /// <exception cref="KeyNotFoundException">A referenced shape, relationship, or field is missing.</exception>
    [JsonConstructor]
    public SchemaMappingDocument(string version, RelationId id, GraphShapeId domain, GraphShapeId codomain,
        RelationshipCatalog relationships, ImmutableArray<SchemaMappingField> fields)
    {
        if (version != CurrentVersion)
            throw new InvalidOperationException($"Unsupported schema mapping version '{version}'.");
        Id = Guard.RequireNotNull(id);
        Domain = domain;
        Codomain = codomain;
        ArgumentNullException.ThrowIfNull(domain.Graph);
        ArgumentNullException.ThrowIfNull(codomain.Graph);
        if (domain.Graph.HasErrors || codomain.Graph.HasErrors)
            throw new ArgumentException("Schema mapping requires graphs without error diagnostics.");
        if (domain.Graph.Id == codomain.Graph.Id && !ReferenceEquals(domain.Graph, codomain.Graph)
            && !StrictDocumentJson.ComputeCanonicalSha256(domain.Graph, Options).AsSpan()
                .SequenceEqual(StrictDocumentJson.ComputeCanonicalSha256(codomain.Graph, Options)))
            throw new ArgumentException("A graph identifier cannot denote two different snapshots in a pullback.");
        Relationships = Guard.RequireNotNull(relationships);
        var validation = RelationshipCatalogValidator.Validate(relationships, codomain.Graph);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join(", ", validation.Diagnostics.Select(d => d.Message)), nameof(relationships));
        if (fields.IsDefault || fields.Any(field => field is null || field.Hops.IsDefault))
            throw new ArgumentException("Field assignments and their path arrays must be present.", nameof(fields));

        var output = domain.Graph.GetShape(domain.ShapeId);
        var root = codomain.Graph.GetShape(codomain.ShapeId);
        var targetIdentity = Identity(output);
        var sourceIdentity = Identity(root);
        if (targetIdentity.Type != sourceIdentity.Type)
            throw new InvalidOperationException("Domain and codomain identity types must match exactly.");
        domainKey = targetIdentity.Name;
        codomainKey = sourceIdentity.Name;
        Fields = [.. fields.OrderBy(f => f.Target.Value, StringComparer.Ordinal)];
        HashSet<FieldName> assigned = [domainKey];
        foreach (var field in Fields)
        {
            if (!assigned.Add(field.Target))
                throw new InvalidOperationException($"Field '{field.Target}' is already assigned.");
            var endpoint = codomain.QualifiedId;
            HashSet<QualifiedShapeId> visited = [endpoint];
            foreach (var hop in field.Hops)
            {
                var edge = relationships.GetRelationship(hop);
                if (edge.SourceShape != endpoint || !visited.Add(edge.TargetShape))
                    throw new InvalidOperationException($"Disconnected or cyclic path at '{hop}'.");
                if (!edge.SourceReference.TryGetDirectFieldName(out var reference))
                    throw new InvalidOperationException($"Expected a direct reference for '{hop}'.");
                RequireTotal(codomain.Graph.GetShape(endpoint).GetField(reference));
                endpoint = edge.TargetShape;
            }
            var source = codomain.Graph.GetShape(endpoint).GetField(field.Source.Value);
            var target = output.GetField(field.Target.Value);
            RequireTotal(source);
            RequireTotal(target);
            if (source.Type is not ScalarTypeRef || source.Type != target.Type)
                throw new InvalidOperationException($"Field '{field.Target}' requires an exact scalar projection.");
        }
        foreach (var field in output.Fields)
            if (field.Presence == FieldPresence.Required && !assigned.Contains(field.Name))
                throw new InvalidOperationException($"Required field '{field.Name}' has no mapping.");
    }

    /// <summary>Version of mapping semantics and the structural lowering convention.</summary>
    public string Version => CurrentVersion;
    /// <summary>Stable mapping identity.</summary>
    public RelationId Id { get; }
    /// <summary>Exact domain graph and mapped extent.</summary>
    public GraphShapeId Domain { get; }
    /// <summary>Exact codomain graph and root extent.</summary>
    public GraphShapeId Codomain { get; }
    /// <summary>Authoritative forward edges available to the mapping.</summary>
    public RelationshipCatalog Relationships { get; }
    /// <summary>Explicit scalar assignments in ordinal target field order.</summary>
    public ImmutableArray<SchemaMappingField> Fields { get; }

    /// <summary>Fingerprints the canonical document, including its governing schema snapshots.</summary>
    /// <returns>Lowercase SHA-256 digest of the canonical UTF-8 document.</returns>
    public string Fingerprint() => Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(this, Options));

    /// <summary>Interprets the domain extent over the codomain through the structural Relations authoring core.</summary>
    /// <returns>The rooted canonical relation with validation and mapping clause provenance.</returns>
    public RelationQueryAuthoringResult<RelationDefinition> Pullback()
    {
        var core = RelationQuery.Structural();
        var origin = new AuthoringSource(CurrentVersion, Id.Value);
        var root = core.Source(Codomain.QualifiedId, source: origin);
        RelationQueryNodeHandle<LogicalQueryNode> current = new(core, root.Node.Id);
        Dictionary<(ValueBindingId, RelationshipId), RelationQueryBindingHandle> prefixes = [];
        var assignments = ImmutableArray.CreateBuilder<RelationQueryProjectionAssignment>(Fields.Length + 1);
        assignments.Add(new(FieldPath.FromField(domainKey.Value), root.Binding.Field(FieldPath.FromField(codomainKey.Value)),
            assignmentSource: origin));
        foreach (var field in Fields)
        {
            var clause = new AuthoringSource(CurrentVersion, Id.Value, field.Target.Value);
            var binding = root.Binding;
            foreach (var hop in field.Hops)
            {
                var prefix = (binding.Id, hop);
                if (!prefixes.TryGetValue(prefix, out var next))
                {
                    var traversal = core.Traverse(current, binding, hop, requirement: QueryInputRequirement.Required, source: clause);
                    current = new(core, traversal.Node.Id);
                    next = traversal.Binding;
                    prefixes.Add(prefix, next);
                }
                binding = next;
            }
            assignments.Add(new(FieldPath.FromField(field.Target.Value), binding.Field(FieldPath.FromField(field.Source.Value)),
                assignmentSource: clause));
        }
        var projection = core.Project(current, Domain.QualifiedId, assignments.MoveToImmutable(), source: origin);
        return core.BuildRelation(Id, new(Id.Value), root.Binding, projection.Node, Domain.QualifiedId,
            RelationOutputMode.OnePerRoot, projection.Binding.Field(FieldPath.FromField(domainKey.Value)), source: origin);
    }

    static FieldDefinition Identity(Shape shape)
    {
        var keys = shape.Fields.Where(f => f.Role == FieldRole.Identity).ToArray();
        if (keys.Length != 1 || keys[0].Type is not ScalarTypeRef { Kind: ScalarTypeKind.String })
            throw new InvalidOperationException($"Shape '{shape.Id}' requires one declared string identity field.");
        RequireTotal(keys[0]);
        return keys[0];
    }

    static void RequireTotal(FieldDefinition field)
    {
        if (field.Cardinality != FieldCardinality.Single || field.Presence != FieldPresence.Required
            || field.Nullability != FieldNullability.NonNullable)
            throw new InvalidOperationException($"Field '{field.Name}' must be required, non-null and single-valued.");
    }
}
