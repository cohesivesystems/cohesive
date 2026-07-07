using ParquetSharp;

namespace Cohesive.Adapters.Parquet;

sealed class TypedColumnWriterVisitor<TValue>(int bufferLength, ILogicalColumnWriterVisitor<Unit> logicalVisitor) : IColumnWriterVisitor<Unit>
{
    Unit IColumnWriterVisitor<Unit>.OnColumnWriter<TPhysical>(ColumnWriter<TPhysical> columnWriter) where TPhysical : struct
    {
        ArgumentNullException.ThrowIfNull(columnWriter);
        columnWriter.LogicalWriter<TValue>(bufferLength: bufferLength).Apply(logicalVisitor);
        return Unit.Value;
    }
}

sealed class TypedLogicalColumnWriterVisitor<TValue>(TValue[] values) : ILogicalColumnWriterVisitor<Unit>
{
    public Unit OnLogicalColumnWriter<TElement>(LogicalColumnWriter<TElement> columnWriter)
    {
        ArgumentNullException.ThrowIfNull(columnWriter);
        if (typeof(TElement) != typeof(TValue))
            throw new InvalidOperationException($"Parquet logical writer type mismatch. Expected '{typeof(TValue).FullName}', received '{typeof(TElement).FullName}'.");
        ((LogicalColumnWriter<TValue>)(object)columnWriter).WriteBatch(values);
        return Unit.Value;
    }
}