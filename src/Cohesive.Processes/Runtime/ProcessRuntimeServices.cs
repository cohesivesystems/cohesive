using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Immutable runtime bundle for process execution planning and node execution.
/// </summary>
public sealed class ProcessRuntimeServices
{
    readonly Dictionary<string, IEffectHandlerBinding> handlersByRequestName = new(StringComparer.Ordinal);
    readonly Dictionary<string, ProcessPlace> placesByName = new(StringComparer.Ordinal);
    readonly JsonSerializerOptions jsonOptions = ProcessSerialization.CreateJsonOptions();

    /// <summary>
    /// Creates runtime services from fully supplied dependencies.
    /// </summary>
    public ProcessRuntimeServices(
        IProcessTransitionHost transitionHost,
        IProcessEntityRepository entityRepository,
        IProcessDeadLetterSink deadLetterSink,
        IProcessCheckpointRepository? checkpointRepository = null,
        IReadRepositoryRegistry? entityReadRepositoryRegistry = null,
        IProcessTransactionGateway? transactionGateway = null,
        IProcessWaitAdapter? waitAdapter = null,
        IProcessSignalSink? signalSink = null,
        IOperationContextScopeFactory? operationContextScopeFactory = null,
        ILoggerFactory? loggerFactory = null,
        ProcessEngineOptions? options = null
        )
    {
        ArgumentNullException.ThrowIfNull(transitionHost);
        ArgumentNullException.ThrowIfNull(entityRepository);
        ArgumentNullException.ThrowIfNull(deadLetterSink);

        TransitionHost = transitionHost;
        EntityRepository = entityRepository;
        CheckpointRepository = checkpointRepository;
        EntityReadRepositoryRegistry = entityReadRepositoryRegistry;
        TransactionGateway = transactionGateway;
        WaitAdapter = waitAdapter;
        DeadLetterSink = deadLetterSink;
        SignalSink = signalSink ?? waitAdapter as IProcessSignalSink;
        OperationContextScopeFactory = operationContextScopeFactory;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Options = options ?? new ProcessEngineOptions();

        if (Options.MaxEffectAttempts <= 0)
            throw new ArgumentException("Process engine option MaxEffectAttempts must be greater than zero.");

        RegisterPlace(ProcessPlace.WithAllCapabilities(Options.DefaultPlaceName));
    }

    /// <summary>
    /// Transition host used for entity transition decisions.
    /// </summary>
    public IProcessTransitionHost TransitionHost { get; }

    /// <summary>
    /// Entity repository used for transition execution, continuation validation, and transition persistence.
    /// </summary>
    public IProcessEntityRepository EntityRepository { get; }

    /// <summary>
    /// Optional checkpoint repository used to persist execution state.
    /// </summary>
    public IProcessCheckpointRepository? CheckpointRepository { get; }

    /// <summary>
    /// Optional batch-capable read repositories used by process-native queries.
    /// </summary>
    public IReadRepositoryRegistry? EntityReadRepositoryRegistry { get; }

    /// <summary>
    /// Optional transaction gateway used by transaction nodes.
    /// </summary>
    public IProcessTransactionGateway? TransactionGateway { get; }

    /// <summary>
    /// Optional wait adapter used for blocking wait execution.
    /// </summary>
    public IProcessWaitAdapter? WaitAdapter { get; }

    /// <summary>
    /// Dead-letter sink used for failed effect executions.
    /// </summary>
    public IProcessDeadLetterSink DeadLetterSink { get; }

    /// <summary>
    /// Optional external signal sink, when supported by the wait adapter.
    /// </summary>
    public IProcessSignalSink? SignalSink { get; }

    /// <summary>
    /// Optional operation-context scope factory used by runtime execution.
    /// </summary>
    public IOperationContextScopeFactory? OperationContextScopeFactory { get; }

    /// <summary>
    /// Optional logger factory used by runtime execution components.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Process runtime options.
    /// </summary>
    public ProcessEngineOptions Options { get; }

    /// <summary>
    /// Registers an execution place.
    /// </summary>
    public ProcessRuntimeServices RegisterPlace(ProcessPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (!placesByName.TryAdd(place.Name, place))
            throw new SemanticRuleViolationException($"A place named '{place.Name}' is already registered.");

        return this;
    }

    /// <summary>
    /// Registers a handler binding for effect-request dispatch.
    /// </summary>
    public ProcessRuntimeServices RegisterHandler(IEffectHandlerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!handlersByRequestName.TryAdd(binding.RequestName, binding))
            throw new SemanticRuleViolationException($"A handler is already registered for effect request '{binding.RequestName}'.");

        return this;
    }

    /// <summary>
    /// Registers a typed effect handler using JSON payload deserialization.
    /// </summary>
    public ProcessRuntimeServices RegisterHandler<TRequest, TResult>(IEffectHandler<TRequest, TResult> handler)
        where TRequest : IEffectRequest<TResult>
        => RegisterHandler(EffectHandlerBinding<TRequest, TResult>.FromJson(handler, jsonOptions));

    internal IDisposable? PushOperationContext(OperationContext context) =>
        OperationContextScopeFactory?.Push(context);

    internal ProcessPlace ResolvePlace(string placeName)
    {
        if (!placesByName.TryGetValue(placeName, out var place))
            throw new SemanticRuleViolationException($"Execution place '{placeName}' is not registered.");

        return place;
    }

    internal void EnsureCapability(ProcessCapability capability, ProcessExecutionContext context, string operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var currentPlace = ResolvePlace(context.CurrentPlace);
        if (currentPlace.HasCapability(capability))
            return;

        throw new ProcessCapabilityViolationException(
            $"Operation '{operation}' requires capability '{capability}' but place '{currentPlace.Name}' does not provide it.");
    }

    internal bool TryResolveHandler(string requestName, out IEffectHandlerBinding handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestName);

        if (handlersByRequestName.TryGetValue(requestName, out var resolved))
        {
            handler = resolved;
            return true;
        }

        handler = default!;
        return false;
    }

    internal IProcessCheckpointRepository? TryGetCheckpointRepository() => CheckpointRepository;

    internal IProcessTransactionGateway RequireTransactionGateway(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return TransactionGateway
            ?? throw new InvalidOperationException(
                $"Process runtime requires '{nameof(IProcessTransactionGateway)}' to {operation}, but none was configured.");
    }

    internal IProcessWaitAdapter RequireWaitAdapter(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return WaitAdapter
            ?? throw new InvalidOperationException(
                $"Process runtime requires '{nameof(IProcessWaitAdapter)}' to {operation}, but none was configured.");
    }

    internal IProcessSignalSink RequireSignalSink(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return SignalSink
            ?? throw new InvalidOperationException(
                $"Process runtime requires '{nameof(IProcessSignalSink)}' to {operation}, but none was configured.");
    }

    internal IReadRepositoryRegistry RequireEntityReadRepositoryRegistry(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return EntityReadRepositoryRegistry
            ?? throw new InvalidOperationException(
                $"Process runtime requires '{nameof(IReadRepositoryRegistry)}' to {operation}, but none was configured.");
    }

}
