using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra;

/// <summary>Entry point for deterministic fluent authoring of canonical infrastructure definitions.</summary>
public static class Infrastructure
{
    /// <summary>Authors, normalizes, and fingerprints one canonical infrastructure definition.</summary>
    /// <param name="id">Stable identity shared by semantic revisions.</param>
    /// <param name="revision">Stable identity of the authored semantic revision.</param>
    /// <param name="configure">Synchronous callback that produces canonical IR data and is not retained.</param>
    /// <returns>A current-version canonical infrastructure-definition document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity or authored semantic value is invalid, or <paramref name="configure"/> is asynchronous.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The authored graph is empty, incomplete, duplicated, or references an undeclared node.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime type.</exception>
    public static InfrastructureDefinitionDocument Define(
        InfrastructureDefinitionId id,
        InfrastructureRevisionId revision,
        Action<InfrastructureDefinitionBuilder> configure)
    {
        ValidateConfigure(configure);

        InfrastructureDefinitionBuilder builder = new(id, revision);
        configure(builder);
        return InfrastructureDefinitionDocument.FromDefinition(builder.Build());
    }

    /// <summary>
    /// Authors one canonical infrastructure definition and its exact binding-elaboration profile together.
    /// </summary>
    /// <param name="id">Stable identity shared by semantic revisions.</param>
    /// <param name="revision">Stable identity of the authored semantic revision.</param>
    /// <param name="bindingProfileId">Stable versioned identity of the coordinated binding-elaboration profile.</param>
    /// <param name="configure">Synchronous callback that produces canonical IR data and is not retained.</param>
    /// <returns>The immutable definition document and binding-elaboration profile produced by the session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity or authored semantic value is invalid, or <paramref name="configure"/> is asynchronous.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The authored graph or a contract declaration is empty, incomplete, duplicated, unused, or inconsistent.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime type.</exception>
    public static InfrastructureAuthoringResult Define(
        InfrastructureDefinitionId id,
        InfrastructureRevisionId revision,
        InfrastructureBindingElaborationProfileId bindingProfileId,
        Action<InfrastructureDefinitionBuilder> configure)
    {
        ValidateConfigure(configure);

        InfrastructureDefinitionBuilder builder = new(id, revision, bindingProfileId);
        configure(builder);
        return builder.BuildResult();
    }

    /// <summary>Completes a binding with a contract declared by the same coordinated authoring session.</summary>
    /// <param name="binding">Final binding stage awaiting its semantic contract.</param>
    /// <param name="contract">Typed contract handle that also owns the single elaboration rule.</param>
    /// <returns>The owning definition builder for continued authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/> or <paramref name="contract"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="contract"/> belongs to another authoring session.</exception>
    /// <exception cref="InvalidOperationException">The binding stage was already completed.</exception>
    public static InfrastructureDefinitionBuilder As(
        this InfrastructureBindingContractBuilder binding,
        InfrastructureBindingContractHandle contract)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.AsContract(contract);
    }

    static void ValidateConfigure(Action<InfrastructureDefinitionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (configure.GetInvocationList().Any(static callback =>
                callback.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false)))
        {
            throw new ArgumentException(
                "Infrastructure authoring callbacks must complete synchronously and cannot be async.",
                nameof(configure));
        }
    }
}

/// <summary>Mutable fluent producer that lowers exclusively to canonical <see cref="InfrastructureDefinition"/> IR.</summary>
/// <remarks>
/// This builder and its child builders are authoring projections, not semantic authorities. <see cref="Build"/>
/// returns immutable normalized IR and retains no callback, builder object, ambient configuration, or provider type.
/// </remarks>
public sealed class InfrastructureDefinitionBuilder
{
    readonly InfrastructureDefinitionId id;
    readonly InfrastructureRevisionId revision;
    readonly InfrastructureBindingElaborationProfileId? bindingProfileId;
    readonly List<InfrastructureWorkloadBuilder> workloads = [];
    readonly List<InfrastructureResourceBuilder> resources = [];
    readonly List<InfrastructureBindingDraft> bindings = [];
    readonly Dictionary<InfrastructureBindingContractId, InfrastructureBindingContractHandle> contracts = [];
    readonly HashSet<InfrastructureBindingContractId> usedContracts = [];
    readonly HashSet<InfrastructureNodeId> nodeIds = [];
    readonly HashSet<InfrastructureBindingId> bindingIds = [];

