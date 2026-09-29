using Cohesive.Api.Services;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Single-scope service policy over normalized, explicit identity grants and a logical ownership field.</summary>
/// <remarks>
/// Every declared requirement is a capability that must be explicitly granted to the actor in the selected scope.
/// Scope selection is not a grant. Expired grants, ambiguous placement, anonymous actors and delegation are denied.
/// Delegation requires a separately qualified authority policy. Grant resolution is owned by the host's identity
/// boundary; this policy neither parses credentials nor upgrades ambient scopes to permissions.
/// </remarks>
public sealed class IdentityServiceInvocationAuthorization : IServiceInvocationAuthorization
{
    /// <summary>Declares the scope kind and entity field carrying logical scope ownership.</summary>
    /// <exception cref="ArgumentException">A required identity is empty.</exception>
    public IdentityServiceInvocationAuthorization(string scopeKind, FieldName ownershipField)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipField.Value);
        ScopeKind = scopeKind;
        OwnershipField = ownershipField;
    }

    /// <summary>Normalized scope family this policy admits.</summary>
    public string ScopeKind { get; }
    /// <summary>Logical ownership field; independent of repository partition layout.</summary>
    public FieldName OwnershipField { get; }

    /// <inheritdoc />
    public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation)
    {
        context.ThrowIfCancellationRequested();
        var scope = Resolve(context, operation);
        return ValueTask.FromResult(scope);
    }

    /// <inheritdoc />
    public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceOperation operation, EntitySnapshot snapshot)
    {
        context.ThrowIfCancellationRequested();
        var scope = Resolve(context, operation);
        return ValueTask.FromResult(scope is not null
            && snapshot.Entity.Observation.Fields.TryGetValue(OwnershipField.Value, out var owner)
            && owner.Kind == ObservationValueKind.String && owner.GetString() == scope.Id);
    }

    ScopeRef? Resolve(OperationContext context, ServiceOperation operation)
    {
        var identity = context.GetIdentityContextOrDefault();
        if (identity is null || identity.Actor.Kind == PrincipalKind.Anonymous || string.IsNullOrWhiteSpace(identity.Actor.Id)
            || identity.Subject is not null || identity.Grants.IsDefaultOrEmpty
            || !identity.TryGetSingleEffectiveScope(ScopeKind, out var selected))
            return null;
        var grants = identity.Grants.Where(grant => grant.Grantee.Id == identity.Actor.Id
            && grant.Grantee.Kind == identity.Actor.Kind && grant.Scope.Kind == ScopeKind && grant.Scope.Id == selected!.Id
            && (grant.ExpiresAtUtc is null || grant.ExpiresAtUtc > context.UtcNow)).ToArray();
        if (grants.Length == 0 || grants.Select(grant => grant.Scope.ResolvePartitionKey()).Distinct(StringComparer.Ordinal).Count() != 1
            || operation.AuthorizationRequirements.Any(requirement => !grants.Any(grant =>
                !grant.Capabilities.IsDefault && grant.Capabilities.Contains(requirement.Id, StringComparer.Ordinal))))
            return null;
        return grants[0].Scope;
    }
}
