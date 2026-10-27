using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Cohesive.Model;

// Immutable sparse physical storage used by strict nested ingestion and benchmarks. Meaning and name/ordinal
// identity remain layout-owned. Present values are packed in declaration order; a bitmap plus
// per-word rank makes reads independent of the number of preceding fields, including wide layouts.
internal sealed class CompactOrdinalObservationFields : IOrdinalObservationStorage
{
    readonly ImmutableArray<ObservationValue> values;
    readonly ImmutableArray<RankedWord> words;
    readonly ulong inlinePresence;

    CompactOrdinalObservationFields(ObservationLayout layout, ImmutableArray<ObservationValue> values,
        ulong inlinePresence, ImmutableArray<RankedWord> words)
    { Layout = layout; this.values = values; this.inlinePresence = inlinePresence; this.words = words; }

    // Borrow source slots only during construction; every retained array is freshly owned.
    internal static CompactOrdinalObservationFields Create(ObservationLayout layout, ReadOnlySpan<ObservationValue> slots)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (slots.Length != layout.Count) throw new ArgumentException("One source slot per layout field is required.", nameof(slots));
        var count = 0;
        foreach (var value in slots) if (value.Kind != ObservationValueKind.Undefined) count++;
        if (count == 0) return new(layout, [], 0, default);
        var packed = ImmutableArray.CreateBuilder<ObservationValue>(count);
        var wordCount = ((layout.Count - 1) >> 6) + 1;
        var ranked = wordCount > 1 ? new RankedWord[wordCount] : null;
        ulong inline = 0;
        for (var word = 0; word < wordCount; word++)
        {
            var prefix = packed.Count;
            ulong bits = 0;
            var start = word << 6;
            var end = start + Math.Min(64, slots.Length - start);
            for (var ordinal = start; ordinal < end; ordinal++)
                if (slots[ordinal].Kind != ObservationValueKind.Undefined)
                {
                    bits |= 1UL << (ordinal & 63);
                    packed.Add(slots[ordinal]);
                }
            if (ranked is null) inline = bits;
            else ranked[word] = new(bits, prefix);
        }
        return new(layout, packed.MoveToImmutable(), inline,
            ranked is null ? default : ImmutableCollectionsMarshal.AsImmutableArray(ranked));
    }

    public ObservationLayout Layout { get; }
    public QualifiedShapeId ShapeId => Layout.ShapeId;
    public int Count => values.Length;
    public ObservationValue this[string key] => TryGetField(key, out var value) ? value : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys { get { foreach (var pair in this) yield return pair.Key; } }
    public IEnumerable<ObservationValue> Values { get { foreach (var pair in this) yield return pair.Value; } }
    public bool ContainsKey(string key) => TryGetField(key, out _);
    public bool TryGetValue(string key, out ObservationValue value) => TryGetField(key, out value);
    public bool TryGetField(string fieldIdentity, out ObservationValue field)
    {
        if (Layout.TryGetOrdinal(fieldIdentity, out var ordinal)) return TryGetField(ordinal, out field);
        field = default; return false;
    }
    public bool TryGetField(int ordinal, out ObservationValue field)
    {
        if ((uint)ordinal >= (uint)Layout.Count || values.IsEmpty) { field = default; return false; }
        var word = ordinal >> 6;
        var bits = words.IsDefault ? inlinePresence : words[word].Bits;
        var bit = 1UL << (ordinal & 63);
        if ((bits & bit) == 0) { field = default; return false; }
        var prefix = words.IsDefault ? 0 : words[word].Prefix;
        field = values[prefix + BitOperations.PopCount(bits & (bit - 1))];
        return true;
    }

    // Scans presence words only; absent runs cost one masked word read, not a rank lookup per ordinal.
    public int NextPresentOrdinal(int after)
    {
        var start = after + 1;
        if (start >= Layout.Count || values.IsEmpty) return -1;
        var word = start >> 6;
        var wordCount = words.IsDefault ? 1 : words.Length;
        var bits = (words.IsDefault ? inlinePresence : words[word].Bits) & (ulong.MaxValue << (start & 63));
        while (bits == 0)
        {
            if (++word >= wordCount) return -1;
            bits = words[word].Bits;
        }
        return (word << 6) + BitOperations.TrailingZeroCount(bits);
    }

    public Enumerator GetEnumerator() => new(this, canonical: false);
    internal Enumerator GetCanonicalEnumerator() => new(this, canonical: true);
    IEnumerator<KeyValuePair<string, ObservationValue>> IEnumerable<KeyValuePair<string, ObservationValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Unlike dense enumeration, ordinary iteration visits set bits only. Canonical iteration
    // follows the layout's already-prepared lexical permutation, without a per-record sort/index.
    internal struct Enumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        readonly CompactOrdinalObservationFields owner;
        readonly bool canonical;
        int emitted;
        int position;
        int word;
        ulong remaining;
        internal Enumerator(CompactOrdinalObservationFields owner, bool canonical)
        {
            this.owner = owner; this.canonical = canonical; emitted = 0; position = -1; word = 0;
            remaining = owner.inlinePresence;
            if (!owner.words.IsDefault) remaining = owner.words[0].Bits;
            Current = default;
        }
        public KeyValuePair<string, ObservationValue> Current { get; private set; }
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            if (emitted == owner.Count) return false;
            if (canonical)
            {
                while (++position < owner.Layout.Count)
                {
                    var ordinal = owner.Layout.CanonicalJsonOrdinals[position];
                    if (!owner.TryGetField(ordinal, out var field)) continue;
                    Current = new(owner.Layout.FieldIdentities[ordinal], field); emitted++; return true;
                }
                return false;
            }
            while (remaining == 0) remaining = owner.words[++word].Bits;
            var slot = (word << 6) + BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            Current = new(owner.Layout.FieldIdentities[slot], owner.values[emitted++]);
            return true;
        }
        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }

    readonly struct RankedWord(ulong bits, int prefix)
    {
        internal ulong Bits { get; } = bits;
        internal int Prefix { get; } = prefix;
    }
}
