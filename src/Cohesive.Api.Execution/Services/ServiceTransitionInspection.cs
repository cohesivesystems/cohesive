using Cohesive.Api.Services;
using Cohesive.Model;
using Cohesive.Model.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>Complete commands supplied for an entity's homogeneous command family.</summary>
/// <typeparam name="TInput">The exact CLR projection shared by the selected entity operations.</typeparam>
/// <param name="Inputs">Commands keyed by service operation identity; omitted entries require input.</param>
public sealed record ServiceTransitionInspection<TInput>(IReadOnlyDictionary<string, TInput>? Inputs = null);

/// <summary>Public action availability projected from runtime discovery.</summary>
/// <param name="OperationId">Service operation identity.</param>
/// <param name="ApiOperationId">Generated API identity for matching the invocation client.</param>
/// <param name="Kind">Native availability classification.</param>
/// <param name="ConcurrencyToken">Opaque version token to submit with invocation.</param>
/// <param name="Diagnostics">Sanitized reasons for unavailable or indeterminate actions.</param>
public sealed record ServiceTransitionAction(string OperationId, string ApiOperationId,
    ServiceTransitionAvailabilityKind Kind, string ConcurrencyToken, IReadOnlyList<ApiValidationIssue> Diagnostics);

public sealed partial class ServiceRuntime
{
    /// <summary>Projects entity discovery into a typed API endpoint.</summary>
    /// <typeparam name="TInput">Shared command projection for every direct transition on the selected entity.</typeparam>
    /// <param name="entity">Entity authority selected by the host.</param>
    /// <param name="http">HTTP inspection binding with a typed request body.</param>
    /// <returns>The declaration consumed by endpoint adapters and client generators.</returns>
    /// <exception cref="ArgumentNullException">The HTTP binding is null.</exception>
    /// <exception cref="ArgumentException">No matching operations exist, input contracts differ, or the body type is incorrect.</exception>
    /// <remarks>This projection supports entities whose direct transitions share one input contract. Per-operation authorization is performed by discovery.</remarks>
    public ApiEndpoint ProjectTransitionInspection<TInput>(QualifiedShapeId entity, HttpBinding http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var matching = operations.Values.Where(item => item.Operation is ServiceTransitionOperation op && op.Entity == entity).ToArray();
        var input = new ValueContract(new DefaultClrTypeRefMapper().Map(typeof(TInput), nullability: null));
        if (matching.Length == 0 || matching.Any(item => ((ServiceTransitionBinding)item.Binding).Plan.Definition.Input != input))
            throw new ArgumentException("Inspection requires an entity with a shared exact command contract.", nameof(entity));
        if (http.Body?.BodyType != typeof(ServiceTransitionInspection<TInput>))
            throw new ArgumentException("The inspection body must use its typed command envelope.", nameof(http));
        var entityName = ((ServiceTransitionBinding)matching[0].Binding).Entity.Name;
        var operation = new ApiOperation("InspectTransitions", ApiOperationKind.Query,
            typeof(ServiceTransitionInspection<TInput>), typeof(ServiceTransitionAction[]),
            id: new(ServiceApiProjection.EntityInspectionIdentity(definitionReference, entity)), entity: entityName,
            results: [new(ApiResultKind.Success, typeof(ServiceTransitionAction[]), isPrimary: true),
                new(ApiResultKind.ValidationFailed, typeof(ApiValidationProblem))]).WithHttp(http);
        return new ApiDefinition([operation]).Endpoints[0];
    }

    /// <summary>Projects runtime availability into the public API contract.</summary>
    /// <param name="availability">Authorized discovery entry.</param>
    /// <returns>Payload-free action with the corresponding invocation API identity.</returns>
    public ServiceTransitionAction ProjectTransitionAction(ServiceTransitionAvailability availability) =>
        new(availability.OperationId, ServiceOperationIdentity(availability.OperationId), availability.Kind,
            availability.ConcurrencyToken.Value,
            availability.Diagnostics.Select(d => new ApiValidationIssue(d.Location, d.Code, d.Message)).ToArray());
}