    internal InfrastructureDefinitionBuilder(
        InfrastructureDefinitionId id,
        InfrastructureRevisionId revision,
        InfrastructureBindingElaborationProfileId? bindingProfileId = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Infrastructure authoring requires a definition identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(revision.Value))
            throw new ArgumentException("Infrastructure authoring requires a revision identity.", nameof(revision));

        this.id = id;
        this.revision = revision;
        if (bindingProfileId is { } profileId && string.IsNullOrWhiteSpace(profileId.Value))
        {
            throw new ArgumentException(
                "Coordinated infrastructure authoring requires a binding-elaboration profile identity.",
                nameof(bindingProfileId));
        }
        this.bindingProfileId = bindingProfileId;
    }

    /// <summary>Adds one executable workload node.</summary>
    /// <param name="id">Stable definition-local workload identity.</param>
    /// <returns>A workload builder owned by this definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already used by another node.</exception>
    public InfrastructureWorkloadBuilder Workload(InfrastructureNodeId id)
    {
        RegisterNode(id);
        InfrastructureWorkloadBuilder workload = new(this, id);
        workloads.Add(workload);
        return workload;
    }

    /// <summary>Adds one logical resource whose lifecycle will be selected fluently.</summary>
    /// <param name="id">Stable definition-local resource identity.</param>
    /// <returns>A resource builder owned by this definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already used by another node.</exception>
    public InfrastructureResourceBuilder Resource(InfrastructureNodeId id)
    {
        RegisterNode(id);
        InfrastructureResourceBuilder resource = new(this, id);
        resources.Add(resource);
        return resource;
    }

    /// <summary>Adds one logical resource with an explicit lifecycle.</summary>
    /// <param name="id">Stable definition-local resource identity.</param>
    /// <param name="lifecycle">Desired ownership and replacement lifecycle.</param>
    /// <returns>A resource builder owned by this definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already used by another node.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifecycle"/> is unsupported.</exception>
    public InfrastructureResourceBuilder Resource(
        InfrastructureNodeId id,
        InfrastructureResourceLifecycle lifecycle)
    {
        if (!Enum.IsDefined(lifecycle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifecycle),
                lifecycle,
                "Unsupported infrastructure resource lifecycle.");
        }

        return Resource(id).WithLifecycle(lifecycle);
    }

    /// <summary>Declares one binding contract and its single elaboration-rule authority.</summary>
    /// <param name="contract">Exact provider-neutral binding contract identity.</param>
    /// <param name="rule">Stable versioned identity of the rule that elaborates the contract.</param>
    /// <returns>A typed contract handle used both to author the rule and to complete bindings.</returns>
    /// <exception cref="InvalidOperationException">
    /// This definition was not opened for coordinated binding authoring, or the contract is already declared.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="contract"/> or <paramref name="rule"/> is default.</exception>
    public InfrastructureBindingContractHandle Contract(
        InfrastructureBindingContractId contract,
        InfrastructureBindingElaborationRuleId rule)
    {
        if (bindingProfileId is null)
        {
            throw new InvalidOperationException(
                "Binding contracts can be declared only by the Infrastructure.Define overload that supplies a binding profile identity.");
        }
        if (string.IsNullOrWhiteSpace(contract.Value))
            throw new ArgumentException("Infrastructure contract authoring requires a contract identity.", nameof(contract));
        if (string.IsNullOrWhiteSpace(rule.Value))
            throw new ArgumentException("Infrastructure contract authoring requires an elaboration-rule identity.", nameof(rule));
        if (contracts.ContainsKey(contract))
            throw new InvalidOperationException($"Infrastructure binding contract '{contract.Value}' is already declared.");

        InfrastructureBindingContractHandle handle = new(this, contract, rule);
        contracts.Add(contract, handle);
        return handle;
    }

    /// <summary>Begins a conventionally identified directed binding from a node.</summary>
    /// <param name="source">Node that consumes or initiates the binding contract.</param>
    /// <returns>The binding stage that requires a target node.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is default.</exception>
    public InfrastructureBindingTargetBuilder Bind(InfrastructureNodeId source) =>
        BeginBinding(explicitId: null, source);

    /// <summary>Begins a conventionally identified directed binding from a workload builder.</summary>
    /// <param name="source">Workload that consumes or initiates the binding contract.</param>
    /// <returns>The binding stage that requires a target node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> belongs to another definition.</exception>
    public InfrastructureBindingTargetBuilder Bind(InfrastructureWorkloadBuilder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Owner, this))
            throw new ArgumentException("The source workload belongs to another infrastructure definition.", nameof(source));
        return Bind(source.Id);
    }

    /// <summary>Begins a conventionally identified directed binding from a resource builder.</summary>
    /// <param name="source">Resource that consumes or initiates the binding contract.</param>
    /// <returns>The binding stage that requires a target node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> belongs to another definition.</exception>
    public InfrastructureBindingTargetBuilder Bind(InfrastructureResourceBuilder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Owner, this))
            throw new ArgumentException("The source resource belongs to another infrastructure definition.", nameof(source));
        return Bind(source.Id);
    }

    /// <summary>Begins an explicitly identified directed binding from a node.</summary>
    /// <param name="id">Stable definition-local binding identity.</param>
    /// <param name="source">Node that consumes or initiates the binding contract.</param>
    /// <returns>The binding stage that requires a target node.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="source"/> is default, or the binding identity is already used.
    /// </exception>
    public InfrastructureBindingTargetBuilder Bind(
        InfrastructureBindingId id,
        InfrastructureNodeId source)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Infrastructure binding authoring requires a binding identity.", nameof(id));
        if (!bindingIds.Add(id))
            throw new ArgumentException($"Infrastructure binding identity '{id.Value}' is already used.", nameof(id));
        return BeginBinding(id, source);
    }

    /// <summary>Begins an explicitly identified directed binding from a workload builder.</summary>
    /// <param name="id">Stable definition-local binding identity.</param>
    /// <param name="source">Workload that consumes or initiates the binding contract.</param>
    /// <returns>The binding stage that requires a target node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already used.</exception>
    public InfrastructureBindingTargetBuilder Bind(
        InfrastructureBindingId id,
        InfrastructureWorkloadBuilder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Owner, this))
            throw new ArgumentException("The source workload belongs to another infrastructure definition.", nameof(source));
        return Bind(id, source.Id);
    }

    /// <summary>Begins an explicitly identified directed binding from a resource builder.</summary>
    /// <param name="id">Stable definition-local binding identity.</param>
    /// <param name="source">Resource that consumes or initiates the binding contract.</param>
    /// <returns>The binding stage that requires a target node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already used.</exception>
    public InfrastructureBindingTargetBuilder Bind(
        InfrastructureBindingId id,
        InfrastructureResourceBuilder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Owner, this))
            throw new ArgumentException("The source resource belongs to another infrastructure definition.", nameof(source));
        return Bind(id, source.Id);
    }

    /// <summary>Lowers the complete fluent state into immutable normalized infrastructure IR.</summary>
    /// <returns>A canonical infrastructure definition structurally equal to equivalent direct IR construction.</returns>
    /// <exception cref="InvalidOperationException">
    /// A resource lifecycle or binding target and contract is absent.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The resulting graph is empty, duplicated, or contains an unresolved binding endpoint.
    /// </exception>
    public InfrastructureDefinition Build()
    {
        var incompleteBinding = bindings.FirstOrDefault(static binding => !binding.IsComplete);
        if (incompleteBinding is not null)
        {
            throw new InvalidOperationException(
                $"Infrastructure binding '{incompleteBinding.DisplayIdentity}' must complete To(...).As(...). before Build().");
        }

        return new(
            id,
            revision,
            [.. workloads.Select(static workload => workload.Build())],
            [.. resources.Select(static resource => resource.Build())],
            [.. bindings.Select(static binding => binding.Definition!)]);
    }

    internal InfrastructureAuthoringResult BuildResult()
    {
        var profileId = bindingProfileId
            ?? throw new InvalidOperationException("This infrastructure authoring session does not own a binding profile.");
        var definition = InfrastructureDefinitionDocument.FromDefinition(Build());

        var undeclared = usedContracts
            .Where(contract => !contracts.ContainsKey(contract))
            .OrderBy(static contract => contract.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(undeclared.Value))
        {
            throw new InvalidOperationException(
                $"Infrastructure binding contract '{undeclared.Value}' is used but has no coordinated elaboration declaration.");
        }

        var unused = contracts.Keys
            .Where(contract => !usedContracts.Contains(contract))
            .OrderBy(static contract => contract.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(unused.Value))
        {
            throw new InvalidOperationException(
                $"Infrastructure binding contract '{unused.Value}' is declared but is not used by any binding.");
        }

        var rules = contracts.Values
            .OrderBy(static contract => contract.Id.Value, StringComparer.Ordinal)
            .Select(static contract => contract.BuildRule())
            .ToImmutableArray();
        InfrastructureBindingElaborationProfile profile = new(
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            profileId,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            rules);
        return new(definition, profile);
    }

    internal InfrastructureDefinitionBuilder CompleteBinding(
        InfrastructureBindingDraft draft,
        InfrastructureNodeId target,
        InfrastructureBindingContractId contract)
    {
        var definition = draft.Complete(target, contract);
        if (!draft.HasExplicitId && !bindingIds.Add(definition.Id))
        {
            throw new InvalidOperationException(
                $"Conventional infrastructure binding identity '{definition.Id.Value}' is already used; supply an explicit identity only when the bindings are semantically distinct.");
        }
        if (bindingProfileId is not null)
            usedContracts.Add(contract);
        return this;
    }

    internal InfrastructureDefinitionBuilder CompleteBinding(
        InfrastructureBindingDraft draft,
        InfrastructureNodeId target,
        InfrastructureBindingContractHandle contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (!ReferenceEquals(contract.Owner, this))
        {
            throw new ArgumentException(
                "The binding contract belongs to another infrastructure authoring session.",
                nameof(contract));
        }

        return CompleteBinding(draft, target, contract.Id);
    }

    InfrastructureBindingTargetBuilder BeginBinding(
        InfrastructureBindingId? explicitId,
        InfrastructureNodeId source)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("Infrastructure binding authoring requires a source node.", nameof(source));

        InfrastructureBindingDraft draft = new(explicitId, source);
        bindings.Add(draft);
        return new(this, draft);
    }

    void RegisterNode(InfrastructureNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("Infrastructure authoring requires a node identity.", nameof(node));
        if (!nodeIds.Add(node))
            throw new ArgumentException($"Infrastructure node identity '{node.Value}' is already used.", nameof(node));
    }
}

