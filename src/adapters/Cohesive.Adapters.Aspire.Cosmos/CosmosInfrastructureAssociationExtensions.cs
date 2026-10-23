using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Cohesive.Adapters.Cosmos;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Relations.Infra;

namespace Cohesive.Adapters.Aspire.Cosmos;

/// <summary>Validates native Cosmos placement against the storage declaration before associating prepared evidence.</summary>
public static class CosmosInfrastructureAssociationExtensions
{
    /// <summary>Attaches a container using the canonical node's prepared source evidence.</summary>
    /// <param name="associations">Current association session.</param>
    /// <param name="node">Canonical prepared storage resource.</param>
    /// <param name="native">Existing native container and its database/account parents.</param>
    /// <param name="storage">Storage declaration used for both query acquisition and repository binding.</param>
    /// <param name="authority">Explicit owner, or native Aspire ownership.</param>
    /// <param name="sources">Attribution, or deterministic association defaults.</param>
    /// <returns>The same association builder; no resources are started or queried.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Native placement or prepared source identity differs.</exception>
    /// <exception cref="InvalidOperationException">Preparation is absent or association has ended.</exception>
    public static AspireInfrastructureAssociationBuilder Resource(this AspireInfrastructureAssociationBuilder associations,
        InfrastructureNodeId node, IResourceBuilder<AzureCosmosDBContainerResource> native,
        CosmosEntityStorageBinding storage, InfrastructureLifecycleAuthorityId? authority = null,
        ImmutableArray<SourceReference> sources = default)
    {
        ArgumentNullException.ThrowIfNull(associations);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(storage);
        var source = storage.Query;
        var container = native.Resource;
        if (container.Parent.Parent.Name != source.Account || container.Parent.DatabaseName != source.Database
            || container.ContainerName != source.Container || container.PartitionKeyPaths.Count() != 1
            || container.PartitionKeyPath != storage.PartitionKeyPath)
            throw new ArgumentException("Native Cosmos account, database, container or partition path differs from the storage declaration.", nameof(native));
        var evidence = associations.RequireResourcePreparation<ComposedQueryResourceEvidence>(node);
        return associations.Resource(node, native, evidence.ForSource(source.Binding.Source.Id.Value),
            authority ?? new InfrastructureLifecycleAuthorityId("aspire/native"), sources);
    }
}
