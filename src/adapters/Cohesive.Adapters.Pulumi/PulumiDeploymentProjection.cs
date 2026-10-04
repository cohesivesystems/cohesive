using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi;

/// <summary>
/// A native projection authored alongside physical placements. The portable manifest is inspectable without
/// executing factories; invocation-specific native configuration is supplied only to <see cref="Execute"/>.
/// </summary>
/// <typeparam name="TContext">Application-owned native configuration, including existing resources if needed.</typeparam>
public sealed class PulumiDeploymentProjection<TContext>
{
    readonly ImmutableArray<PulumiDeploymentFactory<TContext>> factories;
    internal PulumiDeploymentProjection(InfrastructureTargetDeploymentManifest manifest,
        ImmutableArray<PulumiDeploymentFactory<TContext>> factories)
    { Manifest = manifest; this.factories = factories; }

    /// <summary>Ordinary canonical deployment manifest, containing no callbacks or native objects.</summary>
    public InfrastructureTargetDeploymentManifest Manifest { get; }

    /// <summary>Authors implementation evidence, placements and native factories in one declaration.</summary>
    /// <param name="id">Stable deployment identity.</param>
    /// <param name="definition">Exact canonical application definition.</param>
    /// <param name="facilityManifestId">Identity of the derived facility manifest.</param>
    /// <param name="profileId">Identity of the derived capability profile.</param>
    /// <param name="target">Native interpreter identity.</param>
    /// <param name="variant">Coherent target variant.</param>
    /// <param name="supportedDefinitionSchemaVersions">Schemas understood by the target.</param>
    /// <param name="configure">Synchronous capability rules, placements and native factories.</param>
    /// <returns>The portable manifest paired with deferred native interpretation.</returns>
    /// <exception cref="ArgumentNullException">Definition or configure is null.</exception>
    /// <exception cref="ArgumentException">Canonical authoring rejects identities, evidence or placements.</exception>
    /// <exception cref="InvalidOperationException">A placement group has no native factory.</exception>
    public static PulumiDeploymentProjection<TContext> Define(
        InfrastructureTargetDeploymentManifestId id, InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifestId facilityManifestId, InfrastructureCapabilityProfileId profileId,
        InfrastructureTargetId target, InfrastructureCapabilityVariantId variant,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        Action<PulumiDeploymentProjectionBuilder<TContext>> configure) =>
        Define(author => InfrastructureTargetDeployments.Define(id, definition, facilityManifestId, profileId,
            target, variant, supportedDefinitionSchemaVersions, author), configure);

    /// <summary>Coauthors placements and native factories using an existing canonical manifest producer.</summary>
    /// <param name="define">Calls InfrastructureTargetDeployments.Define once with the supplied authoring callback.</param>
    /// <param name="configure">Synchronous placements and their native factories.</param>
    /// <returns>An immutable native projection whose manifest can be compiled independently.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The producer omits/repeats configuration or a placement has no factory.</exception>
    /// <exception cref="ArgumentException">Canonical authoring rejects a declaration.</exception>
    public static PulumiDeploymentProjection<TContext> Define(
        Func<Action<InfrastructureTargetDeploymentManifestBuilder>, InfrastructureTargetDeploymentManifest> define,
        Action<PulumiDeploymentProjectionBuilder<TContext>> configure)
    {
        ArgumentNullException.ThrowIfNull(define);
        ArgumentNullException.ThrowIfNull(configure);
        PulumiDeploymentProjectionBuilder<TContext>? builder = null;
        ImmutableArray<PulumiDeploymentFactory<TContext>> factories = [];
        var manifest = define(deployment =>
        {
            if (builder is not null) throw new InvalidOperationException("The manifest producer must configure exactly once.");
            builder = new(deployment);
            configure(builder);
            factories = builder.Complete();
        });
        if (builder is null) throw new InvalidOperationException("The manifest producer did not configure its projection.");
        return new(manifest, factories);
    }

