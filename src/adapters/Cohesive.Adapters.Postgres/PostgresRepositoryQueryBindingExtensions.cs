using Cohesive.Relations.Authoring;
using Cohesive.Model;

namespace Cohesive.Adapters.Postgres;

/// <summary>Projects existing repository column mappings into native relation/query storage bindings.</summary>
public static class PostgresRepositoryQueryBindingExtensions
{
    /// <summary>Attaches the repository table and demanded columns without declaring a second column catalog.</summary>
    /// <param name="builder">Plan-affine native query binding builder.</param>
    /// <param name="input">Exact placed semantic input.</param>
    /// <param name="mapping">Existing repository mapping for that entity.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The mapping conflicts with the placed input or native binding contract.</exception>
    /// <remarks>Text equality uses PostgreSQL C collation. This does not assert ordinal ordering or global
    /// uniqueness of a partitioned identity; the query and schema must establish their required scope.</remarks>
    public static PostgresRelationQueryStorageBindingBuilder Table(this PostgresRelationQueryStorageBindingBuilder builder,
        RelationQueryPlacedInput input, PostgresEntityRepositoryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(mapping);
        return builder.Table(input, mapping.Table.TableName.Value, table =>
        {
            if (mapping.Table.SchemaName is { } schema) table.Schema(schema.Value);
            table.ColumnsExplicitly();
            foreach (var field in mapping.Fields)
            {
                var options = new PostgresRelationQueryColumnOptions(field.ScalarType,
                    textSemantics: field.ScalarType == PostgresRelationQueryScalarType.Text
                        ? new("C", PostgresRelationQueryTextEqualitySemantics.Ordinal, PostgresRelationQueryTextOrderingSemantics.Unspecified)
                        : null);
                var path = FieldPath.FromField(field.FieldName);
                if (input.TryGetField(path, out _) || input.Plan.InputContract.Traversals.Any(traversal =>
                    traversal.Definition.SourceShape == input.Shape && traversal.Definition.SourceReference == path))
                    table.Column(path, field.Column.Value, options);
                if (field.FieldName == mapping.IdentityField)
                    table.Identity(FieldPath.FromField(field.FieldName), field.Column.Value, options);
            }
        });
    }
}
