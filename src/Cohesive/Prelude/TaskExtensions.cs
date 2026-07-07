using System.Collections.Concurrent;

namespace Cohesive.Prelude;

/// <summary>
/// Extension methods for <see cref="Task"/>.
/// </summary>
public static class TaskExtensions
{
    extension<T>(Task<T> task)
    {
        /// <summary>
        /// Chains a continuation to the task.
        /// </summary>
        /// <param name="continuation">The continuation to execute after the task completes.</param>
        /// <typeparam name="TResult">The type of the result returned by the continuation.</typeparam>
        /// <returns>A task representing the result of the continuation.</returns>
        public async Task<TResult> Then<TResult>(Func<T, TResult> continuation) => 
            continuation(await task);
    }

    extension(Task)
    {
        /// <summary>
        /// Awaits a set of already-started tasks and returns their results in source order.
        /// </summary>
        /// <param name="tasks">The tasks to await.</param>
        /// <param name="ct">The cancellation token to monitor for cancellation requests.</param>
        /// <typeparam name="T">The task result type.</typeparam>
        /// <returns>The results of the awaited tasks in source order.</returns>
        /// <exception cref="AggregateException"></exception>
        public static async Task<IReadOnlyList<T>> AwaitAll<T>(
            IEnumerable<Task<T>> tasks,
            CancellationToken ct = default
            )
        {
            ArgumentNullException.ThrowIfNull(tasks);

            var taskList = tasks as IReadOnlyList<Task<T>> ?? tasks.ToArray();
            if (taskList.Count == 0)
                return [];

            var combined = Task.WhenAll(taskList);
            try
            {
                return await combined.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (combined.IsFaulted)
                    throw combined.Exception!;

                throw;
            }
        }

        /// <summary>
        /// Throttles the execution of multiple tasks concurrently.
        /// </summary>
        /// <param name="source">The source collection.</param>
        /// <param name="selector">The task selector.</param>
        /// <param name="options">The throttling and completion options.</param>
        /// <param name="ct">The cancellation token to monitor for cancellation requests.</param>
        /// <typeparam name="TSource">The source item type.</typeparam>
        /// <typeparam name="TResult">The task result type.</typeparam>
        /// <returns>The results of the executed tasks.</returns>
        /// <exception cref="AggregateException"></exception>
        public static async Task<IReadOnlyList<TResult>> WhenAllThrottled<TSource, TResult>(
            IEnumerable<TSource> source,
            Func<TSource, Task<TResult>> selector,
            TaskParallelOptions options,
            CancellationToken ct = default
            )
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrency, 1);
            ct.ThrowIfCancellationRequested();

            var preserveOrder = options.PreserveOrder;
            var failFast = options.FailFast;
            
            var sourceList = source as IReadOnlyList<TSource> ?? source.ToArray();
            if (sourceList.Count == 0)
                return [];

            var results = new TResult[sourceList.Count];
            var workerCount = Math.Min(options.MaxConcurrency, sourceList.Count);
            var exceptions = new ConcurrentQueue<Exception>();
            var nextSourceIndex = -1;
            var nextCompletionIndex = -1;
            var failFastSignaled = 0;
            var workers = new Task[workerCount];

            for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                workers[workerIndex] = WorkerAsync();
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            if (!exceptions.IsEmpty)
                throw new AggregateException(exceptions);

            ct.ThrowIfCancellationRequested();

            return results;

            async Task WorkerAsync()
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ShouldStopForFailFast())
                        return;

                    var sourceIndex = Interlocked.Increment(ref nextSourceIndex);
                    if ((uint)sourceIndex >= (uint)sourceList.Count)
                        return;

                    try
                    {
                        var result = await selector(sourceList[sourceIndex]).ConfigureAwait(false);
                        var resultIndex = preserveOrder
                            ? sourceIndex
                            : Interlocked.Increment(ref nextCompletionIndex);
                        results[resultIndex] = result;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                        if (failFast)
                            Interlocked.Exchange(ref failFastSignaled, 1);
                    }
                }
            }

            bool ShouldStopForFailFast() => failFast && Volatile.Read(ref failFastSignaled) != 0;
        }
    }
}
