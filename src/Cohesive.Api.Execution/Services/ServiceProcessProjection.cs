using System.Collections.Frozen;
using Cohesive.Api.Services;
using Cohesive.Execution;

namespace Cohesive.Api.Execution.Services;

public sealed partial class ServiceRuntime
{
    static readonly Lazy<FrozenDictionary<string, ApiOperation>> NativeProcessOperations = new(() =>
        ExecutionControlApiCatalog.Create().Definition.Operations.ToFrozenDictionary(operation => operation.Name, StringComparer.Ordinal));

    internal static ApiOperation NativeProcessOperation(string action) => NativeProcessOperations.Value[action];

    /// <summary>Projects a declared Process entry or lifecycle control using the native API contract.</summary>
    /// <remarks>
    /// Native request types, result variants, scope policies and semantic references are retained. Service identity
    /// and authorization requirements derive from the service document. Admission validation has a distinct result
    /// variant so native control decisions and service-boundary diagnostics do not become competing result models.
    /// No dispatcher or repository is resolved during projection.
    /// </remarks>
    /// <typeparam name="TRequest">Exact native request type declared by the selected operation.</typeparam>
    /// <param name="operationId">Operation identity in the service document.</param>
    /// <param name="http">Optional medium-specific projection with an exact native body type.</param>
    /// <returns>An endpoint in the existing API model, suitable for HTTP, OpenAPI and client projection.</returns>
    /// <exception cref="ArgumentException">The operation family, request type or HTTP body type is incorrect.</exception>
    public ApiEndpoint ProjectProcess<TRequest>(string operationId, HttpBinding? http = null)
        where TRequest : class
        => ServiceApiProjection.ProjectProcess<TRequest>(Declaration, operationId, http);
}
