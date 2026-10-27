using System.Collections.Immutable;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

/// <summary>Shared terminal assembly for native and composed complete row results.</summary>
public static class NestedQueryResultAssembler
{
    /// <summary>Assembles one parent using the portable result contract.</summary>
    /// <param name="definition">Canonical field, identity, absence and ordering policy.</param>
    /// <param name="rows">Complete rows from the declared result, after execution has established completeness.</param>
    /// <param name="cancellationToken">Cancels assembly between rows.</param>
    /// <returns>Null observation for no parent, otherwise the nested object.</returns>
    /// <exception cref="ArgumentNullException">Definition is null.</exception>
    /// <exception cref="ArgumentException">The assembly contract is empty or ambiguous.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="InvalidOperationException">Rows violate identity, required-field or consistency guarantees.</exception>
    public static ObservationValue Assemble(NestedQueryResultAssembly definition, ImmutableArray<ObservationValue> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.IsDefaultOrEmpty) return ObservationValue.Null;
        var parentId = Key(rows[0], definition.Identity, required: true)!;
        var parent = Project(rows[0], definition.Fields);
        var children = definition.Collections.Select(_ => new SortedDictionary<string, ObservationValue>(StringComparer.Ordinal)).ToArray();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Key(row, definition.Identity, required: true) != parentId || !Matches(row, definition.Fields, parent))
                throw new InvalidOperationException("Nested result contains multiple parents or conflicting parent fields.");
            for (var index = 0; index < definition.Collections.Length; index++)
            {
                var collection = definition.Collections[index];
                var key = Key(row, collection.Identity, required: false);
                if (key is null) continue;
                var child = Project(row, collection.Fields);
                if (children[index].TryGetValue(key, out var existing) && !existing.Equals(child))
                    throw new InvalidOperationException("Nested result contains conflicting fields for a repeated child identity.");
                children[index][key] = child;
            }
        }
        for (var index = 0; index < definition.Collections.Length; index++)
            parent = parent.WithField(definition.Collections[index].Target, ObservationValue.FromArray([.. children[index].Values]));
        return parent;
    }

    /// <summary>Assembles complete rooted relation rows independently, preserving input provenance and root identity.</summary>
    /// <param name="definition">Canonical nested-result mappings on the relation.</param>
    /// <param name="rows">Complete raw rows with retained root occurrences.</param>
    /// <param name="cancellationToken">Cancellation between roots and rows.</param>
    /// <returns>One nested object for each represented root; absent roots produce no object.</returns>
    /// <exception cref="ArgumentNullException">The assembly is null.</exception>
    /// <exception cref="ArgumentException">The assembly contract or contributing occurrence identities are inconsistent.</exception>
    /// <exception cref="InvalidOperationException">Rows are incomplete, unrooted, or contain inconsistent parent/child data.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static ImmutableArray<RelationQueryOutputRow> AssembleRoots(NestedQueryResultAssembly definition,
        ImmutableArray<RelationQueryOutputRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.IsDefaultOrEmpty) return [];
        if (rows.Any(row => !row.IsComplete || row.Root?.ObservationIdentity is null))
            throw new InvalidOperationException("Nested relation assembly requires complete rooted rows.");
        var result = ImmutableArray.CreateBuilder<RelationQueryOutputRow>();
        foreach (var group in rows.GroupBy(row => (row.Root!.Binding, row.Root.ObservationIdentity))
                     .OrderBy(group => group.Key.Binding.Value, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.ObservationIdentity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = group.First();
            if (Key(first.Value, definition.Identity, required: true) != group.Key.ObservationIdentity)
                throw new InvalidOperationException("Nested relation identity must match its root observation identity.");
            var value = Assemble(definition, [.. group.Select(row => row.Value)], cancellationToken);
            var occurrences = group.SelectMany(row => row.InputOccurrences).ToImmutableArray();
            result.Add(new(definition.Shape, value, ObservationValue.FromString(group.Key.ObservationIdentity!),
                first.Root, occurrences, []));
        }
        return result.ToImmutable();
    }

    static string? Key(ObservationValue row, FieldPath path, bool required)
    {
        if (!row.TryGetField(path, out var value) || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            if (required) throw new InvalidOperationException("Nested result requires a parent identity.");
            return null;
        }
        if (value.Kind != ObservationValueKind.String || string.IsNullOrWhiteSpace(value.String)) throw new InvalidOperationException("Nested result identities must be nonempty ordinal strings.");
        return value.String;
    }

    static bool Matches(ObservationValue row, ImmutableArray<QueryResultFieldMapping> fields, ObservationValue projected)
    {
        foreach (var field in fields)
            if (!row.TryGetField(field.Source, out var actual)
                || !projected.TryGetField(field.Target, out var expected) || !actual.Equals(expected))
                return false;
        return true;
    }

    static ObservationValue Project(ObservationValue row, ImmutableArray<QueryResultFieldMapping> fields)
    {
        var values = new Dictionary<string, ObservationValue>(fields.Length, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!row.TryGetField(field.Source, out var value) || value.Kind == ObservationValueKind.Undefined)
                throw new InvalidOperationException("Nested result is missing a declared field.");
            values.Add(field.Target.Segments[0].Segment!, value);
        }
        return ObservationValue.FromObject(values);
    }
}
