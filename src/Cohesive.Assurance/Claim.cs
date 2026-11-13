using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Assurance;

/// <summary>Authoritative structural claim source; immutable edits are lowered only by Build.</summary>
public sealed class Claim
{
    readonly string id;
    readonly string revision;
    readonly ClaimReference source;
    readonly ClaimReference configuration;
    readonly ClaimReference observation;
    readonly ImmutableArray<ClaimAttachment> attachments;
    readonly ImmutableArray<ClaimReference> assumptions;
    readonly ClaimForm? form;
    readonly ClaimReference? selection;
    readonly ClaimReference? witness;
    readonly InitialStateSelection initial;
    readonly long? deadline;
    readonly Expr? predicate;
    readonly ImmutableArray<ClaimInput> inputs;
    readonly string producer;
    readonly ImmutableArray<ClaimReference> evidenceRequirements;

    /// <summary>Exact source attribution available to typed source adapters.</summary>
    public ClaimReference AuthoringSource => source;

    Claim(string id, string revision, ClaimReference source, ClaimReference configuration,
        ClaimReference observation, ImmutableArray<ClaimAttachment> attachments,
        ImmutableArray<ClaimReference> assumptions = default, ClaimForm? form = null,
        ClaimReference? selection = null, ClaimReference? witness = null,
        InitialStateSelection initial = InitialStateSelection.Exclude, long? deadline = null,
        Expr? predicate = null, ImmutableArray<ClaimInput> inputs = default,
        string producer = ClaimDocument.LoweringVersion, ImmutableArray<ClaimReference> evidenceRequirements = default)
    {
        this.id = id; this.revision = revision; this.source = source;
        this.configuration = configuration; this.observation = observation;
        this.attachments = attachments; this.assumptions = assumptions.IsDefault ? [] : assumptions;
        this.form = form; this.selection = selection; this.witness = witness; this.initial = initial;
        this.deadline = deadline; this.predicate = predicate;
        this.inputs = inputs.IsDefault ? [] : inputs; this.producer = producer;
        this.evidenceRequirements = evidenceRequirements.IsDefault ? [] : evidenceRequirements;
    }

    /// <summary>Starts source authoring with exact semantic and provenance references.</summary>
    /// <param name="id">Stable claim identity.</param>
    /// <param name="revision">Immutable claim revision.</param>
    /// <param name="subject">Exact primary semantic subject.</param>
    /// <param name="observation">Exact observation/binding contract.</param>
    /// <param name="source">Exact reviewed authoring source.</param>
    /// <param name="configuration">Exact effective lowering configuration/convention profile.</param>
    /// <returns>A new immutable authoring declaration.</returns>
    /// <exception cref="ArgumentNullException">A reference or string is null.</exception>
    /// <exception cref="ArgumentException">Identity or revision is empty.</exception>
    public static Claim Define(string id, string revision, ClaimReference subject,
        ClaimReference observation, ClaimReference source, ClaimReference configuration) =>
        new(Guard.RequireNotNullOrWhiteSpace(id), Guard.RequireNotNullOrWhiteSpace(revision),
            Guard.RequireNotNull(source), Guard.RequireNotNull(configuration), Guard.RequireNotNull(observation),
            [new("subject", Guard.RequireNotNull(subject))]);

    Claim Copy(ImmutableArray<ClaimAttachment>? attachments = null, ImmutableArray<ClaimReference>? assumptions = null,
        Expr? predicate = null, ImmutableArray<ClaimInput>? inputs = null, string? producer = null,
        ImmutableArray<ClaimReference>? evidenceRequirements = null) =>
        new(id, revision, source, configuration, observation, attachments ?? this.attachments,
            assumptions ?? this.assumptions, form, selection, witness, initial, deadline,
            predicate ?? this.predicate, inputs ?? this.inputs, producer ?? this.producer,
            evidenceRequirements ?? this.evidenceRequirements);

    Claim Select(ClaimForm selectedForm, ClaimReference selected, ClaimReference? matching = null,
        InitialStateSelection initialState = InitialStateSelection.Exclude, long? ticks = null)
    {
        if (form.HasValue) throw new InvalidOperationException("A claim form is already selected; start a new source revision.");
        return new(id, revision, source, configuration, observation, attachments, assumptions,
            selectedForm, Guard.RequireNotNull(selected), matching, initialState, ticks, predicate, inputs, producer, evidenceRequirements);
    }

    /// <summary>Selects a state invariant over an exact population.</summary>
    /// <param name="population">Exact population selector.</param>
    /// <param name="initialState">Explicit initial boundary policy.</param>
    /// <returns>A new declaration selecting invariant semantics.</returns>
    /// <exception cref="ArgumentNullException">Population is null.</exception>
    /// <exception cref="InvalidOperationException">A form was already selected.</exception>
    public Claim AtEveryCommittedState(ClaimReference population, InitialStateSelection initialState) =>
        Select(ClaimForm.StateInvariant, population, initialState: initialState);

    /// <summary>Selects successful commits of an exact transition.</summary>
    /// <param name="transition">Exact transition selector.</param>
    /// <returns>A new transition-contract declaration.</returns>
    /// <exception cref="ArgumentNullException">Transition is null.</exception>
    /// <exception cref="InvalidOperationException">A form was already selected.</exception>
    public Claim ForEachCommitted(ClaimReference transition) => Select(ClaimForm.TransitionContract, transition);

