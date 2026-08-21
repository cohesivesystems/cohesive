using System.Globalization;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.MaterializationHarness.Control;

static class MaterializationHarnessControlObservation
{
    internal static MaterializationHarnessControlOperationObservation Create(
        string operation,
        ApiResultKind resultKind,
        object body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(body);
        return body switch
        {
            ProcessStartResult start => new(
                Operation: operation,
                ResultKind: resultKind,
                Disposition: start.Disposition.ToString(),
                BeforeRevision: null,
                AfterRevision: null,
                CurrentRevision: start.Admission is null ? null : Revision(start.Admission.ControlRevision.Value),
                ControlMode: null),
            ExecutionControlResult control => new(
                Operation: operation,
                ResultKind: resultKind,
                Disposition: control.Disposition.ToString(),
                BeforeRevision: control.Receipt is null ? null : Revision(control.Receipt.BeforeRevision.Value),
                AfterRevision: control.Receipt is null ? null : Revision(control.Receipt.AfterRevision.Value),
                CurrentRevision: Revision(control.Status.ControlRevision.Value),
                ControlMode: control.Status.ControlMode),
            ControlLimitUpdateResult limits => new(
                Operation: operation,
                ResultKind: resultKind,
                Disposition: limits.Disposition.ToString(),
                BeforeRevision: null,
                AfterRevision: null,
                CurrentRevision: Revision(limits.Revision.Value),
                ControlMode: null),
            ExecutionApiProblem problem => new(
                Operation: operation,
                ResultKind: resultKind,
                Disposition: problem.Code,
                BeforeRevision: null,
                AfterRevision: null,
                CurrentRevision: null,
                ControlMode: null),
            _ => new(
                Operation: operation,
                ResultKind: resultKind,
                Disposition: "Available",
                BeforeRevision: null,
                AfterRevision: null,
                CurrentRevision: null,
                ControlMode: null)
        };
    }

    static long Revision(string value) =>
        long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