    /// <summary>Validates the exact compiled manifest and delegates construction to the existing graph interpreter.</summary>
    /// <param name="deployment">Complete plan compiled from this projection's exact manifest.</param>
    /// <param name="native">Invocation-owned native configuration; not persisted or cached.</param>
    /// <returns>Native associations and values from this invocation.</returns>
    /// <exception cref="ArgumentNullException">Deployment is null.</exception>
    /// <exception cref="ArgumentException">Deployment belongs to another manifest.</exception>
    /// <exception cref="InvalidOperationException">Graph coverage, ordering, or native association fails. Factory failures propagate.</exception>
    public PulumiProjectionResult Execute(InfrastructureTargetDeploymentPlan deployment, TContext native)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (deployment.Manifest.Fingerprint != Manifest.Fingerprint)
            throw new ArgumentException("The compiled deployment does not match this native projection.", nameof(deployment));
        var graph = new PulumiGraphProjection(deployment);
        foreach (var factory in factories)
        {
            var registration = graph.Map(factory.Nodes, context => factory.Factory!(native, context));
            foreach (var (node, reason) in factory.Dependencies) registration.After(node, reason);
        }
        return graph.Execute();
    }
}

/// <summary>Invocation-scoped authoring builder; each placement group has exactly one native factory.</summary>
/// <typeparam name="TContext">Native invocation configuration.</typeparam>
public sealed class PulumiDeploymentProjectionBuilder<TContext>
{
    readonly InfrastructureTargetDeploymentManifestBuilder deployment;
    readonly List<PulumiDeploymentFactory<TContext>> factories = [];
    bool complete;
    internal PulumiDeploymentProjectionBuilder(InfrastructureTargetDeploymentManifestBuilder deployment) => this.deployment = deployment;

    /// <summary>Begins a native construction group; one factory may realize several related declarations.</summary>
    /// <returns>A group for physical placements and one factory.</returns>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentFactory<TContext> Group()
    {
        EnsureMutable();
        var group = new PulumiDeploymentFactory<TContext>(this, deployment);
        factories.Add(group);
        return group;
    }

    /// <summary>Begins a single resource placement; native construction remains deferred.</summary>
    /// <param name="node">Canonical declaration identity.</param>
    /// <param name="sourceFile">Authoring source file.</param>
    /// <param name="sourceLine">Authoring source line.</param>
    /// <param name="sourceMember">Authoring member.</param>
    /// <returns>A placement builder with named configuration operations.</returns>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentPlacement<TContext> Resource(InfrastructureNodeId node,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "") =>
        new(Group(), node, InfrastructureNodeKind.Resource, sourceFile, sourceLine, sourceMember);

    /// <summary>Begins a single workload placement; native construction remains deferred.</summary>
    /// <param name="node">Canonical declaration identity.</param>
    /// <param name="sourceFile">Authoring source file.</param>
    /// <param name="sourceLine">Authoring source line.</param>
    /// <param name="sourceMember">Authoring member.</param>
    /// <returns>A placement builder with named configuration operations.</returns>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentPlacement<TContext> Workload(InfrastructureNodeId node,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "") =>
        new(Group(), node, InfrastructureNodeKind.Workload, sourceFile, sourceLine, sourceMember);

    /// <summary>Adds an attributable cross-implementation capability composition rule.</summary>
    /// <param name="rule">Canonical composition rule.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">Rule is null.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed or uses a separate facility manifest.</exception>
    public PulumiDeploymentProjectionBuilder<TContext> Composes(InfrastructureCapabilityRule rule)
    {
        EnsureMutable();
        deployment.Composes(rule);
        return this;
    }

    internal void EnsureMutable()
    {
        if (complete) throw new InvalidOperationException("Projection authoring has completed.");
    }
    internal ImmutableArray<PulumiDeploymentFactory<TContext>> Complete()
    {
        EnsureMutable();
        complete = true;
        if (factories.Any(f => f.Factory is null || f.Nodes.Count == 0))
            throw new InvalidOperationException("Every native group requires placements and exactly one factory.");
        return [.. factories];
    }
}

/// <summary>Co-locates canonical placement and native realization; additional provider resources remain entirely native.</summary>
/// <typeparam name="TContext">Native invocation configuration.</typeparam>
public sealed class PulumiDeploymentFactory<TContext>
{
    readonly PulumiDeploymentProjectionBuilder<TContext> owner;
    readonly InfrastructureTargetDeploymentManifestBuilder deployment;
    internal readonly List<InfrastructureNodeId> Nodes = [];
    internal readonly Dictionary<InfrastructureNodeId, string> Dependencies = [];
    internal Func<TContext, PulumiProjectionContext, object>? Factory;
    internal PulumiDeploymentFactory(PulumiDeploymentProjectionBuilder<TContext> owner,
        InfrastructureTargetDeploymentManifestBuilder deployment)
    { this.owner = owner; this.deployment = deployment; }

    internal void EnsureAuthoringMutable() => owner.EnsureMutable();

    void EnsurePlacementsMutable()
    {
        owner.EnsureMutable();
        if (Factory is not null) throw new InvalidOperationException("Add placements before attaching the native factory.");
    }

