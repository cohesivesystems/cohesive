using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// A slot binds its owner, index type and factory once at construction. Consumers cannot refill
// it from another owner or index kind. Graph plans share declarations; standalone keys remain weak.
internal abstract class ObservationValidationMetadata
{
    static readonly ConditionalWeakTable<object, ObservationValidationMetadata> Standalone = new();
    internal abstract object Owner { get; }

    internal static ObservationValidationMetadata<TOwner, TIndex> For<TOwner, TIndex>(TOwner owner,
        ObservationValidationPlan? plan, ShapeGraph? graph = null) where TOwner : class where TIndex : class
    {
        if (plan is null && graph is not null && owner is TypeRef type)
            plan = ObservationValidationPlan.Get(type, graph);
        var metadata = plan?.Metadata ?? Standalone.GetValue(owner, Create);
        if (!ReferenceEquals(metadata.Owner, owner)
            || metadata is not ObservationValidationMetadata<TOwner, TIndex> typed)
            throw new InvalidOperationException("Validation metadata owner or index type does not match the declaration.");
        return typed;
    }

    internal static ObservationValidationMetadata Create(object owner) => owner switch
    {
        ObjectTypeRef type => new ObservationValidationMetadata<ObjectTypeRef, HashSet<string>>(type,
            static value => new(value.Fields.Select(static field => field.Name), StringComparer.OrdinalIgnoreCase)),
        TypeDefinition.Structural type => new ObservationValidationMetadata<TypeDefinition.Structural, HashSet<string>>(type,
            static value => new(value.Fields.Select(static field => field.Name.Value), StringComparer.OrdinalIgnoreCase)),
        EnumTypeRef type => new ObservationValidationMetadata<EnumTypeRef, HashSet<string>>(type,
            static value => new(value.Members, StringComparer.Ordinal)),
        TypeDefinition.Enum type => new ObservationValidationMetadata<TypeDefinition.Enum, HashSet<string>>(type, static value =>
        {
            HashSet<string> result = new(value.Values.Length * 2, StringComparer.Ordinal);
            foreach (var member in value.Values)
            {
                result.Add(member.Name);
                if (member.Value is { } literal) result.Add(literal);
            }
            return result;
        }),
        TypeDefinition.Union type => new ObservationValidationMetadata<TypeDefinition.Union, Dictionary<string, int>>(type, static value =>
        {
            Dictionary<string, int> result = new(value.Cases.Length, StringComparer.Ordinal);
            for (var i = 0; i < value.Cases.Length; i++) result.TryAdd(value.Cases[i].DiscriminatorValue, i);
            return result;
        }),
        _ => throw new InvalidOperationException("This declaration does not have validation indexes.")
    };
}

internal sealed class ObservationValidationMetadata<TOwner, TIndex>(TOwner owner, Func<TOwner, TIndex> factory)
    : ObservationValidationMetadata where TOwner : class where TIndex : class
{
    TIndex? index;
    internal override object Owner => owner;
    internal TIndex Index
    {
        get
        {
            var prepared = Volatile.Read(ref index);
            if (prepared is not null) return prepared;
            lock (this)
            {
                prepared = index;
                if (prepared is null)
                {
                    prepared = factory(owner);
                    Volatile.Write(ref index, prepared);
                }
                return prepared;
            }
        }
    }
}
