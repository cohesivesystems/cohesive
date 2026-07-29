using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.Api.Execution;

/// <summary>Stable wire coordinates owned by the execution-control API catalog.</summary>
public static class ExecutionControlApiWireNames
{
    /// <summary>Authority that owns the transport-neutral API declaration.</summary>
    public const string SemanticAuthority = "cohesive.api.execution-control";

    /// <summary>Gets the canonical API semantic path for one declared operation.</summary>
    /// <param name="action">Canonical action name from the nine-operation execution-control surface.</param>
    /// <returns>The semantic operation path owned by this API catalog.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a declared action.</exception>
    public static ExecutionSemanticPath OperationPath(string action) => action switch
    {
        ProcessStartWireNames.Start
            or ExecutionControlWireNames.Inspect
            or ExecutionControlWireNames.Signal
            or ExecutionControlWireNames.Pause
            or ExecutionControlWireNames.Continue
            or ExecutionControlWireNames.RestartAttempt
            or ExecutionControlWireNames.Cancel
            or ExecutionControlWireNames.Terminate
            or ControlLimitUpdateWireNames.UpdateLimits => new(["operations", action]),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported execution-control API action.")
    };

    /// <summary>Gets the stable authorization requirement identity for one declared operation.</summary>
    /// <param name="action">Canonical action name from the nine-operation execution-control surface.</param>
    /// <returns>The operation's transport-neutral authorization requirement identity.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a declared action.</exception>
    public static string AuthorizationRequirement(string action) =>
        $"{SemanticAuthority}.{OperationPath(action).Segments[^1]}";
}

/// <summary>
/// Typed handles for the transport-neutral execution-control API surface.
/// </summary>
/// <remarks>
/// Endpoint handles and semantic result definitions are the binding authority for HTTP, CLI, generated-client,
/// and in-memory projections. Adapters must bind these handles rather than repeating action or route strings.
/// </remarks>
public sealed class ExecutionControlApiCatalog
{
    /// <summary>Current semantic schema version of the execution-control API declaration.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-execution-control-api/v1");

    ExecutionControlApiCatalog(
        ApiDefinition definition,
        ApiEndpoint start,
        ApiEndpoint inspect,
        ApiEndpoint signal,
        ApiEndpoint pause,
        ApiEndpoint continueProcess,
        ApiEndpoint restartAttempt,
        ApiEndpoint cancel,
        ApiEndpoint terminate,
        ApiEndpoint updateLimits)
    {
        Definition = definition;
        Start = start;
        Inspect = inspect;
        Signal = signal;
        Pause = pause;
        Continue = continueProcess;
        RestartAttempt = restartAttempt;
        Cancel = cancel;
        Terminate = terminate;
        UpdateLimits = updateLimits;
    }

    /// <summary>Complete immutable semantic API definition in stable operation order.</summary>
    public ApiDefinition Definition { get; }

    /// <summary>Process-start admission endpoint.</summary>
    public ApiEndpoint Start { get; }

    /// <summary>Read-only Process status inspection endpoint.</summary>
    public ApiEndpoint Inspect { get; }

    /// <summary>Canonical Signal-admission endpoint.</summary>
    public ApiEndpoint Signal { get; }

    /// <summary>Invariant-safe Process pause endpoint.</summary>
    public ApiEndpoint Pause { get; }

    /// <summary>Process continuation endpoint that retains the current attempt.</summary>
    public ApiEndpoint Continue { get; }

    /// <summary>Explicit Process-attempt replacement endpoint.</summary>
    public ApiEndpoint RestartAttempt { get; }

    /// <summary>Cooperative Process cancellation endpoint.</summary>
    public ApiEndpoint Cancel { get; }

    /// <summary>Immediate irreversible Process termination endpoint.</summary>
    public ApiEndpoint Terminate { get; }

    /// <summary>Bounded Control operating-limit update endpoint.</summary>
    public ApiEndpoint UpdateLimits { get; }

