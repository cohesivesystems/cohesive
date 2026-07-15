using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Tests;

/// <summary>Deterministic bounded source reader used by physical-execution acceptance tests.</summary>
sealed class DeterministicRelationQuerySourceReader : IRelationQuerySourceReader
{
    readonly ImmutableArray<SourceRow> rows;
    readonly Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult>? resultFactory;
    readonly Action<RelationQuerySourceReadRequest>? afterRead;
    readonly ConcurrentQueue<RelationQuerySourceReadRequest> requests = new();

    public DeterministicRelationQuerySourceReader(
        RelationQuerySourceReaderDescriptor descriptor,
        ImmutableArray<SourceRow> rows,
        Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult>? resultFactory = null,
        Action<RelationQuerySourceReadRequest>? afterRead = null)
    {
        Descriptor = descriptor;
        this.rows = rows.IsDefault
            ? []
            : [.. rows.OrderBy(static row => row.Identity, StringComparer.Ordinal)];
        this.resultFactory = resultFactory;
        this.afterRead = afterRead;
    }

    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    public ImmutableArray<RelationQuerySourceReadRequest> Requests => [.. requests];

    public ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        requests.Enqueue(request);

        var result = resultFactory is not null
            ? resultFactory(request)
            : new RelationQuerySourceReadResult(
                RelationQuerySourceReadState.Complete,
                [.. SelectRows(request.Constraint).Select(row => Project(request, row))],
                $"fake/{request.Stage.Value}");
        afterRead?.Invoke(request);
        return ValueTask.FromResult(result);
    }

    ImmutableArray<SourceRow> SelectRows(RelationQuerySourceReadConstraint constraint) => constraint switch
    {
        RelationQueryBoundedEnumeration enumeration =>
        [
            .. rows.Take(checked((int)Math.Min(enumeration.MaximumRows, int.MaxValue)))
        ],
        RelationQueryIdentityBatchLookup lookup =>
        [
            .. rows.Where(row => lookup.Identities.Contains(row.Identity, StringComparer.Ordinal))
        ],
        RelationQueryRelationshipKeyBatchLookup lookup =>
        [
            .. rows.Where(row =>
                row.Fields.TryGetValue(lookup.RelationshipReference, out var field)
                && field.State == RelationQuerySourceReadFieldState.Value
                && field.Value is { Kind: ObservationValueKind.String, String: { } value }
                && lookup.Keys.Contains(value, StringComparer.Ordinal))
        ],
        _ => throw new NotSupportedException(
            $"The deterministic reader does not support '{constraint.GetType().Name}'.")
    };

    static RelationQuerySourceReadObservation Project(
        RelationQuerySourceReadRequest request,
        SourceRow row) => new(
        row.Identity,
        request.Shape,
        [
            .. request.Fields.Select(field => row.Fields.TryGetValue(field.SemanticPath, out var result)
                ? result.ToResult(field)
                : new RelationQuerySourceReadFieldResult(
                    field,
                    RelationQuerySourceReadFieldState.Missing,
                    evidenceReference: $"fake/missing/{field.SemanticPath}"))
        ]);

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
