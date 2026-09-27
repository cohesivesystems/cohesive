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
    {
        if (!operations.TryGetValue(operationId, out var linked))
            throw new ArgumentException("The operation is not declared.", nameof(operationId));
        var action = linked.Operation switch
        {
            ServiceProcessOperation => ProcessStartWireNames.Start,
            ServiceProcessControlOperation control => control.Action,
            _ => throw new ArgumentException("The operation is not a Process entry or lifecycle control.", nameof(operationId))
        };
        var native = NativeProcessOperation(action);
        if (native.RequestType != typeof(TRequest) || http?.Body is { } body && body.BodyType != native.RequestType)
            throw new ArgumentException("The request and HTTP body must exactly project the native Process command contract.");
        var operation = new ApiOperation(operationId, native.Kind, native.RequestType, native.ResponseType,
            id: new(ServiceOperationIdentity(operationId)),
            summary: native.Summary, description: native.Description, tags: native.Tags,
            results: native.Results.Any(result => result.Kind == ApiResultKind.ValidationFailed && result.BodyType == typeof(ExecutionApiProblem))
                ? native.Results
                : [.. native.Results, new(ApiResultKind.ValidationFailed, typeof(ExecutionApiProblem), id: "admissionValidationFailed")],
            scopePolicies: native.ScopePolicies,
            authorizationRequirements: linked.Operation.AuthorizationRequirements,
            semanticReferences: native.SemanticReferences);
        if (http is not null) operation = operation.WithHttp(http);
        return new ApiDefinition([operation]).Endpoints[0];
    }
}
