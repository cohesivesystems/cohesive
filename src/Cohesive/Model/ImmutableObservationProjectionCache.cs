using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cohesive.Model;

/// <summary>Declares a CLR interpretation safe to reuse for one immutable observation payload.</summary>
/// <remarks>
/// Implementations and their entire reachable graph must be deeply immutable and safe for concurrent reads.
/// Reuse is scoped to the exact observation storage, projection type, frozen serializer options instance and
/// binary encoding. It conveys no validation, tenant, authorization or deployment-admission evidence.
/// Mutable projections must not implement this interface. Projection, including constructors and converters, must be deterministic and
/// independent of ambient invocation, authorization, tenant and service state. Changing any field creates
/// independent preparation. The payload and serializer profile are weakly owned, not retained globally.
/// </remarks>
public interface IImmutableObservationProjection;

// The immutable observation's field storage owns these interpretations. Weak keys do not retain a
// discarded payload; no fingerprint, request identity or tenant result is used as a cache key.
internal static class ImmutableObservationProjectionCache
{
    static readonly ConditionalWeakTable<object, Projections> Owners = new();
    sealed class Projections
    {
        internal readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<Key, PreparationSlot<object?>>> Profiles = new();
    }
    readonly record struct Key(Type Type, ObservationBytesJsonEncoding BytesEncoding);

    internal static T? Read<T>(ObservationValue value, JsonSerializerOptions? options, ObservationBytesJsonEncoding bytesEncoding,
        Func<ObservationValue, T?>? materialize = null)
    {
        options ??= JsonSerializerOptions.Default;
        var profiles = Owners.GetValue(value.Fields!, static _ => new()).Profiles;
        var entries = profiles.GetValue(options, static _ => new());
        var key = new Key(typeof(T), bytesEncoding);
        var slot = entries.GetOrAdd(key, static _ => new());
        return (T?)slot.Get((value, options, bytesEncoding, materialize), static context =>
            context.materialize is { } read ? read(context.value)
                : context.value.DeserializeIndependent<T>(context.options, context.bytesEncoding));
    }
}
