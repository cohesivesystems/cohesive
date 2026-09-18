using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Semantic association and explicit consumer access policy for a native Pulumi vault.</summary>
/// <remarks>Physical identity and consumers remain canonical Infra data. This policy never contains secret values.</remarks>
public sealed record AzureKeyVaultPolicy
{
    /// <summary>Canonical vault resource to construct.</summary>
    public required InfrastructureNodeId Resource { get; init; }
    /// <summary>Canonical contract identifying secret-reading consumers.</summary>
    public required InfrastructureBindingContractId SecretReadContract { get; init; }
    /// <summary>Expected exclusive Pulumi state authority.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Explicit Azure subscription, checked against the host.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Explicit vault tenant, checked against the host's declared tenant during association.</summary>
    public required Guid TenantId { get; init; }
    /// <summary>Exactly one attributed decision for each participating canonical secret consumer.</summary>
    public required ImmutableArray<AzureKeyVaultBindingAccess> Access { get; init; }
    /// <summary>Non-empty references attributing declared scope and binding policy.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}

/// <summary>Explicit action for a canonical consumer; no default action is inferred from a binding.</summary>
public enum AzureKeyVaultAccessAction
{
    /// <summary>Missing policy, always rejected.</summary>
    Unspecified = 0,
    /// <summary>Allow generation of a vault-scoped Key Vault Secrets User role assignment.</summary>
    AssignSecretsUser = 1,
    /// <summary>Deliberately construct no grant. This is not evidence of effective access or readiness.</summary>
    NoManagedGrant = 2
}

/// <summary>Attributable decision about one canonical consumer, including deliberate absence of a managed grant.</summary>
/// <param name="Binding">Participating canonical secret-reader binding.</param>
/// <param name="Action">Explicit grant action; unspecified or unknown values fail validation.</param>
/// <param name="Reason">Non-secret rationale for the decision, retained for inspection but not copied into diagnostics.</param>
/// <param name="SourceReferences">Non-empty references to authorization evidence or an unresolved-access follow-up.</param>
public sealed record AzureKeyVaultBindingAccess(InfrastructureBindingId Binding, AzureKeyVaultAccessAction Action,
    string Reason, ImmutableArray<SourceReference> SourceReferences);
