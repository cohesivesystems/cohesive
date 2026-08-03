using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Projects retained Transition compilation and decision evidence into the shared explain contract.</summary>
public static class TransitionExecutionExplainProjector
{
    const string CompilationEvidenceKind = "transition.compilation";
    const string ObservationRequirementKind = "transition.requirement.observation";
    const string WriteRequirementKind = "transition.requirement.write";
    const string EmissionRequirementKind = "transition.requirement.emission";
    const string MachineRequirementKind = "transition.requirement.machineMovement";
    const string CapabilityRequirementKind = "transition.requirement.capability";
    const string OutcomeRequirementKind = "transition.requirement.outcome";

    /// <summary>Reference-interpreter profile used by convention when no explicit profile is supplied.</summary>
    public static ExecutionInterpreterProfileReference ReferenceInterpreterProfile { get; } = new(
        id: "cohesive.transitions.reference",
        version: "v1",
        schemaCompatibility: new([ExecutionDefinitionDocument.CurrentSchemaVersion]),
        definitionKinds: [TransitionDefinitionDocuments.Kind],
        provenance: new(
            new("Cohesive.Transitions", "v1"),
            new("Cohesive.Transitions/TransitionReferenceInterpreter"),
            DocumentOrigin.System));

    /// <summary>Projects exactly the Transition lifecycle artifacts already supplied by the caller.</summary>
    /// <param name="compilation">Target-independent Transition compilation result.</param>
    /// <param name="decision">Optional finite Transition decision to normalize without re-execution.</param>
    /// <param name="interpreter">
    /// Explicit interpreter profile, or null to use <see cref="ReferenceInterpreterProfile"/> by convention.
    /// </param>
    /// <param name="additionalEvidence">Optional realization or adapter evidence from later lifecycle stages.</param>
    /// <param name="additionalDiagnostics">Optional diagnostics from later lifecycle stages.</param>
    /// <returns>An execution explain artifact, or structured projection-affinity diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Supplied evidence or diagnostics are malformed.</exception>
    /// <exception cref="InvalidOperationException">Explain or trace content cannot be materialized.</exception>
    /// <exception cref="System.Text.Json.JsonException">Explain or trace content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain or trace content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult Project(
        TransitionCompilationResult compilation,
        TransitionDecision? decision = null,
        ExecutionInterpreterProfileReference? interpreter = null,
        ImmutableArray<ExecutionExplainEvidence> additionalEvidence = default,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        List<ExecutionExplainEvidence> evidence =
        [
            new(
                ExecutionExplainStageNames.StaticCompilation,
                CompilationEvidenceKind,
                compilation.Document.Metadata.DefinitionId.Value,
                ExecutionExplainEvidenceAuthority.Derived,
                compilation.IsSuccessful ? "Complete" : "Invalid",
                sourceReferences: [compilation.Document.Metadata.Provenance.Source.Reference])
        ];
        if (compilation.Analysis is { } analysis)
        {
            foreach (var requirement in analysis.Requirements)
                evidence.Add(ProjectRequirement(requirement));
        }
        if (!additionalEvidence.IsDefaultOrEmpty)
            evidence.AddRange(additionalEvidence);

        List<DocumentValidationDiagnostic> diagnostics = [.. compilation.Validation.Diagnostics];
        if (!additionalDiagnostics.IsDefaultOrEmpty)
            diagnostics.AddRange(additionalDiagnostics);

        NormalizedExecutionTrace? trace = null;
        if (decision is not null)
        {
            if (compilation.Plan is null)
            {
                return ExecutionExplainProjectionResult.Failure([
                    new(
                        ExecutionExplainDiagnosticCodes.TraceDefinitionMismatch,
                        DiagnosticSeverity.Error,
                        "A Transition decision cannot be explained against a compilation that produced no plan.",
                        Evidence: new(
                            stage: ExecutionExplainStageNames.ExecutionTrace,
                            subject: compilation.Document.Metadata.DefinitionId.Value,
                            sourceReferences: [compilation.Document.Metadata.Provenance.Source.Reference]))
                ]);
            }

            var traceProjection = TransitionExecutionTraceProjector.Project(compilation.Plan, decision);
            if (!traceProjection.IsSuccessful)
                return ExecutionExplainProjectionResult.Failure(traceProjection.Validation.Diagnostics);
            trace = traceProjection.Trace;
        }

        return ExecutionExplainArtifactProjector.Project(
            compilation.Document,
            interpreter ?? ReferenceInterpreterProfile,
            [.. evidence],
            trace,
            diagnostics: [.. diagnostics]);
    }

    static ExecutionExplainEvidence ProjectRequirement(TransitionSemanticRequirement requirement)
    {
        var (kind, subject, related, authority) = requirement switch
        {
            TransitionObservationRequirement observation =>
                (ObservationRequirementKind,
                    observation.Access.ToString(),
                    ImmutableArray<string>.Empty,
                    ExecutionExplainEvidenceAuthority.Derived),
            TransitionWriteRequirement write =>
                (WriteRequirementKind,
                    write.Path.ToString(),
                    ImmutableArray<string>.Empty,
                    write.IsDerived
                        ? ExecutionExplainEvidenceAuthority.Derived
                        : ExecutionExplainEvidenceAuthority.Declared),
            TransitionEmissionRequirement emission =>
                (EmissionRequirementKind,
                    DefinitionIdentity(emission.Contract),
                    ImmutableArray<string>.Empty,
                    ExecutionExplainEvidenceAuthority.Declared),
            TransitionMachineMovementRequirement movement =>
                (MachineRequirementKind,
                    $"{DefinitionIdentity(movement.Machine)}/{movement.Edge.Value}",
                    ImmutableArray<string>.Empty,
                    ExecutionExplainEvidenceAuthority.Declared),
            TransitionCapabilityRequirement capability =>
                (CapabilityRequirementKind,
                    $"{capability.Capability.Kind}:{capability.Capability.Capability.Value}",
                    ImmutableArray<string>.Empty,
                    ExecutionExplainEvidenceAuthority.Derived),
            TransitionOutcomeRequirement outcome =>
                (OutcomeRequirementKind,
                    outcome.DecisionKind.ToString(),
                    ImmutableArray<string>.Empty,
                    ExecutionExplainEvidenceAuthority.Declared),
            _ => throw new ArgumentOutOfRangeException(
                nameof(requirement),
                requirement.GetType().FullName,
                "Unsupported Transition semantic requirement.")
        };
        var occurrenceSubjects = requirement.Occurrences
            .SelectMany(static occurrence => new[] { occurrence.Node.Value, occurrence.Location })
            .Concat(related)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var sourceReferences = requirement.Occurrences
            .SelectMany(static occurrence => occurrence.SourceReferences)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            ExecutionExplainStageNames.StaticCompilation,
            kind,
            subject,
            authority,
            requirement.InvocationStrength.ToString(),
            relatedSubjects: occurrenceSubjects,
            sourceReferences: sourceReferences);
    }

    static string DefinitionIdentity(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";
}
