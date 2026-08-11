using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Closed evaluated algebra for one sparse Transition patch operation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$operation")]
[JsonDerivedType(typeof(EvaluatedSetTransitionPatch), "set")]
[JsonDerivedType(typeof(EvaluatedRemoveTransitionPatch), "remove")]
[JsonDerivedType(typeof(EvaluatedIncrementTransitionPatch), "increment")]
[JsonDerivedType(typeof(EvaluatedAddToSetTransitionPatch), "addToSet")]
[JsonDerivedType(typeof(EvaluatedAppendTransitionPatch), "append")]
[JsonDerivedType(typeof(EvaluatedUpsertOwnedChildTransitionPatch), "upsertOwnedChild")]
[JsonDerivedType(typeof(EvaluatedRemoveOwnedChildTransitionPatch), "removeOwnedChild")]
public abstract record EvaluatedTransitionPatchOperation
{
    private protected EvaluatedTransitionPatchOperation()
    {
    }
}

/// <summary>Evaluated replacement value for a Set patch.</summary>
/// <param name="Value">Exact typed replacement value.</param>
public sealed record EvaluatedSetTransitionPatch(PortableValue Value) : EvaluatedTransitionPatchOperation;

/// <summary>Evaluated field-removal patch.</summary>
public sealed record EvaluatedRemoveTransitionPatch : EvaluatedTransitionPatchOperation;

/// <summary>Evaluated numeric increment operand.</summary>
/// <param name="Amount">Exact typed increment amount.</param>
public sealed record EvaluatedIncrementTransitionPatch(PortableValue Amount) : EvaluatedTransitionPatchOperation;

/// <summary>Evaluated set element to add.</summary>
/// <param name="Value">Exact typed candidate set element.</param>
public sealed record EvaluatedAddToSetTransitionPatch(PortableValue Value) : EvaluatedTransitionPatchOperation;

/// <summary>Evaluated ordered collection element to append.</summary>
/// <param name="Value">Exact typed appended element.</param>
public sealed record EvaluatedAppendTransitionPatch(PortableValue Value) : EvaluatedTransitionPatchOperation;

/// <summary>Evaluated owned-child upsert operands.</summary>
/// <param name="IdentityPath">Child-relative semantic identity path.</param>
/// <param name="Identity">Exact typed child identity.</param>
/// <param name="Value">Exact typed complete child value.</param>
public sealed record EvaluatedUpsertOwnedChildTransitionPatch(
    FieldPath IdentityPath,
    PortableValue Identity,
    PortableValue Value) : EvaluatedTransitionPatchOperation;

/// <summary>Evaluated owned-child removal operands.</summary>
/// <param name="IdentityPath">Child-relative semantic identity path.</param>
/// <param name="Identity">Exact typed child identity.</param>
public sealed record EvaluatedRemoveOwnedChildTransitionPatch(
    FieldPath IdentityPath,
    PortableValue Identity) : EvaluatedTransitionPatchOperation;

/// <summary>One executed algebraic sparse patch and its observed change evidence.</summary>
public sealed record TransitionExecutedPatch
{
    /// <summary>Creates an executed sparse patch.</summary>
    /// <param name="node">Stable authored or compiler-derived patch node.</param>
    /// <param name="path">Aggregate-relative target path.</param>
    /// <param name="operation">Evaluated algebraic operation.</param>
    /// <param name="before">Value observed before the operation.</param>
    /// <param name="after">Candidate value after the operation.</param>
    /// <exception cref="ArgumentException"><paramref name="node"/> or <paramref name="path"/> is default.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operation"/>, <paramref name="before"/>, or <paramref name="after"/> is
    /// <see langword="null"/>.
    /// </exception>
    public TransitionExecutedPatch(
        ExecutionNodeId node,
        FieldPath path,
        EvaluatedTransitionPatchOperation operation,
        PortableValue before,
        PortableValue after)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("An executed patch requires a stable node identity.", nameof(node));
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("An executed patch requires a non-empty target path.", nameof(path));
        Node = node;
        Path = path;
        Operation = Guard.RequireNotNull(operation);
        Before = Guard.RequireNotNull(before);
        After = Guard.RequireNotNull(after);
    }

    /// <summary>Stable authored or compiler-derived patch node.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Aggregate-relative target path.</summary>
    public FieldPath Path { get; }

    /// <summary>Evaluated algebraic operation.</summary>
    public EvaluatedTransitionPatchOperation Operation { get; }

    /// <summary>Value observed before the operation.</summary>
    public PortableValue Before { get; }

    /// <summary>Candidate value after the operation.</summary>
    public PortableValue After { get; }

    /// <summary>Whether the operation changed semantic value.</summary>
    public bool Changed => Before != After;
}

