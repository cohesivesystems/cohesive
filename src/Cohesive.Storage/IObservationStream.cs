namespace Cohesive.Storage;

/// <summary>
/// Checkpointed observation stream.
/// </summary>
public interface IObservationStream
{
    /// <summary>
    /// Logical stream name.
    /// </summary>
    string StreamName { get; }

    /// <summary>
    /// Processes the stream. Implementations are responsible for checkpoint advancement after successful batch completion.
    /// </summary>
    Task Process(Func<ObservationBatchContext, IReadOnlyCollection<ObservationRecord>, CancellationToken, Task> handle, CancellationToken ct = default);

    /// <summary>
    /// Emits lag samples for the stream.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<ObservationStreamLagSnapshot>> LagStream(CancellationToken ct = default);
}

/// <summary>
/// Observation stream batch metadata.
/// </summary>
public sealed record ObservationBatchContext(
    string StreamName,
    string ProcessorName,
    string? LeaseToken = null,
    object? NativeContext = null
);

/// <summary>
/// Stream lag sample.
/// </summary>
public sealed record ObservationStreamLagSnapshot(
    string StreamName,
    long EstimatedLag,
    DateTimeOffset SampledAtUtc
);

/// <summary>
/// A repository that supports change streams.
/// </summary>
public interface IChangeStreamRepository
{
    /// <summary>
    /// Returns the raw entity change stream for this repository.
    /// </summary>
    /// <param name="processorName">The name of the processor that will consume the stream.</param>
    /// <param name="startTime">An optional start time for the stream. If null, the stream will start from the point configured in the change feed builder.</param>
    /// <exception cref="NotSupportedException">The repository does not support a change stream.</exception>
    IObservationStream GetChangeStream(string processorName, DateTimeOffset? startTime = null);
}