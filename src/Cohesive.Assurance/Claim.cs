using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Assurance;

/// <summary>Authoritative structural claim source; immutable edits are lowered only by Build.</summary>
public sealed record Claim
{
    string id { get; init; } = null!;
    string revision { get; init; } = null!;
    ClaimReference source { get; init; } = null!;
    ClaimReference configuration { get; init; } = null!;
    ClaimReference observation { get; init; } = null!;
    ImmutableArray<ClaimAttachment> attachments { get; init; } = [];
    ImmutableArray<ClaimReference> assumptions { get; init; } = [];
    ClaimForm form { get; init; }
    ClaimReference selection { get; init; } = null!;
    ClaimReference? witness { get; init; }
    InitialStateSelection initial { get; init; }
    long? deadline { get; init; }
    Expr predicate { get; init; } = null!;
    ImmutableArray<ClaimInput> inputs { get; init; } = [];
    string producer { get; init; } = ClaimDocument.LoweringVersion;
    ImmutableArray<ClaimReference> evidenceRequirements { get; init; } = [];

    private Claim() { }

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
    public static Declaration Define(string id, string revision, ClaimReference subject,
        ClaimReference observation, ClaimReference source, ClaimReference configuration) =>
        new(new Claim
        {
            id = Guard.RequireNotNullOrWhiteSpace(id), revision = Guard.RequireNotNullOrWhiteSpace(revision),
            source = Guard.RequireNotNull(source), configuration = Guard.RequireNotNull(configuration),
            observation = Guard.RequireNotNull(observation), attachments = [new("subject", Guard.RequireNotNull(subject))]
        });

    /// <summary>Immutable source awaiting exactly one claim form. Only form selection is available.</summary>
    public sealed class Declaration
    {
        readonly Claim value;
        internal Declaration(Claim value) => this.value = value;

        Predicate Select(ClaimForm form, ClaimReference selected, ClaimReference? matching = null,
            InitialStateSelection initialState = InitialStateSelection.Exclude, long? ticks = null) =>
            new(value with { form = form, selection = Guard.RequireNotNull(selected), witness = matching,
                initial = initialState, deadline = ticks });

        /// <summary>Selects a state invariant over an exact population.</summary>
        /// <param name="population">Exact population selector.</param>
        /// <param name="initialState">Explicit initial boundary policy.</param>
        /// <returns>A new declaration selecting invariant semantics.</returns>
        /// <exception cref="ArgumentNullException">Population is null.</exception>
        public Predicate AtEveryCommittedState(ClaimReference population, InitialStateSelection initialState) =>
            Select(ClaimForm.StateInvariant, population, initialState: initialState);

        /// <summary>Selects successful commits of an exact transition.</summary>
        /// <param name="transition">Exact transition selector.</param>
        /// <returns>A new transition-contract declaration.</returns>
        /// <exception cref="ArgumentNullException">Transition is null.</exception>
        public Predicate ForEachCommitted(ClaimReference transition) => Select(ClaimForm.TransitionContract, transition);

        /// <summary>Requires an existential, non-consuming, strictly earlier matching event.</summary>
        /// <param name="trigger">Exact trigger event selector.</param>
        /// <param name="earlierEvent">Exact required earlier-event selector.</param>
        /// <returns>A new historical-requirement declaration.</returns>
        /// <exception cref="ArgumentNullException">A selector is null.</exception>
        public Predicate RequireEarlier(ClaimReference trigger, ClaimReference earlierEvent) =>
            Select(ClaimForm.HistoricalRequirement, trigger, Guard.RequireNotNull(earlierEvent));

        /// <summary>Requires a non-consuming matching outcome at or before an inclusive deadline.</summary>
        /// <param name="trigger">Exact trigger event selector.</param>
        /// <param name="outcome">Exact outcome selector.</param>
        /// <param name="deadline">Nonnegative duration; same-step matching is allowed.</param>
        /// <returns>A new bounded-response declaration.</returns>
        /// <exception cref="ArgumentNullException">A selector is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Deadline is negative.</exception>
        public Predicate RequireWithin(ClaimReference trigger, ClaimReference outcome, TimeSpan deadline)
        {
            if (deadline < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
            return Select(ClaimForm.BoundedResponse, trigger, Guard.RequireNotNull(outcome), ticks: deadline.Ticks);
        }
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
        return this with { attachments = [.. attachments, new(role, Guard.RequireNotNull(reference))] };
    }

    /// <summary>Adds an exact externally justified assumption.</summary>
    /// <param name="assumption">Exact assumption reference.</param>
    /// <returns>A new declaration retaining the assumption.</returns>
    /// <exception cref="ArgumentNullException">Assumption is null.</exception>
    public Claim Assuming(ClaimReference assumption) =>
        this with { assumptions = [.. assumptions, Guard.RequireNotNull(assumption)] };

    /// <summary>Attaches an exact requirement for evidence without inventing a readiness policy.</summary>
    /// <param name="requirement">Exact evidence requirement/policy reference.</param>
    /// <returns>A new declaration retaining the requirement.</returns>
    /// <exception cref="ArgumentNullException">Requirement is null.</exception>
    public Claim RequireEvidence(ClaimReference requirement) =>
        this with { evidenceRequirements = [.. evidenceRequirements, Guard.RequireNotNull(requirement)] };

    /// <summary>Immutable source with a selected form, awaiting its required predicate.</summary>
    public sealed class Predicate
    {
        readonly Claim value;
        internal Predicate(Claim value) => this.value = value;
        /// <summary>Exact reviewed source attribution used by typed authoring adapters.</summary>
        public ClaimReference AuthoringSource => value.source;

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
            return value with { predicate = Guard.RequireNotNull(predicate), inputs = [.. inputs],
                producer = Guard.RequireNotNullOrWhiteSpace(producer) };
        }
    }

    /// <summary>Lowers complete source into validated immutable canonical IR and provenance.</summary>
    /// <returns>A deterministic document with semantic and complete artifact fingerprints.</returns>
    /// <exception cref="ArgumentException">Roles, contracts or form-specific values are invalid.</exception>
    /// <exception cref="ClaimValidationException">Predicate analysis fails.</exception>
    public ClaimDocument Build()
    {
        ClaimDefinition definition = new(id, revision, form,
            [.. attachments.OrderBy(x => x.Role, StringComparer.Ordinal)], observation, selection, witness,
            initial, deadline, ClaimReference.NormalizeSet(assumptions),
            [.. inputs.OrderBy(x => x.Id.Value, StringComparer.Ordinal)], predicate,
            ClaimReference.NormalizeSet(evidenceRequirements));
        var analysis = definition.Analyze();
        if (!analysis.IsValid) throw new ClaimValidationException(analysis.Validation);
        return ClaimDocument.Create(definition, source, configuration, producer);
    }
}