/// <summary>One pure interaction intent staged by a Transition decision.</summary>
public sealed record TransitionEmissionIntent
{
    /// <summary>Creates a staged emission intent.</summary>
    /// <param name="node">Stable emission identity within the originating Transition definition.</param>
    /// <param name="contract">Exact referenced interaction definition.</param>
    /// <param name="payload">Evaluated typed payload.</param>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/> or <paramref name="payload"/> is <see langword="null"/>.
    /// </exception>
    public TransitionEmissionIntent(
        ExecutionNodeId node,
        ExecutionDefinitionReference contract,
        PortableValue payload)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("An emission intent requires a stable node identity.", nameof(node));
        Node = node;
        Contract = Guard.RequireNotNull(contract);
        Payload = Guard.RequireNotNull(payload);
    }

    /// <summary>Stable emission identity within the originating Transition definition.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Exact referenced interaction definition.</summary>
    public ExecutionDefinitionReference Contract { get; }

    /// <summary>Evaluated typed payload.</summary>
    public PortableValue Payload { get; }
}

/// <summary>One actual legal movement through an exact Cohesive.Machines edge.</summary>
public sealed record TransitionMachineMovement
{
    /// <summary>Creates actual Machine movement evidence.</summary>
    /// <param name="node">Stable MoveMachine node identity.</param>
    /// <param name="machine">Exact Machine definition reference.</param>
    /// <param name="edge">Stable moved edge identity.</param>
    /// <param name="assignments">Executed edge-owned configuration assignments.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="node"/> or <paramref name="edge"/> is default, or <paramref name="assignments"/> is
    /// default or contains a null entry.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> is <see langword="null"/>.</exception>
    public TransitionMachineMovement(
        ExecutionNodeId node,
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        ImmutableArray<TransitionExecutedPatch> assignments)
    {
        Machine = Guard.RequireNotNull(machine);
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("Machine movement evidence requires a stable node identity.", nameof(node));
        if (string.IsNullOrWhiteSpace(edge.Value))
            throw new ArgumentException("Machine movement evidence requires a stable edge identity.", nameof(edge));
        if (assignments.IsDefault || assignments.Any(static assignment => assignment is null))
        {
            throw new ArgumentException(
                "Machine movement evidence requires an initialized assignment collection with no null entries.",
                nameof(assignments));
        }

        Node = node;
        Edge = edge;
        Assignments = assignments;
    }

    /// <summary>Stable MoveMachine node identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Exact Machine definition reference.</summary>
    public ExecutionDefinitionReference Machine { get; }

    /// <summary>Stable moved edge identity.</summary>
    public ExecutionNodeId Edge { get; }

    /// <summary>Executed edge-owned configuration assignments.</summary>
    public ImmutableArray<TransitionExecutedPatch> Assignments { get; }
}