    /// <summary>Creates the canonical nine-operation transport-neutral API declaration.</summary>
    /// <returns>
    /// A catalog whose endpoint identities, request contracts, result variants, authorization requirements, and
    /// semantic provenance all derive from their owning Cohesive authorities.
    /// </returns>
    public static ExecutionControlApiCatalog Create()
    {
        var builder = Api.Define(ExecutionControlApiWireNames.SemanticAuthority);

        var start = Describe(
                builder.Command(ProcessStartWireNames.Start),
                ProcessStartWireNames.Start,
                ProcessStartWireNames.SemanticAuthority,
                ProcessStartRequest.CurrentSchemaVersion,
                ProcessStartWireNames.RequestPath)
            .Accepts<ProcessStartRequest>()
            .Returns<ProcessStartResult>()
            .Result<ProcessStartResult>(ApiResultKind.Conflict)
            .Result<ExecutionApiProblem>(ApiResultKind.Forbidden)
            .Result<ExecutionApiProblem>(ApiResultKind.ValidationFailed)
            .Build();

        var inspect = DescribeLifecycle(
                builder.Query(ExecutionControlWireNames.Inspect),
                ExecutionControlWireNames.Inspect)
            .Accepts<InspectProcessCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var signal = DescribeLifecycle(
                builder.Command(ExecutionControlWireNames.Signal),
                ExecutionControlWireNames.Signal)
            .Accepts<SignalProcessCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var pause = DescribeLifecycle(
                builder.Command(ExecutionControlWireNames.Pause),
                ExecutionControlWireNames.Pause)
            .Accepts<PauseProcessCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var continueProcess = DescribeLifecycle(
                builder.Command(ExecutionControlWireNames.Continue),
                ExecutionControlWireNames.Continue)
            .Accepts<ContinueProcessCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var restartAttempt = DescribeLifecycle(
                builder.Command(ExecutionControlWireNames.RestartAttempt),
                ExecutionControlWireNames.RestartAttempt)
            .Accepts<RestartProcessAttemptCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var cancel = DescribeLifecycle(
                builder.Command(ExecutionControlWireNames.Cancel),
                ExecutionControlWireNames.Cancel)
            .Accepts<CancelProcessCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var terminate = DescribeLifecycle(
                builder.Command(ExecutionControlWireNames.Terminate),
                ExecutionControlWireNames.Terminate)
            .Accepts<TerminateProcessCommand>()
            .Returns<ExecutionControlResult>()
            .AddLifecycleResults()
            .Build();

        var updateLimits = Describe(
                builder.Command(ControlLimitUpdateWireNames.UpdateLimits),
                ControlLimitUpdateWireNames.UpdateLimits,
                ControlLimitUpdateWireNames.SemanticAuthority,
                ControlLoopDefinition.CurrentSchemaVersion,
                ControlLimitUpdateWireNames.CommandPath)
            .Accepts<ControlLimitUpdateCommand>()
            .Returns<ControlLimitUpdateResult>()
            .Result<ControlLimitUpdateResult>(ApiResultKind.PreconditionFailed)
            .Result<ControlLimitUpdateResult>(ApiResultKind.Conflict)
            .Result<ControlLimitUpdateResult>(ApiResultKind.ValidationFailed)
            .Result<ExecutionApiProblem>(ApiResultKind.Forbidden)
            .Result<ExecutionApiProblem>(ApiResultKind.NotFound)
            .Build();

        return new(
            builder.Build(),
            start,
            inspect,
            signal,
            pause,
            continueProcess,
            restartAttempt,
            cancel,
            terminate,
            updateLimits);
    }

    /// <summary>Gets the unique semantic result variant of one operation by result kind.</summary>
    /// <param name="endpoint">Typed endpoint handle owned by this catalog.</param>
    /// <param name="kind">Semantic result kind to resolve.</param>
    /// <returns>The exact declared result variant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The endpoint is not in this catalog, or zero or multiple variants declare <paramref name="kind"/>.
    /// </exception>
    public ApiResultDefinition GetResult(ApiEndpoint endpoint, ApiResultKind kind)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var operation = Definition.GetOperation(endpoint);
        ApiResultDefinition? match = null;
        for (var i = 0; i < operation.Results.Count; i++)
        {
            var candidate = operation.Results[i];
            if (candidate.Kind != kind)
                continue;

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Execution API endpoint '{endpoint.Id}' declares multiple '{kind}' result variants.");
            }

            match = candidate;
        }

        return match ?? throw new InvalidOperationException(
            $"Execution API endpoint '{endpoint.Id}' does not declare a '{kind}' result variant.");
    }

    static OperationBuilder<TParent> DescribeLifecycle<TParent>(
        OperationBuilder<TParent> operation,
        string action) =>
        Describe(
            operation,
            action,
            ExecutionControlWireNames.SemanticAuthority,
            ProcessControlCommand.CurrentSchemaVersion,
            ExecutionControlWireNames.CommandPath(action));

    static OperationBuilder<TParent> Describe<TParent>(
        OperationBuilder<TParent> operation,
        string action,
        string kernelAuthority,
        ExecutionIrSchemaVersion kernelSchema,
        ExecutionSemanticPath kernelPath)
    {
        var requirement = new ApiAuthorizationRequirement(ExecutionControlApiWireNames.AuthorizationRequirement(action));
        return operation
            .Requirement(requirement)
            .SemanticReference(new(
                ExecutionControlApiWireNames.SemanticAuthority,
                CurrentSchemaVersion,
                ExecutionControlApiWireNames.OperationPath(action)))
            .SemanticReference(new(
                kernelAuthority,
                kernelSchema,
                kernelPath))
            .Tag("Execution Control");
    }
}

static class ExecutionControlApiOperationBuilderExtensions
{
    internal static OperationBuilder<TParent> AddLifecycleResults<TParent>(
        this OperationBuilder<TParent> operation) =>
        operation
            .Result<ExecutionControlResult>(ApiResultKind.PreconditionFailed)
            .Result<ExecutionControlResult>(ApiResultKind.Conflict)
            .Result<ExecutionControlResult>(ApiResultKind.ValidationFailed)
            .Result<ExecutionApiProblem>(ApiResultKind.Forbidden)
            .Result<ExecutionApiProblem>(ApiResultKind.NotFound);
}
