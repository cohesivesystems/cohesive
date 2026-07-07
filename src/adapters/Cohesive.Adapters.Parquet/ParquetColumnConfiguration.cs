using ParquetSharp;

namespace Cohesive.Adapters.Parquet;

/// <summary>
/// Associates one parquet column definition with the logic that writes it from a row collection.
/// </summary>
public readonly record struct ParquetColumnConfiguration<TRow>(
    Column Column,
    IParquetColumnWriter<TRow> Writer
);