/// <summary>One commit-time mismatch for an actual observation that influenced a commit decision.</summary>
public sealed record TransitionObservationConflict
{
    /// <summary>Creates observation-conflict evidence.</summary>
    /// <param name="access">Actual aggregate access whose value changed.</param>
    /// <param name="expected">Value used during semantic evaluation.</param>
    /// <param name="observed">Fresh value supplied for commit validation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="access"/>, <paramref name="expected"/>, or <paramref name="observed"/> is
    /// <see langword="null"/>.
    /// </exception>
    public TransitionObservationConflict(
        TransitionObservationAccess access,
        PortableValue expected,
        PortableValue observed)
    {
        Access = Guard.RequireNotNull(access);
        Expected = Guard.RequireNotNull(expected);
        Observed = Guard.RequireNotNull(observed);
    }

    /// <summary>Actual aggregate access whose value changed.</summary>
    public TransitionObservationAccess Access { get; }

    /// <summary>Value used during semantic evaluation.</summary>
    public PortableValue Expected { get; }

    /// <summary>Fresh value supplied for commit validation.</summary>
    public PortableValue Observed { get; }
}

/// <summary>Derived semantic commit guarantees carried by a Transition decision.</summary>
public sealed record TransitionGuaranteeDemands
{
    /// <summary>Creates derived commit demands.</summary>
    /// <param name="commitRequired">
    /// Whether the decision has authoritative state, Machine movement, or emission intent to commit.
    /// </param>
    /// <param name="atomicPatchAndEmissions">
    /// Whether retained patch and emission intents form one aggregate-local atomic commit unit.
    /// </param>
    /// <param name="concurrencyObservations">
    /// Actual evaluation observations that must remain coherent through commit.
    /// </param>
    public TransitionGuaranteeDemands(
        bool commitRequired,
        bool atomicPatchAndEmissions,
        ImmutableArray<TransitionObservationAccess> concurrencyObservations)
    {
        CommitRequired = commitRequired;
        AtomicPatchAndEmissions = atomicPatchAndEmissions;
        ConcurrencyObservations = concurrencyObservations.IsDefault ? [] : concurrencyObservations;
    }

    /// <summary>Whether the decision has authoritative state, Machine movement, or emission intent to commit.</summary>
    public bool CommitRequired { get; }

    /// <summary>Whether retained patch and emission intents form one aggregate-local atomic commit unit.</summary>
    public bool AtomicPatchAndEmissions { get; }

    /// <summary>Actual evaluation observations that must remain coherent through commit.</summary>
    public ImmutableArray<TransitionObservationAccess> ConcurrencyObservations { get; }
}

/// <summary>Stable kinds recorded by the single ordered Transition execution trace.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransitionTraceEventKind
{
    /// <summary>An aggregate observation was read.</summary>
    ObservationRead = 0,

    /// <summary>An ordered admission predicate was evaluated.</summary>
    AdmissionEvaluated = 1,

    /// <summary>A lexical Let binding was evaluated and installed.</summary>
    BindingCreated = 2,

    /// <summary>An ordered Choice case, Match case, or fallback was selected.</summary>
    CaseSelected = 3,

    /// <summary>An algebraic sparse patch was executed against candidate state.</summary>
    PatchExecuted = 4,

    /// <summary>A legal Machine edge was moved.</summary>
    MachineMoved = 5,

    /// <summary>A pure emission intent was produced.</summary>
    EmissionProduced = 6,

    /// <summary>A terminal typed outcome was returned.</summary>
    OutcomeReturned = 7,

    /// <summary>Affected computed state was recomputed.</summary>
    DerivedFieldRecomputed = 8,

    /// <summary>A post-update invariant was evaluated.</summary>
    InvariantEvaluated = 9,

    /// <summary>A complete initial aggregate observation was derived for an absent subject.</summary>
    SubjectInitialized = 10
}

