using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Factory for reflection-configured builder for object-to-observed-shape mapping.
/// </summary>
public static class ObjectObservationMapper
{
    /// <summary>
    /// Starts a mapper builder for <typeparamref name="T"/>.
    /// </summary>
    public static ObjectObservationMapperBuilder<T> For<T>(
        ShapeId schemaId,
        ShapeMappingContext? context = null
        ) => new(schemaId, context);
}

/// <summary>
/// Reflection-backed mapper from object instances to observed shapes.
/// </summary>
public sealed class ObjectObservationMapper<T> : IObjectObservationMapper<T>
{
    readonly PropertyAccessor<T>[] accessors;
    readonly ObjectObservationMetadataAccessors<T> metadataAccessors;
    readonly ulong[] hasValueBitMask;

    internal ObjectObservationMapper(
        ObservationLayout layout,
        PropertyAccessor<T>[] accessors,
        ObjectObservationMetadataAccessors<T> metadataAccessors
        )
    {
        Layout = layout;
        this.accessors = Guard.RequireNotNull(accessors);
        this.metadataAccessors = Guard.RequireNotNull(metadataAccessors);
        hasValueBitMask = BuildFullPresenceBitMask(layout.Count);
    }
    
    /// <summary>Gets the layout.</summary>
    public ObservationLayout Layout { get; }

    /// <inheritdoc />
    public Observation Map(T source, ObjectObservationMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        metadata ??= new();

        var resolvedId = metadata.Id;
        if (resolvedId is null)
        {
            if (metadataAccessors.Id is null)
                throw new InvalidOperationException($"Mapper for '{typeof(T).Name}' cannot infer observation id by convention. Configure id extraction with WithId(...) or pass id explicitly.");

            resolvedId = metadataAccessors.Id(source);
        }
        else
        {
            resolvedId = Guard.RequireNotNullOrWhiteSpace(resolvedId);
        }

        var resolvedVersion = metadata.Version ?? metadataAccessors.Version?.Invoke(source) ?? 0;

        var values = new ObservationValue[accessors.Length];
        for (var i = 0; i < accessors.Length; i++)
        {
            values[i] = ObservationValue.FromObject(accessors[i].Getter(source));
        }

        return new(
            layout: Layout,
            id: resolvedId,
            valuesByOrdinal: new(values),
            hasValueBitMask: new(hasValueBitMask),
            version: resolvedVersion,
            lineage: metadata.Lineage
            );
    }

    static ulong[] BuildFullPresenceBitMask(int fieldCount)
    {
        var requiredWords = ObservationBuffer.RequiredWordCount(fieldCount);
        if (requiredWords == 0)
            return [];
        
        var bitMask = new ulong[requiredWords];
        Array.Fill(bitMask, ulong.MaxValue);

        var trailingBits = fieldCount & 63;
        if (trailingBits != 0)
            bitMask[^1] = (1UL << trailingBits) - 1UL;

        return bitMask;
    }
}

sealed record PropertyAccessor<T>(string FieldIdentity, Func<T, object?> Getter);

sealed record ObjectObservationMetadataAccessors<T>(
    Func<T, string>? Id,
    Func<T, long>? Version
    );