    /// <summary>Selects a workload implementation and native identity in this factory group.</summary>
    /// <param name="node">Canonical workload.</param>
    /// <param name="implementation">Implementation and capability evidence.</param>
    /// <param name="physical">Native identity.</param>
    /// <param name="sources">Attributable placement sources.</param>
    /// <param name="sourceFile">Compiler-supplied authoring source path; non-semantic attribution only.</param>
    /// <param name="sourceLine">Compiler-supplied authoring line.</param>
    /// <param name="sourceMember">Compiler-supplied authoring member.</param>
    /// <returns>This group.</returns>
    /// <exception cref="ArgumentException">Placement is invalid.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentFactory<TContext> Workload(InfrastructureNodeId node, InfrastructureTargetImplementation implementation,
        InfrastructurePhysicalResourceId physical, ImmutableArray<SourceReference> sources,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "")
    {
        EnsurePlacementsMutable();
        deployment.WorkloadUsing(node, implementation, physical, sources, sourceFile, sourceLine, sourceMember);
        Nodes.Add(node);
        return this;
    }

    /// <summary>Selects a resource implementation, native identity and lifecycle owner in this factory group.</summary>
    /// <param name="node">Canonical resource.</param>
    /// <param name="implementation">Implementation and capability evidence.</param>
    /// <param name="physical">Native identity.</param>
    /// <param name="authority">Lifecycle owner; association does not transfer native ownership.</param>
    /// <param name="sources">Attributable placement sources.</param>
    /// <param name="sourceFile">Compiler-supplied authoring source path; non-semantic attribution only.</param>
    /// <param name="sourceLine">Compiler-supplied authoring line.</param>
    /// <param name="sourceMember">Compiler-supplied authoring member.</param>
    /// <returns>This group.</returns>
    /// <exception cref="ArgumentException">Placement is invalid.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentFactory<TContext> Resource(InfrastructureNodeId node, InfrastructureTargetImplementation implementation,
        InfrastructurePhysicalResourceId physical, InfrastructureLifecycleAuthorityId authority, ImmutableArray<SourceReference> sources,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "")
    {
        EnsurePlacementsMutable();
        deployment.ResourceUsing(node, implementation, physical, authority, sources, sourceFile, sourceLine, sourceMember);
        Nodes.Add(node);
        return this;
    }

    /// <summary>Attaches deferred native construction or association of existing resources.</summary>
    /// <typeparam name="T">Exact native result type, retained without conversion.</typeparam>
    /// <param name="factory">Runs once per execution; records native associations through its context.</param>
    /// <returns>This group for explicit native dependency refinements.</returns>
    /// <exception cref="ArgumentNullException">Factory is null.</exception>
    /// <exception cref="InvalidOperationException">A factory already exists or authoring has completed.</exception>
    public PulumiDeploymentFactory<TContext> Create<T>(Func<TContext, PulumiProjectionContext, T> factory) where T : notnull
    {
        owner.EnsureMutable();
        ArgumentNullException.ThrowIfNull(factory);
        if (Factory is not null) throw new InvalidOperationException("A native group already has a factory.");
        Factory = (native, context) => factory(native, context);
        return this;
    }

    /// <summary>Associates an already constructed native resource without creating or importing it.</summary>
    /// <param name="resource">Selects the existing resource from invocation configuration.</param>
    /// <returns>This group.</returns>
    /// <exception cref="ArgumentNullException">Resource selector is null.</exception>
    /// <exception cref="InvalidOperationException">The group does not own exactly one node or already has a factory.</exception>
    public PulumiDeploymentFactory<TContext> UseExisting(Func<TContext, global::Pulumi.Resource> resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (Nodes.Count != 1) throw new InvalidOperationException("Existing-resource association requires exactly one placement.");
        var node = Nodes[0];
        return Create((native, context) =>
        {
            var value = resource(native);
            context.Associate(node, value);
            return value;
        });
    }

    /// <summary>Declares native ordering beyond canonical relationships; does not fabricate Pulumi DependsOn.</summary>
    /// <param name="node">Canonical prerequisite.</param>
    /// <param name="reason">Inspectable reason for native ordering.</param>
    /// <returns>This group.</returns>
    /// <exception cref="ArgumentException">Reason is blank.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public PulumiDeploymentFactory<TContext> After(InfrastructureNodeId node, string reason)
    {
        owner.EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Dependencies[node] = reason;
        return this;
    }
}
