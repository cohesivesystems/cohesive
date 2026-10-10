using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// Each accessor owns its index type, factory and typed slot. The shared container only coordinates
// successful publication. Its exact owner is fixed, including for nodes that need no index.
internal sealed class ObservationValidationMetadata(object owner)
{
    static readonly ConditionalWeakTable<object, ObservationValidationMetadata> Standalone = new();
    internal object Owner { get; } = owner;
    internal HashSet<string>? KnownNames;
    internal HashSet<string>? Members;
    internal Dictionary<string, int>? Cases;

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
        ref var slot = ref TAccessor.Slot(this);
        var prepared = Volatile.Read(ref slot);
        if (prepared is not null) return prepared;
        lock (this)
        {
            prepared = slot;
            if (prepared is null)
            {
                prepared = TAccessor.Create(owner);
                Volatile.Write(ref slot, prepared);
            }
            return prepared;
        }
    }
}

internal interface IValidationIndexAccessor<TOwner, TIndex> where TOwner : class where TIndex : class
{
    static abstract TIndex Create(TOwner owner);
    static abstract ref TIndex? Slot(ObservationValidationMetadata metadata);
}
