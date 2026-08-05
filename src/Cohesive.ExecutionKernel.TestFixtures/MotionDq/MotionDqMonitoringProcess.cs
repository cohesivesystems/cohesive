using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using AuthoredMonitoringProcess = Cohesive.Processes.Authoring.Process<
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqMonitoringCaseReference,
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqMonitoringOutcome>;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Finite terminal outcomes of the Motion DQ monitoring Process fixture.</summary>
public enum MotionDqMonitoringOutcome
{
    /// <summary>The authoritative evidence query cleared the monitored subject.</summary>
    Cleared,

    /// <summary>The authoritative evidence query required escalation.</summary>
    Escalated,

    /// <summary>The monitoring authority or an endogenous signal cancelled the case.</summary>
    Cancelled,

    /// <summary>A newer monitoring case superseded this process.</summary>
    Superseded,

    /// <summary>The finite occurrence budget was exhausted.</summary>
    OccurrenceLimitReached,

    /// <summary>The authoritative evidence revision stopped advancing.</summary>
    EvidenceStalled,

    /// <summary>External human-work creation failed terminally.</summary>
    InterventionSchedulingFailed,

    /// <summary>An observation or interaction violated the declared coordination contract.</summary>
    CoordinationRejected
}

/// <summary>
/// Canonical Motion DQ monitoring Process built from exact query, durable interaction, wait, and recurrence semantics.
/// </summary>
/// <remarks>
/// The Process retains only its case reference, exact interaction references, and recurrence progress. Telematics
/// events, evidence history, human-work lifecycle, and monitoring-case state remain authoritative in their owning
/// modules. Every recurrence pass crosses <see cref="RepeatAcrossActivationProcessNode"/> and is therefore finite and
/// durable; the graph contains no free cycle, host polling loop, or ambient clock access.
/// </remarks>
public sealed class MotionDqMonitoringProcess
{
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();
    internal static readonly ExecutionDefinitionReference ObservationQueryReference = new(
        new("relation/motion-dq/monitoring-observation"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('8', 64)));

    MotionDqMonitoringProcess(
        AuthoredMonitoringProcess authored,
        ProcessDefinitionValidationContext linkingContext,
        ProcessCompilationResult compilation,
        MotionDqMonitoringInteractionContracts interactions,
        ProcessDefinitionLink observationQuery,
        ImmutableArray<ExecutionDefinitionDocument> documents)
    {
        Authored = authored;
        LinkingContext = linkingContext;
        Compilation = compilation;
        Plan = compilation.Plan
            ?? throw new ArgumentException("A Motion DQ monitoring artifact requires a compiled plan.", nameof(compilation));
        Interactions = interactions;
        ObservationQuery = observationQuery;
        Documents = documents;
    }

    /// <summary>Canonical version-one Motion DQ monitoring Process and exact dependencies.</summary>
    public static MotionDqMonitoringProcess Version1 { get; } = AuthorVersion1();

    /// <summary>Authors and links a fresh deterministic version-one fixture.</summary>
    /// <returns>A fresh artifact graph with the same normalized semantic fingerprint as <see cref="Version1"/>.</returns>
    /// <exception cref="InvalidOperationException">Authoring or linked Process compilation is invalid.</exception>
    public static MotionDqMonitoringProcess AuthorVersion1() => CreateVersion1();

    /// <summary>Typed authored Process whose canonical document is the semantic authority.</summary>
    public AuthoredMonitoringProcess Authored { get; }

    /// <summary>Canonical persisted Process document.</summary>
    public ExecutionDefinitionDocument Document => Authored.Document;

    /// <summary>Typed canonical Process IR projected from <see cref="Document"/>.</summary>
    public CanonicalProcessDefinition Definition => Plan.Definition;

    /// <summary>Exact fingerprinted Process reference.</summary>
    public ExecutionDefinitionReference Reference => Authored.Reference;

    /// <summary>Exact query and interaction evidence used for linked validation.</summary>
    public ProcessDefinitionValidationContext LinkingContext { get; }

    /// <summary>Target-independent compilation result.</summary>
    public ProcessCompilationResult Compilation { get; }

    /// <summary>Executable reference plan shared by reference and durable interpretations.</summary>
    public CompiledProcessPlan Plan { get; }

    /// <summary>Exact monitoring interaction contracts.</summary>
    public MotionDqMonitoringInteractionContracts Interactions { get; }

    /// <summary>Exact Relation/Query link that projects authoritative monitoring observations.</summary>
    public ProcessDefinitionLink ObservationQuery { get; }

    /// <summary>Canonical interaction documents followed by the Process document.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>Durable binding for external intervention-work creation.</summary>
    public DurableRequestBinding ScheduleInterventionBinding => Interactions.ScheduleInterventionBinding;

