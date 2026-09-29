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
        if (declared is ServiceProcessOperation process) ServiceProcessBinding.ValidateExecution(process);
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
        if (declared is not ServiceProcessOperation process)
            throw new ArgumentException("Domain input projection requires a declared Process start.", nameof(operationId));
        ServiceProcessBinding.ValidateExecution(process);
        if (http?.Body is { } body && body.BodyType != typeof(TRequest))
            throw new ArgumentException("The HTTP body must match the medium request type.", nameof(http));
        return CreateProcess(declaration, declared, ServiceRuntime.NativeProcessOperation(ProcessStartWireNames.Start),
            typeof(TRequest), http, scopePolicies);
    }

    /// <summary>Projects an HTTP command that starts a Process and offers a bounded committed-entity response.</summary>
    /// <remarks>This is a medium composition of two independently authorized operations, not a new workflow.
    /// The start and result must reference the same exact Process. Start authorization remains endpoint admission;
    /// the result operation must authorize disclosure separately when invoked. The pending response belongs to
    /// the medium and must preserve the admitted execution identity for later result retrieval.
    /// This projection alone performs no start, wait, result read or infrastructure resolution.</remarks>
    /// <exception cref="ArgumentException">Operation families, exact Process references or request body disagree.</exception>
    public static ApiEndpoint ProjectProcessEntityCommand<TRequest, TResponse, TPending>(
        ExecutionDefinitionDocument declaration, string startOperationId, string resultOperationId,
        HttpBinding? http = null, IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        var start = ProjectProcessInput<TRequest>(declaration, startOperationId, http, scopePolicies);
        if (GetOperation(declaration, resultOperationId) is not ServiceProcessEntityResultOperation result)
            throw new ArgumentException("The response operation must read a committed entity.", nameof(resultOperationId));
        var entry = (ServiceProcessOperation)GetOperation(declaration, startOperationId);
        if (entry.Execution is not null)
            throw new ArgumentException("An explicit admission-only operation cannot be projected as terminal entity completion.", nameof(startOperationId));
        if (entry.Process != result.Process)
            throw new ArgumentException("Start and result must reference the same exact Process definition, revision and fingerprint.",
                nameof(resultOperationId));
        var response = ProjectCommittedEntityResult<TResponse>(declaration, resultOperationId);
        var operation = new ApiOperation(startOperationId, ApiOperationKind.Command, typeof(TRequest), typeof(TResponse),
            id: start.Operation.Id, authorizationRequirements: entry.AuthorizationRequirements,
            scopePolicies: scopePolicies,
            results: response.Operation.Results.Select(item => item.Kind == ApiResultKind.Accepted
                ? new ApiResultDefinition(ApiResultKind.Accepted, typeof(TPending)) : item).ToArray());
        if (http is not null) operation = operation.WithHttp(http);
        return new ApiDefinition([operation]).Endpoints[0];
    }

    static ApiEndpoint CreateProcess(ExecutionDefinitionDocument declaration, ServiceOperation declared, ApiOperation native,
        Type requestType, HttpBinding? http, IReadOnlyList<ApiScopePolicy>? scopePolicies)
    {
        var operationId = declared.Id;
        var reference = new ExecutionDefinitionReference(declaration.Metadata.DefinitionId,
            declaration.Metadata.RevisionId, declaration.Metadata.Fingerprint);
        var nativeResults = declared is ServiceProcessOperation { Execution.Completion: ServiceProcessCompletion.Admission }
            ? native.Results.Select(result => result.Kind == ApiResultKind.Success
                ? new ApiResultDefinition(ApiResultKind.Accepted, result.BodyType, result.IsPrimary,
                    description: "The Process was durably admitted; terminal completion is not promised.")
                : result).ToArray()
            : native.Results;
        var operation = new ApiOperation(operationId, native.Kind, requestType, native.ResponseType,
            id: new(OperationIdentity(reference, operationId)),
            summary: native.Summary, description: native.Description, tags: native.Tags,
            results: nativeResults.Any(result => result.Kind == ApiResultKind.ValidationFailed && result.BodyType == typeof(ExecutionApiProblem))
                ? nativeResults
                : [.. nativeResults, new(ApiResultKind.ValidationFailed, typeof(ExecutionApiProblem), id: "admissionValidationFailed")],
            scopePolicies: scopePolicies ?? native.ScopePolicies,
            authorizationRequirements: declared.AuthorizationRequirements,
            semanticReferences: native.SemanticReferences);
        if (http is not null) operation = operation.WithHttp(http);
        return new ApiDefinition([operation]).Endpoints[0];
    }

    /// <summary>Projects a declared query with typed medium input/output and independent admission failures.</summary>
    /// <remarks>The native query owns parameters, output demand and evaluation diagnostics. The response projection
    /// receives the complete outcome, including failed evaluations. Scope selection does not grant access.</remarks>
    public static ApiEndpoint ProjectQuery<TRequest, TResponse>(ExecutionDefinitionDocument declaration, string operationId,
        HttpBinding? http = null, IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        if (GetOperation(declaration, operationId) is not ServiceQueryOperation query)
            throw new ArgumentException("The operation is not a declared query.", nameof(operationId));
        if (http?.Body is { } body && body.BodyType != typeof(TRequest))
            throw new ArgumentException("The HTTP body must match the medium request type.", nameof(http));
        var reference = new ExecutionDefinitionReference(declaration.Metadata.DefinitionId,
            declaration.Metadata.RevisionId, declaration.Metadata.Fingerprint);
        var operation = new ApiOperation(operationId, ApiOperationKind.Query, typeof(TRequest), typeof(TResponse),
            id: new(OperationIdentity(reference, operationId)), scopePolicies: scopePolicies,
            authorizationRequirements: query.AuthorizationRequirements,
            results: [new(ApiResultKind.Success, typeof(TResponse), isPrimary: true),
                new(ApiResultKind.ValidationFailed, typeof(TResponse), id: "queryEvaluationFailed"),
                new(ApiResultKind.ValidationFailed, typeof(ApiValidationProblem), id: "admissionValidationFailed"),
                new(ApiResultKind.Forbidden, typeof(ApiProblem))]);
        if (http is not null) operation = operation.WithHttp(http);
        return new ApiDefinition([operation]).Endpoints[0];
    }

    /// <summary>Projects terminal ephemeral invocation from the exact typed Process contracts without resolving a runtime.</summary>
    /// <exception cref="ArgumentException">The definition, policy or body contract does not match.</exception>
    public static ApiEndpoint ProjectEphemeralProcess<TInput, TOutput>(ExecutionDefinitionDocument declaration,
        string operationId, Cohesive.Processes.Authoring.Process<TInput, TOutput> process, HttpBinding? http = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        return ProjectEphemeralProcess<TInput, TOutput, TInput, TOutput>(declaration, operationId, process, http, scopePolicies);
    }

    /// <summary>Projects an ephemeral Process with independent medium request and response contracts.</summary>
    /// <remarks>The Process remains the execution authority. The adapter must supply pure mappings between
    /// the medium contracts and the exact Process input and public output; these mappings cannot orchestrate work.</remarks>
    /// <param name="declaration">Canonical service declaration.</param>
    /// <param name="operationId">Declared ephemeral terminal operation.</param>
    /// <param name="process">Exact typed Process referenced by the operation.</param>
    /// <param name="http">Optional HTTP binding whose body must match the medium request.</param>
    /// <param name="scopePolicies">Optional medium scope policies.</param>
    /// <returns>The endpoint derived from the declared operation and medium contracts.</returns>
    /// <exception cref="ArgumentException">The operation, Process, or HTTP body is incompatible.</exception>
    /// <exception cref="ServiceBindingValidationException">The declaration or Process is invalid.</exception>
    public static ApiEndpoint ProjectEphemeralProcess<TRequest, TResponse, TInput, TOutput>(
        ExecutionDefinitionDocument declaration, string operationId,
        Cohesive.Processes.Authoring.Process<TInput, TOutput> process, HttpBinding? http = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.IsValid) throw new ServiceBindingValidationException(process.Validation);
        if (GetOperation(declaration, operationId) is not ServiceProcessOperation operation
            || operation.Process != process.Reference
            || operation.Execution is not { Lifetime: ProcessExecutionLifetime.Ephemeral, Completion: ServiceProcessCompletion.Terminal })
            throw new ArgumentException("Terminal projection requires the exact ephemeral Process declaration.", nameof(operationId));
        if (http?.Body is { } body && body.BodyType != typeof(TRequest))
            throw new ArgumentException("HTTP input must match the medium request contract.", nameof(http));
        var projected = new ApiOperation(operation.Id, ApiOperationKind.Command, typeof(TRequest), typeof(TResponse),
            id: new(OperationIdentity(new(declaration.Metadata.DefinitionId, declaration.Metadata.RevisionId, declaration.Metadata.Fingerprint), operationId)),
            authorizationRequirements: operation.AuthorizationRequirements, scopePolicies: scopePolicies,
            results: [new(ApiResultKind.Success, typeof(TResponse), isPrimary: true),
                new(ApiResultKind.Forbidden, typeof(ApiProblem)),
                new(ApiResultKind.ValidationFailed, typeof(ApiValidationProblem)),
                new(ApiResultKind.DomainError, typeof(ApiProblem)),
                new(ApiResultKind.InfrastructureError, typeof(ApiProblem))]);
        if (http is not null) projected = projected.WithHttp(http);
        return new ApiDefinition([projected]).Endpoints[0];
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
