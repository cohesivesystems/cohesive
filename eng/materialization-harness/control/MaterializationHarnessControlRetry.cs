using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.MaterializationHarness.Control;

static class MaterializationHarnessControlRetry
{
    internal static bool IsTransient(ExecutionControlResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Disposition != ProcessControlDecisionDisposition.InvalidState)
            return false;
        return result.DiagnosticCodes.Any(static code =>
            EndsWith(code, ProcessDurableRuntimeDisposition.LeaseHeld)
            || EndsWith(code, ProcessDurableRuntimeDisposition.RevisionConflict)
            || EndsWith(code, ProcessDurableRuntimeDisposition.StaleFence)
            || EndsWith(code, ProcessDurableRuntimeDisposition.LeaseExpired));
    }

    static bool EndsWith(string code, ProcessDurableRuntimeDisposition disposition) =>
        code.EndsWith($".{disposition}", StringComparison.Ordinal);
}
