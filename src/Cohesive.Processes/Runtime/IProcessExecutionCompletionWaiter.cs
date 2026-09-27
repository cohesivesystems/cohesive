using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Processes.Runtime;

/// <summary>Optional provider capability for bounded waiting on an already admitted logical Process.</summary>
/// <remarks>Waiting establishes neither authorization nor success. It must not start, retry, terminate or
/// cancel execution. Callers read canonical results separately and recheck authorization after waiting.</remarks>
public interface IProcessExecutionCompletionWaiter
{
    /// <summary>Waits for terminal provider status within the supplied bound.</summary>
    /// <param name="context">Invocation cancellation; cancellation stops this wait only.</param>
    /// <param name="authorityScope">Previously authorized logical execution authority.</param>
    /// <param name="processInstanceId">Already admitted logical Process instance.</param>
    /// <param name="maximumWait">Positive finite duration supported by the provider.</param>
    /// <returns>True when terminal status was observed; false when the wait bound expired.</returns>
    /// <exception cref="ArgumentException">The instance or duration is invalid.</exception>
    /// <exception cref="ArgumentNullException">Context or authority is null.</exception>
    /// <exception cref="NotSupportedException">The selected provider cannot wait on canonical executions.</exception>
    /// <exception cref="OperationCanceledException">Invocation cancellation was requested.</exception>
    /// <remarks>Provider failures propagate. A true result includes failed or cancelled executions and
    /// does not imply that a canonical terminal result artifact remains available.</remarks>
    ValueTask<bool> WaitForCompletionAsync(OperationContext context, InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId, TimeSpan maximumWait);
}
