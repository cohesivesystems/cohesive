using Cohesive.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Attaches a prepared service process to persistent admission, execution and protected result reads.</summary>
/// <remarks>Preparation must not start execution. The host owns native clients and worker lifecycle. A provider must
/// retain accepted work and its retry identities across host replacement; in-memory admission cannot implement this contract.</remarks>
public interface IDurableServiceProcessProvider
{
    /// <summary>Admits the exact process and binds the service's start and result operations before the worker starts.</summary>
    /// <param name="process">Compiled process and exact repository/query bindings.</param>
    /// <param name="declaration">Service requiring durable admission and result access.</param>
    /// <param name="authority">Logical execution authority, combined with the authorized invocation scope.</param>
    /// <param name="authorization">Invocation-time authorization, also applied independently to result polling.</param>
    /// <returns>Runtime sharing the provider's durable admission and result authority.</returns>
    /// <exception cref="ServiceBindingValidationException">The service or process cannot be realized by the provider.</exception>
    /// <exception cref="InvalidOperationException">Preparation has already completed or native registration is missing.</exception>
    ServiceRuntime Bind(PreparedServiceProcess process, ExecutionDefinitionDocument declaration,
        string authority, IServiceInvocationAuthorization authorization);
}
