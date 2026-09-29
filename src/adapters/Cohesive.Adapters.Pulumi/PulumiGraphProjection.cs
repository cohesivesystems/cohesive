using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Pulumi;

namespace Cohesive.Adapters.Pulumi;

/// <summary>
/// Interprets one complete deployment graph through native factories. Canonical bindings and readiness
/// dependencies order construction; they do not cause runtime readiness checks or implicit grants.
/// </summary>
/// <remarks>
/// Instances are invocation-scoped, single-use and not thread safe. Callbacks and native objects never
/// enter canonical IR. Coverage and ordering are checked before callbacks run. A callback failure may
/// leave Pulumi registrations behind; execution is not transactional and must not be automatically retried.
/// Native output validation remains the responsibility of the provider-specific attachment adapters.
/// </remarks>
public sealed class PulumiGraphProjection
{
    readonly Dictionary<InfrastructureNodeId, Registration> registrations = [];
    readonly HashSet<InfrastructureNodeId> selected;
    readonly List<Registration> factories = [];
    bool started;

    /// <summary>Creates a native interpretation for an already compiled deployment.</summary>
    /// <param name="deployment">Complete canonical plan, including explicit target non-participation.</param>
    /// <exception cref="ArgumentException">The plan is incomplete.</exception>
    /// <exception cref="ArgumentNullException">The plan is null.</exception>
    public PulumiGraphProjection(InfrastructureTargetDeploymentPlan deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (!deployment.IsComplete) throw new ArgumentException("A complete deployment plan is required.", nameof(deployment));
        Deployment = deployment;
        selected = [.. deployment.Manifest.Resources.Select(r => r.Resource), .. deployment.Manifest.Workloads.Select(w => w.Workload)];
    }

    /// <summary>Canonical authority for this invocation.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }

    /// <summary>Registers native configuration for one logical node.</summary>
    /// <typeparam name="T">Native resource or existing adapter result retained without conversion.</typeparam>
    /// <param name="node">Selected declaration handled by the factory.</param>
    /// <param name="factory">Constructs native resources and records their associations through the context.</param>
    /// <returns>The registration, for explicit native ordering refinements.</returns>
    /// <exception cref="ArgumentException">The declaration is absent or already handled.</exception>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <exception cref="InvalidOperationException">Execution has begun.</exception>
    public Registration Map<T>(InfrastructureNodeId node, Func<PulumiProjectionContext, T> factory) where T : notnull =>
        Map([node], factory);

