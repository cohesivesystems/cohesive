namespace Cohesive.AI.Training;

/// <summary>
/// Projects examples into a collection of dataset rows.
/// </summary>
/// <typeparam name="TExample"></typeparam>
/// <typeparam name="TRow"></typeparam>
public interface IDatasetProjector<in TExample, out TRow>
{
    /// <summary>
    /// Projects one logical dataset from examples.
    /// </summary>
    IEnumerable<TRow> Project(IEnumerable<TExample> examples);
}

/// <summary>
/// Materializes a dataset projection into a stream.
/// </summary>
/// <typeparam name="TExample"></typeparam>
public interface IDatasetProjectionMaterializer<in TExample>
{
    /// <summary>
    /// Materializes examples into a stream.
    /// </summary>
    /// <param name="rows">The rows to materialize.</param>
    /// <param name="stream">The stream to materialize into.</param>
    /// <returns>The dataset materialization result.</returns>
    ValueTask<DatasetMaterializationResult> Materialize(IReadOnlyCollection<TExample> rows, Stream stream);
}

/// <summary>
/// The result of materializing a dataset projection into a stream.
/// </summary>
/// <param name="RowsWritten"></param>
public readonly record struct DatasetMaterializationResult(int RowsWritten);

/// <summary>
/// Default implementation that projects rows and delegates serialization to an <see cref="IStreamRowWriter{TRow}"/>.
/// </summary>
/// <typeparam name="TExample"></typeparam>
public sealed class DatasetProjectionMaterializer<TExample>(
    Func<IReadOnlyCollection<TExample>, Stream, ValueTask<DatasetMaterializationResult>> materialize
    ) : IDatasetProjectionMaterializer<TExample>
{
    readonly Func<IReadOnlyCollection<TExample>, Stream, ValueTask<DatasetMaterializationResult>> materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));

    public ValueTask<DatasetMaterializationResult> Materialize(IReadOnlyCollection<TExample> rows, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(stream);
        return materialize(rows, stream);
    }

    public static DatasetProjectionMaterializer<TExample> Create<TRow>(IDatasetProjector<TExample, TRow> projector, IStreamRowWriter<TRow> streamRowWriter)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(streamRowWriter);
        return new(async (examples, stream) =>
        {
            var projected = projector.Project(examples).ToArray();
            var result = await streamRowWriter.Write(projected, stream).ConfigureAwait(false);
            return new(RowsWritten: result.RowsWritten);
        });
    }
}
