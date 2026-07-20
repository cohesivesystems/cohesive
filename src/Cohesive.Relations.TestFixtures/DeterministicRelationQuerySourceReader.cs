using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;

namespace Cohesive.Relations.TestFixtures;

/// <summary>Deterministic bounded source reader used by physical-execution acceptance tests.</summary>
sealed class DeterministicRelationQuerySourceReader : IRelationQuerySourceReader
{
    readonly ImmutableArray<SourceRow> rows;
    readonly ImmutableDictionary<string, SourceRow> rowsByIdentity;
    readonly Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult>? resultFactory;
    readonly Action<RelationQuerySourceReadRequest>? afterRead;
    readonly bool recordRequests;
    readonly ConcurrentQueue<RelationQuerySourceReadRequest> requests = new();

    public DeterministicRelationQuerySourceReader(
        RelationQuerySourceReaderDescriptor descriptor,
        ImmutableArray<SourceRow> rows,
        Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult>? resultFactory = null,
        Action<RelationQuerySourceReadRequest>? afterRead = null,
        bool recordRequests = true)
    {
        Descriptor = descriptor;
        this.rows = rows.IsDefault
            ? []
            : [.. rows.OrderBy(static row => row.Identity, StringComparer.Ordinal)];
        rowsByIdentity = this.rows.ToImmutableDictionary(
            static row => row.Identity,
            StringComparer.Ordinal);
        this.resultFactory = resultFactory;
        this.afterRead = afterRead;
        this.recordRequests = recordRequests;
    }

    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    public ImmutableArray<RelationQuerySourceReadRequest> Requests => [.. requests];

    public ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (recordRequests)
            requests.Enqueue(request);

        var result = resultFactory is not null
            ? resultFactory(request)
            : new RelationQuerySourceReadResult(
                RelationQuerySourceReadState.Complete,
                SelectAndProject(request),
                $"fake/{request.Stage.Value}");
        afterRead?.Invoke(request);
        return ValueTask.FromResult(result);
    }

    ImmutableArray<RelationQuerySourceReadObservation> SelectAndProject(
        RelationQuerySourceReadRequest request)
    {
        var capacity = request.Constraint switch
        {
            RelationQueryBoundedEnumeration enumeration => checked((int)Math.Min(
                Math.Min(enumeration.MaximumRows, int.MaxValue),
                rows.Length)),
            RelationQueryIdentityBatchLookup lookup => Math.Min(rows.Length, lookup.Identities.Length),
            RelationQueryRelationshipKeyBatchLookup lookup => Math.Min(rows.Length, lookup.Keys.Length),
            _ => throw new NotSupportedException(
                $"The deterministic reader does not support '{request.Constraint.GetType().Name}'.")
        };
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(capacity);
        switch (request.Constraint)
        {
            case RelationQueryBoundedEnumeration:
                for (var index = 0; index < capacity; index++)
                    selected.Add(Project(request, rows[index]));
                break;
            case RelationQueryIdentityBatchLookup lookup:
                foreach (var identity in lookup.Identities)
                {
                    if (rowsByIdentity.TryGetValue(identity, out var row))
                        selected.Add(Project(request, row));
                }
                break;
            case RelationQueryRelationshipKeyBatchLookup lookup:
                foreach (var row in rows)
                {
                    if (Matches(lookup, row))
                        selected.Add(Project(request, row));
                }
                break;
        }

        return selected.Count == selected.Capacity
            ? selected.MoveToImmutable()
            : selected.ToImmutable();
    }

    static bool Matches(RelationQueryRelationshipKeyBatchLookup lookup, SourceRow row) =>
        row.Fields.TryGetValue(lookup.RelationshipReference, out var field)
        && field.State == RelationQuerySourceReadFieldState.Value
        && field.Value is { Kind: ObservationValueKind.String, String: { } value }
        && lookup.Keys.Contains(value, StringComparer.Ordinal);

    static RelationQuerySourceReadObservation Project(
        RelationQuerySourceReadRequest request,
        SourceRow row)
    {
        var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(request.Fields.Length);
        foreach (var field in request.Fields)
        {
            fields.Add(row.Fields.TryGetValue(field.SemanticPath, out var result)
                ? result.ToResult(field)
                : new RelationQuerySourceReadFieldResult(
                    field,
                    RelationQuerySourceReadFieldState.Missing,
                    evidenceReference: $"fake/missing/{field.SemanticPath}"));
        }

        return new(row.Identity, request.Shape, fields.MoveToImmutable());
    }

    public sealed record SourceRow
    {
        public SourceRow(
            string identity,
            IReadOnlyDictionary<FieldPath, SourceField> fields)
        {
            Identity = Guard.RequireNotNullOrWhiteSpace(identity);
            ArgumentNullException.ThrowIfNull(fields);
            Fields = fields.ToImmutableDictionary();
        }

        public string Identity { get; }

        public ImmutableDictionary<FieldPath, SourceField> Fields { get; }

        public static SourceRow Create(
            string identity,
            params (FieldPath Path, ObservationValue Value)[] fields) => new(
            identity,
            fields.ToDictionary(
                static item => item.Path,
                static item => SourceField.FromValue(item.Value)));
    }

    public readonly record struct SourceField(
        RelationQuerySourceReadFieldState State,
        ObservationValue? Value = null,
        string? EvidenceReference = null)
    {
        public static SourceField FromValue(ObservationValue value) => value.Kind switch
        {
            ObservationValueKind.Null => new(RelationQuerySourceReadFieldState.Null),
            ObservationValueKind.Undefined => new(RelationQuerySourceReadFieldState.Missing),
            _ => new(RelationQuerySourceReadFieldState.Value, value)
        };

        public RelationQuerySourceReadFieldResult ToResult(RelationQuerySourceReadField field) =>
            new(field, State, Value, EvidenceReference);
    }
}
