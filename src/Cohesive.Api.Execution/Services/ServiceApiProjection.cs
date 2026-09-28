using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Api.Execution.Services;

/// <summary>Projects portable service declarations without constructing execution or repository bindings.</summary>
public static class ServiceApiProjection
{
    /// <summary>Derives a result endpoint's identity, authorization and outcome contracts from its service document.</summary>
    /// <remarks>The response view belongs to the medium. Exact Process/Transition/entity binding validation still
    /// occurs when constructing the runtime. This projection resolves no dispatcher, plan or repository.
    /// Scope policies attach host-owned scope selection at the medium boundary; they do not replace service authorization.</remarks>
    /// <exception cref="ArgumentException">The document, operation family or HTTP body is invalid.</exception>
    public static ApiEndpoint ProjectCommittedEntityResult<TResponse>(ExecutionDefinitionDocument declaration,
        string operationId, HttpBinding? http = null, IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
        => ProjectDeclaredResult<TResponse, ServiceProcessEntityResultOperation>(declaration, operationId, http, scopePolicies);

    /// <summary>Projects independently authorized terminal-value reads without resolving execution infrastructure.</summary>
    /// <remarks>The medium supplies its response view. Runtime reads still validate the retained value against
    /// the exact Process result contract. Scope-selection metadata does not grant access.</remarks>
    /// <exception cref="ArgumentException">The declaration, selected operation, or HTTP body is invalid.</exception>
    public static ApiEndpoint ProjectProcessResult<TResponse>(ExecutionDefinitionDocument declaration,
        string operationId, HttpBinding? http = null, IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
        => ProjectDeclaredResult<TResponse, ServiceProcessResultOperation>(declaration, operationId, http, scopePolicies);

    /// <summary>Projects native Process entry/control contracts directly from a portable service declaration.</summary>
    /// <remarks>Runtime construction is deferred. The exact native request and result contracts remain authoritative;
    /// supplied scope policies attach host scope selection without granting authorization.</remarks>
    /// <exception cref="ArgumentException">The operation family or native request/body contract is invalid.</exception>
    public static ApiEndpoint ProjectProcess<TRequest>(ExecutionDefinitionDocument declaration, string operationId,
        HttpBinding? http = null, IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        var declared = GetOperation(declaration, operationId);
        var native = declared switch
        {
            ServiceProcessOperation => ServiceRuntime.NativeProcessOperation(ProcessStartWireNames.Start),
            ServiceProcessControlOperation control => ServiceRuntime.NativeLifecycleOperation(control.Action),
            _ => throw new ArgumentException("The operation is not a Process entry or lifecycle control.", nameof(operationId))
        };
        if (native.RequestType != typeof(TRequest) || http?.Body is { } body && body.BodyType != native.RequestType)
            throw new ArgumentException("The request and HTTP body must exactly project the native Process command contract.");
        return CreateProcess(declaration, declared, native, typeof(TRequest), http, scopePolicies);
    }

    /// <summary>Attaches a typed medium request to a declared Process start while retaining native admission outcomes.</summary>
    /// <remarks>The medium projects caller-owned retry identities and domain input. The runtime validates that input
    /// against the exact Process contract and derives trusted authority; this request type is not a new Process contract.</remarks>
    /// <exception cref="ArgumentException">The operation is not a start or the HTTP body differs from the request type.</exception>
    public static ApiEndpoint ProjectProcessInput<TRequest>(ExecutionDefinitionDocument declaration, string operationId,
        HttpBinding? http = null, IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        var declared = GetOperation(declaration, operationId);
        if (declared is not ServiceProcessOperation)
            throw new ArgumentException("Domain input projection requires a declared Process start.", nameof(operationId));
        if (http?.Body is { } body && body.BodyType != typeof(TRequest))
            throw new ArgumentException("The HTTP body must match the medium request type.", nameof(http));
        return CreateProcess(declaration, declared, ServiceRuntime.NativeProcessOperation(ProcessStartWireNames.Start),
            typeof(TRequest), http, scopePolicies);
    }

    static ApiEndpoint CreateProcess(ExecutionDefinitionDocument declaration, ServiceOperation declared, ApiOperation native,
        Type requestType, HttpBinding? http, IReadOnlyList<ApiScopePolicy>? scopePolicies)
    {
        var operationId = declared.Id;
        var reference = new ExecutionDefinitionReference(declaration.Metadata.DefinitionId,
            declaration.Metadata.RevisionId, declaration.Metadata.Fingerprint);
        var operation = new ApiOperation(operationId, native.Kind, requestType, native.ResponseType,
            id: new(OperationIdentity(reference, operationId)),
            summary: native.Summary, description: native.Description, tags: native.Tags,
            results: native.Results.Any(result => result.Kind == ApiResultKind.ValidationFailed && result.BodyType == typeof(ExecutionApiProblem))
                ? native.Results
                : [.. native.Results, new(ApiResultKind.ValidationFailed, typeof(ExecutionApiProblem), id: "admissionValidationFailed")],
            scopePolicies: scopePolicies ?? native.ScopePolicies,
            authorizationRequirements: declared.AuthorizationRequirements,
            semanticReferences: native.SemanticReferences);
        if (http is not null) operation = operation.WithHttp(http);
        return new ApiDefinition([operation]).Endpoints[0];
    }

    static ServiceOperation GetOperation(ExecutionDefinitionDocument declaration, string operationId)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var validation = ServiceDefinitionDocuments.ValidateAndProject(declaration, out var definition);
        if (!validation.IsValid) throw new ServiceBindingValidationException(validation);
        if (!declaration.Extensions.IsEmpty)
            throw ServiceBindingValidationException.Error("services.binding.extensionsUnsupported",
                "This service profile does not support semantic extensions.", "/extensions");
        return definition!.Operations.SingleOrDefault(operation => operation.Id == operationId)
            ?? throw new ArgumentException("The operation is not declared.", nameof(operationId));
    }

    static ApiEndpoint ProjectDeclaredResult<TResponse, TOperation>(ExecutionDefinitionDocument declaration,
        string operationId, HttpBinding? http, IReadOnlyList<ApiScopePolicy>? scopePolicies)
        where TOperation : ServiceOperation
    {
        if (GetOperation(declaration, operationId) is not TOperation result)
            throw new ArgumentException("The operation is not a declared Process result read.", nameof(operationId));
        return CreateResult<TResponse>(new(declaration.Metadata.DefinitionId,
            declaration.Metadata.RevisionId, declaration.Metadata.Fingerprint), result, http, scopePolicies: scopePolicies);
    }

    internal static string OperationIdentity(ExecutionDefinitionReference reference, string operationId) =>
        $"service/{Uri.EscapeDataString(reference.DefinitionId.Value)}/operation/{Uri.EscapeDataString(operationId)}";

    internal static ApiEndpoint CreateCommittedEntityResult<TResponse>(ExecutionDefinitionReference reference,
        ServiceProcessEntityResultOperation operation, HttpBinding? http,
        EntityTypeName? entity = null, ExecutionDefinitionReference? transition = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null) =>
        CreateResult<TResponse>(reference, operation, http, entity, transition, scopePolicies);

    static ApiEndpoint CreateResult<TResponse>(ExecutionDefinitionReference reference,
        ServiceOperation operation, HttpBinding? http,
        EntityTypeName? entity = null, ExecutionDefinitionReference? transition = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        if (http?.Body is not null)
            throw new ArgumentException("A Process result read has no request body.", nameof(http));
        var projected = new ApiOperation(operation.Id, ApiOperationKind.Query, typeof(string), typeof(TResponse),
            id: new(OperationIdentity(reference, operation.Id)), entity: entity,
            transitionReference: transition,
            authorizationRequirements: operation.AuthorizationRequirements, scopePolicies: scopePolicies,
            results: [new(ApiResultKind.Success, typeof(TResponse), isPrimary: true),
                new(ApiResultKind.Accepted, typeof(ApiProblem)), new(ApiResultKind.ValidationFailed, typeof(ApiValidationProblem)),
                new(ApiResultKind.Forbidden, typeof(ApiProblem)),
                new(ApiResultKind.Conflict, typeof(ApiProblem)), new(ApiResultKind.PreconditionFailed, typeof(ApiProblem)),
                new(ApiResultKind.NotFound, typeof(ApiProblem)), new(ApiResultKind.DomainError, typeof(ApiProblem)),
                new(ApiResultKind.InfrastructureError, typeof(ApiProblem))]);
        if (http is not null) projected = projected.WithHttp(http);
        return new ApiDefinition([projected]).Endpoints[0];
    }
}