    /// <summary>Registers one facility factory that realizes several related logical nodes together.</summary>
    /// <typeparam name="T">Existing native facility result shared by the selected nodes.</typeparam>
    /// <param name="nodes">Exact nonempty logical ownership set; each needs its own native association.</param>
    /// <param name="factory">Invoked once, even when several consumers need the facility.</param>
    /// <returns>The registration, for explicit native ordering refinements.</returns>
    /// <exception cref="ArgumentException">Nodes are empty, duplicated, absent or already handled.</exception>
    /// <exception cref="ArgumentNullException">Nodes or factory are null.</exception>
    /// <exception cref="InvalidOperationException">Execution has begun.</exception>
    public Registration Map<T>(IEnumerable<InfrastructureNodeId> nodes, Func<PulumiProjectionContext, T> factory) where T : notnull
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(factory);
        var owned = nodes.ToImmutableArray();
        if (owned.IsEmpty || owned.Distinct().Count() != owned.Length)
            throw new ArgumentException("A factory requires a nonempty, unique ownership set.", nameof(nodes));
        foreach (var node in owned)
            if (!selected.Contains(node) || registrations.ContainsKey(node))
                throw new ArgumentException($"Projection node '{node.Value}' is absent or already handled.", nameof(nodes));
        var registration = new Registration(this, owned, context => factory(context));
        factories.Add(registration);
        foreach (var node in owned) registrations.Add(node, registration);
        return registration;
    }

    /// <summary>Preflights the whole selected graph, then invokes native factories in stable dependency order.</summary>
    /// <returns>Exact native results and associations from this invocation.</returns>
    /// <exception cref="InvalidOperationException">Coverage is incomplete, ordering is cyclic, a factory omits an association,
    /// returns null, reads an undeclared dependency, or execution has already begun. Factory exceptions propagate unchanged.</exception>
    public PulumiProjectionResult Execute()
    {
        EnsureMutable();
        started = true;
        var missing = selected.Except(registrations.Keys).OrderBy(n => n.Value, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Missing Pulumi projections: {string.Join(", ", missing.Select(n => n.Value))}.");
        var dependencies = factories.ToDictionary(f => f, _ => new HashSet<Registration>());
        var definition = Deployment.FacilityPlan.Definition.Definition;
        void Edge(InfrastructureNodeId source, InfrastructureNodeId target)
        {
            if (!selected.Contains(source)) return; // Explicit non-participation belongs to the compiled manifest.
            if (!registrations.TryGetValue(target, out var prerequisite))
                throw new InvalidOperationException($"Selected '{source.Value}' requires unselected '{target.Value}'.");
            var consumer = registrations[source];
            if (consumer != prerequisite) dependencies[consumer].Add(prerequisite);
        }
        foreach (var binding in definition.Bindings) Edge(binding.Source, binding.Target);
        foreach (var dependency in definition.ReadinessDependencies) Edge(dependency.Subject, dependency.Dependency);
        foreach (var factory in factories)
            foreach (var dependency in factory.NativeDependencies.Keys)
                Edge(factory.Nodes[0], dependency);

        // Complete topological preflight before the first native constructor, including native refinements.
        var ordered = new List<Registration>(factories.Count);
        var remaining = dependencies.ToDictionary(p => p.Key, p => p.Value.Count);
        var dependents = factories.ToDictionary(f => f, _ => new List<Registration>());
        foreach (var (consumer, required) in dependencies)
            foreach (var dependency in required) dependents[dependency].Add(consumer);
        var ready = new SortedSet<Registration>(Comparer<Registration>.Create((a, b) =>
            StringComparer.Ordinal.Compare(a.Key, b.Key)));
        foreach (var (factory, count) in remaining) if (count == 0) ready.Add(factory);
        while (ready.Count != 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            ordered.Add(next);
            foreach (var consumer in dependents[next]) if (--remaining[consumer] == 0) ready.Add(consumer);
        }
        if (ordered.Count != factories.Count)
            throw new InvalidOperationException($"Cyclic Pulumi construction dependencies: {string.Join(", ", remaining.Where(p => p.Value > 0).Select(p => p.Key.Key).Order(StringComparer.Ordinal))}.");

        var result = new PulumiProjectionResult(Deployment);
        foreach (var factory in ordered)
        {
            var allowed = dependencies[factory].SelectMany(f => f.Nodes).ToHashSet();
            var context = new PulumiProjectionContext(result, factory.Nodes, allowed);
            object value;
            try
            {
                value = factory.Factory(context) ?? throw new InvalidOperationException($"Projection '{factory.Key}' returned null.");
                context.Complete();
            }
            finally { context.Invalidate(); }
            foreach (var node in factory.Nodes) result.Values.Add(node, value);
        }
        return result;
    }

    void EnsureMutable()
    {
        if (started) throw new InvalidOperationException("A Pulumi projection is single-use; create a fresh invocation after inspecting any partial registrations.");
    }

    /// <summary>Native factory registration; dependency refinements do not alter canonical IR.</summary>
    public sealed class Registration
    {
        readonly PulumiGraphProjection owner;
        internal readonly ImmutableArray<InfrastructureNodeId> Nodes;
        internal readonly Func<PulumiProjectionContext, object> Factory;
        internal readonly Dictionary<InfrastructureNodeId, string> NativeDependencies = [];
        internal string Key => Nodes.MinBy(n => n.Value, StringComparer.Ordinal).Value;
        internal Registration(PulumiGraphProjection owner, ImmutableArray<InfrastructureNodeId> nodes, Func<PulumiProjectionContext, object> factory)
        { this.owner = owner; Nodes = nodes; Factory = factory; }

        /// <summary>Adds a native construction prerequisite beyond the canonical relationship graph.</summary>
        /// <param name="node">Selected declaration whose native result is required.</param>
        /// <param name="reason">Inspectable target-specific reason, such as provider propagation sequencing.</param>
        /// <returns>This registration for further refinements.</returns>
        /// <exception cref="ArgumentException">The node is absent, owned by this factory, or reason is blank.</exception>
        /// <exception cref="InvalidOperationException">Execution has begun.</exception>
        public Registration After(InfrastructureNodeId node, string reason)
        {
            owner.EnsureMutable();
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (!owner.selected.Contains(node) || Nodes.Contains(node))
                throw new ArgumentException($"Invalid native prerequisite '{node.Value}'.", nameof(node));
            NativeDependencies[node] = reason;
            return this;
        }
    }
}

