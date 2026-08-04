using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Projects durable Cohesive.Control state into explicit Process admission evidence.</summary>
/// <remarks>
/// The projection is deliberately one-way. Control owns measurement, recommendation, hard-limit intersection, and
/// safe-point actuation; the Process continuation owns the exact operating point it observed for branch admission.
/// </remarks>
public static class ProcessControlAdmissionProjection
{
    const string ControlAuthority = "cohesive.control/operating-point";

    /// <summary>Projects the currently effective Control concurrency point for one canonical Process Fork.</summary>
    /// <param name="fork">Canonical Fork node receiving the operating point.</param>
    /// <param name="state">Complete durable Control state whose effective point has already been applied.</param>
    /// <returns>Attributable Process activation evidence retaining the Control revision and source reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The effective Control point has no concurrency actuator.</exception>
    /// <exception cref="OverflowException">Effective concurrency exceeds the portable Process integer range.</exception>
    public static ProcessAdmissionOperatingPoint ProjectFork(
        ExecutionNodeId fork,
        ControlLoopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var concurrency = checked((int)state.OperatingPoint
            .Get(ControlActuatorKind.Concurrency)
            .Quantity.Value);
        var latestAdaptive = state.LastActuation is { } adaptive && adaptive.Revision == state.Revision
            ? adaptive.ApplicationPoint
            : null;
        var latestManual = state.LimitUpdateActuations.LastOrDefault(candidate => candidate.Revision == state.Revision)
            ?.ApplicationPoint;
        var applicationPoint = latestAdaptive ?? latestManual;
        return new(
            fork,
            concurrency,
            state.Revision.Ordinal,
            applicationPoint?.Authority ?? ControlAuthority,
            applicationPoint?.SourceReference
                ?? $"control-loop/{state.LoopId.Value}/{state.Epoch.Value}/{state.Revision.Value}");
    }
}