/// <summary>Fluent producer for one canonical infrastructure workload node.</summary>
public sealed class InfrastructureWorkloadBuilder
{
    readonly InfrastructureRequirementBuilder requirements;

    internal InfrastructureWorkloadBuilder(
        InfrastructureDefinitionBuilder owner,
        InfrastructureNodeId id)
    {
        Owner = owner;
        Id = id;
        requirements = new(id);
    }

    internal InfrastructureDefinitionBuilder Owner { get; }

    /// <summary>Stable identity of the workload being authored.</summary>
    public InfrastructureNodeId Id { get; }

    /// <summary>Adds a capability with a deterministic node-and-capability-derived requirement identity.</summary>
    /// <param name="capability">Provider-neutral capability required by the workload.</param>
    /// <returns>This workload builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="capability"/> is default or already required by this workload.
    /// </exception>
    public InfrastructureWorkloadBuilder Requires(InfrastructureCapabilityId capability)
    {
        requirements.Add(InfrastructureCapabilityRequirement.ForNode(Id, capability));
        return this;
    }

    /// <summary>Adds a capability with an explicit stable requirement identity.</summary>
    /// <param name="id">Stable definition-local requirement identity.</param>
    /// <param name="capability">Provider-neutral capability required by the workload.</param>
    /// <returns>This workload builder.</returns>
    /// <exception cref="ArgumentException">
    /// An identity is default, or the identity or capability is already used by this workload.
    /// </exception>
    public InfrastructureWorkloadBuilder Requires(
        InfrastructureRequirementId id,
        InfrastructureCapabilityId capability)
    {
        requirements.Add(new(id, capability));
        return this;
    }

