using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Runtime;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Api.Execution.Services;

public sealed partial class ServiceRuntime
{
    // One protected read/wait lifecycle for terminal values and retained entity receipts.
    // Invocation context and values are never cached or shared across callers.
    sealed record AdmittedProcessResult(OperationContext Context, ScopeRef Scope,
        InteractionAuthorityScope Authority, ProcessExecutionValues Values);

    async ValueTask<ServiceOperationResult<T>> ReadProcessResultCoreAsync<T>(OperationContext context,
        ServiceOperation operation, ExecutionDefinitionReference process, string authorityName,
        IProcessExecutionValueRepository provider, ProcessInstanceId instance, TimeSpan? maximumWait,
        Func<AdmittedProcessResult, ServiceInvocationEvidence, ValueTask<ServiceOperationResult<T>>> project)
        where T : class
    {
        using var evidence = new ServiceInvocationEvidence(definitionReference, operation.Id, process, new(instance.Value));
        try
        {
            context.ThrowIfCancellationRequested();
            var scope = await authorization.AdmitAsync(context, operation).ConfigureAwait(false);
            if (scope is null) return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "Result access is not authorized.");
            evidence.Record("authorityAdmitted");
            var trusted = context.WithSingleEffectiveScope(scope.Kind, scope.Id, partitionKey: scope.ResolvePartitionKey());
            var authority = new InteractionAuthorityScope(authorityName, scope.Id);
            var read = await provider.GetValuesAsync(trusted, authority, instance).ConfigureAwait(false);
            if (read.State == ProcessExecutionValueReadState.InProgress && maximumWait is { } wait)
            {
                if (read.Values!.Definition != process || read.Values.ProcessInstanceId != instance)
                    return Reject(ApiResultKind.NotFound, "services.process.notFound", "No execution is visible at the declared exact definition.");
                if (provider is not IProcessExecutionCompletionWaiter waiter)
                    throw new NotSupportedException("The bound Process value provider does not support bounded completion waiting.");
                evidence.Record("completionWaitStarted");
                var completed = await waiter.WaitForCompletionAsync(trusted, authority, instance, wait).ConfigureAwait(false);
                evidence.Record(completed ? "completionWaitFinished" : "completionWaitExpired");
                var refreshedScope = await authorization.AdmitAsync(context, operation).ConfigureAwait(false);
                if (refreshedScope is null || refreshedScope != scope)
                    return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "Result access is no longer authorized.");
                if (completed)
                    read = await provider.GetValuesAsync(trusted, authority, instance).ConfigureAwait(false);
            }
            if (read.State == ProcessExecutionValueReadState.NotFound)
                return Reject(ApiResultKind.NotFound, "services.process.notFound", "No execution is visible at this target.");
            var values = read.Values!;
            if (values.Definition != process || values.ProcessInstanceId != instance)
                return Reject(ApiResultKind.NotFound, "services.process.notFound", "No execution is visible at the declared exact definition.");
            if (read.State == ProcessExecutionValueReadState.InProgress)
                return Reject(ApiResultKind.Accepted, "services.process.inProgress", "The execution has not completed.");
            if (read.State != ProcessExecutionValueReadState.Available || values.TerminalContinuation is null || values.Evidence.IsDefault)
                return Reject(ApiResultKind.InfrastructureError, "services.process.evidenceUnavailable", "Exact terminal execution evidence is unavailable.");
            evidence.Record("terminalValuesRead");
            if (values.TerminalOutcome!.Kind != ExecutionTerminalOutcomeKind.Completed)
            {
                // Only the native captured-token failure at the declared commit can establish this conflict.
                // A matching diagnostic elsewhere, or an earlier attempt, is not commit evidence.
                if (operation is ServiceProcessEntityResultOperation entityResult
                    && values.TerminalOutcome.Kind == ExecutionTerminalOutcomeKind.Failed
                    && !values.OperationFailures.IsDefault && values.OperationFailures.Length == 1
                    && values.OperationFailures[0] is var failure
                    && failure.Operation.Node == entityResult.CommitNode
                    && values.Evidence.SelectMany(item => item.Trace).LastOrDefault(trace =>
                        trace.Continuation == values.TerminalContinuation && trace.Kind == ProcessTraceEventKind.TerminalReached) is { } terminal
                    && terminal.Detail == "failed" && terminal.Activation == failure.Operation.Activation
                    && terminal.Token == failure.Operation.Token && terminal.Node == failure.Operation.Node
                    && terminal.Sequence > failure.Operation.Sequence
                    && failure.Diagnostic.Code == ProcessTransitionOperationAdapterDiagnosticCodes.SubjectChanged)
                {
                    evidence.Record("commitConflict", failure.Diagnostic.Code);
                    return new(ApiResultKind.Conflict, null, [failure.Diagnostic], evidence.Complete(ApiResultKind.Conflict));
                }
                return Reject(ApiResultKind.DomainError, "services.process.notCompleted", "The Process did not complete successfully.");
            }
            return await project(new(trusted, scope, authority, values), evidence).ConfigureAwait(false);
        }
        catch (Exception exception) { evidence.Fail(exception); throw; }

        ServiceOperationResult<T> Reject(ApiResultKind kind, string code, string message) =>
            RejectProcessResult<T>(evidence, kind, code, message);
    }

    static ServiceOperationResult<T> RejectProcessResult<T>(ServiceInvocationEvidence evidence,
        ApiResultKind kind, string code, string message) where T : class
    {
        evidence.Record("resultUnavailable", code);
        return new(kind, null,
            [new(code, kind == ApiResultKind.Accepted ? DiagnosticSeverity.Info : DiagnosticSeverity.Error, message, "/result")],
            evidence.Complete(kind));
    }
}
