using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Bounded discovery over the same atomic Process authority that admits starts.</summary>
/// <remarks>No enqueue is required after admission. Consumers rescan from the beginning after the last page;
/// concurrent insertions behind a cursor are found on that next pass. Discovery is not authorization or a claim.</remarks>
public interface IProcessInstanceSource
{
    /// <summary>Lists durable instance identities in provider-stable order after an exclusive cursor.</summary>
    /// <param name="context">Trusted store access and cancellation.</param>
    /// <param name="after">Previous last identity, or null to begin a new pass.</param>
    /// <param name="maximumInstances">Positive page bound, no greater than 1000.</param>
    /// <returns>At most the requested number of identities; an empty page ends a pass. Cursors are scoped to one provider authority.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page bound is outside 1–1000.</exception>
    Task<ImmutableArray<ProcessInstanceId>> ListInstancesAsync(OperationContext context, ProcessInstanceId? after, int maximumInstances);
}
