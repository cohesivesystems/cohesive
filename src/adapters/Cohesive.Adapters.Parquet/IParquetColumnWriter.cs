using ParquetSharp;

namespace Cohesive.Adapters.Parquet;

/// <summary>
/// Writes a single parquet column from a row collection.
/// </summary>
public interface IParquetColumnWriter<in TRow>
{
    /// <summary>
    /// Compiles a delegate that writes a column for each row into the row group.
    /// </summary>
    /// <param name="columnIndex"></param>
    /// <returns></returns>
    Action<RowGroupWriter, IReadOnlyList<TRow>> Compile(int columnIndex);
}

/// <summary>
/// Column writer that projects rows into a typed value batch.
/// </summary>
sealed class ParquetColumnWriter<TRow, TValue> : IParquetColumnWriter<TRow>
{
    readonly Func<IReadOnlyList<TRow>, TValue[]> valuesFactory;
    readonly Func<TValue[], int, IColumnWriterVisitor<Unit>> columnVisitorFactory;
    readonly int minimumBufferLength;

    public ParquetColumnWriter(Func<TRow, TValue> valueSelector, int minimumBufferLength = 1)
        : this(rows => [..rows.Select(valueSelector)], minimumBufferLength)
    {
    }

    public ParquetColumnWriter(Func<IReadOnlyList<TRow>, TValue[]> valuesFactory, int minimumBufferLength = 1)
    {
        ArgumentNullException.ThrowIfNull(valuesFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBufferLength);
        this.valuesFactory = valuesFactory;
        this.minimumBufferLength = minimumBufferLength;
        columnVisitorFactory = static (values, bufferLength) 
            => new TypedColumnWriterVisitor<TValue>(bufferLength, new TypedLogicalColumnWriterVisitor<TValue>(values));
    }

    public Action<RowGroupWriter, IReadOnlyList<TRow>> Compile(int columnIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        return (rowGroupWriter, rows) =>
        {
            ArgumentNullException.ThrowIfNull(rowGroupWriter);
            ArgumentNullException.ThrowIfNull(rows);

            var values = valuesFactory(rows);
            ArgumentNullException.ThrowIfNull(values);
            if (values.Length != rows.Count)
                throw new InvalidOperationException($"Configured parquet column writer for '{typeof(TRow).FullName}' produced {values.Length} values for {rows.Count} rows.");

            var bufferLength = Math.Max(minimumBufferLength, Math.Max(values.Length, 1));
            rowGroupWriter.Column(columnIndex).Apply(columnVisitorFactory(values, bufferLength));
        };
    }
}