    internal InfrastructureWorkloadDefinition Build() => new(Id, requirements.ToImmutable());
}

/// <summary>Fluent producer for one canonical infrastructure resource node.</summary>
public sealed class InfrastructureResourceBuilder
{
    readonly InfrastructureRequirementBuilder requirements;
    InfrastructureResourceLifecycle? lifecycle;

    internal InfrastructureResourceBuilder(
        InfrastructureDefinitionBuilder owner,
        InfrastructureNodeId id)
    {
        Owner = owner;
        Id = id;
        requirements = new(id);
    }

    internal InfrastructureDefinitionBuilder Owner { get; }

    /// <summary>Stable identity of the resource being authored.</summary>
    public InfrastructureNodeId Id { get; }

    /// <summary>Declares that the resource normally survives workload replacement.</summary>
    /// <returns>This resource builder.</returns>
    /// <exception cref="InvalidOperationException">A lifecycle was already declared.</exception>
    public InfrastructureResourceBuilder Persistent() =>
        WithLifecycle(InfrastructureResourceLifecycle.Persistent);

    /// <summary>Declares that the resource may be recreated with its owning deployment.</summary>
    /// <returns>This resource builder.</returns>
    /// <exception cref="InvalidOperationException">A lifecycle was already declared.</exception>
    public InfrastructureResourceBuilder Ephemeral() =>
        WithLifecycle(InfrastructureResourceLifecycle.Ephemeral);

