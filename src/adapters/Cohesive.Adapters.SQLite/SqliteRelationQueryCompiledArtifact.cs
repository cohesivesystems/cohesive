using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Exact scalar result mapping, independent of provider column names.</summary>
/// <param name="Field">Canonical output field.</param>
/// <param name="Contract">Value contract used by the shared SQLite codec.</param>
/// <param name="ValueOrdinal">Zero-based payload column.</param>
/// <param name="PresenceOrdinal">Zero-based INTEGER presence-bit column.</param>
public sealed record SqliteRelationQueryResultField(RelationQueryFieldReference Field, ValueContract Contract,
    int ValueOrdinal, int PresenceOrdinal);

/// <summary>Winning-row contributor retained independently of projected fields.</summary>
/// <param name="Binding">Canonical source binding.</param>
/// <param name="Shape">Exact source shape.</param>
/// <param name="IdentityOrdinal">Zero-based nullable INTEGER identity column; null denotes outer-join absence.</param>
public sealed record SqliteRelationQueryOccurrenceColumn(ValueBindingId Binding, QualifiedShapeId Shape, int IdentityOrdinal);

/// <summary>Canonical parameter encoded once per invocation.</summary>
/// <param name="Id">Canonical parameter identity and shared SQL runtime binding name.</param>
/// <param name="Contract">Exact required scalar contract.</param>
public sealed record SqliteRelationQueryParameter(QueryParameterId Id, ValueContract Contract);

/// <summary>Canonical projected value and contributing source occurrences for one native row.</summary>
/// <param name="Value">Projected field object, or undefined for an absent output binding.</param>
/// <param name="Occurrences">Winning contributors; absent outer-join sources are omitted.</param>
public sealed record SqliteRelationQueryRow(ObservationValue Value, ImmutableArray<RelationQueryObservationOccurrence> Occurrences);

/// <summary>Reusable SQLite statement, ordinal result layout and exact canonical compilation proof.</summary>
/// <remarks>
/// SQL is compiled once. Execute <see cref="Command"/> through <see cref="SqliteCommandScope"/> with
/// <see cref="BindParameters"/>; call <see cref="ReadCurrentRow"/> for each provider row. The connection must
/// access the database whose schema authority is pinned by <see cref="Provenance"/>.
/// </remarks>
public sealed class SqliteRelationQueryCompiledArtifact
{
    internal SqliteRelationQueryCompiledArtifact(RelationQueryNativeResultBranch branch, SqlCommandTemplate statement,
        ImmutableArray<SqliteRelationQueryResultField> fields, ImmutableArray<SqliteRelationQueryOccurrenceColumn> occurrences,
        ImmutableArray<SqliteRelationQueryParameter> parameters, RelationQueryNativeCompilationProvenance provenance,
        int bindingPresenceOrdinal)
    {
        Branch = branch;
        Statement = statement;
        ResultFields = fields;
        OccurrenceColumns = occurrences;
        Parameters = parameters;
        Provenance = provenance;
        BindingPresenceOrdinal = bindingPresenceOrdinal;
        Command = new(statement);
        Fingerprint = SqliteRelationQueryHash.Compute(new
        {
            SchemaVersion, Branch, Statement, ResultFields, OccurrenceColumns, Parameters, Provenance, BindingPresenceOrdinal
        });
    }

    /// <summary>Version of the inspectable native artifact layout.</summary>
    public string SchemaVersion => "cohesive.relations.sqlite-artifact/v1";
    /// <summary>Exact demanded canonical terminal.</summary>
    public RelationQueryNativeResultBranch Branch { get; }
    /// <summary>Immutable shared SQL construction artifact.</summary>
    public SqlCommandTemplate Statement { get; }
    /// <summary>Cached provider binding plan, safe to share across independent command scopes.</summary>
    [JsonIgnore]
    public SqliteCommandTemplate Command { get; }
    /// <summary>Ordered output field decoding metadata.</summary>
    public ImmutableArray<SqliteRelationQueryResultField> ResultFields { get; }
    /// <summary>Ordinal metadata for source occurrence reconstruction.</summary>
    public ImmutableArray<SqliteRelationQueryOccurrenceColumn> OccurrenceColumns { get; }
    /// <summary>Required runtime parameters in canonical identity order.</summary>
    public ImmutableArray<SqliteRelationQueryParameter> Parameters { get; }
    /// <summary>Exact IR, placement, binding, decisions and contextual proof behind this statement.</summary>
    public RelationQueryNativeCompilationProvenance Provenance { get; }
    /// <summary>Zero-based column distinguishing an absent output binding from a present object with missing fields.</summary>
    public int BindingPresenceOrdinal { get; }
    /// <summary>SHA-256 over the normalized v1 layout, statement and provenance.</summary>
    public string Fingerprint { get; }

