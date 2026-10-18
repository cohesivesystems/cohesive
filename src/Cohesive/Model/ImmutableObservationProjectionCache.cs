using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cohesive.Model;

/// <summary>Declares a CLR interpretation safe to reuse for one immutable observation payload.</summary>
/// <remarks>
/// Implementations and their entire reachable graph must be deeply immutable and safe for concurrent reads.
/// When projected through a declared JSON contract, the same immutable CLR source may also retain its
/// prepared observation by exact object identity and declared type under the framework's fixed profile.
/// Decoded reuse is scoped to the exact observation storage, projection type, frozen serializer options instance and
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
        Profile[] profiles = [];
        internal ConcurrentDictionary<Key, PreparationSlot<object?>> For(JsonSerializerOptions options)
        {
            Profile? found = null;
            var expired = false;
            foreach (var profile in Volatile.Read(ref profiles))
                if (!profile.Options.TryGetTarget(out var owner)) expired = true;
                else if (ReferenceEquals(owner, options)) found = profile;
            if (found is not null && !expired) return found.Entries;
            lock (this)
            {
                var live = new List<Profile>();
                found = null;
                foreach (var profile in profiles)
                    if (profile.Options.TryGetTarget(out var owner))
                    {
                        if (ReferenceEquals(owner, options)) found = profile;
                        live.Add(profile);
                    }
                if (found is null) { found = new(options); live.Add(found); }
                Volatile.Write(ref profiles, live.ToArray());
                return found.Entries;
            }
        }
    }
    // Do not put strong projections behind a second weak table keyed by globally live options:
    // decoded objects can lead back to this payload after reprojection. Weak profile references
    // inside the payload-owned value keep that cycle entirely behind the outer ephemeron.
    sealed class Profile(JsonSerializerOptions options)
    {
        internal readonly WeakReference<JsonSerializerOptions> Options = new(options);
        internal readonly ConcurrentDictionary<Key, PreparationSlot<object?>> Entries = new();
    }
    readonly record struct Key(Type Type, ObservationBytesJsonEncoding BytesEncoding);

    internal static T? Read<T>(ObservationValue value, JsonSerializerOptions? options, ObservationBytesJsonEncoding bytesEncoding,
        Func<ObservationValue, T?>? materialize = null)
    {
        options ??= JsonSerializerOptions.Default;
        var entries = Owners.GetValue(value.Fields!, static _ => new()).For(options);
        var key = new Key(typeof(T), bytesEncoding);
        var slot = entries.GetOrAdd(key, static _ => new());
        return (T?)slot.Get((value, options, bytesEncoding, materialize), static context =>
        {
            var result = context.materialize is { } read ? read(context.value)
                : context.value.DeserializeIndependent<T>(context.options, context.bytesEncoding);
            if (result is IImmutableObservationProjection immutable)
                PreparedDeclaredObservationProjection.RememberRead(immutable, typeof(T), context.value,
                    retainSource: ReferenceEquals(context.options, Cohesive.Execution.ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions()));
            return result;
        });
    }
}
