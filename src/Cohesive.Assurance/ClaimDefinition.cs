using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>A semantic subject bound to a named role in a claim.</summary>
/// <param name="Role">Non-empty domain-defined role; subject is the primary role.</param>
/// <param name="Reference">Exact domain-owned subject reference.</param>
public sealed record ClaimAttachment(string Role, ClaimReference Reference);

/// <summary>Read-only canonical claim produced by lowering or verified persistence.</summary>
/// <remarks>No authoring callbacks, CLR types or executable dependencies are retained.</remarks>
public sealed class ClaimDefinition
{
    [JsonConstructor]
    internal ClaimDefinition(string id, string revision, ClaimForm form,
        ImmutableArray<ClaimAttachment> attachments, ClaimReference observation,
        ClaimReference selection, ClaimReference? witness, InitialStateSelection initialState,
        long? deadlineTicks, ImmutableArray<ClaimReference> assumptions,
        ImmutableArray<ClaimInput> inputs, Expr predicate, ImmutableArray<ClaimReference> evidenceRequirements)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        if (!Enum.IsDefined(form)) throw new ArgumentException("Unsupported claim form.", nameof(form));
        if (!Enum.IsDefined(initialState)) throw new ArgumentException("Unsupported initial-state selection.", nameof(initialState));
        Form = form;
        Observation = Guard.RequireNotNull(observation);
        Selection = Guard.RequireNotNull(selection);
        Predicate = Guard.RequireNotNull(predicate);
        Attachments = attachments.IsDefault ? [] : attachments;
        Assumptions = assumptions.IsDefault ? [] : assumptions;
        EvidenceRequirements = evidenceRequirements.IsDefault ? [] : evidenceRequirements;
        Inputs = inputs.IsDefault ? [] : inputs;
        Witness = witness;
        InitialState = initialState;
        DeadlineTicks = deadlineTicks;
        if (Attachments.IsEmpty || Attachments.Any(x => x is null || string.IsNullOrWhiteSpace(x.Role) || x.Reference is null)
            || Attachments.Select(x => x.Role).Distinct(StringComparer.Ordinal).Count() != Attachments.Length
            || !Attachments.Any(x => x.Role == "subject"))
            throw new ArgumentException("Unique attachment roles including subject are required.", nameof(attachments));
        if (Assumptions.Any(x => x is null) || Assumptions.Distinct().Count() != Assumptions.Length)
            throw new ArgumentException("Assumptions must be non-null and unique.", nameof(assumptions));
        if (EvidenceRequirements.Any(x => x is null) || EvidenceRequirements.Distinct().Count() != EvidenceRequirements.Length)
            throw new ArgumentException("Evidence requirements must be non-null and unique.", nameof(evidenceRequirements));
        if (Inputs.IsEmpty || Inputs.Any(x => x is null)
            || Inputs.Select(x => x.Id).Distinct().Count() != Inputs.Length
            || Inputs.Select(x => x.Role).Distinct().Count() != Inputs.Length)
            throw new ArgumentException("Predicate inputs require unique identities and roles.", nameof(inputs));
        if ((form is ClaimForm.HistoricalRequirement or ClaimForm.BoundedResponse) != (witness is not null))
            throw new ArgumentException("Only event obligations require a witness selector.", nameof(witness));
        if ((form == ClaimForm.BoundedResponse) != deadlineTicks.HasValue || deadlineTicks < 0)
            throw new ArgumentException("Only bounded responses require a nonnegative deadline.", nameof(deadlineTicks));
        if (form != ClaimForm.StateInvariant && initialState != InitialStateSelection.Exclude)
            throw new ArgumentException("Only invariants select initial states.", nameof(initialState));
        foreach (var input in Inputs)
        {
            var allowed = form switch
            {
                ClaimForm.StateInvariant => input.Role == ClaimInputRole.State,
                ClaimForm.TransitionContract => input.Role is ClaimInputRole.Before or ClaimInputRole.Command or ClaimInputRole.After,
                _ => input.Role is ClaimInputRole.Trigger or ClaimInputRole.Witness
            };
            if (!allowed) throw new ArgumentException("Input role is not valid for this claim form.", nameof(inputs));
        }
        if (witness is not null && Inputs.Length != 2)
            throw new ArgumentException("Event matching binds both trigger and witness.", nameof(inputs));
        if (!Attachments.SequenceEqual(Attachments.OrderBy(x => x.Role, StringComparer.Ordinal))
            || !Inputs.SequenceEqual(Inputs.OrderBy(x => x.Id.Value, StringComparer.Ordinal))
            || !Assumptions.SequenceEqual(ClaimReference.NormalizeSet(Assumptions))
            || !EvidenceRequirements.SequenceEqual(ClaimReference.NormalizeSet(EvidenceRequirements)))
            throw new ArgumentException("Persisted claim sets must use canonical order.");
    }

    /// <summary>Stable claim identity.</summary>
    public string Id { get; }
    /// <summary>Immutable claim revision.</summary>
    public string Revision { get; }
    /// <summary>Temporal/state interpretation form.</summary>
    public ClaimForm Form { get; }
    /// <summary>Exact semantic subjects in canonical role order.</summary>
    public ImmutableArray<ClaimAttachment> Attachments { get; }
    /// <summary>Exact observation contract defining clock, completeness and binding.</summary>
    public ClaimReference Observation { get; }
    /// <summary>Exact population, committed-transition or trigger selector.</summary>
    public ClaimReference Selection { get; }
    /// <summary>Exact earlier-event or outcome selector, if required.</summary>
    public ClaimReference? Witness { get; }
    /// <summary>Initial-state selection for invariants.</summary>
    public InitialStateSelection InitialState { get; }
    /// <summary>Inclusive response duration in TimeSpan ticks (100 ns); null for other forms.</summary>
    public long? DeadlineTicks { get; }
    /// <summary>Exact externally justified assumptions, in canonical order.</summary>
    public ImmutableArray<ClaimReference> Assumptions { get; }
    /// <summary>Exact evidence-policy requirements; empty means no additional policy was declared.</summary>
    public ImmutableArray<ClaimReference> EvidenceRequirements { get; }
    /// <summary>Named inputs in canonical binding order.</summary>
    public ImmutableArray<ClaimInput> Inputs { get; }
    /// <summary>State/transition predicate or event correlation predicate.</summary>
    public Expr Predicate { get; }

    /// <summary>Validates predicate requirements against the exact input contracts.</summary>
    /// <returns>Structured diagnostics from the shared core analyzer.</returns>
    public ExprAnalysisResult Analyze() => AssurancePredicates.Analyze(new(Id), Predicate,
        new ExprScope(bindings: Inputs.Select(x => new ExprScopeBinding(x.Id, x.Contract))));
}

/// <summary>A failed claim lowering with structured, source-addressable diagnostics.</summary>
public sealed class ClaimValidationException : ArgumentException
{
    /// <summary>Creates a failed-lowering exception.</summary>
    /// <param name="validation">Diagnostics that prevented canonical materialization.</param>
    /// <exception cref="ArgumentNullException">Validation is null.</exception>
    public ClaimValidationException(DocumentValidationResult validation)
        : base(string.Join("; ", (validation ?? throw new ArgumentNullException(nameof(validation)))
            .Diagnostics.Select(x => $"{x.Code} at {x.Location}: {x.Message}"))) => Validation = validation;
    /// <summary>Structured failure details.</summary>
    public DocumentValidationResult Validation { get; }
}