    /// <summary>Declares that the resource exists outside this definition's provisioning ownership.</summary>
    /// <returns>This resource builder.</returns>
    /// <exception cref="InvalidOperationException">A lifecycle was already declared.</exception>
    public InfrastructureResourceBuilder External() =>
        WithLifecycle(InfrastructureResourceLifecycle.External);

    /// <summary>Declares the resource's desired ownership and replacement lifecycle.</summary>
    /// <param name="value">Desired resource lifecycle.</param>
    /// <returns>This resource builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">A lifecycle was already declared.</exception>
    public InfrastructureResourceBuilder WithLifecycle(InfrastructureResourceLifecycle value)
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported infrastructure resource lifecycle.");
        if (lifecycle is not null)
            throw new InvalidOperationException($"Infrastructure resource '{Id.Value}' already declares a lifecycle.");

        lifecycle = value;
        return this;
    }

    /// <summary>Adds a capability with a deterministic node-and-capability-derived requirement identity.</summary>
    /// <param name="capability">Provider-neutral capability required by the resource.</param>
    /// <returns>This resource builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="capability"/> is default or already required by this resource.
    /// </exception>
    public InfrastructureResourceBuilder Requires(InfrastructureCapabilityId capability)
    {
        requirements.Add(InfrastructureCapabilityRequirement.ForNode(Id, capability));
        return this;
    }

    /// <summary>Adds a capability with an explicit stable requirement identity.</summary>
    /// <param name="id">Stable definition-local requirement identity.</param>
    /// <param name="capability">Provider-neutral capability required by the resource.</param>
    /// <returns>This resource builder.</returns>
    /// <exception cref="ArgumentException">
    /// An identity is default, or the identity or capability is already used by this resource.
    /// </exception>
    public InfrastructureResourceBuilder Requires(
        InfrastructureRequirementId id,
        InfrastructureCapabilityId capability)
    {
        requirements.Add(new(id, capability));
        return this;
    }

    internal InfrastructureResourceDefinition Build()
    {
        var selectedLifecycle = lifecycle
            ?? throw new InvalidOperationException(
                $"Infrastructure resource '{Id.Value}' must declare Persistent(), Ephemeral(), or External().");
        return new(Id, selectedLifecycle, requirements.ToImmutable());
    }
}

/// <summary>Fluent binding stage that selects the target of a directed source binding.</summary>
public sealed class InfrastructureBindingTargetBuilder
{
    readonly InfrastructureDefinitionBuilder owner;
    readonly InfrastructureBindingDraft draft;
    bool targetSelected;

    internal InfrastructureBindingTargetBuilder(
        InfrastructureDefinitionBuilder owner,
        InfrastructureBindingDraft draft)
    {
        this.owner = owner;
        this.draft = draft;
    }

