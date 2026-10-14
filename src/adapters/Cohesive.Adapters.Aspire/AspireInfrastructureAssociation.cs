using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Aspire;

/// <summary>Associates canonical requirements with an existing native AppHost model without creating resources.</summary>
/// <remarks>
/// Aspire owns configuration, references, startup and disposal. Project metadata supports only the shared
/// .NET project execution capability. Other evidence is explicit; neither establishes live readiness. The native map is invocation-local;
/// only the ordinary deployment manifest and compiler result are portable.
/// </remarks>
public sealed class AspireInfrastructureAssociation
{
    AspireInfrastructureAssociation(InfrastructureTargetDeploymentPlan deployment,
        ImmutableDictionary<InfrastructureNodeId, IResource> resources)
    { Deployment = deployment; Resources = resources; }

    /// <summary>Canonical compiler result, including incomplete capability or coverage diagnostics.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }

    /// <summary>Original Aspire resource objects; association does not transfer their ownership.</summary>
    public ImmutableDictionary<InfrastructureNodeId, IResource> Resources { get; }

    /// <summary>Identity of this native association interpreter.</summary>
    public static InfrastructureTargetId Target { get; } = new("aspire/native-association/v1");

    /// <summary>Attaches explicit implementation selections to resources already registered in an AppHost.</summary>
    /// <param name="application">Native model owning every selected object. It is neither built nor started.</param>
    /// <param name="definition">Canonical requirements and bindings to validate.</param>
    /// <param name="id">Stable deployment identity; facility/profile IDs derive by appending /facilities and /profile.</param>
    /// <param name="variant">Explicit target/environment variant.</param>
    /// <param name="configure">Synchronous associations. Retained builders are frozen after the callback.</param>
    /// <returns>Immutable associations and canonical diagnostics; inspect Deployment.IsComplete before starting.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Canonical declarations are invalid, duplicated, or a resource belongs to another model.</exception>
    public static AspireInfrastructureAssociation Attach(IDistributedApplicationBuilder application,
        InfrastructureAuthoringResult definition, InfrastructureTargetDeploymentManifestId id,
        InfrastructureCapabilityVariantId variant, Action<AspireInfrastructureAssociationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(configure);
        AspireInfrastructureAssociationBuilder? associations = null;
        var manifest = InfrastructureTargetDeployments.Define(id, definition.Definition,
            new($"{id.Value}/facilities"), new($"{id.Value}/profile"), Target, variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], deployment =>
            {
                associations = new(application, deployment, id);
                try { configure(associations); }
                finally { associations.Freeze(); }
            });
        return new(InfrastructureTargetDeploymentCompiler.Compile(definition, manifest), associations!.Snapshot());
    }
}

/// <summary>Invocation-scoped associations, lowered directly into existing canonical deployment authoring.</summary>
public sealed class AspireInfrastructureAssociationBuilder
{
    readonly IDistributedApplicationBuilder application;
    readonly InfrastructureTargetDeploymentManifestId manifestId;
    readonly InfrastructureTargetDeploymentManifestBuilder deployment;
    readonly Dictionary<InfrastructureNodeId, IResource> resources = [];
    readonly HashSet<IResource> existing;
    bool frozen;

    internal AspireInfrastructureAssociationBuilder(IDistributedApplicationBuilder application,
        InfrastructureTargetDeploymentManifestBuilder deployment, InfrastructureTargetDeploymentManifestId manifestId)
    {
        this.application = application;
        this.manifestId = manifestId;
        this.deployment = deployment;
        existing = new(application.Resources, ReferenceEqualityComparer.Instance);
    }

    void EnsureMutable()
    {
        if (frozen) throw new InvalidOperationException("Aspire association authoring has completed.");
    }

    void Validate(InfrastructureNodeId node, IResource resource)
    {
        EnsureMutable();
        if (!existing.Contains(resource))
            throw new ArgumentException("The selected resource must belong to this AppHost before association authoring begins.", nameof(resource));
        if (resources.ContainsKey(node))
            throw new ArgumentException($"Canonical node '{node.Value}' already has a native association.", nameof(node));
    }

