using System.Collections.Immutable;
using System.Text;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace Cohesive.Adapters.SQLite;

/// <summary>Immutable scalar-column realization of one canonical entity definition in one SQLite table.</summary>
/// <remarks>Field contracts and graph-qualified identity come from EntityDefinition. Physical names are projections,
/// not semantic identities. The initial profile requires present scalar fields; nullable values are supported.</remarks>
public sealed class SqliteEntityRepositoryMapping
{
    /// <summary>Column storing the semantic entity version, independently of the concurrency token.</summary>
    public const string VersionColumn = "__cohesive_version";
    /// <summary>Column storing the opaque storage concurrency token.</summary>
    public const string TokenColumn = "__cohesive_token";
    /// <summary>Column retaining the authoritative graph revision identity.</summary>
    public const string GraphColumn = "__cohesive_graph";
    /// <summary>Column retaining the semantic shape identity within its graph.</summary>
    public const string ShapeColumn = "__cohesive_shape";
    /// <summary>Default maximum writes in one batch, validated before database access.</summary>
    public const int DefaultMaximumBatchItems = 1_000;

    /// <summary>Resolves physical naming conventions and validates the complete supported field contract.</summary>
    /// <param name="entityDefinition">Semantic authority retained by the mapping and repository.</param>
    /// <param name="identityField">Required non-null textual field matching the snapshot's EntityId.</param>
    /// <param name="partitionField">Required non-null textual partition field, or null to use the identity field.</param>
    /// <param name="tableName">Physical table name, or null to use the logical entity name.</param>
    /// <param name="columnNames">Optional physical column overrides keyed by canonical field name; snapshotted at construction.</param>
    /// <param name="maximumBatchItems">Positive maximum batch size, or null for the default limit of 1,000.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entityDefinition"/> is null.</exception>
    /// <exception cref="ArgumentException">Keys, identifiers, or column overrides are invalid or collide with reserved metadata.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The batch limit is not positive.</exception>
    /// <exception cref="NotSupportedException">A field requires an optional, structured, or other unsupported scalar representation.</exception>
    public SqliteEntityRepositoryMapping(EntityDefinition entityDefinition, string identityField,
        string? partitionField = null, string? tableName = null,
        IReadOnlyDictionary<string, string>? columnNames = null, int? maximumBatchItems = null)
    {
        EntityDefinition = entityDefinition ?? throw new ArgumentNullException(nameof(entityDefinition));
        ArgumentException.ThrowIfNullOrWhiteSpace(identityField);
        IdentityField = identityField;
        PartitionField = partitionField ?? identityField;
        TableName = tableName ?? entityDefinition.Name.Value;
        QuotedTable = SqliteDatabase.QuoteIdentifier(TableName);
        if (TableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
            || TableName.StartsWith("__cohesive_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The table name uses a reserved SQLite or Cohesive prefix.", nameof(tableName));
        MaximumBatchItems = maximumBatchItems ?? DefaultMaximumBatchItems;
        if (MaximumBatchItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchItems));

        var columns = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        HashSet<string> usedColumns = new(StringComparer.OrdinalIgnoreCase) { VersionColumn, TokenColumn, GraphColumn, ShapeColumn };
        var bindings = ImmutableArray.CreateBuilder<FieldBinding>(entityDefinition.Fields.Length);
        foreach (var field in entityDefinition.Fields.OrderBy(static field => field.Name.Value, StringComparer.Ordinal))
        {
            var name = field.Name.Value;
            var contract = ValueContract.FromField(field);
            if (field.Presence != FieldPresence.Required)
                throw new NotSupportedException($"SQLite entity field '{name}' requires an explicit missing-value representation; only present fields are supported.");
            var storageType = SqliteScalarCodec.GetStorageType(contract);
            var column = columnNames is not null && columnNames.TryGetValue(name, out var declared) ? declared : name;
            var quoted = SqliteDatabase.QuoteIdentifier(column);
            if (!usedColumns.Add(column))
                throw new ArgumentException($"SQLite column '{column}' collides with another field or reserved metadata.", nameof(columnNames));
            columns.Add(name, column);
            bindings.Add(new(field, contract, quoted, "$field" + bindings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), storageType));
        }
        if (columnNames is not null)
            foreach (var name in columnNames.Keys)
                if (!columns.ContainsKey(name))
                    throw new ArgumentException($"Column override refers to unknown entity field '{name}'.", nameof(columnNames));
        Bindings = bindings.MoveToImmutable();
        FieldColumns = columns.ToImmutable();
        Layout = ObservationLayout.Create(entityDefinition.StateShape, Bindings.Select(static binding => binding.Field.Name.Value));
        Identity = RequireKey(IdentityField, "identity");
        Partition = RequireKey(PartitionField, "partition");
        IdentityOrdinal = Layout.GetOrdinal(IdentityField);
        PartitionOrdinal = Layout.GetOrdinal(PartitionField);

