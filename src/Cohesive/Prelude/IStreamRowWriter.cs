using System.Buffers;

namespace Cohesive.Prelude;

/// <summary>
/// Writes a collection of rows to a <see cref="Stream"/>.
/// </summary>
/// <typeparam name="TRow"></typeparam>
public interface IStreamRowWriter<in TRow>
{
    /// <summary>
    /// Writes a collection of rows to a stream.
    /// </summary>
    /// <param name="rows">The rows to write to the stream.</param>
    /// <param name="stream">The stream to write the rows to.</param>
    /// <returns>A write result value.</returns>
    ValueTask<StreamRowWriterResult> Write(IReadOnlyCollection<TRow> rows, Stream stream);
}

/// <summary>
/// The result of writing a collection of rows to a stream.
/// </summary>
/// <param name="RowsWritten"></param>
public readonly record struct StreamRowWriterResult(int RowsWritten);