    /// <summary>Selects the target node of the directed binding.</summary>
    /// <param name="target">Node that supplies or receives the binding contract.</param>
    /// <returns>The binding stage that requires a provider-neutral contract.</returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is default.</exception>
    /// <exception cref="InvalidOperationException">A target was already selected through this stage.</exception>
    public InfrastructureBindingContractBuilder To(InfrastructureNodeId target)
    {
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("Infrastructure binding authoring requires a target node.", nameof(target));
        if (targetSelected)
            throw new InvalidOperationException($"Infrastructure binding '{draft.DisplayIdentity}' already selected a target.");

        targetSelected = true;
        return new(owner, draft, target);
    }

    /// <summary>Selects a workload builder as the target of the directed binding.</summary>
    /// <param name="target">Workload that supplies or receives the binding contract.</param>
    /// <returns>The binding stage that requires a provider-neutral contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A target was already selected through this stage.</exception>
    public InfrastructureBindingContractBuilder To(InfrastructureWorkloadBuilder target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ReferenceEquals(target.Owner, owner))
            throw new ArgumentException("The target workload belongs to another infrastructure definition.", nameof(target));
        return To(target.Id);
    }

    /// <summary>Selects a resource builder as the target of the directed binding.</summary>
    /// <param name="target">Resource that supplies or receives the binding contract.</param>
    /// <returns>The binding stage that requires a provider-neutral contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A target was already selected through this stage.</exception>
    public InfrastructureBindingContractBuilder To(InfrastructureResourceBuilder target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ReferenceEquals(target.Owner, owner))
            throw new ArgumentException("The target resource belongs to another infrastructure definition.", nameof(target));
        return To(target.Id);
    }
}

/// <summary>Final fluent binding stage that selects the semantic contract carried by a binding.</summary>
public sealed class InfrastructureBindingContractBuilder
{
    readonly InfrastructureDefinitionBuilder owner;
    readonly InfrastructureBindingDraft draft;
    readonly InfrastructureNodeId target;
    bool completed;

    internal InfrastructureBindingContractBuilder(
        InfrastructureDefinitionBuilder owner,
        InfrastructureBindingDraft draft,
        InfrastructureNodeId target)
    {
        this.owner = owner;
        this.draft = draft;
        this.target = target;
    }

    /// <summary>Completes the binding with its provider-neutral semantic contract.</summary>
    /// <param name="contract">Stable binding-contract identity.</param>
    /// <returns>The owning definition builder for continued authoring.</returns>
    /// <exception cref="ArgumentException"><paramref name="contract"/> is default.</exception>
    /// <exception cref="InvalidOperationException">This binding stage was already completed.</exception>
    public InfrastructureDefinitionBuilder As(InfrastructureBindingContractId contract)
    {
        if (string.IsNullOrWhiteSpace(contract.Value))
            throw new ArgumentException("Infrastructure binding authoring requires a contract identity.", nameof(contract));
        if (completed)
            throw new InvalidOperationException($"Infrastructure binding '{draft.DisplayIdentity}' is already complete.");

        completed = true;
        return owner.CompleteBinding(draft, target, contract);
    }

    internal InfrastructureDefinitionBuilder AsContract(InfrastructureBindingContractHandle contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (completed)
            throw new InvalidOperationException($"Infrastructure binding '{draft.DisplayIdentity}' is already complete.");

        completed = true;
        return owner.CompleteBinding(draft, target, contract);
    }
}

/// <summary>
/// Typed authoring handle that co-locates one binding contract with its single elaboration-rule authority.
/// </summary>
/// <remarks>
/// The handle is a session-owned producer only. Bindings and the elaboration profile retain canonical identities,
/// capabilities, and source references; no handle or callback survives in either materialized IR artifact.
/// </remarks>
public sealed class InfrastructureBindingContractHandle
{
    readonly InfrastructureBindingElaborationRuleId rule;
    readonly HashSet<InfrastructureCapabilityId> capabilities = [];
    readonly HashSet<SourceReference> sourceReferences = [];

    internal InfrastructureBindingContractHandle(
        InfrastructureDefinitionBuilder owner,
        InfrastructureBindingContractId id,
        InfrastructureBindingElaborationRuleId rule)
    {
        Owner = owner;
        Id = id;
        this.rule = rule;
    }

    internal InfrastructureDefinitionBuilder Owner { get; }

