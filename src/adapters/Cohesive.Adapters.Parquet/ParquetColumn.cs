using ParquetSharp;

namespace Cohesive.Adapters.Parquet;

/// <summary>
/// Helpers for constructing typed parquet column configurations.
/// </summary>
public static class ParquetColumn
{
    public static ParquetColumnConfiguration<TRow> Create<TRow, TValue>(Column<TValue> column, Func<TRow, TValue> valueSelector)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(valueSelector);
        return new(column, new ParquetColumnWriter<TRow, TValue>(valueSelector));
    }

    public static ParquetColumnConfiguration<TRow> Create<TRow, TValue>(Column<TValue> column, Func<IReadOnlyList<TRow>, TValue[]> valuesFactory)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(valuesFactory);
        return new(column, new ParquetColumnWriter<TRow, TValue>(valuesFactory));
    }
}