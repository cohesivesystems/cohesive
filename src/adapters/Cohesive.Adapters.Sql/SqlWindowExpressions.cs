using System.Collections.Immutable;
using System.Text;

namespace Cohesive.Adapters.Sql;

public abstract partial record SqlExpression
{
    /// <summary>Numbers rows from one within each SQL partition in the supplied key order.</summary>
    /// <param name="partitions">Partition expressions; empty or default means one partition for the entire input.</param>
    /// <param name="orderings">Nonempty sequence of ordering terms with explicit direction and null placement.</param>
    /// <returns>A window expression requiring <see cref="SqlFeature.RowNumber"/> when rendered.</returns>
    /// <exception cref="ArgumentException">An expression or ordering is null, or no ordering is supplied.</exception>
    /// <remarks>
    /// Use in a SELECT projection or ORDER BY where the target permits window expressions. Filter its result in
    /// a containing derived query. This constructs SQL grammar only: the caller must establish partition equality,
    /// comparison, and unique-order guarantees required by its semantic model. Tied SQL keys have unspecified row
    /// numbering; this expression neither validates ties nor establishes the final result order.
    /// </remarks>
    public static SqlExpression RowNumber(
        ImmutableArray<SqlExpression> partitions,
        ImmutableArray<SqlOrdering> orderings)
    {
        partitions = partitions.IsDefault ? [] : partitions;
        if (partitions.Any(static key => key is null))
            throw new ArgumentException("Partition expressions cannot be null.", nameof(partitions));
        if (orderings.IsDefaultOrEmpty || orderings.Any(static ordering => ordering is null))
            throw new ArgumentException("Row numbering requires non-null ordering terms.", nameof(orderings));
        return new RowNumberExpression(partitions, orderings);
    }

    sealed record RowNumberExpression(
        ImmutableArray<SqlExpression> Partitions,
        ImmutableArray<SqlOrdering> Orderings) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            context.Dialect.Require(SqlFeature.RowNumber);
            builder.Append("ROW_NUMBER() OVER (");
            context.Indentation++;
            context.LineBreak(builder);
            if (!Partitions.IsEmpty)
            {
                builder.Append("PARTITION BY ");
                for (var index = 0; index < Partitions.Length; index++)
                {
                    if (index != 0) builder.Append(", ");
                    Partitions[index].WriteTo(context, builder);
                }
                context.Separator(builder);
            }
            builder.Append("ORDER BY ");
            for (var index = 0; index < Orderings.Length; index++)
            {
                if (index != 0) builder.Append(", ");
                Orderings[index].WriteTo(context, builder);
            }
            context.Indentation--;
            context.LineBreak(builder);
            builder.Append(')');
        }
    }
}
