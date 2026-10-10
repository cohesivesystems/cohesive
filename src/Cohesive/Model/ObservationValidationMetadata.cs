using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// Graph-bound preparation belongs to the plan node. Standalone declaration checks share one weak
// fallback table. Each index publishes successes only; failed preparation leaves its slot retryable.
internal sealed class ObservationValidationMetadata
{
    static readonly ConditionalWeakTable<object, ObservationValidationMetadata> Standalone = new();
    HashSet<string>? names;
    HashSet<string>? literals;
    Dictionary<string, int>? cases;

    internal static ObservationValidationMetadata For(object owner, ObservationValidationPlan? plan,
        ShapeGraph? graph = null)
    {
        if (plan is null && graph is not null && owner is TypeRef type)
            plan = ObservationValidationPlan.Get(type, graph);
        Debug.Assert(plan is null || ReferenceEquals(owner, plan.Type) || ReferenceEquals(owner, plan.Definition),
            "Metadata owner must match its validation plan.");
        return plan?.Metadata ?? Standalone.GetValue(owner, static _ => new());
    }

    internal HashSet<string> KnownNames(ObjectTypeRef owner) => Prepare(ref names, owner,
        static type => new(type.Fields.Select(static field => field.Name), StringComparer.OrdinalIgnoreCase));
    internal HashSet<string> KnownNames(TypeDefinition.Structural owner) => Prepare(ref names, owner,
        static type => new(type.Fields.Select(static field => field.Name.Value), StringComparer.OrdinalIgnoreCase));
    internal bool Contains(EnumTypeRef owner, string value) => Prepare(ref literals, owner,
        static type => new(type.Members, StringComparer.Ordinal)).Contains(value);
    internal bool Contains(TypeDefinition.Enum owner, string value) => Prepare(ref literals, owner, static type =>
    {
        HashSet<string> result = new(type.Values.Length * 2, StringComparer.Ordinal);
        foreach (var member in type.Values)
        {
            result.Add(member.Name);
            if (member.Value is { } literal) result.Add(literal);
        }
        return result;
    }).Contains(value);
    internal int FindCase(TypeDefinition.Union owner, string value)
    {
        var index = Prepare(ref cases, owner, static type =>
        {
            Dictionary<string, int> result = new(type.Cases.Length, StringComparer.Ordinal);
            for (var i = 0; i < type.Cases.Length; i++) result.TryAdd(type.Cases[i].DiscriminatorValue, i);
            return result;
        });
        return index.TryGetValue(value, out var matched) ? matched : -1;
    }

    TValue Prepare<TOwner, TValue>(ref TValue? slot, TOwner owner, Func<TOwner, TValue> factory)
        where TValue : class
    {
        var prepared = Volatile.Read(ref slot);
        if (prepared is not null) return prepared;
        lock (this)
        {
            prepared = slot;
            if (prepared is null)
            {
                prepared = factory(owner);
                Volatile.Write(ref slot, prepared);
            }
            return prepared;
        }
    }
}
