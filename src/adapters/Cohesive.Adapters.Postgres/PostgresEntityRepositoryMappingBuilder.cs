using System.Linq.Expressions;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Transitions.Model;

namespace Cohesive.Adapters.Postgres;

/// <summary>Invocation-local, non-thread-safe authoring of an immutable entity repository mapping.</summary>
/// <typeparam name="T">POCO with canonical serialized field names.</typeparam>
/// <remarks>Selectors use the shared field-path conventions, including JsonPropertyName. Only direct scalar
/// fields are supported. No schema is created; Build snapshots and validates the existing mapping contract.</remarks>
public sealed class PostgresEntityRepositoryMappingBuilder<T> where T : notnull
{
    readonly EntityDefinition definition;
    readonly List<PostgresEntityRepositoryFieldBinding> fields = [];
    SqlQualifiedTable? table;
    string? identity;
    string? partition;

    internal PostgresEntityRepositoryMappingBuilder(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        this.definition = definition;
    }

    /// <summary>Selects the physical table.</summary>
    /// <param name="schema">SQL schema name.</param>
    /// <param name="name">SQL table name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A physical identifier is invalid.</exception>
    public PostgresEntityRepositoryMappingBuilder<T> Table(string schema, string name)
    {
        table = new(schema, name);
        return this;
    }

    /// <summary>Maps a direct canonical field, inferring its exact supported PostgreSQL scalar encoding.</summary>
    /// <param name="selector">Direct property selector using canonical serialized names.</param>
    /// <param name="column">Physical column name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">Selector, field, encoding or physical column is invalid.</exception>
    /// <exception cref="ArgumentNullException">Selector is null.</exception>
    public PostgresEntityRepositoryMappingBuilder<T> Column(Expression<Func<T, object?>> selector, string column)
    {
        Add(selector, column);
        return this;
    }

    /// <summary>Maps and selects the identity field; Build requires a required, non-null TEXT key.</summary>
    /// <param name="selector">Direct canonical identity selector.</param>
    /// <param name="column">Physical column name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">Selector, field or column is invalid.</exception>
    /// <exception cref="ArgumentNullException">Selector is null.</exception>
    /// <exception cref="InvalidOperationException">Identity was already selected.</exception>
    public PostgresEntityRepositoryMappingBuilder<T> Identity(Expression<Func<T, object?>> selector, string column)
    {
        if (identity is not null) throw new InvalidOperationException("Identity is already selected.");
        identity = Add(selector, column);
        return this;
    }

    /// <summary>Maps and selects the partition field; Build requires a required, non-null TEXT key.</summary>
    /// <param name="selector">Direct canonical partition selector.</param>
    /// <param name="column">Physical column name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">Selector, field or column is invalid.</exception>
    /// <exception cref="ArgumentNullException">Selector is null.</exception>
    /// <exception cref="InvalidOperationException">Partition was already selected.</exception>
    public PostgresEntityRepositoryMappingBuilder<T> Partition(Expression<Func<T, object?>> selector, string column)
    {
        if (partition is not null) throw new InvalidOperationException("Partition is already selected.");
        partition = Add(selector, column);
        return this;
    }

    /// <summary>Snapshots the declaration and validates complete canonical coverage and repository invariants.</summary>
    /// <param name="versionColumn">Physical observation-version column.</param>
    /// <param name="maximumBatchItems">Positive batch admission bound.</param>
    /// <returns>An immutable mapping, unaffected by subsequent builder edits.</returns>
    /// <exception cref="InvalidOperationException">Table, identity or partition was not selected.</exception>
    /// <exception cref="ArgumentException">Mapping violates canonical coverage, scalar or key requirements.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Batch bound is not positive.</exception>
    public PostgresEntityRepositoryMapping Build(string versionColumn = "observation_version", int maximumBatchItems = 1_000)
    {
        var mapping = new PostgresEntityRepositoryMapping(
            table ?? throw new InvalidOperationException("Select a table before Build."), fields,
            identity ?? throw new InvalidOperationException("Select an identity before Build."),
            partition ?? throw new InvalidOperationException("Select a partition before Build."), versionColumn, maximumBatchItems);
        PostgresEntityRepository.ValidateMapping(definition, mapping);
        return mapping;
    }

    string Add(Expression<Func<T, object?>> selector, string column)
    {
        var path = FieldPath.Capture(selector);
        if (!path.TryGetDirectFieldName(out var name))
            throw new ArgumentException("Entity repository mappings require a direct field selector.", nameof(selector));
        var field = definition.Fields.SingleOrDefault(candidate => candidate.Name.Value == name)
            ?? throw new ArgumentException($"Canonical entity does not contain field '{name}'.", nameof(selector));
        if (field.Cardinality != FieldCardinality.Single
            || !PostgresRelationQueryScalarCatalog.TryFromSemanticType(field.Type, out var scalar))
            throw new ArgumentException($"Field '{name}' has no supported scalar encoding.", nameof(selector));
        fields.Add(new(name, column, scalar));
        return name;
    }
}