    /// <summary>Exact provider-neutral contract authored by this handle.</summary>
    public InfrastructureBindingContractId Id { get; }

    /// <summary>Adds one capability or assurance obligation induced by this contract.</summary>
    /// <param name="capability">Provider-neutral obligation induced for every binding carrying this contract.</param>
    /// <returns>This contract handle.</returns>
    /// <exception cref="ArgumentException"><paramref name="capability"/> is default or already declared.</exception>
    public InfrastructureBindingContractHandle Requires(InfrastructureCapabilityId capability)
    {
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("A binding contract obligation requires a capability identity.", nameof(capability));
        if (!capabilities.Add(capability))
        {
            throw new ArgumentException(
                $"Infrastructure binding contract '{Id.Value}' already requires capability '{capability.Value}'.",
                nameof(capability));
        }
        return this;
    }

    /// <summary>Adds one attributable producer or specification reference supporting the elaboration rule.</summary>
    /// <param name="sourceReference">Stable typed source reference.</param>
    /// <returns>This contract handle.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceReference"/> is default or already declared.</exception>
    public InfrastructureBindingContractHandle SourcedFrom(SourceReference sourceReference)
    {
        if (string.IsNullOrWhiteSpace(sourceReference.Value))
        {
            throw new ArgumentException("A binding contract source reference cannot be default.", nameof(sourceReference));
        }

        if (!sourceReferences.Add(sourceReference))
        {
            throw new ArgumentException(
                $"Infrastructure binding contract '{Id.Value}' already cites source '{sourceReference.Value}'.",
                nameof(sourceReference));
        }
        return this;
    }

    internal InfrastructureBindingElaborationRule BuildRule()
    {
        if (capabilities.Count == 0)
        {
            throw new InvalidOperationException(
                $"Infrastructure binding contract '{Id.Value}' must declare at least one capability obligation.");
        }
        if (sourceReferences.Count == 0)
        {
            throw new InvalidOperationException(
                $"Infrastructure binding contract '{Id.Value}' must declare at least one source reference.");
        }

        return new(rule, Id, [.. capabilities], [.. sourceReferences]);
    }
}

sealed class InfrastructureRequirementBuilder
{
    readonly InfrastructureNodeId node;
    readonly List<InfrastructureCapabilityRequirement> requirements = [];
    readonly HashSet<InfrastructureRequirementId> ids = [];
    readonly HashSet<InfrastructureCapabilityId> capabilities = [];

    public InfrastructureRequirementBuilder(InfrastructureNodeId node) => this.node = node;

    public void Add(InfrastructureCapabilityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (ids.Contains(requirement.Id))
        {
            throw new ArgumentException(
                $"Infrastructure node '{node.Value}' already uses requirement identity '{requirement.Id.Value}'.",
                nameof(requirement));
        }
        if (capabilities.Contains(requirement.Capability))
        {
            throw new ArgumentException(
                $"Infrastructure node '{node.Value}' already requires capability '{requirement.Capability.Value}'.",
                nameof(requirement));
        }

        ids.Add(requirement.Id);
        capabilities.Add(requirement.Capability);
        requirements.Add(requirement);
    }

    public ImmutableArray<InfrastructureCapabilityRequirement> ToImmutable() => [.. requirements];
}

sealed class InfrastructureBindingDraft
{
    readonly InfrastructureBindingId? explicitId;

    public InfrastructureBindingDraft(InfrastructureBindingId? explicitId, InfrastructureNodeId source)
    {
        this.explicitId = explicitId;
        Source = source;
    }

    public bool HasExplicitId => explicitId is not null;

    public string DisplayIdentity => explicitId?.Value ?? $"from {Source.Value}";

    public InfrastructureNodeId Source { get; }

    public InfrastructureBindingDefinition? Definition { get; private set; }

    public bool IsComplete => Definition is not null;

    public InfrastructureBindingDefinition Complete(
        InfrastructureNodeId target,
        InfrastructureBindingContractId contract)
    {
        if (Definition is not null)
            throw new InvalidOperationException($"Infrastructure binding '{DisplayIdentity}' is already complete.");

        var id = explicitId ?? InfrastructureBindingDefinition.DeriveId(Source, target, contract);
        Definition = new(id, Source, target, contract);
        return Definition;
    }
}
