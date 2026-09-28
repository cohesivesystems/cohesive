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
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var validation = ServiceDefinitionDocuments.ValidateAndProject(declaration, out var definition);
        if (!validation.IsValid) throw new ServiceBindingValidationException(validation);
        if (!declaration.Extensions.IsEmpty)
            throw ServiceBindingValidationException.Error("services.binding.extensionsUnsupported",
                "This service profile does not support semantic extensions.", "/extensions");
        if (definition!.Operations.SingleOrDefault(operation => operation.Id == operationId)
            is not ServiceProcessEntityResultOperation result)
            throw new ArgumentException("The operation is not a declared committed-entity result read.", nameof(operationId));
        return CreateCommittedEntityResult<TResponse>(new(declaration.Metadata.DefinitionId,
            declaration.Metadata.RevisionId, declaration.Metadata.Fingerprint), result, http, scopePolicies: scopePolicies);
    }

    internal static string OperationIdentity(ExecutionDefinitionReference reference, string operationId) =>
        $"service/{Uri.EscapeDataString(reference.DefinitionId.Value)}/operation/{Uri.EscapeDataString(operationId)}";

    internal static ApiEndpoint CreateCommittedEntityResult<TResponse>(ExecutionDefinitionReference reference,
        ServiceProcessEntityResultOperation operation, HttpBinding? http,
        EntityTypeName? entity = null, ExecutionDefinitionReference? transition = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        if (http?.Body is not null)
            throw new ArgumentException("A committed-entity result read has no request body.", nameof(http));
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
