using System.Diagnostics;
using Cohesive.Api.Services;
using Cohesive.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>One payload-free event stream projected into native telemetry and inspectable service evidence.</summary>
internal sealed class ServiceInvocationEvidence : IDisposable
{
    readonly ExecutionDefinitionReference service;
    readonly ExecutionDefinitionReference operationDefinition;
    readonly string operation;
    readonly ActivationId activation;
    readonly Activity? activity;
    readonly List<NormalizedExecutionTraceEvent> events = [];
    ExecutionTelemetryOutcome outcome = ExecutionTelemetryOutcome.Failed;
    Exception? failure;

    internal ServiceInvocationEvidence(ExecutionDefinitionReference service, string operation,
        ExecutionDefinitionReference operationDefinition, ActivationId activation)
    {
        this.service = service;
        this.operation = operation;
        this.operationDefinition = operationDefinition;
        this.activation = activation;
        activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Activation);
        activity?.SetTag("cohesive.service.definition", service.DefinitionId.Value);
        activity?.SetTag("cohesive.service.revision", service.RevisionId.Value);
        activity?.SetTag("cohesive.service.operation", operation);
    }

    internal void Record(string kind, string? diagnostic = null)
    {
        events.Add(new(events.Count, kind, new(operation), relatedDefinition: operationDefinition, detail: diagnostic));
        if (activity?.IsAllDataRequested == true)
            activity.AddEvent(new ActivityEvent(kind, tags: diagnostic is null ? null
                : new ActivityTagsCollection { ["cohesive.service.diagnostic"] = diagnostic }));
    }

    internal NormalizedExecutionTrace Complete(ApiResultKind kind)
    {
        outcome = kind switch
        {
            ApiResultKind.Success => ExecutionTelemetryOutcome.Succeeded,
            ApiResultKind.Accepted => ExecutionTelemetryOutcome.Pending,
            ApiResultKind.InfrastructureError => ExecutionTelemetryOutcome.Failed,
            _ => ExecutionTelemetryOutcome.Rejected
        };
        var trace = new NormalizedExecutionTrace(NormalizedExecutionTrace.CurrentSchemaVersion, ServiceDefinitionDocuments.Kind,
            service, null, activation, ApiWireNames.ResultKind(kind), null, null, [.. events]);
        ExecutionTelemetry.CorrelateActivity(activity, trace: trace);
        return trace;
    }

    internal void Fail(Exception exception)
    {
        failure = exception;
        outcome = exception is OperationCanceledException ? ExecutionTelemetryOutcome.Cancelled : ExecutionTelemetryOutcome.Failed;
        Record(exception is OperationCanceledException ? "invocationCancelled" : "invocationFailed");
    }

    public void Dispose() => ExecutionTelemetry.CompleteActivity(activity, outcome, failure);
}
