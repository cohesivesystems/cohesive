using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;

using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi;

/// <summary>Named placement authoring lowered into the existing canonical placement and native factory group.</summary>
/// <typeparam name="TContext">Invocation-owned native configuration.</typeparam>
public sealed class PulumiDeploymentPlacement<TContext>
{
    readonly PulumiDeploymentFactory<TContext> group;
    readonly InfrastructureNodeId node;
    readonly InfrastructureNodeKind kind;
    readonly string file, member;
    readonly int line;
    InfrastructureTargetImplementation? implementation;
    InfrastructurePhysicalResourceId? physical;
    InfrastructureLifecycleAuthorityId? authority;
    ImmutableArray<SourceReference> sources = [];
    bool attached;

    internal PulumiDeploymentPlacement(PulumiDeploymentFactory<TContext> group, InfrastructureNodeId node,
        InfrastructureNodeKind kind, string file, int line, string member)
    { this.group = group; this.node = node; this.kind = kind; this.file = file; this.line = line; this.member = member; }

    void EnsureMutable()
    {
        group.EnsureAuthoringMutable();
        if (attached) throw new InvalidOperationException("Placement already has a native factory.");
    }

    /// <summary>Selects explicit implementation evidence; requirements never imply evidence.</summary>
    /// <param name="value">Implementation of the selected node kind.</param>
    /// <returns>This placement.</returns>
    /// <exception cref="ArgumentException">Implementation kind differs from the placement.</exception>
    /// <exception cref="ArgumentNullException">Implementation is null.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentPlacement<TContext> Using(InfrastructureTargetImplementation value)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(value);
        if (value.Facility.NodeKind != kind) throw new ArgumentException("Implementation kind does not match placement.", nameof(value));
        implementation = value; return this;
    }

    /// <summary>Sets the native physical identity.</summary>
    /// <param name="value">Native identity.</param>
    /// <returns>This placement.</returns>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentPlacement<TContext> At(InfrastructurePhysicalResourceId value)
    { EnsureMutable(); physical = value; return this; }

    /// <summary>Sets explicit resource lifecycle ownership; does not transfer native ownership.</summary>
    /// <param name="value">Lifecycle authority.</param>
    /// <returns>This placement.</returns>
    /// <exception cref="InvalidOperationException">This is a workload or authoring has completed.</exception>
    public PulumiDeploymentPlacement<TContext> OwnedBy(InfrastructureLifecycleAuthorityId value)
    {
        EnsureMutable();
        if (kind != InfrastructureNodeKind.Resource) throw new InvalidOperationException("Only resources declare lifecycle ownership.");
        authority = value; return this;
    }

    /// <summary>Sets attributable placement sources.</summary>
    /// <param name="values">Explicit source references.</param>
    /// <returns>This placement.</returns>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentPlacement<TContext> SourcedFrom(params SourceReference[] values)
    { EnsureMutable(); ArgumentNullException.ThrowIfNull(values); sources = [.. values]; return this; }

    void Attach()
    {
        EnsureMutable();
        if (implementation is null || physical is null || (kind == InfrastructureNodeKind.Resource && authority is null))
            throw new InvalidOperationException("Placement requires implementation, physical identity and resource lifecycle ownership.");
        if (kind == InfrastructureNodeKind.Resource)
            group.Resource(node, implementation, physical.Value, authority!.Value, sources, file, line, member);
        else group.Workload(node, implementation, physical.Value, sources, file, line, member);
        attached = true;
    }

    /// <summary>Attaches native construction without wrapping provider options.</summary>
    /// <typeparam name="T">Native result type.</typeparam>
    /// <param name="factory">Deferred native construction callback.</param>
    /// <returns>The completed group for dependency refinement.</returns>
    /// <exception cref="InvalidOperationException">Required placement fields are absent or authoring has completed.</exception>
    /// <exception cref="ArgumentNullException">Factory is null.</exception>
    public PulumiDeploymentFactory<TContext> Create<T>(Func<TContext, PulumiProjectionContext, T> factory) where T : notnull
    { ArgumentNullException.ThrowIfNull(factory); Attach(); return group.Create(factory); }

    /// <summary>Associates an existing native object without importing or recreating it.</summary>
    /// <param name="resource">Existing resource selector.</param>
    /// <returns>The completed group for dependency refinement.</returns>
    /// <exception cref="InvalidOperationException">Required fields are absent or authoring has completed.</exception>
    /// <exception cref="ArgumentNullException">Selector is null.</exception>
    public PulumiDeploymentFactory<TContext> UseExisting(Func<TContext, global::Pulumi.Resource> resource)
    { ArgumentNullException.ThrowIfNull(resource); Attach(); return group.UseExisting(resource); }
}
