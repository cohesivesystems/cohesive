using System.Collections;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

// The immutable payload owns one declared-JSON observation representation. This is deliberately
// separate from decoding: constructors and converter reads still run for its first interpretation.
// The fixed serializer profile is shared with ObservationValue's existing declared-value boundary.
internal static class PreparedDeclaredObservationProjection
{
    static readonly WeakPreparationCache<object, Interpretations> Owners = new();
    static readonly WeakPreparationCache<Type, Contract> Contracts = new();
    static readonly IEqualityComparer<(object Owner, Type Type)> OwnerTypes = EqualityComparer<(object Owner, Type Type)>.Create(
        static (left, right) => ReferenceEquals(left.Owner, right.Owner) && left.Type == right.Type,
        static key => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Owner), key.Type));

    sealed class Interpretations
    {
        internal readonly ConcurrentDictionary<Type, Interpretation> Types = new();
    }

    sealed class Interpretation
    {
        internal readonly PreparationSlot<PreparedValue> Prepared = new();
        internal ReadCandidate? ReadCandidate;
    }

    sealed class ReadCandidate(IReadOnlyDictionary<string, ObservationValue> fields, bool retain)
    {
        readonly IReadOnlyDictionary<string, ObservationValue>? retained = retain ? fields : null;
        readonly WeakReference<IReadOnlyDictionary<string, ObservationValue>>? weak = retain ? null : new(fields);
        internal bool TryGet(out ObservationValue value)
        {
            var current = retained;
            if (current is not null || weak!.TryGetTarget(out current))
            { value = ObservationValue.FromObject(current); return true; }
            value = default;
            return false;
        }
    }

    // A successful read supplies only a candidate, not encoding evidence. Reprojection verifies
    // the declared representation before reusing storage; normalization and custom profiles can differ.
    internal static void RememberRead(IImmutableObservationProjection value, Type type, ObservationValue observation, bool retainSource)
    {
        if (type.IsValueType || !Contracts.TryGet(type, out _)) return;
        var state = Owners.Get(value, static _ => new()).Types.GetOrAdd(type, static _ => new());
        if (state.Prepared.IsPrepared) return;
        lock (state)
        {
            if (state.Prepared.IsPrepared) return;
            if (state.ReadCandidate is null || !state.ReadCandidate.TryGet(out _))
                state.ReadCandidate = new(observation.Fields!, retainSource);
        }
        if (state.Prepared.IsPrepared) Volatile.Write(ref state.ReadCandidate, null);
    }

    internal static ObservationValue Project(object value, Type declaredType) =>
        ProjectValue(value, declaredType, ObservationValue.DeclaredJsonProfile, 0);

    static ObservationValue ProjectValue(object? value, Type declaredType, JsonSerializerOptions options, int depth, HashSet<(object Owner, Type Type)>? active = null, ObservationValue? candidate = null)
    {
        if (value is null) return ObservationValue.Null;
        // The deep-immutability contract also covers deterministic converter output. Prepare
        // opaque leaves by the same owner/type identity instead of rebuilding their tree per envelope.
        if (value is IImmutableObservationProjection && !value.GetType().IsValueType)
        {
            // The declared type is significant: a polymorphic base emits metadata which a concrete
            // interpretation may omit. No ID, content hash or equality override identifies the owner.
            var types = Owners.Get(value, static _ => new()).Types;
            var state = types.GetOrAdd(declaredType, static _ => new());
            var slot = state.Prepared;
            if (active?.Contains((value, declaredType)) == true) throw new JsonException("A cycle was detected in the immutable declared value.");
            var context = (value, declaredType, depth, active, state);
            // Nested preparation happens outside another owner's lock. This avoids lock-order
            // deadlocks for invalid cyclic graphs; publication still keeps the first successful value.
            var prepared = active is null || slot.IsPrepared
                ? slot.Get(context, Prepare)
                : slot.Get(Prepare(context), static completed => completed);
            Volatile.Write(ref state.ReadCandidate, null);
            if (depth + prepared.Height > 64) throw new JsonException("The declared value exceeds the JSON maximum depth of 64.");
            return prepared.Value;
        }
        return ProjectCore(value, declaredType, options, depth, active, candidate);
    }

    static PreparedValue Prepare((object value, Type declaredType, int depth, HashSet<(object Owner, Type Type)>? active, Interpretation state) context)
    {
        var active = context.active ?? new HashSet<(object Owner, Type Type)>(OwnerTypes);
        if (!active.Add((context.value, context.declaredType))) throw new JsonException("A cycle was detected in the immutable declared value.");
        try
        {
            ObservationValue? candidate = Volatile.Read(ref context.state.ReadCandidate) is { } prior
                && prior.TryGet(out var previous) ? previous : null;
            var observed = ProjectCore(context.value, context.declaredType, ObservationValue.DeclaredJsonProfile, context.depth, active, candidate);
            return new(observed, Height(observed));
        }
        finally { active.Remove((context.value, context.declaredType)); }
    }

    static ObservationValue ProjectCore(object value, Type declaredType, JsonSerializerOptions options, int depth, HashSet<(object Owner, Type Type)>? active, ObservationValue? candidate = null)
    {
        if (value is string textValue) return ProjectString(textValue, options, depth);
        if (value is bool boolean) return ObservationValue.FromBool(boolean);
        if (value is int integer) return ObservationValue.FromInt64(integer);
        var contract = Contracts.Get(declaredType, static type => new(type, ObservationValue.DeclaredJsonProfile));
        if (contract.Literals is { } literals && literals.TryGetValue(value, out var literal)) return literal;
        var info = contract.Info;
        if (info.Kind == JsonTypeInfoKind.Object)
        {
            if (info.OnSerializing is not null || info.OnSerialized is not null)
                return ReuseCandidate(Serialize(value, declaredType, options, depth), candidate);
            if (depth >= 64) throw new JsonException("The declared value exceeds the JSON maximum depth of 64.");
            var properties = info.Properties;
            object? tag = null;
            string? tagName = null;
            if (info.PolymorphismOptions is { } polymorphism)
            {
                var runtimeType = value.GetType();
                for (var derivedIndex = 0; derivedIndex < polymorphism.DerivedTypes.Count; derivedIndex++)
                {
                    var derived = polymorphism.DerivedTypes[derivedIndex];
                    if (derived.DerivedType == runtimeType)
                    {
                        tag = derived.TypeDiscriminator;
                        tagName = polymorphism.TypeDiscriminatorPropertyName;
                        var derivedInfo = Contracts.Get(runtimeType, static type => new(type, ObservationValue.DeclaredJsonProfile)).Info;
                        if (derivedInfo.Kind != JsonTypeInfoKind.Object || derivedInfo.OnSerializing is not null || derivedInfo.OnSerialized is not null)
                            return ReuseCandidate(Serialize(value, declaredType, options, depth), candidate);
                        properties = derivedInfo.Properties;
                        break;
                    }
                }
                // Keep unknown subtype policy and converter dispatch serializer-owned.
                if (tag is null) return ReuseCandidate(Serialize(value, declaredType, options, depth), candidate);
            }
            var capacity = properties.Count + (tag is null ? 0 : 1);
            if (capacity > 16) return ReuseCandidate(Serialize(value, declaredType, options, depth), candidate);
            var previous = candidate is { Kind: ObservationValueKind.Object } ? candidate.Value.Fields : null;
            var fields = previous is null ? new KeyValuePair<string, ObservationValue>[capacity] : null;
            var count = 0;
            if (tagName is not null)
            {
                var name = ProjectString(tagName, options, depth).String!;
                var tagLiteral = tag is string textTag ? ProjectString(textTag, options, depth) : ObservationValue.FromObject(tag);
                if (fields is not null) fields[count] = new(name, tagLiteral);
                else if (!previous!.TryGetValue(name, out var tagValue) || !SameRepresentation(tagValue, tagLiteral))
                    return ProjectCore(value, declaredType, options, depth, active);
                count++;
            }
            for (var propertyIndex = 0; propertyIndex < properties.Count; propertyIndex++)
            {
                var property = properties[propertyIndex];
                if (property.Get is not { } get) continue;
                // These contracts currently have no contextual member converters/extension data.
                // Future additions retain the complete owning serializer rather than a partial interpretation.
                if (property.CustomConverter is not null || property.IsExtensionData || property.NumberHandling is not null)
                    return ReuseCandidate(Serialize(value, declaredType, options, depth), candidate);
                var member = get(value);
                if (property.ShouldSerialize is { } shouldSerialize && !shouldSerialize(value, member)) continue;
                var name = ProjectString(property.Name, options, depth).String!;
                ObservationValue? old = previous is not null && previous.TryGetValue(name, out var oldValue) ? oldValue : null;
                var projected = ProjectValue(member, property.PropertyType, options, depth + 1, active, old);
                if (fields is not null) fields[count] = new(name, projected);
                else if (old is null || !SameRepresentation(old.Value, projected))
                    return ProjectCore(value, declaredType, options, depth, active);
                count++;
            }
            if (fields is null)
                return previous!.Count == count ? candidate!.Value : ProjectCore(value, declaredType, options, depth, active);
            return ObservationValue.FromObject(SmallObservationFields.FromOwnedProperties(fields, count));
        }
        if (info.Kind == JsonTypeInfoKind.Enumerable && contract.ElementType is { } elementType)
        {
            if (depth >= 64) throw new JsonException("The declared value exceeds the JSON maximum depth of 64.");
            if (candidate is { Kind: ObservationValueKind.Array } prior)
            {
                var index = 0;
                foreach (var item in (IEnumerable)value)
                {
                    if (index >= prior.Array.Length || !SameRepresentation(prior.Array[index],
                        ProjectValue(item, elementType, options, depth + 1, active, prior.Array[index])))
                        return ProjectCore(value, declaredType, options, depth, active);
                    index++;
                }
                return index == prior.Array.Length ? prior : ProjectCore(value, declaredType, options, depth, active);
            }
            var values = ImmutableArray.CreateBuilder<ObservationValue>(value is ICollection collection ? collection.Count : 0);
            foreach (var item in (IEnumerable)value)
                values.Add(ProjectValue(item, elementType, options, depth + 1, active));
            return ObservationValue.FromImmutableArray(values.Count == values.Capacity ? values.MoveToImmutable() : values.ToImmutable());
        }
        // Annotation values already own JSON. No write/parse/clone is needed to interpret it.
        if (value is AnnotationValue annotation)
        {
            if (candidate is { } tokenCandidate && !annotation.TryGetString(out _)
                && MatchesJson(annotation.Value, tokenCandidate)) return tokenCandidate;
            var observed = annotation.TryGetString(out var text)
                ? ProjectString(text!, options, depth) : ObservationValue.FromStrictJsonElement(annotation.Value);
            RequireDepth(observed, depth);
            return ReuseCandidate(observed, candidate);
        }
        if (value is ImmutableDictionary<AnnotationKey, AnnotationValue> annotations)
        {
            if (depth >= 64) throw new JsonException("The declared value exceeds the JSON maximum depth of 64.");
            if (annotations.Count == 0) return ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty);
            if (candidate is { Kind: ObservationValueKind.Object } prior && prior.Fields!.Count == annotations.Count)
            {
                var matches = true;
                foreach (var entry in annotations)
                {
                    ArgumentNullException.ThrowIfNull(entry.Key.Value);
                    var name = ProjectString(entry.Key.Value, options, depth).String!;
                    if (!StringComparer.Ordinal.Equals(name, entry.Key.Value)
                        || !prior.Fields.TryGetValue(name, out var old)
                        || !SameRepresentation(old, ProjectValue(entry.Value, typeof(AnnotationValue), options, depth + 1, active, old)))
                    { matches = false; break; }
                }
                if (matches) return prior;
            }
            var fields = new Dictionary<string, ObservationValue>(annotations.Count, StringComparer.Ordinal);
            foreach (var entry in annotations)
            {
                ArgumentNullException.ThrowIfNull(entry.Key.Value);
                if (!fields.TryAdd(ProjectString(entry.Key.Value, options, depth).String!,
                    ProjectValue(entry.Value, typeof(AnnotationValue), options, depth + 1, active)))
                    throw new JsonException("Duplicate annotation property.");
            }
            return ReuseCandidate(ObservationValue.FromOwnedObjectInOrdinalOrder(fields), candidate);
        }
        if (info.Converter is SingleValueWrapperJsonConverter.IObservationStringProjection wrapper
            && wrapper.TryGetString(value, out var wrapped))
            return wrapped is not null ? ProjectString(wrapped, options, depth) : ObservationValue.Null;
        return ReuseCandidate(Serialize(value, declaredType, options, depth), candidate);
    }

    // Reuse the scalar reader as authority. Small token objects compare using existing field names,
    // avoiding new strings; wider objects retain the linear strict projection instead of quadratic search.
    static bool MatchesJson(JsonElement token, ObservationValue value)
    {
        if (token.ValueKind == JsonValueKind.String)
            return value.Kind == ObservationValueKind.String && token.ValueEquals(value.String);
        if (token.ValueKind == JsonValueKind.Array)
        {
            if (value.Kind != ObservationValueKind.Array || token.GetArrayLength() != value.Array.Length) return false;
            var index = 0;
            foreach (var item in token.EnumerateArray()) if (!MatchesJson(item, value.Array[index++])) return false;
            return true;
        }
        if (token.ValueKind == JsonValueKind.Object)
        {
            if (value.Kind != ObservationValueKind.Object || token.GetPropertyCount() != value.Fields!.Count || value.Fields.Count > 8) return false;
            var visitor = new TokenEqualityVisitor(token);
            ObservationFieldTraversal.Visit(value.Fields, ref visitor);
            return visitor.Equal;
        }
        return SameRepresentation(ObservationValue.FromJsonElement(token), value);
    }

    struct TokenEqualityVisitor(JsonElement token) : IObservationFieldVisitor
    {
        internal bool Equal = true;
        public void Visit<TEnumerator>(TEnumerator fields, bool canonical)
            where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
        {
            using (fields)
                while (fields.MoveNext())
                    if (!token.TryGetProperty(fields.Current.Key, out var member) || !MatchesJson(member, fields.Current.Value))
                    { Equal = false; return; }
        }
    }

    static ObservationValue ReuseCandidate(ObservationValue projected, ObservationValue? candidate) =>
        candidate is { } previous && SameRepresentation(previous, projected) ? previous : projected;

    // Encoding equality is stricter than semantic ObservationValue equality: decimal scale and
    // floating signed zero affect lexical fingerprints. Never authorize reuse with semantic equality.
    static bool SameRepresentation(ObservationValue left, ObservationValue right)
    {
        if (left.Kind != right.Kind) return false;
        if (left.Kind == ObservationValueKind.Decimal)
        {
            Span<int> a = stackalloc int[4], b = stackalloc int[4];
            decimal.GetBits(left.Decimal, a); decimal.GetBits(right.Decimal, b);
            return a.SequenceEqual(b);
        }
        if (left.Kind == ObservationValueKind.Double)
            return BitConverter.DoubleToInt64Bits(left.Double) == BitConverter.DoubleToInt64Bits(right.Double);
        if (left.Kind == ObservationValueKind.Array)
        {
            if (left.Array == right.Array) return true;
            if (left.Array.Length != right.Array.Length) return false;
            for (var i = 0; i < left.Array.Length; i++)
                if (!SameRepresentation(left.Array[i], right.Array[i])) return false;
            return true;
        }
        if (left.Kind == ObservationValueKind.Object)
        {
            if (ReferenceEquals(left.Fields, right.Fields)) return true;
            if (left.Fields!.Count != right.Fields!.Count) return false;
            var visitor = new EqualityVisitor(right.Fields);
            ObservationFieldTraversal.Visit(left.Fields, ref visitor);
            return visitor.Equal;
        }
        return left.Equals(right);
    }

    struct EqualityVisitor(IReadOnlyDictionary<string, ObservationValue> right) : IObservationFieldVisitor
    {
        internal bool Equal = true;
        public void Visit<TEnumerator>(TEnumerator fields, bool canonical)
            where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
        {
            using (fields)
                while (fields.MoveNext())
                    if (!right.TryGetValue(fields.Current.Key, out var other) || !SameRepresentation(fields.Current.Value, other))
                    { Equal = false; return; }
        }
    }

    static ObservationValue ProjectString(string text, JsonSerializerOptions options, int depth)
    {
        if (!JsonText.IsWellFormed(text)) return Serialize(text, typeof(string), options, depth);
        return ObservationValue.FromString(text);
    }

    static ObservationValue Serialize(object value, Type type, JsonSerializerOptions options, int depth)
    {
        using var document = JsonSerializer.SerializeToDocument(value, type, options);
        var observed = ObservationValue.FromJsonElement(document.RootElement);
        RequireDepth(observed, depth);
        return observed;
    }

    static void RequireDepth(ObservationValue value, int depth) => _ = Height(value, depth);

    static int Height(ObservationValue value, int depth = 0)
    {
        if (value.Kind is not (ObservationValueKind.Array or ObservationValueKind.Object)) return 0;
        if (depth >= 64) throw new JsonException("The declared value exceeds the JSON maximum depth of 64.");
        var maximum = 0;
        if (value.Kind == ObservationValueKind.Array)
        {
            foreach (var item in value.Array) maximum = Math.Max(maximum, Height(item, depth + 1));
        }
        else
        {
            var visitor = new HeightVisitor(depth);
            ObservationFieldTraversal.Visit(value.Fields!, ref visitor);
            maximum = visitor.Maximum;
        }
        return maximum + 1;
    }

    struct HeightVisitor(int depth) : IObservationFieldVisitor
    {
        internal int Maximum;
        public void Visit<TEnumerator>(TEnumerator fields, bool canonical)
            where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
        {
            using (fields)
                while (fields.MoveNext()) Maximum = Math.Max(Maximum, Height(fields.Current.Value, depth + 1));
        }
    }

    sealed record PreparedValue(ObservationValue Value, int Height);

    sealed class Contract
    {
        internal readonly JsonTypeInfo Info;
        internal readonly Type? ElementType;
        internal readonly Dictionary<object, ObservationValue>? Literals;
        internal Contract(Type type, JsonSerializerOptions options)
        {
            Info = options.GetTypeInfo(type);
            if (type.IsEnum)
            {
                Literals = new();
                foreach (var literal in Enum.GetValues(type))
                    Literals[literal] = Serialize(literal, type, options, 0);
            }
            if (Info.Kind == JsonTypeInfoKind.Enumerable)
                ElementType = type.IsArray ? type.GetElementType() : type.GetInterfaces()
                    .Append(type).FirstOrDefault(candidate => candidate.IsGenericType
                        && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
        }
    }
}
