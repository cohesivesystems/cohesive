using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// Each accessor owns its index type and factory. The shared container only coordinates
// successful publication. Its exact owner is fixed, including for nodes that need no index.
internal sealed class ObservationValidationMetadata(object owner)
{
    static readonly ConditionalWeakTable<object, ObservationValidationMetadata> Standalone = new();
    internal object Owner { get; } = owner;
    object? index;
    Type? accessorType;

    // Read-only inspection never prepares an index or exposes its publication slot.
    internal object? PreparedIndex => Volatile.Read(ref index);

    internal static ObservationValidationMetadata For(object owner, ObservationValidationPlan? plan,
        ShapeGraph? graph = null)
    {
        var metadata = plan?.Metadata;
        if (metadata is null && graph is not null)
            metadata = owner is TypeDefinition definition
                ? ObservationValidationPlan.GetDefinitionMetadata(definition, graph)
                : owner is TypeRef type ? ObservationValidationPlan.Get(type, graph).Metadata : null;
        metadata ??= Standalone.GetValue(owner, static value => new(value));
        if (!ReferenceEquals(metadata.Owner, owner))
            throw new InvalidOperationException("Validation metadata does not belong to the expected declaration.");
        return metadata;
    }

    internal TIndex Get<TOwner, TIndex, TAccessor>(TOwner owner)
        where TOwner : class where TIndex : class
        where TAccessor : struct, IValidationIndexAccessor<TOwner, TIndex>
    {
        if (!ReferenceEquals(Owner, owner))
            throw new InvalidOperationException("Validation index does not belong to the expected declaration.");
        var prepared = Volatile.Read(ref index);
        if (prepared is not null)
        {
            RequireAccessor<TAccessor>();
            return (TIndex)prepared;
        }
        lock (this)
        {
            prepared = index;
            if (prepared is null)
            {
                prepared = TAccessor.Create(owner);
                // Publish identity before the release write; warm readers acquire both together.
                accessorType = typeof(TAccessor);
                Volatile.Write(ref index, prepared);
            }
            RequireAccessor<TAccessor>();
            return (TIndex)prepared;
        }
    }

    void RequireAccessor<TAccessor>()
    {
        if (accessorType != typeof(TAccessor))
            throw new InvalidOperationException($"Validation metadata was prepared by {accessorType}; requested accessor {typeof(TAccessor)} does not match.");
    }
}

internal interface IValidationIndexAccessor<TOwner, TIndex> where TOwner : class where TIndex : class
{
    static abstract TIndex Create(TOwner owner);
}

// A zero-state token binds the generic pairing once, with no boxing, delegate or writable slot.
internal readonly struct ValidationIndexAccessor<TOwner, TIndex, TAccessor>
    where TOwner : class where TIndex : class
    where TAccessor : struct, IValidationIndexAccessor<TOwner, TIndex>
{
    internal TIndex Get(ObservationValidationMetadata metadata, TOwner owner) =>
        metadata.Get<TOwner, TIndex, TAccessor>(owner);
}