    static MotionDqMonitoringProcess CreateVersion1()
    {
        var interactions = MotionDqMonitoringInteractionContracts.Version1;
        var observationQuery = new ProcessDefinitionLink(
            definition: ObservationQueryReference,
            kind: ProcessDefinitionLinkKind.RelationQuery,
            input: Contract<MotionDqMonitoringCaseReference>(),
            result: Contract<MotionDqMonitoringObservation>());
        var linkingContext = new ProcessDefinitionValidationContext(
            definitions: [observationQuery],
            interactionContracts: interactions.Catalog);
        var authored = Author();
        RequireValid(authored.Validation, "context-free Process authoring");
        var compilation = authored.Compile(linkingContext);
        RequireValid(compilation.Validation, "linked Process compilation");
        if (compilation.Plan is null)
        {
            throw new InvalidOperationException("Linked Motion DQ monitoring compilation produced no reference plan.");
        }

        ImmutableArray<ExecutionDefinitionDocument> documents =
        [
            .. interactions.Documents,
            authored.Document
        ];
        return new(
            authored: authored,
            linkingContext: linkingContext,
            compilation: compilation,
            interactions: interactions,
            observationQuery: observationQuery,
            documents: documents);
    }

    static AuthoredMonitoringProcess Author() =>
        MotionDqMonitoringProcessDefinition.Define(
            new(
                new("process/motion-dq/monitoring"),
                new("revision/1"),
                Identities.EvaluateObservation,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance(),
                displayName: "Motion DQ monitoring and escalation",
                description: "Coordinates bounded evidence evaluation, human intervention, and durable recurrence."));

    static ValueContract Contract<TValue>() => new(TypeMapper.Map(typeof(TValue), null));

    static ExecutionProvenance Provenance() => new(
        new("cohesive-motion-dq-fixture", "1"),
        new("ari-182/motion-dq-monitoring"),
        DocumentOrigin.User);

    static void RequireValid(DocumentValidationResult validation, string stage)
    {
        if (validation.IsValid)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Motion DQ monitoring {stage} failed: {string.Join(
                "; ",
                validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"))}");
    }

    internal static class Identities
    {
        public static readonly ExecutionNodeId EvaluateObservation = new("motion-dq/monitoring/evaluate-observation");
        public static readonly ExecutionNodeId ClassifyObservation = new("motion-dq/monitoring/classify-observation");
        public static readonly ExecutionNodeId ScheduleIntervention = new("motion-dq/monitoring/schedule-intervention");
        public static readonly ExecutionNodeId AwaitIntervention = new("motion-dq/monitoring/await-intervention");
        public static readonly ExecutionNodeId Repeat = new("motion-dq/monitoring/repeat-across-activation");
        public static readonly ExecutionNodeId ReturnDisposition = new("motion-dq/monitoring/return-disposition");
        public static readonly ExecutionNodeId Cleared = new("motion-dq/monitoring/terminal/cleared");
        public static readonly ExecutionNodeId Escalated = new("motion-dq/monitoring/terminal/escalated");
        public static readonly ExecutionNodeId Cancelled = new("motion-dq/monitoring/terminal/cancelled-by-input");
        public static readonly ExecutionNodeId Superseded = new("motion-dq/monitoring/terminal/superseded-by-input");
        public static readonly ExecutionNodeId OccurrenceLimitReached = new("motion-dq/monitoring/terminal/occurrence-limit");
        public static readonly ExecutionNodeId EvidenceStalled = new("motion-dq/monitoring/terminal/evidence-stalled");
        public static readonly ExecutionNodeId InterventionSchedulingFailed =
            new("motion-dq/monitoring/terminal/intervention-scheduling-failed");
        public static readonly ExecutionNodeId CoordinationRejected =
            new("motion-dq/monitoring/terminal/coordination-rejected");
        public static readonly ExecutionNodeId ObservationContinue =
            new("motion-dq/monitoring/observation/continue");
        public static readonly ExecutionNodeId ObservationCleared =
            new("motion-dq/monitoring/observation/cleared");
        public static readonly ExecutionNodeId ObservationEscalated =
            new("motion-dq/monitoring/observation/escalated");
        public static readonly ExecutionNodeId ObservationCancelled =
            new("motion-dq/monitoring/observation/cancelled");
        public static readonly ExecutionNodeId ObservationSuperseded =
            new("motion-dq/monitoring/observation/superseded");
        public static readonly ExecutionNodeId ObservationInvalid =
            new("motion-dq/monitoring/observation/invalid");
        public static readonly ExecutionNodeId InterventionScheduled =
            new("motion-dq/monitoring/intervention/scheduled");
        public static readonly ExecutionNodeId InterventionScheduleFailed =
            new("motion-dq/monitoring/intervention/failed");
        public static readonly ExecutionNodeId InterventionCancelled =
            new("motion-dq/monitoring/intervention/cancelled");
        public static readonly ExecutionNodeId InterventionSuperseded =
            new("motion-dq/monitoring/intervention/superseded");
        public static readonly ExecutionNodeId InterventionCompleted =
            new("motion-dq/monitoring/intervention/completed");
        public static readonly ExecutionNodeId InterventionEvaluationDue =
            new("motion-dq/monitoring/intervention/evaluation-due");
        public static readonly ExecutionNodeId TerminalCleared =
            new("motion-dq/monitoring/terminal-disposition/cleared");
        public static readonly ExecutionNodeId TerminalEscalated =
            new("motion-dq/monitoring/terminal-disposition/escalated");
        public static readonly ExecutionNodeId TerminalCancelled =
            new("motion-dq/monitoring/terminal-disposition/cancelled");
        public static readonly ExecutionNodeId TerminalSuperseded =
            new("motion-dq/monitoring/terminal-disposition/superseded");
        public static readonly ExecutionNodeId TerminalInvalid =
            new("motion-dq/monitoring/terminal-disposition/invalid");
    }
}
