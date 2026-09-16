using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>One explicit construction owner for all canonical blob containers sharing an Azure Storage account.</summary>
/// <remarks>Physical names come exclusively from the deployment manifest. This bounded slice creates a StorageV2,
/// Standard_LRS account, HTTPS-only with minimum TLS 1.2 and anonymous blob access disabled, and private containers.
/// It does not configure account keys, networking, hierarchical namespaces, queue/table services or lifecycle policies.</remarks>
public sealed record AzureBlobStoragePolicy
{
    /// <summary>Canonical container whose lifecycle authority owns construction of the shared account.</summary>
    public required InfrastructureNodeId AccountOwner { get; init; }
    /// <summary>Expected exclusive lifecycle authority shared by every container in the account.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Explicit canonical blob read/write binding contract; queue/table contracts are not accepted.</summary>
    public required InfrastructureBindingContractId BlobContract { get; init; }
    /// <summary>Declared subscription of the host's existing Azure Native provider.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Existing deterministic resource-group name; creation dependency is passed to Register separately.</summary>
    public required string ResourceGroupName { get; init; }
    /// <summary>Explicit account region.</summary>
    public required string Location { get; init; }
    /// <summary>Preserved Pulumi account logical name, not its manifest-owned physical name.</summary>
    public required string AccountName { get; init; }
    /// <summary>All and only the canonical containers sharing the selected physical account; no duplicate aliases.</summary>
    public required ImmutableArray<AzureBlobContainerPolicy> Containers { get; init; }
    /// <summary>Exactly one explicit scope decision per participating canonical binding, with no inferred broad grants.</summary>
    public required ImmutableArray<AzureBlobBindingAccess> Access { get; init; }
    /// <summary>Non-secret account tags.</summary>
    public ImmutableSortedDictionary<string, string> Tags { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
    /// <summary>Evidence for shared ownership, admission policy and any broad migration grants.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}

/// <summary>Preserved logical identity for a manifest-owned physical blob container.</summary>
/// <param name="Resource">Exact canonical resource, not a second physical name catalog.</param>
/// <param name="LogicalName">Existing or explicitly chosen Pulumi logical name.</param>
public sealed record AzureBlobContainerPolicy(InfrastructureNodeId Resource, string LogicalName);

/// <summary>Supported blob RBAC scopes. The default value deliberately grants nothing.</summary>
public enum AzureBlobScope
{
    /// <summary>Missing scope, always rejected.</summary>
    Unspecified,
    /// <summary>Explicit account-wide blob access; queue/table access is not included.</summary>
    Account,
    /// <summary>Only the binding's exact canonical target container.</summary>
    Container
}

/// <summary>Attributable target-specific scope for one canonical blob binding.</summary>
/// <param name="Binding">Participating binding identity.</param>
/// <param name="Scope">Explicit account or exact bound-container scope.</param>
public sealed record AzureBlobBindingAccess(InfrastructureBindingId Binding, AzureBlobScope Scope);
