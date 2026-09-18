using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureAD;
using Pulumi.AzureAD.Inputs;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Native Entra resources with checked identity and explicit authorization projections.</summary>
public sealed class AzureEntraResources
{
    internal AzureEntraResources(InfrastructureTargetDeploymentPlan deployment, AzureEntraPolicy policy, Application application,
        ServicePrincipal? principal, (string Application, string? ServicePrincipal) names, ImmutableArray<InfrastructureBindingDefinition> bindings)
    {
        Deployment = deployment; Policy = policy; Application = application; ServicePrincipal = principal; AccessBindings = bindings;
        var authority = policy.LifecycleAuthority.Value.Split('/');
        var appIdentity = Output.Tuple(application.Urn, application.Id, application.ObjectId, application.ClientId).Apply(value =>
        {
            if (!ValidUrn(value.Item1, "azuread:index/application:Application", names.Application) ||
                !ValidGuid(value.Item3) || !ValidGuid(value.Item4) ||
                !string.Equals(value.Item2, "/applications/" + value.Item3, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The native application identity does not match its canonical association.");
            return (Id: value.Item2, ClientId: value.Item4);
        });
        Output<string?> principalIdentity = Output.Create((string?)null);
        if (principal is not null)
            principalIdentity = Output.Tuple(principal.Urn, principal.ObjectId, principal.ClientId, principal.ApplicationTenantId, appIdentity, principal.Id).Apply(value =>
            {
                if (!ValidUrn(value.Item1, "azuread:index/servicePrincipal:ServicePrincipal", names.ServicePrincipal!) || !ValidGuid(value.Item2) ||
                    !string.Equals(value.Item6, "/servicePrincipals/" + value.Item2, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParse(value.Item4, out var tenant) || tenant != policy.TenantId ||
                    !string.Equals(value.Item3, value.Item5.ClientId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The native service principal identity, application or tenant does not match its canonical association.");
                return (string?)value.Item2;
            });
        var identity = Output.Tuple(appIdentity, principalIdentity);
        ApplicationId = identity.Apply(value => value.Item1.Id);
        ClientId = identity.Apply(value => value.Item1.ClientId);
        principalObjectId = identity.Apply(value => value.Item2);
        ConsentDiagnostics = [.. policy.Permissions.Where(p => p.Action == AzureEntraPermissionAction.RequestDelegatedScope)
            .OrderBy(p => p.Binding.Value, StringComparer.Ordinal)
            .Select(p => AzureEntraBinding.Diagnostic(deployment, policy, "consent-unverified",
                $"Binding '{p.Binding.Value}' requests a delegated scope; user/admin consent is not established by this association.",
                global::Cohesive.Model.DiagnosticSeverity.Warning))];

        bool ValidUrn(string urn, string type, string name)
        {
            var parts = urn.Split("::", StringSplitOptions.None);
            return parts.Length == 4 && parts[0] == "urn:pulumi:" + authority[2] && parts[1] == authority[1] &&
                parts[2].Split('$')[^1] == type && parts[3] == name;
        }
    }
    readonly Output<string?> principalObjectId;
    /// <summary>Exact canonical plan retained with its provenance.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Explicit ownership and permission decisions.</summary>
    public AzureEntraPolicy Policy { get; }
    /// <summary>Original native application; raw SDK outputs bypass association checks.</summary>
    public Application Application { get; }
    /// <summary>Original optional native principal, preserving caller options and parents.</summary>
    public ServicePrincipal? ServicePrincipal { get; }
    /// <summary>Actual provider application ID after identity checks; unknown previews stay unknown.</summary>
    public Output<string> ApplicationId { get; }
    /// <summary>Actual client ID after identity checks; resolved mismatches fault with InvalidOperationException.</summary>
    public Output<string> ClientId { get; }
    /// <summary>Participating incoming canonical access bindings in ordinal ID order.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> AccessBindings { get; }
    /// <summary>Explicit warnings that requested delegated permissions do not establish consent.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> ConsentDiagnostics { get; }

    /// <summary>Projects a requested delegated scope into native required-resource-access inputs without granting consent.</summary>
    /// <param name="binding">Participating binding explicitly selected for RequestDelegatedScope.</param>
    /// <returns>Native input object; caller configures the canonical source application's native resource.</returns>
    /// <exception cref="ArgumentException">Binding has no matching explicit scope-request decision.</exception>
    /// <remarks>Missing/disabled resolved scopes fault with InvalidOperationException; unknown outputs remain unknown.</remarks>
    public ApplicationRequiredResourceAccessArgs RequiredScopeAccess(InfrastructureBindingId binding)
    {
        var permission = Permission(binding, AzureEntraPermissionAction.RequestDelegatedScope);
        return new()
        {
            ResourceAppId = ClientId,
            ResourceAccesses = { new ApplicationRequiredResourceAccessResourceAccessArgs
            {
                Type = "Scope",
                Id = Output.Tuple(ClientId, Application.Api).Apply(value => value.Item2 is not null && !value.Item2.Oauth2PermissionScopes.IsDefaultOrEmpty &&
                    value.Item2.Oauth2PermissionScopes.Any(s => s.Enabled == true && Guid.TryParse(s.Id, out var id) && id == permission.PermissionId)
                    ? permission.PermissionId.ToString("D") : throw new InvalidOperationException("The requested delegated scope is absent or disabled on the attached application."))
            } }
        };
    }

    /// <summary>Projects an explicitly authorized application-role assignment using the source principal's resolved tenant evidence.</summary>
    /// <param name="binding">Canonical binding explicitly selected for AssignApplicationRole.</param>
    /// <param name="principalSource">Canonical source associated by the host with the native principal outputs.</param>
    /// <param name="principalObjectId">Actual source principal ID; never inferred from a display name.</param>
    /// <param name="principalTenantId">Actual tenant output/evidence from the same native principal, not an assumed default.</param>
    /// <returns>Native role-assignment inputs; caller owns registration, logical name, provider and parent.</returns>
    /// <exception cref="ArgumentNullException">Principal input is null.</exception>
    /// <exception cref="ArgumentException">Binding/action/source is not explicitly authorized.</exception>
    /// <remarks>Foreign/invalid principals and missing/disabled/non-application roles fault outputs with InvalidOperationException. No directory lookup is performed.</remarks>
    public AppRoleAssignmentArgs AppRoleGrant(InfrastructureBindingId binding, InfrastructureNodeId principalSource,
        Input<string> principalObjectId, Input<string> principalTenantId)
    {
        ArgumentNullException.ThrowIfNull(principalObjectId); ArgumentNullException.ThrowIfNull(principalTenantId);
        var permission = Permission(binding, AzureEntraPermissionAction.AssignApplicationRole);
        if (AccessBindings.Single(b => b.Id == binding).Source != principalSource)
            throw new ArgumentException("Associate the principal with the canonical binding source.", nameof(principalSource));
        var checkedPrincipal = Output.Tuple(principalObjectId, principalTenantId).Apply(value =>
            ValidGuid(value.Item1) && Guid.TryParse(value.Item2, out var tenant) && tenant == Policy.TenantId ? value.Item1 :
                throw new InvalidOperationException("The source principal ID or tenant does not match the authorized Entra scope."));
        return new()
        {
            PrincipalObjectId = checkedPrincipal,
            ResourceObjectId = this.principalObjectId.Apply(value => value ?? throw new InvalidOperationException("An owned resource principal is required for role assignment.")),
            AppRoleId = Output.Tuple(ClientId, Application.AppRoles).Apply(value => !value.Item2.IsDefaultOrEmpty && value.Item2.Any(role => role.Enabled == true &&
                role.AllowedMemberTypes.Contains("Application") && Guid.TryParse(role.Id, out var id) && id == permission.PermissionId)
                ? permission.PermissionId.ToString("D") : throw new InvalidOperationException("The application role is absent, disabled or not assignable to applications."))
        };
    }

    /// <summary>Returns a caller-created application's password as a secret output after checking its application association.</summary>
    /// <param name="password">Existing native credential; the host owns rotation, lifetime and canonical consumer configuration.</param>
    /// <returns>Always-secret value preserving native dependencies; creates no credential.</returns>
    /// <exception cref="ArgumentNullException">Password is null.</exception>
    /// <remarks>A mismatched application or empty resolved secret faults with InvalidOperationException without exposing values.</remarks>
    public Output<string> ClientSecret(ApplicationPassword password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Output.CreateSecret(Output.Tuple(ApplicationId, password.ApplicationId, password.Value).Apply(value =>
            string.Equals(value.Item1, value.Item2, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value.Item3) ? value.Item3 :
                throw new InvalidOperationException("The native credential does not belong to the attached application or has no resolved value.")));
    }

    AzureEntraPermission Permission(InfrastructureBindingId binding, AzureEntraPermissionAction action) =>
        Policy.Permissions.SingleOrDefault(p => p.Binding == binding && p.Action == action) ??
            throw new ArgumentException("Select a participating binding with the explicit required permission action.", nameof(binding));
    static bool ValidGuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
}
