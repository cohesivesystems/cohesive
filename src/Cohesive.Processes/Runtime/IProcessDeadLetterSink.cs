using System.Collections.Concurrent;
using Cohesive.Prelude;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Dead-lettered effect request.
/// </summary>
public sealed record ProcessDeadLetter(
    EffectRequest Request,
    ProcessEntityRef? ContinuationEntity,
    int Attempts,
    bool IsTransient,
    string ErrorType,
    string ErrorMessage
);

/// <summary>
/// Dead-letter sink for effect failures.
/// </summary>
public interface IProcessDeadLetterSink
{
    /// <summary>
    /// Persists dead-lettered effect metadata.
    /// </summary>
    Task EnqueueAsync(OperationContext context, ProcessDeadLetter deadLetter);
}

/// <summary>
/// In-memory dead-letter sink.
/// </summary>
public sealed class InMemoryProcessDeadLetterSink : IProcessDeadLetterSink
{
    readonly ConcurrentQueue<ProcessDeadLetter> deadLetters = [];

    /// <summary>
    /// Dead-letter snapshot.
    /// </summary>
    public IReadOnlyList<ProcessDeadLetter> DeadLetters => [.. deadLetters];

    /// <inheritdoc />
    public Task EnqueueAsync(OperationContext context, ProcessDeadLetter deadLetter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(deadLetter);
        context.CancellationToken.ThrowIfCancellationRequested();

        deadLetters.Enqueue(deadLetter);
        return Task.CompletedTask;
    }
}
