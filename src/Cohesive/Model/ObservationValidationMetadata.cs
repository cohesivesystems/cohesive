using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// Each accessor owns its index type and factory. The shared container only coordinates
// successful publication. Its exact owner is fixed, including for nodes that need no index.
internal sealed class ObservationValidationMetadata(object owner)
{
    static readonly ConditionalWeakTable<object, ObservationValidationMetadata> Standalone = new();
    internal object Owner { get; } = owner;
    object? index;

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
        if (prepared is not null) return (TIndex)prepared;
        lock (this)
        {
            prepared = index;
            if (prepared is null)
            {
                prepared = TAccessor.Create(owner);
                Volatile.Write(ref index, prepared);
            }
            return (TIndex)prepared;
        }
    }
}

internal interface IValidationIndexAccessor<TOwner, TIndex> where TOwner : class where TIndex : class
{
    static abstract TIndex Create(TOwner owner);
}