/// <summary>Completed native values and declared associations. Pulumi outputs retain their dependency and secret semantics.</summary>
public sealed class PulumiProjectionResult
{
    internal readonly Dictionary<InfrastructureNodeId, object> Values = [];
    internal readonly Dictionary<InfrastructureNodeId, Resource> Resources = [];
    internal readonly Dictionary<InfrastructureNodeId, string> References = [];
    internal PulumiProjectionResult(InfrastructureTargetDeploymentPlan deployment)
    {
        Deployment = deployment;
        NativeResources = new ReadOnlyDictionary<InfrastructureNodeId, Resource>(Resources);
        ExternalReferences = new ReadOnlyDictionary<InfrastructureNodeId, string>(References);
    }
    /// <summary>Exact plan interpreted by this result.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Primary native associations; provider children remain native implementation detail.</summary>
    public IReadOnlyDictionary<InfrastructureNodeId, Resource> NativeResources { get; }
    /// <summary>Explicit external references and their reasons.</summary>
    public IReadOnlyDictionary<InfrastructureNodeId, string> ExternalReferences { get; }

    /// <summary>Retrieves the existing native result for a selected declaration.</summary>
    /// <typeparam name="T">Expected factory result type.</typeparam>
    /// <param name="node">Logical declaration identity.</param>
    /// <returns>The exact factory result, without awaiting or unwrapping Pulumi outputs.</returns>
    /// <exception cref="InvalidOperationException">The node is unresolved or the requested type does not match.</exception>
    public T Get<T>(InfrastructureNodeId node) where T : notnull =>
        Values.TryGetValue(node, out var value) && value is T typed ? typed :
            throw new InvalidOperationException($"Projection '{node.Value}' is unavailable as {typeof(T).Name}.");
}

/// <summary>Invocation-local native construction context with declared dependency access and exact ownership checks.</summary>
public sealed class PulumiProjectionContext
{
    readonly PulumiProjectionResult result;
    readonly ImmutableArray<InfrastructureNodeId> owned;
    readonly HashSet<InfrastructureNodeId> allowed;
    bool complete;
    internal PulumiProjectionContext(PulumiProjectionResult result, ImmutableArray<InfrastructureNodeId> owned, HashSet<InfrastructureNodeId> allowed)
    { this.result = result; this.owned = owned; this.allowed = allowed; }
    /// <summary>The canonical plan, available to native provider attachment APIs.</summary>
    public InfrastructureTargetDeploymentPlan Deployment => result.Deployment;

    /// <summary>Reads an already constructed declared or explicitly refined prerequisite.</summary>
    /// <typeparam name="T">Native factory result type.</typeparam>
    /// <param name="node">Prerequisite declaration.</param>
    /// <returns>The exact native value, preserving Pulumi output semantics.</returns>
    /// <exception cref="InvalidOperationException">The dependency is undeclared, unresolved, mistyped or the callback has ended.</exception>
    public T Get<T>(InfrastructureNodeId node) where T : notnull
    {
        if (complete || !allowed.Contains(node)) throw new InvalidOperationException($"Undeclared native dependency '{node.Value}'.");
        return result.Get<T>(node);
    }

    /// <summary>Associates a constructed or natively referenced Pulumi resource with a factory-owned declaration.</summary>
    /// <param name="node">Exact owned node.</param>
    /// <param name="resource">Primary native resource; provider children need not become canonical declarations.</param>
    /// <exception cref="ArgumentNullException">The resource is null.</exception>
    /// <exception cref="InvalidOperationException">The node is not owned, already associated, or the callback has ended.</exception>
    public void Associate(InfrastructureNodeId node, Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        CheckOwnership(node);
        result.Resources.Add(node, resource);
    }

    /// <summary>Records a metadata-only external reference without fabricating a Pulumi resource.</summary>
    /// <param name="node">Owned declaration with canonical external lifecycle.</param>
    /// <param name="reason">Why the external authority is referenced rather than managed.</param>
    /// <exception cref="ArgumentException">Reason is blank.</exception>
    /// <exception cref="InvalidOperationException">The declaration is not external, is already associated, or is not owned.</exception>
    public void Reference(InfrastructureNodeId node, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CheckOwnership(node);
        if (!Deployment.FacilityPlan.Definition.Definition.Resources.Any(r => r.Id == node && r.Lifecycle == InfrastructureResourceLifecycle.External))
            throw new InvalidOperationException($"'{node.Value}' is not an external resource declaration.");
        result.References.Add(node, reason);
    }

    void CheckOwnership(InfrastructureNodeId node)
    {
        if (complete || !owned.Contains(node) || result.Resources.ContainsKey(node) || result.References.ContainsKey(node))
            throw new InvalidOperationException($"Invalid or duplicate native association '{node.Value}'.");
    }

    internal void Complete()
    {
        complete = true;
        foreach (var node in owned)
            if (!result.Resources.ContainsKey(node) && !result.References.ContainsKey(node))
                throw new InvalidOperationException($"Projection omitted native association '{node.Value}'.");
    }

    internal void Invalidate() => complete = true;
}
