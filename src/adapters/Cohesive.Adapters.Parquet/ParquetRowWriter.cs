using ParquetSharp;
using ParquetSharp.IO;

namespace Cohesive.Adapters.Parquet;

/// <summary>
/// Writes row collections to Parquet using a configured column schema.
/// </summary>
/// <typeparam name="TRow">Logical row type.</typeparam>
public sealed class ParquetRowWriter<TRow> : IStreamRowWriter<TRow>
{
    readonly Column[] columns;
    readonly Action<RowGroupWriter, IReadOnlyList<TRow>>[] columnWriters;
    readonly Compression compression;

    public ParquetRowWriter(IReadOnlyList<ParquetColumnConfiguration<TRow>> columns, Compression compression = Compression.Snappy)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "At least one parquet column must be configured.");

        this.columns = [..columns.Select(static configuration => configuration.Column)];
        columnWriters = [..columns.Select(static (configuration, index) => configuration.Writer.Compile(index))];
        this.compression = compression;
    }

    public ValueTask<StreamRowWriterResult> Write(IReadOnlyCollection<TRow> rows, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rows);

        var rowList = rows as IReadOnlyList<TRow> ?? [.. rows];

        using var outputStream = new ManagedOutputStream(stream, leaveOpen: true);
        using var fileWriter = new ParquetFileWriter(outputStream, columns, compression: this.compression, keyValueMetadata: null);
        using var rowGroupWriter = fileWriter.AppendBufferedRowGroup();
        
        foreach (var writeColumn in columnWriters)
            writeColumn(rowGroupWriter, rowList);

        fileWriter.Close();

        return ValueTask.FromResult(new StreamRowWriterResult(RowsWritten: rows.Count));
    }
}

/// <summary>
/// Helpers for constructing <see cref="ParquetRowWriter{TRow}"/>.
/// </summary>
public static class ParquetRowWriter
{
    /// <summary>
    /// Creates a row-scoped set of parquet column configurations.
    /// </summary>
    /// <typeparam name="TRow"></typeparam>
    /// <returns></returns>
    public static RowScopedColumns<TRow> For<TRow>() => new();
    
    /// <summary>
    /// A row-scoped column configuration set.
    /// </summary>
    /// <typeparam name="TRow"></typeparam>
    public class RowScopedColumns<TRow>()
    {
        readonly List<ParquetColumnConfiguration<TRow>> columns = [];
        
        public ParquetRowWriter<TRow> ToWriter() => new([..columns]);
        
        public RowScopedColumns<TRow> Column<TValue>(Column<TValue> column, Func<TRow, TValue> valueSelector)
        {
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(valueSelector);
            columns.Add(new(column, new ParquetColumnWriter<TRow, TValue>(valueSelector)));
            return this;
        }

        public RowScopedColumns<TRow> Column<TValue>(Column<TValue> column, Func<IReadOnlyList<TRow>, TValue[]> valuesFactory)
        {
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(valuesFactory);
            columns.Add(new(column, new ParquetColumnWriter<TRow, TValue>(valuesFactory)));
            return this;
        }
    }
}