        var conventions = ImmutableArray.CreateBuilder<string>();
        if (tableName is null) conventions.Add(nameof(TableName));
        if (partitionField is null) conventions.Add(nameof(PartitionField));
        if (maximumBatchItems is null) conventions.Add(nameof(MaximumBatchItems));
        foreach (var binding in Bindings)
            if (columnNames is null || !columnNames.ContainsKey(binding.Field.Name.Value))
                conventions.Add($"{nameof(FieldColumns)}/{binding.Field.Name.Value}");
        ConventionSuppliedSettings = conventions.ToImmutable();
        BatchCapabilities = new(SupportsNativeBatching: true, SupportsSamePartitionAtomicity: true,
            SupportsAllOrNothingAtomicity: true, MaxItemsPerBatch: MaximumBatchItems);
        InitialMigration = new(version: 1, statements: [CreateTableStatement()]);
    }

    /// <summary>Canonical definition supplying field contracts and shape identity.</summary>
    public EntityDefinition EntityDefinition { get; }
    /// <summary>Resolved physical table name.</summary>
    public string TableName { get; }
    /// <summary>Canonical identity field; its values must match each snapshot identity.</summary>
    public string IdentityField { get; }
    /// <summary>Canonical logical partition field; defaults to the identity field.</summary>
    public string PartitionField { get; }
    /// <summary>Complete immutable canonical-field to physical-column mapping.</summary>
    public ImmutableDictionary<string, string> FieldColumns { get; }
    /// <summary>Names of decisions supplied by deterministic conventions instead of explicit arguments.</summary>
    public ImmutableArray<string> ConventionSuppliedSettings { get; }
    /// <summary>Maximum number of writes accepted in one batch.</summary>
    public int MaximumBatchItems { get; }
    /// <summary>Native batch guarantees within one database, including across logical partitions.</summary>
    public EntityBatchCapabilities BatchCapabilities { get; }
    /// <summary>Inspectable first migration for a new table; apply explicitly in a module-owned SqliteSchema.</summary>
    /// <remarks>No IF NOT EXISTS fallback is used: existing tables need a reviewed adoption/migration plan.</remarks>
    public SqliteMigration InitialMigration { get; }

    /// <summary>Shared observation layout matching database result-column order.</summary>
    public ObservationLayout Layout { get; }

    internal int IdentityOrdinal { get; }
    internal int PartitionOrdinal { get; }
    internal string QuotedTable { get; }
    internal ImmutableArray<FieldBinding> Bindings { get; }
    internal FieldBinding Identity { get; }
    internal FieldBinding Partition { get; }
    internal string KeyColumns => IdentityField == PartitionField ? Identity.QuotedColumn : $"{Partition.QuotedColumn}, {Identity.QuotedColumn}";

    FieldBinding RequireKey(string name, string role)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Field.Name.Value != name) continue;
            if (binding.Contract.Nullability == FieldNullability.NonNullable
                && binding.Contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid } or EnumTypeRef or EntityReferenceTypeRef)
                return binding;
            break;
        }
        throw new ArgumentException($"SQLite {role} field '{name}' must be a required non-null textual field.", nameof(name));
    }

    string CreateTableStatement()
    {
        var text = new StringBuilder($"CREATE TABLE {QuotedTable} (\n");
        foreach (var binding in Bindings)
        {
            text.Append("    ").Append(binding.QuotedColumn).Append(' ').Append(binding.StorageType.ToString().ToUpperInvariant());
            if (binding.Contract.Nullability == FieldNullability.NonNullable) text.Append(" NOT NULL");
            text.AppendLine(",");
        }
        text.Append($"    {VersionColumn} INTEGER NOT NULL CHECK ({VersionColumn} >= 0),\n")
            .Append($"    {TokenColumn} TEXT NOT NULL CHECK (length({TokenColumn}) > 0),\n")
            .Append($"    {GraphColumn} TEXT NOT NULL,\n    {ShapeColumn} TEXT NOT NULL,\n")
            .Append($"    PRIMARY KEY ({KeyColumns})\n) STRICT;");
        return text.ToString();
    }

    internal sealed record FieldBinding(FieldDefinition Field, ValueContract Contract, string QuotedColumn, string Parameter, Microsoft.Data.Sqlite.SqliteType StorageType);
}