/// <summary>One event in deterministic semantic execution order.</summary>
public sealed record TransitionTraceEvent
{
    internal TransitionTraceEvent(
        int sequence,
        TransitionTraceEventKind kind,
        ExecutionNodeId node,
        TransitionObservationAccess? access = null,
        FieldPath? path = null,
        ExecutionNodeId? selectedCase = null,
        PortableValue? before = null,
        PortableValue? after = null,
        bool? changed = null,
        ExecutionDefinitionReference? contract = null,
        ExecutionNodeId? edge = null,
        string? detail = null)
    {
        Sequence = sequence;
        Kind = kind;
        Node = node;
        Access = access;
        Path = path;
        SelectedCase = selectedCase;
        Before = before;
        After = after;
        Changed = changed;
        Contract = contract;
        Edge = edge;
        Detail = detail;
    }

    /// <summary>Zero-based event sequence within the activation.</summary>
    public int Sequence { get; }

    /// <summary>Semantic event kind.</summary>
    public TransitionTraceEventKind Kind { get; }

    /// <summary>Owning Transition rule or node identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Observation access for <see cref="TransitionTraceEventKind.ObservationRead"/>.</summary>
    public TransitionObservationAccess? Access { get; }

    /// <summary>Written path for patch and derived-field events.</summary>
    public FieldPath? Path { get; }

    /// <summary>Selected case or fallback identity for branch events.</summary>
    public ExecutionNodeId? SelectedCase { get; }

    /// <summary>Value before an executed write.</summary>
    public PortableValue? Before { get; }

    /// <summary>Value after an executed write.</summary>
    public PortableValue? After { get; }

    /// <summary>Whether an executed write changed semantic value.</summary>
    public bool? Changed { get; }

    /// <summary>Referenced interaction or Machine definition for emission and movement events.</summary>
    public ExecutionDefinitionReference? Contract { get; }

    /// <summary>Moved Machine edge identity for movement events.</summary>
    public ExecutionNodeId? Edge { get; }

    /// <summary>Stable explanatory detail for predicate and outcome events.</summary>
    public string? Detail { get; }
}

/// <summary>Ordered, provenance-pinned evidence for one Transition activation.</summary>
public sealed class TransitionExecutionEvidence
{
    /// <summary>Creates execution evidence from its authoritative ordered trace.</summary>
    /// <param name="definition">Exact originating Transition definition.</param>
    /// <param name="activation">Stable activation identity.</param>
    /// <param name="trace">Complete ordered semantic trace.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="activation"/> is default, or <paramref name="trace"/> is default or contains a null
    /// entry.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public TransitionExecutionEvidence(
        ExecutionDefinitionReference definition,
        ActivationId activation,
        ImmutableArray<TransitionTraceEvent> trace)
    {
        Definition = Guard.RequireNotNull(definition);
        if (string.IsNullOrWhiteSpace(activation.Value))
        {
            throw new ArgumentException(
                "Transition execution evidence requires a stable activation identity.",
                nameof(activation));
        }
        if (trace.IsDefault || trace.Any(static item => item is null))
        {
            throw new ArgumentException(
                "Transition execution evidence requires an initialized trace with no null entries.",
                nameof(trace));
        }

        Activation = activation;
        Trace = trace;
    }

    /// <summary>Exact originating Transition definition.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Stable activation identity.</summary>
    public ActivationId Activation { get; }

    /// <summary>Complete ordered semantic trace, the authority for all evidence projections.</summary>
    public ImmutableArray<TransitionTraceEvent> Trace { get; }

    /// <summary>Actual aggregate observations in first-read order.</summary>
    public ImmutableArray<TransitionObservationAccess> ActualReads =>
        Distinct(
            Trace.Where(static item => item.Kind == TransitionTraceEventKind.ObservationRead)
                .Select(static item => item.Access)
                .OfType<TransitionObservationAccess>());

    /// <summary>Every executed authored, Machine-derived, or computed write in execution order.</summary>
    public ImmutableArray<FieldPath> ExecutedWrites =>
        [.. Trace.Where(static item => item.Path is not null)
            .Select(static item => item.Path!.Value)];

    /// <summary>Semantically changed aggregate paths in first-change order.</summary>
    public ImmutableArray<FieldPath> ChangedPaths =>
        Distinct(
            Trace.Where(static item => item.Path is not null && item.Changed == true)
                .Select(static item => item.Path!.Value));

    /// <summary>Selected Choice, Match, and fallback identities in execution order.</summary>
    public ImmutableArray<ExecutionNodeId> SelectedCases =>
        [.. Trace.Where(static item => item.Kind == TransitionTraceEventKind.CaseSelected)
            .Select(static item => item.SelectedCase)
            .OfType<ExecutionNodeId>()];

    /// <summary>Produced emission-node identities in execution order.</summary>
    public ImmutableArray<ExecutionNodeId> EmittedIntents =>
        [.. Trace.Where(static item => item.Kind == TransitionTraceEventKind.EmissionProduced)
            .Select(static item => item.Node)];

    /// <summary>
    /// Complete initial aggregate observation retained by absent-subject creation, otherwise <see langword="null"/>.
    /// </summary>
    public PortableValue? InitialObservation => Trace
        .FirstOrDefault(static item => item.Kind == TransitionTraceEventKind.SubjectInitialized)
        ?.After;

    static ImmutableArray<T> Distinct<T>(IEnumerable<T> values)
        where T : notnull
    {
        HashSet<T> seen = [];
        ImmutableArray<T>.Builder result = ImmutableArray.CreateBuilder<T>();
        foreach (var value in values)
        {
            if (seen.Add(value))
                result.Add(value);
        }

        return result.ToImmutable();
    }
}

