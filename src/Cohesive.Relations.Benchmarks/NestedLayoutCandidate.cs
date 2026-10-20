using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

// Benchmark-only physical experiment. Original graph/type definitions and the existing validator
// retain semantic authority. Projected child shapes exist solely to reuse ObservationLayout's token
// dispatch and OrdinalObservationFields; their identities must never escape as domain evidence.
internal sealed class NestedLayoutCandidate
{
    readonly GraphShapeId shape;
    readonly ObservationLayout rootLayout;
    readonly Node root;
    int projectedLayouts;
    int preparationDepth;

    internal NestedLayoutCandidate(GraphShapeId shape)
    {
        this.shape = shape;
        rootLayout = ObservationLayout.Create(shape);
        root = Record(rootLayout, rootLayout.FieldIdentities.Select((_, index) => rootLayout.GetFieldDefinition(index)));
    }

    internal int ProjectedLayoutCount => projectedLayouts;

    internal ObservationValue Parse(ReadOnlySpan<byte> json, out bool fellBack)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || !root.Supported) return Fallback(json, out fellBack);
        if (!ReadRecord(ref reader, root, strictRoot: true, out var value, out var values))
            return Fallback(json, out fellBack);
        // The root is deliberately a one-field payload envelope in this experiment. Both routes
        // use the same authoritative ordinal admission and shape-before-trailing failure order.
        Span<ulong> presence = stackalloc ulong[1];
        presence.Clear();
        for (var index = 0; index < values.Length; index++)
            if (values[index].Kind != ObservationValueKind.Undefined) presence[0] |= 1UL << index;
        if (!ObservationValidator.TryValidateAgainstShape(shape, rootLayout, values, presence, out var error))
            throw new ArgumentException(error, nameof(json));
        foreach (var character in json[checked((int)reader.BytesConsumed)..])
            if (character is not (0x20 or 0x09 or 0x0A or 0x0D))
                throw new JsonException("The JSON observation contains trailing content.");
        fellBack = false;
        return value;
    }

    ObservationValue Fallback(ReadOnlySpan<byte> json, out bool fellBack)
    {
        fellBack = true;
        return ParseExisting(shape, rootLayout, json);
    }

    internal static ObservationValue ParseExisting(GraphShapeId shape, ObservationLayout layout, ReadOnlySpan<byte> json)
    {
        var values = new ObservationValue[layout.Count];
        Span<ulong> presence = stackalloc ulong[1];
        if (!ObservationJsonReader.TryReadShape(json, shape, layout, values, presence, out var error))
            throw new ArgumentException(error, nameof(json));
        var count = 0;
        foreach (var value in values) if (value.Kind != ObservationValueKind.Undefined) count++;
        return ObservationValue.FromOrdinalFields(new OrdinalObservationFields(layout,
            ImmutableCollectionsMarshal.AsImmutableArray(values), count));
    }

    Node Prepare(TypeRef type, FieldCardinality cardinality = FieldCardinality.Single)
    {
        // Recursive named declarations keep the complete existing reader rather than constructing
        // an unbounded physical plan. This is an experimental operating limit, not a semantic limit.
        if (preparationDepth >= 64) return new(supported: false);
        preparationDepth++;
        try { return PrepareCore(type, cardinality); }
        finally { preparationDepth--; }
    }

    Node PrepareCore(TypeRef type, FieldCardinality cardinality)
    {
        if (cardinality == FieldCardinality.Many) return new(element: Prepare(type));
        if (type is ScalarTypeRef { Kind: ScalarTypeKind.Int64 }) return new();
        if (type is ArrayTypeRef array) return new(element: Prepare(array.ElementType));
        IEnumerable<FieldDefinition>? fields = type switch
        {
            ObjectTypeRef objectType => objectType.Fields.Select(field => new FieldDefinition(new(field.Name), field.Type,
                field.Cardinality, field.Presence, field.Nullability)),
            NamedTypeRef named when shape.Graph.TryGetType(named.TypeId, out var definition)
                && definition is TypeDefinition.Structural structural => structural.Fields.Select(field => new FieldDefinition(
                    field.Name, field.Type, field.Cardinality, field.Presence, field.Nullability)),
            _ => null
        };
        if (fields is null) return new(supported: false);
        var declarations = fields.ToImmutableArray();
        // Nested hydration's first case-insensitive declaration wins. Exact dispatch is unsafe
        // when declarations collide under that comparison; retain the existing reader wholesale.
        if (declarations.Select(field => field.Name.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != declarations.Length)
            return new(supported: false);
        Shape projection = new(new("nested-layout-" + projectedLayouts++), declarations);
        ShapeGraph physical = new(new("benchmark/" + shape.Graph.Id.Value + "/" + projection.Id.Value), [projection], shape.Graph.NamedTypes);
        return Record(ObservationLayout.Create(new(physical, projection.Id)), declarations);
    }

    Node Record(ObservationLayout layout, IEnumerable<FieldDefinition> declarations) =>
        new(layout, [.. declarations.Select(field => Prepare(field.Type, field.Cardinality))]);

    static bool Read(ref Utf8JsonReader reader, Node node, out ObservationValue value)
    {
        if (reader.TokenType == JsonTokenType.Null) { value = ObservationValue.Null; return true; }
        if (node.Layout is not null) return ReadRecord(ref reader, node, strictRoot: false, out value, out _);
        if (node.Element is null) { value = ObservationJsonReader.ReadValue(ref reader); return true; }
        if (reader.TokenType != JsonTokenType.StartArray) { value = default; return false; }
        var items = new ObservationJsonArrayBuilder();
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) { value = items.Build(); return true; }
                if (!Read(ref reader, node.Element, out var item)) { value = default; return false; }
                items.Add(item);
            }
            throw new JsonException("The JSON array is incomplete.");
        }
        finally { items.Dispose(); }
    }

    static bool ReadRecord(ref Utf8JsonReader reader, Node node, bool strictRoot,
        out ObservationValue value, out ObservationValue[] values)
    {
        value = default;
        values = [];
        if (reader.TokenType != JsonTokenType.StartObject) return false;
        var layout = node.Layout!;
        values = new ObservationValue[layout.Count];
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                value = ObservationValue.FromOrdinalFields(new OrdinalObservationFields(layout,
                    ImmutableCollectionsMarshal.AsImmutableArray(values), count));
                return true;
            }
            // Case variants/unknown names preserve original spelling and diagnostics by falling
            // back on the complete original input, not canonicalizing or rejecting them here.
            if (reader.TokenType != JsonTokenType.PropertyName || !layout.TryGetJsonOrdinal(ref reader, out var ordinal)) return false;
            var present = values[ordinal].Kind != ObservationValueKind.Undefined;
            if (strictRoot && present) return false;
            if (!reader.Read()) throw new JsonException("The JSON object ended before its property value.");
            if (!Read(ref reader, node.Children![ordinal], out var field)) return false;
            values[ordinal] = field; // Nested decoded exact duplicates retain last-property-wins.
            if (!present) count++;
        }
        throw new JsonException("The JSON object is incomplete.");
    }

    sealed class Node(ObservationLayout? layout = null, Node[]? children = null, Node? element = null, bool supported = true)
    {
        internal ObservationLayout? Layout { get; } = layout;
        internal Node[]? Children { get; } = children;
        internal Node? Element { get; } = element;
        internal bool Supported { get; } = supported && (element?.Supported ?? true) && (children?.All(child => child.Supported) ?? true);
    }
}