    /// <summary>Requires an existential, non-consuming, strictly earlier matching event.</summary>
    /// <param name="trigger">Exact trigger event selector.</param>
    /// <param name="earlierEvent">Exact required earlier-event selector.</param>
    /// <returns>A new historical-requirement declaration.</returns>
    /// <exception cref="ArgumentNullException">A selector is null.</exception>
    /// <exception cref="InvalidOperationException">A form was already selected.</exception>
    public Claim RequireEarlier(ClaimReference trigger, ClaimReference earlierEvent) =>
        Select(ClaimForm.HistoricalRequirement, trigger, Guard.RequireNotNull(earlierEvent));

    /// <summary>Requires a non-consuming matching outcome at or before an inclusive deadline.</summary>
    /// <param name="trigger">Exact trigger event selector.</param>
    /// <param name="outcome">Exact outcome selector.</param>
    /// <param name="deadline">Nonnegative duration; same-step matching is allowed.</param>
    /// <returns>A new bounded-response declaration.</returns>
    /// <exception cref="ArgumentNullException">A selector is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Deadline is negative.</exception>
    /// <exception cref="InvalidOperationException">A form was already selected.</exception>
    public Claim RequireWithin(ClaimReference trigger, ClaimReference outcome, TimeSpan deadline)
    {
        if (deadline < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        return Select(ClaimForm.BoundedResponse, trigger, Guard.RequireNotNull(outcome), ticks: deadline.Ticks);
    }

    /// <summary>Adds a domain-owned subject in an explicit relationship role.</summary>
    /// <param name="role">Unique role name; subject is reserved for the primary subject.</param>
    /// <param name="reference">Exact semantic reference.</param>
    /// <returns>A new declaration with the additional attachment.</returns>
    /// <exception cref="ArgumentNullException">Role or reference is null.</exception>
    /// <exception cref="ArgumentException">Role is empty or already bound.</exception>
    public Claim Attach(string role, ClaimReference reference)
    {
        role = Guard.RequireNotNullOrWhiteSpace(role);
        if (attachments.Any(x => x.Role == role)) throw new ArgumentException("Role already bound.", nameof(role));
        return Copy(attachments: [.. attachments, new(role, Guard.RequireNotNull(reference))]);
    }

    /// <summary>Adds an exact externally justified assumption.</summary>
    /// <param name="assumption">Exact assumption reference.</param>
    /// <returns>A new declaration retaining the assumption.</returns>
    /// <exception cref="ArgumentNullException">Assumption is null.</exception>
    public Claim Assuming(ClaimReference assumption) =>
        Copy(assumptions: [.. assumptions, Guard.RequireNotNull(assumption)]);

    /// <summary>Attaches an exact requirement for evidence without inventing a readiness policy.</summary>
    /// <param name="requirement">Exact evidence requirement/policy reference.</param>
    /// <returns>A new declaration retaining the requirement.</returns>
    /// <exception cref="ArgumentNullException">Requirement is null.</exception>
    public Claim RequireEvidence(ClaimReference requirement) =>
        Copy(evidenceRequirements: [.. evidenceRequirements, Guard.RequireNotNull(requirement)]);

    /// <summary>Supplies structural predicate source or output from a typed source adapter.</summary>
    /// <param name="predicate">Predicate using core expression semantics.</param>
    /// <param name="inputs">Input role/contract declarations, defensively copied.</param>
    /// <param name="producer">Versioned source producer; affects provenance, not semantic equivalence.</param>
    /// <returns>A new declaration with the predicate and its bindings.</returns>
    /// <exception cref="ArgumentNullException">Predicate, inputs or producer is null.</exception>
    /// <exception cref="ArgumentException">Producer is empty.</exception>
    public Claim Require(Expr predicate, IEnumerable<ClaimInput> inputs, string producer = ClaimDocument.LoweringVersion)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return Copy(predicate: Guard.RequireNotNull(predicate), inputs: [.. inputs],
            producer: Guard.RequireNotNullOrWhiteSpace(producer));
    }

    /// <summary>Lowers complete source into validated immutable canonical IR and provenance.</summary>
    /// <returns>A deterministic document with semantic and complete artifact fingerprints.</returns>
    /// <exception cref="InvalidOperationException">A form, selector or predicate is missing.</exception>
    /// <exception cref="ArgumentException">Roles, contracts or form-specific values are invalid.</exception>
    /// <exception cref="ClaimValidationException">Predicate analysis fails.</exception>
    public ClaimDocument Build()
    {
        if (form is null || selection is null || predicate is null)
            throw new InvalidOperationException("Select a claim form and supply its predicate before lowering.");
        ClaimDefinition definition = new(id, revision, form.Value,
            [.. attachments.OrderBy(x => x.Role, StringComparer.Ordinal)], observation, selection, witness,
            initial, deadline, ClaimReference.NormalizeSet(assumptions),
            [.. inputs.OrderBy(x => x.Id.Value, StringComparer.Ordinal)], predicate,
            ClaimReference.NormalizeSet(evidenceRequirements));
        var analysis = definition.Analyze();
        if (!analysis.IsValid) throw new ClaimValidationException(analysis.Validation);
        return ClaimDocument.Create(definition, source, configuration, producer);
    }
}