    /// <summary>Associates an existing resource and explicit lifecycle owner with a canonical resource.</summary>
    /// <typeparam name="T">Original native resource type, retained without conversion.</typeparam>
    /// <param name="node">Canonical resource identity.</param>
    /// <param name="native">Existing native builder; its resource is retained by reference.</param>
    /// <param name="implementation">Explicit resource implementation evidence.</param>
    /// <param name="authority">Lifecycle owner; native ownership is unchanged.</param>
    /// <param name="sources">Attributable selection evidence; omitted sources use a stable native association reference.</param>
    /// <param name="sourceFile">Compiler-provided authoring file.</param>
    /// <param name="sourceLine">Compiler-provided authoring line.</param>
    /// <param name="sourceMember">Compiler-provided authoring member.</param>
    /// <returns>This association builder.</returns>
    /// <exception cref="ArgumentNullException">Native builder or implementation is null.</exception>
    /// <exception cref="ArgumentException">Kind, canonical placement, model membership or identity is invalid.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public AspireInfrastructureAssociationBuilder Resource<T>(InfrastructureNodeId node, IResourceBuilder<T> native,
        InfrastructureTargetImplementation implementation, InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sources = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "") where T : IResource
    {
        ArgumentNullException.ThrowIfNull(native);
        Validate(node, native.Resource);
        deployment.ResourceUsing(node, implementation, new($"aspire/resource/{native.Resource.Name}"), authority,
            Sources(sources, node, native.Resource), sourceFile, sourceLine, sourceMember);
        resources.Add(node, native.Resource);
        return this;
    }

    /// <summary>Infers .NET project execution from an existing Aspire project with valid native metadata.</summary>
    /// <param name="node">Canonical workload identity.</param>
    /// <param name="native">Existing project resource; no process is started.</param>
    /// <param name="sources">Selection attribution, or the deterministic association default.</param>
    /// <param name="sourceFile">Compiler-provided authoring file.</param>
    /// <param name="sourceLine">Compiler-provided authoring line.</param>
    /// <param name="sourceMember">Compiler-provided authoring member.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">Native builder is null.</exception>
    /// <exception cref="ArgumentException">Membership, identity or project path is invalid.</exception>
    /// <exception cref="InvalidOperationException">Metadata is absent/ambiguous or authoring has completed.</exception>
    public AspireInfrastructureAssociationBuilder Workload(InfrastructureNodeId node, IResourceBuilder<ProjectResource> native,
        ImmutableArray<SourceReference> sources = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(native);
        Validate(node, native.Resource);
        var metadata = native.Resource.GetProjectMetadata();
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.ProjectPath);
        return Workload(node, native, AspireInfrastructure.DotNetProject, sources, sourceFile, sourceLine, sourceMember);
    }

    /// <summary>Associates an existing executable, project or container with a canonical workload.</summary>
    /// <typeparam name="T">Original native resource type.</typeparam>
    /// <param name="node">Canonical workload identity.</param>
    /// <param name="native">Existing native builder; this method does not start it or add references.</param>
    /// <param name="implementation">Explicit workload implementation evidence.</param>
    /// <param name="sources">Attributable selection evidence; omitted sources use a stable native association reference.</param>
    /// <param name="sourceFile">Compiler-provided authoring file.</param>
    /// <param name="sourceLine">Compiler-provided authoring line.</param>
    /// <param name="sourceMember">Compiler-provided authoring member.</param>
    /// <returns>This association builder.</returns>
    /// <exception cref="ArgumentNullException">Native builder or implementation is null.</exception>
    /// <exception cref="ArgumentException">Kind, canonical placement, model membership or identity is invalid.</exception>
    /// <exception cref="InvalidOperationException">Authoring has completed.</exception>
    public AspireInfrastructureAssociationBuilder Workload<T>(InfrastructureNodeId node, IResourceBuilder<T> native,
        InfrastructureTargetImplementation implementation, ImmutableArray<SourceReference> sources = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string sourceMember = "") where T : IResource
    {
        ArgumentNullException.ThrowIfNull(native);
        Validate(node, native.Resource);
        deployment.WorkloadUsing(node, implementation, new($"aspire/resource/{native.Resource.Name}"), Sources(sources, node, native.Resource),
            sourceFile, sourceLine, sourceMember);
        resources.Add(node, native.Resource);
        return this;
    }

    ImmutableArray<SourceReference> Sources(ImmutableArray<SourceReference> sources, InfrastructureNodeId node, IResource resource) =>
        sources.IsDefault
            ? [SourceReference.Create("aspire-native-association-v1", $"{manifestId.Value}/{node.Value}/{resource.Name}")]
            : sources;

    internal void Freeze() => frozen = true;
    internal ImmutableDictionary<InfrastructureNodeId, IResource> Snapshot()
    {
        HashSet<IResource> current = new(application.Resources, ReferenceEqualityComparer.Instance);
        foreach (var resource in resources.Values)
            if (!current.Contains(resource))
                throw new ArgumentException("An associated resource was removed from the AppHost during authoring.");
        return resources.ToImmutableDictionary();
    }
}