    /// <summary>Exports inspectable, versioned SQL, result metadata and proof without provider execution state.</summary>
    /// <param name="indented">Whether to indent the JSON for human review.</param>
    /// <returns>A deterministic inspection artifact. Recompile canonical IR and storage evidence to obtain executable state after restart.</returns>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = indented });

    /// <summary>Encodes required canonical values for reuse of the compiled SQLite command.</summary>
    /// <param name="values">Exactly the declared parameter values, keyed by canonical identity.</param>
    /// <returns>Invocation-owned encoded values accepted by the command scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">Parameters are missing, extra, or violate their scalar contracts.</exception>
    /// <exception cref="NotSupportedException">A parameter encoding is unsupported by the SQLite codec.</exception>
    public (string Binding, object? Value)[] BindParameters(IReadOnlyDictionary<QueryParameterId, ObservationValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != Parameters.Length) throw new ArgumentException("Supply exactly the declared query parameters.", nameof(values));
        var result = new (string Binding, object? Value)[Parameters.Length];
        for (var index = 0; index < Parameters.Length; index++)
        {
            var parameter = Parameters[index];
            if (!values.TryGetValue(parameter.Id, out var value))
                throw new ArgumentException($"Missing query parameter '{parameter.Id.Value}'.", nameof(values));
            result[index] = (parameter.Id.Value, SqliteScalarCodec.Encode(parameter.Contract, value));
        }
        return result;
    }

    /// <summary>Decodes the current row using fixed ordinals and preserves outer absence and provenance.</summary>
    /// <param name="reader">Borrowed reader positioned on a row produced by this artifact.</param>
    /// <returns>Invocation-owned canonical row; no provider buffers escape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The row violates the declared identity, presence or value encoding.</exception>
    /// <exception cref="ArgumentException">A scalar violates its canonical contract.</exception>
    public SqliteRelationQueryRow ReadCurrentRow(SqliteDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var fields = new Dictionary<string, ObservationValue>(ResultFields.Length, StringComparer.Ordinal);
        foreach (var field in ResultFields)
        {
            var present = ReadPresence(reader, field.PresenceOrdinal);
            if (present)
                fields.Add(field.Field.Path.ToString(), SqliteScalarCodec.Decode(field.Contract, reader.GetValue(field.ValueOrdinal)));
            else if (!reader.IsDBNull(field.ValueOrdinal))
                throw new InvalidOperationException("Missing field has a non-null SQLite payload.");
        }
        var occurrences = ImmutableArray.CreateBuilder<RelationQueryObservationOccurrence>(OccurrenceColumns.Length);
        foreach (var column in OccurrenceColumns)
        {
            if (reader.IsDBNull(column.IdentityOrdinal)) continue;
            if (reader.GetValue(column.IdentityOrdinal) is not long identity)
                throw new InvalidOperationException("SQLite occurrence identity must be encoded as INTEGER.");
            var value = identity.ToString(CultureInfo.InvariantCulture);
            occurrences.Add(new(new($"sqlite/{Uri.EscapeDataString(column.Binding.Value)}/{value}"), column.Binding, column.Shape, value));
        }
        return new(ReadPresence(reader, BindingPresenceOrdinal) ? ObservationValue.FromObject(fields) : ObservationValue.Undefined,
            occurrences.Count == occurrences.Capacity ? occurrences.MoveToImmutable() : occurrences.ToImmutable());
    }

    static bool ReadPresence(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        0L => false, 1L => true,
        _ => throw new InvalidOperationException("SQLite presence must be encoded as INTEGER 0 or 1.")
    };
}

/// <summary>Native compilation and its canonical contextual realization result.</summary>
public sealed class SqliteRelationQueryCompilationResult
{
    internal SqliteRelationQueryCompilationResult(RelationQueryBoundRealizationReport boundRealization,
        ImmutableArray<SqliteRelationQueryCompiledArtifact> artifacts,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics)
    {
        BoundRealization = boundRealization;
        Artifacts = artifacts;
        Diagnostics = diagnostics;
    }
    /// <summary>Exact binding-scoped proof or explanation of rejection.</summary>
    public RelationQueryBoundRealizationReport BoundRealization { get; }
    /// <summary>All selected executable branches, or empty on failure.</summary>
    public ImmutableArray<SqliteRelationQueryCompiledArtifact> Artifacts { get; }
    /// <summary>Structured failure diagnostics, empty on success.</summary>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics { get; }
    /// <summary>Whether all selected branches have exact native artifacts.</summary>
    public bool IsSuccessful => BoundRealization.IsRealizable && Diagnostics.IsEmpty;
    /// <summary>Native success, unsupported semantics, or invalid inputs.</summary>
    public RelationQueryNativeCompilationStatus Status => IsSuccessful ? RelationQueryNativeCompilationStatus.Exact
        : BoundRealization.Status == RelationQueryRealizationStatus.Invalid ? RelationQueryNativeCompilationStatus.Invalid
        : RelationQueryNativeCompilationStatus.Unsupported;
}
