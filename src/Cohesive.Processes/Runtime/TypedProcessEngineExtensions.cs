namespace Cohesive.Processes.Runtime;

/// <summary>
/// Typed process start result that preserves the expected output type across lifecycle calls.
/// </summary>
/// <typeparam name="TOutput">Expected process result type.</typeparam>
public sealed record ProcessStartResult<TOutput>(
    string ProcessId,
    string ProcessName,
    DateTimeOffset StartedAtUtc
);

/// <summary>
/// Typed process run result produced by a completed process execution.
/// </summary>
/// <typeparam name="TOutput">Expected process result type.</typeparam>
public sealed record ProcessRunResult<TOutput>(
    string ProcessId,
    string ProcessName,
    TOutput Result,
    string FinalPlace,
    IReadOnlyDictionary<string, object?> Variables,
    IReadOnlyList<TransitionResult> Transitions,
    IReadOnlyList<EffectExecution> ExecutedEffects,
    IReadOnlyList<ProcessPendingEffect> PendingEffects,
    IReadOnlyList<ProcessDeadLetter> DeadLetters
);

/// <summary>
/// Strongly typed process-engine helpers layered over <see cref="IProcessEngine"/>.
/// </summary>
public static class TypedProcessEngineExtensions
{
    extension(IProcessEngine engine)
    {
        /// <summary>
        /// Starts a generated strongly typed process definition from a process-definition instance.
        /// </summary>
        public Task<ProcessStartResult<TOutput>> StartAsync<TInput, TOutput>(OperationContext context, IProcessDefinition<TInput, TOutput> processDefinition, TInput input, string? processName = null, ProcessRunOptions? runOptions = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(processDefinition);
            return engine.StartAsync(
                context,
                processDefinition.Define(processName),
                input,
                runOptions);
        }

        /// <summary>
        /// Starts a strongly typed process definition using a typed input value.
        /// </summary>
        public async Task<ProcessStartResult<TOutput>> StartAsync<TInput, TOutput>(OperationContext context, TypedProcessDefinition<TInput, TOutput> process, TInput input, ProcessRunOptions? runOptions = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(process);
            var started = await engine.StartAsync(
                context,
                process.Definition,
                parameters: CreateParameters(process, input),
                runOptions: runOptions
            ).ConfigureAwait(false);
            return new(
                ProcessId: started.ProcessId,
                ProcessName: started.ProcessName,
                StartedAtUtc: started.StartedAtUtc
                );
        }

        /// <summary>
        /// Waits for a strongly typed process execution to complete by process id.
        /// </summary>
        public async Task<ProcessRunResult<TOutput>> WaitForCompletionAsync<TOutput>(OperationContext context, string processId)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(processId);
            var completed = await engine.WaitForCompletionAsync(context, processId).ConfigureAwait(false);
            return ToTypedResult<TOutput>(completed);
        }

        /// <summary>
        /// Waits for a strongly typed process execution to complete.
        /// </summary>
        public async Task<ProcessRunResult<TOutput>> WaitForCompletionAsync<TOutput>(OperationContext context, ProcessStartResult<TOutput> started)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(started);
            var completed = await engine.WaitForCompletionAsync(context, started.ProcessId).ConfigureAwait(false);
            return ToTypedResult<TOutput>(completed);
        }

        /// <summary>
        /// Waits for a generated strongly typed process execution to complete.
        /// </summary>
        public Task<ProcessRunResult<TOutput>> WaitForCompletionAsync<TProcess, TInput, TOutput>(OperationContext context, ProcessStartResult<TOutput> started)
            where TProcess : IProcessDefinition<TInput, TOutput> =>
            engine.WaitForCompletionAsync(context, started);

        /// <summary>
        /// Publishes a signal to a strongly typed process execution.
        /// </summary>
        public Task SignalAsync<TOutput>(OperationContext context, ProcessStartResult<TOutput> started, string signalKey, object? payload = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(started);
            return engine.SignalAsync(context, started.ProcessId, signalKey, payload);
        }

        /// <summary>
        /// Publishes a signal to a generated strongly typed process execution.
        /// </summary>
        public Task SignalAsync<TProcess, TInput, TOutput>(OperationContext context, ProcessStartResult<TOutput> started, string signalKey, object? payload = null)
            where TProcess : IProcessDefinition<TInput, TOutput> =>
            engine.SignalAsync(context, started, signalKey, payload);

        /// <summary>
        /// Executes a generated strongly typed process definition from a process-definition instance.
        /// </summary>
        public Task<ProcessRunResult<TOutput>> ExecuteAsync<TInput, TOutput>(OperationContext context, IProcessDefinition<TInput, TOutput> processDefinition, TInput input, string? processName = null, ProcessRunOptions? runOptions = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(processDefinition);
            return engine.ExecuteAsync(context, processDefinition.Define(processName: processName), input, runOptions);
        }

        /// <summary>
        /// Executes a strongly typed process definition to completion.
        /// </summary>
        public async Task<ProcessRunResult<TOutput>> ExecuteAsync<TInput, TOutput>(OperationContext context, TypedProcessDefinition<TInput, TOutput> process, TInput input, ProcessRunOptions? runOptions = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(process);
            if (engine is ProcessEngine localEngine)
            {
                var completed = await localEngine.ExecuteAsync(
                    context,
                    process.Definition,
                    parameters: CreateParameters(process, input),
                    runOptions: runOptions
                ).ConfigureAwait(false);
                return ToTypedResult<TOutput>(completed);
            }
            var started = await engine.StartAsync(context, process, input, runOptions).ConfigureAwait(false);
            return await engine.WaitForCompletionAsync(context, started).ConfigureAwait(false);
        }
    }

    static IReadOnlyDictionary<string, object?> CreateParameters<TInput, TOutput>(TypedProcessDefinition<TInput, TOutput> process, TInput input) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [process.InputParameterName] = input
        };

    static ProcessRunResult<TOutput> ToTypedResult<TOutput>(ProcessRunResult completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return new(
            ProcessId: completed.ProcessId,
            ProcessName: completed.ProcessName,
            Result: ConvertResult<TOutput>(completed),
            FinalPlace: completed.FinalPlace,
            Variables: completed.Variables,
            Transitions: completed.Transitions,
            ExecutedEffects: completed.ExecutedEffects,
            PendingEffects: completed.PendingEffects,
            DeadLetters: completed.DeadLetters
            );
    }

    static TOutput ConvertResult<TOutput>(ProcessRunResult completed)
    {
        if (completed.Result is TOutput typed)
            return typed;

        if (completed.Result is null)
        {
            var outputType = typeof(TOutput);
            var nullableUnderlyingType = Nullable.GetUnderlyingType(outputType);
            if (outputType.IsValueType && nullableUnderlyingType is null)
            {
                throw new InvalidOperationException(
                    $"Process '{completed.ProcessName}' ({completed.ProcessId}) completed without a result, " +
                    $"but typed execution expected a non-null '{outputType.FullName}'.");
            }
            return default!;
        }

        throw new InvalidOperationException(
            $"Process '{completed.ProcessName}' ({completed.ProcessId}) completed with result type " +
            $"'{completed.Result.GetType().FullName}', but typed execution expected '{typeof(TOutput).FullName}'."
            );
    }
}