/// <summary>Complete non-committing result of one deterministic Transition activation.</summary>
public sealed class TransitionDecision
{
    internal TransitionDecision(
        TransitionDecisionKind kind,
        PortableValue? outcome,
        ImmutableArray<TransitionExecutedPatch> patch,
        ImmutableArray<TransitionEmissionIntent> emissions,
        ImmutableArray<TransitionMachineMovement> machineMovements,
        TransitionGuaranteeDemands guaranteeDemands,
        ImmutableArray<TransitionObservationConflict> conflicts,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        TransitionExecutionEvidence evidence)
    {
        Kind = kind;
        Outcome = outcome;
        Patch = patch.IsDefault ? [] : patch;
        Emissions = emissions.IsDefault ? [] : emissions;
        MachineMovements = machineMovements.IsDefault ? [] : machineMovements;
        GuaranteeDemands = Guard.RequireNotNull(guaranteeDemands);
        Conflicts = conflicts.IsDefault ? [] : conflicts;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        Evidence = Guard.RequireNotNull(evidence);
    }

    /// <summary>Terminal semantic decision category.</summary>
    public TransitionDecisionKind Kind { get; }

    /// <summary>Typed authored outcome when the terminal category carries one.</summary>
    public PortableValue? Outcome { get; }

    /// <summary>Committable algebraic sparse patch; empty for rejected, conflicting, or invalid decisions.</summary>
    public ImmutableArray<TransitionExecutedPatch> Patch { get; }

    /// <summary>Committable pure emission intents; empty for conflicts and invalid decisions.</summary>
    public ImmutableArray<TransitionEmissionIntent> Emissions { get; }

    /// <summary>Actual legal Machine movements retained by an accepted decision.</summary>
    public ImmutableArray<TransitionMachineMovement> MachineMovements { get; }

    /// <summary>Derived semantic commit and freshness demands.</summary>
    public TransitionGuaranteeDemands GuaranteeDemands { get; }

    /// <summary>Commit-time observation mismatches for a Conflict decision.</summary>
    public ImmutableArray<TransitionObservationConflict> Conflicts { get; }

    /// <summary>Structured execution diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Complete actual execution evidence.</summary>
    public TransitionExecutionEvidence Evidence { get; }
}
