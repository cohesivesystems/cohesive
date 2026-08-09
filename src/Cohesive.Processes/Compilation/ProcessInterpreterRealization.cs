using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Compilation;

/// <summary>Stable identity of one Process interpreter target.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessInterpreterTargetId
{
    /// <summary>Creates an interpreter-target identity.</summary>
    /// <param name="value">Stable non-empty target identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public ProcessInterpreterTargetId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of one Process interpreter capability profile.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessInterpreterCapabilityProfileId
{
    /// <summary>Creates a capability-profile identity.</summary>
    /// <param name="value">Stable non-empty identity including a version when behavior can evolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public ProcessInterpreterCapabilityProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable profile identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one target capability assertion or supporting evidence item.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessInterpreterCapabilityEvidenceId
{
    /// <summary>Creates a capability-evidence identity.</summary>
    /// <param name="value">Stable non-empty evidence identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public ProcessInterpreterCapabilityEvidenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable evidence identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one validated operating boundary for a constrained realization.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessInterpreterOperatingBoundaryId
{
    /// <summary>Creates an operating-boundary identity.</summary>
    /// <param name="value">Stable non-empty boundary identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public ProcessInterpreterOperatingBoundaryId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable boundary identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>One target-owned assertion about how a Process requirement can be realized.</summary>
/// <remarks>
/// The constructor retains locally well-formed evidence even when its classification or evidence closure is
/// semantically invalid. <see cref="ProcessInterpreterRealizationCompiler"/> reports those profile-wide failures as
/// structured diagnostics rather than converting malformed evidence into support.
/// </remarks>
public sealed record ProcessInterpreterCapabilityEvidence
{
    /// <summary>Creates one target capability assertion.</summary>
    /// <param name="id">Stable identity and provenance anchor for the assertion.</param>
    /// <param name="requirement">Canonical construct or guarantee that the assertion addresses.</param>
    /// <param name="realization">Claimed realization classification.</param>
    /// <param name="auxiliaryEvidence">Supporting evidence required by a composed strategy.</param>
    /// <param name="operatingBoundaries">Validated boundaries required by a constrained strategy.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="auxiliaryEvidence"/> or <paramref name="operatingBoundaries"/> repeats an identity.
    /// </exception>
    [JsonConstructor]
    public ProcessInterpreterCapabilityEvidence(
        ProcessInterpreterCapabilityEvidenceId id,
        ProcessInterpreterRequirementKey requirement,
        CapabilityRealizationKind realization,
        ImmutableArray<ProcessInterpreterCapabilityEvidenceId> auxiliaryEvidence = default,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> operatingBoundaries = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A Process capability assertion requires an evidence identity.", nameof(id));
        }

        Id = id;
        Requirement = requirement;
        Realization = realization;
        AuxiliaryEvidence = ProcessInterpreterRealizationCollections.NormalizeIdentitySet(
            auxiliaryEvidence,
            static evidence => evidence.Value,
            nameof(auxiliaryEvidence));
        OperatingBoundaries = ProcessInterpreterRealizationCollections.NormalizeIdentitySet(
            operatingBoundaries,
            static boundary => boundary.Value,
            nameof(operatingBoundaries));
    }

    /// <summary>Stable identity and provenance anchor for the assertion.</summary>
    public ProcessInterpreterCapabilityEvidenceId Id { get; }

    /// <summary>Canonical construct or guarantee addressed by the assertion.</summary>
    public ProcessInterpreterRequirementKey Requirement { get; }

    /// <summary>Claimed realization classification.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Supporting evidence identities in deterministic ordinal order.</summary>
    public ImmutableArray<ProcessInterpreterCapabilityEvidenceId> AuxiliaryEvidence { get; }

    /// <summary>Validated operating-boundary identities in deterministic ordinal order.</summary>
    public ImmutableArray<ProcessInterpreterOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Compares assertions by complete normalized evidence.</summary>
    /// <param name="other">Assertion to compare.</param>
    /// <returns><see langword="true"/> when all evidence is equal.</returns>
    public bool Equals(ProcessInterpreterCapabilityEvidence? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Requirement == other.Requirement
        && Realization == other.Realization
        && AuxiliaryEvidence.SequenceEqual(other.AuxiliaryEvidence)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries);

    /// <summary>Returns a structural hash for complete normalized evidence.</summary>
    /// <returns>A hash derived from the assertion and all supporting evidence.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Requirement);
        hash.Add(Realization);
        foreach (var auxiliary in AuxiliaryEvidence)
        {
            hash.Add(auxiliary);
        }

        foreach (var boundary in OperatingBoundaries)
        {
            hash.Add(boundary);
        }

        return hash.ToHashCode();
    }

}

/// <summary>Target-neutral capability closure declared by one Process interpreter target.</summary>
public sealed class ProcessInterpreterCapabilityProfile
{
    /// <summary>Creates a Process interpreter capability profile.</summary>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="target">Stable interpretation-target identity.</param>
    /// <param name="evidence">Target capability assertions to validate and match.</param>
    /// <exception cref="ArgumentException"><paramref name="evidence"/> contains a null entry.</exception>
    [JsonConstructor]
    public ProcessInterpreterCapabilityProfile(
        ProcessInterpreterCapabilityProfileId id,
        ProcessInterpreterTargetId target,
        ImmutableArray<ProcessInterpreterCapabilityEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A Process capability profile requires a stable identity.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(target.Value))
        {
            throw new ArgumentException("A Process capability profile requires a stable target.", nameof(target));
        }

        var normalized = evidence.IsDefault ? [] : evidence;
        if (normalized.Any(static candidate => candidate is null))
        {
            throw new ArgumentException("A Process capability profile cannot contain null evidence.", nameof(evidence));
        }

        Id = id;
        Target = target;
        Evidence =
        [
            .. normalized
                .OrderBy(static candidate => candidate.Requirement.Category)
                .ThenBy(static candidate => candidate.Requirement.Name, StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Stable versioned profile identity.</summary>
    public ProcessInterpreterCapabilityProfileId Id { get; }

    /// <summary>Stable interpretation-target identity.</summary>
    public ProcessInterpreterTargetId Target { get; }

    /// <summary>Target capability assertions in deterministic order.</summary>
    public ImmutableArray<ProcessInterpreterCapabilityEvidence> Evidence { get; }
}

/// <summary>Overall outcome of matching one exact Process inventory to one target capability profile.</summary>
public enum ProcessInterpreterRealizationStatus
{
    /// <summary>Every requirement has one exact native, composed, or constrained realization.</summary>
    Realizable = 0,

    /// <summary>At least one valid requirement is explicitly or implicitly unavailable.</summary>
    NotRealizable = 1,

    /// <summary>Invalid or ambiguous evidence prevents a trustworthy realization result.</summary>
    Invalid = 2
}

/// <summary>Stable diagnostic codes emitted by target-neutral Process realization.</summary>
public static class ProcessInterpreterRealizationDiagnosticCodes
{
    /// <summary>No target assertion addresses one exact inventory requirement.</summary>
    public const string RequirementMissing = "processes.interpreter.realization.requirementMissing";

    /// <summary>The target explicitly declares one exact inventory requirement unavailable.</summary>
    public const string RequirementUnavailable = "processes.interpreter.realization.requirementUnavailable";

    /// <summary>One exact realization is valid only inside attributable operating boundaries.</summary>
    public const string RequirementConstrained = "processes.interpreter.realization.requirementConstrained";

    /// <summary>Several target assertions compete for the same requirement without a selection policy.</summary>
    public const string StrategyAmbiguous = "processes.interpreter.realization.strategyAmbiguous";

    /// <summary>A target capability assertion is unknown, duplicated, or structurally inconsistent.</summary>
    public const string CapabilityEvidenceInvalid = "processes.interpreter.realization.capabilityEvidenceInvalid";

    /// <summary>The disposition ledger does not contain exactly one decision for every inventory requirement.</summary>
    public const string LedgerCoverageMismatch = "processes.interpreter.realization.ledgerCoverageMismatch";
}

/// <summary>Structured attributable Process realization diagnostic.</summary>
public sealed record ProcessInterpreterRealizationDiagnostic
{
    /// <summary>Creates a Process realization diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="requirement">Affected requirement, or <see langword="null"/> for profile-wide evidence.</param>
    /// <param name="evidence">Affected target evidence, or <see langword="null"/>.</param>
    /// <param name="nodes">Canonical source nodes relevant to the diagnostic.</param>
    /// <param name="operatingBoundaries">Relevant constrained operating boundaries.</param>
    /// <param name="resolution">Actionable resolution guidance.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="code"/>, <paramref name="message"/>, or <paramref name="resolution"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A required string is empty or <paramref name="nodes"/> or <paramref name="operatingBoundaries"/> repeats an
    /// identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    public ProcessInterpreterRealizationDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        ProcessInterpreterRequirementKey? requirement,
        ProcessInterpreterCapabilityEvidenceId? evidence,
        ImmutableArray<ExecutionNodeId> nodes,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> operatingBoundaries,
        string resolution)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        }

        Severity = severity;
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        Requirement = requirement;
        Evidence = evidence;
        Nodes = ProcessInterpreterRealizationCollections.NormalizeIdentitySet(
            nodes,
            static node => node.Value,
            nameof(nodes));
        OperatingBoundaries = ProcessInterpreterRealizationCollections.NormalizeIdentitySet(
            operatingBoundaries,
            static boundary => boundary.Value,
            nameof(operatingBoundaries));
        Resolution = Guard.RequireNotNullOrWhiteSpace(resolution);
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>Affected requirement, or <see langword="null"/> for profile-wide evidence.</summary>
    public ProcessInterpreterRequirementKey? Requirement { get; }

    /// <summary>Affected target evidence, or <see langword="null"/>.</summary>
    public ProcessInterpreterCapabilityEvidenceId? Evidence { get; }

    /// <summary>Canonical source nodes relevant to the diagnostic.</summary>
    public ImmutableArray<ExecutionNodeId> Nodes { get; }

    /// <summary>Relevant constrained operating boundaries.</summary>
    public ImmutableArray<ProcessInterpreterOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Actionable resolution guidance.</summary>
    public string Resolution { get; }

}

/// <summary>One final target disposition for one exact Process inventory requirement.</summary>
public sealed record ProcessInterpreterRealizationDecision
{
    /// <summary>Creates one final realization decision.</summary>
    /// <param name="requirement">Exact inventory requirement receiving the decision.</param>
    /// <param name="realization">Final native, composed, constrained, or unavailable classification.</param>
    /// <param name="evidence">Selected target assertion, or null for an implicit unavailable decision.</param>
    /// <param name="auxiliaryEvidence">Supporting evidence for a composed or constrained decision.</param>
    /// <param name="operatingBoundaries">Validated boundaries for a constrained decision.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="realization"/> is unknown, override, or unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">Evidence does not agree with the selected classification.</exception>
    public ProcessInterpreterRealizationDecision(
        ProcessInterpreterRequirementKey requirement,
        CapabilityRealizationKind realization,
        ProcessInterpreterCapabilityEvidenceId? evidence = null,
        ImmutableArray<ProcessInterpreterCapabilityEvidenceId> auxiliaryEvidence = default,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> operatingBoundaries = default)
    {
        if (!Enum.IsDefined(realization)
            || realization is CapabilityRealizationKind.Unknown or CapabilityRealizationKind.Override)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realization),
                realization,
                "A Process realization decision requires a target-owned exact classification.");
        }
        if (requirement.Category == ProcessInterpreterRequirementCategory.Unknown
            || string.IsNullOrWhiteSpace(requirement.Name))
        {
            throw new ArgumentException("A Process realization decision requires an exact requirement key.", nameof(requirement));
        }

        var auxiliaries = ProcessInterpreterRealizationCollections.NormalizeIdentitySet(
            auxiliaryEvidence,
            static item => item.Value,
            nameof(auxiliaryEvidence));
        var boundaries = ProcessInterpreterRealizationCollections.NormalizeIdentitySet(
            operatingBoundaries,
            static item => item.Value,
            nameof(operatingBoundaries));
        if (realization != CapabilityRealizationKind.Unavailable && evidence is null)
        {
            throw new ArgumentException("An available realization decision requires target evidence.", nameof(evidence));
        }

        if (realization == CapabilityRealizationKind.Native && (!auxiliaries.IsDefaultOrEmpty || !boundaries.IsDefaultOrEmpty))
        {
            throw new ArgumentException("A native decision cannot claim auxiliary or boundary evidence.", nameof(realization));
        }

        if (realization == CapabilityRealizationKind.Composed && auxiliaries.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A composed decision requires auxiliary evidence.", nameof(auxiliaryEvidence));
        }

        if (realization == CapabilityRealizationKind.Composed && !boundaries.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A composed decision with operating boundaries must be constrained.", nameof(operatingBoundaries));
        }

        if (realization == CapabilityRealizationKind.Constrained && boundaries.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A constrained decision requires an operating boundary.", nameof(operatingBoundaries));
        }

        if (realization == CapabilityRealizationKind.Unavailable
            && (!auxiliaries.IsDefaultOrEmpty || !boundaries.IsDefaultOrEmpty))
        {
            throw new ArgumentException("An unavailable decision cannot claim support evidence.", nameof(realization));
        }

        Requirement = requirement;
        Realization = realization;
        Evidence = evidence;
        AuxiliaryEvidence = auxiliaries;
        OperatingBoundaries = boundaries;
    }

    /// <summary>Exact inventory requirement receiving the decision.</summary>
    public ProcessInterpreterRequirementKey Requirement { get; }

    /// <summary>Final realization classification.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Selected target assertion, or null for an implicit unavailable decision.</summary>
    public ProcessInterpreterCapabilityEvidenceId? Evidence { get; }

    /// <summary>Supporting evidence for a composed or constrained decision.</summary>
    public ImmutableArray<ProcessInterpreterCapabilityEvidenceId> AuxiliaryEvidence { get; }

    /// <summary>Validated boundaries for a constrained decision.</summary>
    public ImmutableArray<ProcessInterpreterOperatingBoundaryId> OperatingBoundaries { get; }
}

/// <summary>Complete target-neutral disposition ledger for one exact Process inventory and target profile.</summary>
public sealed class ProcessInterpreterRealizationReport
{
    internal ProcessInterpreterRealizationReport(
        ProcessInterpreterRequirementInventory inventory,
        ProcessInterpreterCapabilityProfile targetProfile,
        ImmutableArray<ProcessInterpreterRealizationDecision> decisions,
        ImmutableArray<ProcessInterpreterRealizationDiagnostic> diagnostics,
        ProcessInterpreterRealizationStatus status)
    {
        Inventory = inventory;
        TargetProfile = targetProfile;
        Decisions = decisions;
        Diagnostics = diagnostics;
        Status = status;
    }

    /// <summary>Exact complete requirement inventory consumed by matching.</summary>
    public ProcessInterpreterRequirementInventory Inventory { get; }

    /// <summary>Exact target capability profile consumed by matching.</summary>
    public ProcessInterpreterCapabilityProfile TargetProfile { get; }

    /// <summary>Exactly one final decision for every inventory requirement.</summary>
    public ImmutableArray<ProcessInterpreterRealizationDecision> Decisions { get; }

    /// <summary>Structured profile, matching, constraint, and coverage diagnostics.</summary>
    public ImmutableArray<ProcessInterpreterRealizationDiagnostic> Diagnostics { get; }

    /// <summary>Overall realization outcome.</summary>
    public ProcessInterpreterRealizationStatus Status { get; }

    /// <summary>Whether every requirement has one exact permitted realization.</summary>
    public bool IsRealizable => Status == ProcessInterpreterRealizationStatus.Realizable;
}

/// <summary>Validates the exact one-to-one coverage invariant of a Process realization disposition ledger.</summary>
public static class ProcessInterpreterRealizationLedger
{
    /// <summary>Reports missing, duplicated, and extra decisions relative to an exact requirement inventory.</summary>
    /// <param name="inventory">Complete compiler-acquired requirement inventory.</param>
    /// <param name="decisions">Candidate realization decisions.</param>
    /// <returns>Deterministically ordered coverage diagnostics; empty only for exact one-to-one coverage.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inventory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="decisions"/> contains a null entry.</exception>
    public static ImmutableArray<ProcessInterpreterRealizationDiagnostic> ValidateCoverage(
        ProcessInterpreterRequirementInventory inventory,
        ImmutableArray<ProcessInterpreterRealizationDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var normalized = decisions.IsDefault ? [] : decisions;
        if (normalized.Any(static decision => decision is null))
        {
            throw new ArgumentException("A Process realization ledger cannot contain null decisions.", nameof(decisions));
        }

        var requirements = inventory.Requirements.ToDictionary(static requirement => requirement.Key);
        var groupedDecisions = normalized.GroupBy(static decision => decision.Requirement).ToDictionary(static group => group.Key);
        List<ProcessInterpreterRealizationDiagnostic> diagnostics = [];
        foreach (var requirement in inventory.Requirements)
        {
            if (!groupedDecisions.TryGetValue(requirement.Key, out var group))
            {
                diagnostics.Add(CoverageDiagnostic(
                    requirement.Key,
                    requirement.Nodes,
                    $"The realization ledger omits required inventory item '{requirement.Key}'."));
            }
            else if (group.Count() != 1)
            {
                diagnostics.Add(CoverageDiagnostic(
                    requirement.Key,
                    requirement.Nodes,
                    $"The realization ledger contains {group.Count()} decisions for '{requirement.Key}'; exactly one is required."));
            }
        }

        foreach (var extra in groupedDecisions.Keys.Where(key => !requirements.ContainsKey(key)))
        {
            diagnostics.Add(CoverageDiagnostic(
                extra,
                [],
                $"The realization ledger contains decision '{extra}' which is absent from the source inventory."));
        }

        return
        [
            .. diagnostics
                .OrderBy(static diagnostic => diagnostic.Requirement?.Category)
                .ThenBy(static diagnostic => diagnostic.Requirement?.Name, StringComparer.Ordinal)
        ];
    }

    static ProcessInterpreterRealizationDiagnostic CoverageDiagnostic(
        ProcessInterpreterRequirementKey requirement,
        ImmutableArray<ExecutionNodeId> nodes,
        string message) => new(
            ProcessInterpreterRealizationDiagnosticCodes.LedgerCoverageMismatch,
            DiagnosticSeverity.Error,
            message,
            requirement,
            evidence: null,
            nodes,
            operatingBoundaries: [],
            "Regenerate the ledger from the exact requirement inventory before target-specific planning or execution.");
}

/// <summary>Matches an exact canonical Process plan to a target-neutral interpreter capability profile.</summary>
public static class ProcessInterpreterRealizationCompiler
{
    /// <summary>Acquires requirements, validates target evidence, and produces an exhaustive disposition ledger.</summary>
    /// <param name="plan">Successfully compiled exact canonical Process plan.</param>
    /// <param name="targetProfile">Target capability assertions to validate and match.</param>
    /// <returns>A complete realization report, including an unavailable decision for every unmatched requirement.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="targetProfile"/> is <see langword="null"/>.
    /// </exception>
    public static ProcessInterpreterRealizationReport Compile(
        CompiledProcessPlan plan,
        ProcessInterpreterCapabilityProfile targetProfile)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targetProfile);

        var inventory = ProcessInterpreterRequirementCollector.Collect(plan);
        List<ProcessInterpreterRealizationDiagnostic> diagnostics = [];
        HashSet<ProcessInterpreterCapabilityEvidenceId> invalidEvidence = [];
        ValidateProfile(targetProfile, diagnostics, invalidEvidence);

        var assertions = targetProfile.Evidence
            .GroupBy(static evidence => evidence.Requirement)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        var decisions = ImmutableArray.CreateBuilder<ProcessInterpreterRealizationDecision>(inventory.Requirements.Length);
        foreach (var requirement in inventory.Requirements)
        {
            if (!assertions.TryGetValue(requirement.Key, out var candidates))
            {
                decisions.Add(new(requirement.Key, CapabilityRealizationKind.Unavailable));
                diagnostics.Add(new(
                    ProcessInterpreterRealizationDiagnosticCodes.RequirementMissing,
                    DiagnosticSeverity.Error,
                    $"Target profile '{targetProfile.Id.Value}' does not declare '{requirement.Key}'.",
                    requirement.Key,
                    evidence: null,
                    requirement.Nodes,
                    operatingBoundaries: [],
                    "Declare an exact target disposition, including Unavailable when the target cannot preserve it."));
                continue;
            }

            if (candidates.Length != 1)
            {
                decisions.Add(new(requirement.Key, CapabilityRealizationKind.Unavailable));
                continue;
            }

            var candidate = candidates[0];
            if (invalidEvidence.Contains(candidate.Id))
            {
                decisions.Add(new(requirement.Key, CapabilityRealizationKind.Unavailable));
                continue;
            }

            decisions.Add(new(
                requirement.Key,
                candidate.Realization,
                candidate.Id,
                candidate.AuxiliaryEvidence,
                candidate.OperatingBoundaries));
            if (candidate.Realization == CapabilityRealizationKind.Unavailable)
            {
                diagnostics.Add(new(
                    ProcessInterpreterRealizationDiagnosticCodes.RequirementUnavailable,
                    DiagnosticSeverity.Error,
                    $"Target '{targetProfile.Target.Value}' cannot preserve '{requirement.Key}'.",
                    requirement.Key,
                    candidate.Id,
                    requirement.Nodes,
                    operatingBoundaries: [],
                    "Select a capable target, add attributable exact support, or remove the semantic demand."));
            }
            else if (candidate.Realization == CapabilityRealizationKind.Constrained)
            {
                diagnostics.Add(new(
                    ProcessInterpreterRealizationDiagnosticCodes.RequirementConstrained,
                    DiagnosticSeverity.Warning,
                    $"Target '{targetProfile.Target.Value}' preserves '{requirement.Key}' only inside declared boundaries.",
                    requirement.Key,
                    candidate.Id,
                    requirement.Nodes,
                    candidate.OperatingBoundaries,
                    "Validate every named operating boundary before admitting execution."));
            }
        }

        var normalizedDecisions = decisions.MoveToImmutable();
        diagnostics.AddRange(ProcessInterpreterRealizationLedger.ValidateCoverage(inventory, normalizedDecisions));
        var normalizedDiagnostics = diagnostics
            .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Requirement?.Category)
            .ThenBy(static diagnostic => diagnostic.Requirement?.Name, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Evidence?.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        var invalid = normalizedDiagnostics.Any(static diagnostic =>
            diagnostic.Code is ProcessInterpreterRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                or ProcessInterpreterRealizationDiagnosticCodes.StrategyAmbiguous
                or ProcessInterpreterRealizationDiagnosticCodes.LedgerCoverageMismatch);
        var status = invalid
            ? ProcessInterpreterRealizationStatus.Invalid
            : normalizedDecisions.Any(static decision => decision.Realization == CapabilityRealizationKind.Unavailable)
                ? ProcessInterpreterRealizationStatus.NotRealizable
                : ProcessInterpreterRealizationStatus.Realizable;
        return new(inventory, targetProfile, normalizedDecisions, normalizedDiagnostics, status);
    }

    static void ValidateProfile(
        ProcessInterpreterCapabilityProfile profile,
        ICollection<ProcessInterpreterRealizationDiagnostic> diagnostics,
        ISet<ProcessInterpreterCapabilityEvidenceId> invalidEvidence)
    {
        foreach (var duplicateId in profile.Evidence
                     .GroupBy(static evidence => evidence.Id)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var evidence in duplicateId)
            {
                invalidEvidence.Add(evidence.Id);
            }

            diagnostics.Add(InvalidEvidence(
                duplicateId.First(),
                $"Capability evidence identity '{duplicateId.Key.Value}' is declared {duplicateId.Count()} times."));
        }

        foreach (var ambiguous in profile.Evidence
                     .GroupBy(static evidence => evidence.Requirement)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var evidence in ambiguous)
            {
                invalidEvidence.Add(evidence.Id);
            }

            diagnostics.Add(new(
                ProcessInterpreterRealizationDiagnosticCodes.StrategyAmbiguous,
                DiagnosticSeverity.Error,
                $"Target profile '{profile.Id.Value}' declares {ambiguous.Count()} strategies for '{ambiguous.Key}'.",
                ambiguous.Key,
                evidence: null,
                nodes: [],
                operatingBoundaries: [],
                "Publish one effective disposition per requirement or add an explicit deterministic selection policy."));
        }

        foreach (var evidence in profile.Evidence)
        {
            string? error = ValidateEvidence(evidence);
            if (error is null)
            {
                continue;
            }

            invalidEvidence.Add(evidence.Id);
            diagnostics.Add(InvalidEvidence(evidence, error));
        }
    }

    static string? ValidateEvidence(ProcessInterpreterCapabilityEvidence evidence)
    {
        var knownRequirement = evidence.Requirement.Category switch
        {
            ProcessInterpreterRequirementCategory.Construct => ProcessNodeConstructCatalog.Contains(evidence.Requirement),
            ProcessInterpreterRequirementCategory.Guarantee => ProcessInterpreterGuarantees.Contains(evidence.Requirement),
            _ => false
        };
        if (!knownRequirement)
        {
            return $"Capability evidence '{evidence.Id.Value}' names unknown requirement '{evidence.Requirement}'.";
        }

        if (!Enum.IsDefined(evidence.Realization)
            || evidence.Realization is CapabilityRealizationKind.Unknown or CapabilityRealizationKind.Override)
        {
            return $"Capability evidence '{evidence.Id.Value}' uses target-invalid classification '{evidence.Realization}'.";
        }

        return evidence.Realization switch
        {
            CapabilityRealizationKind.Native when !evidence.AuxiliaryEvidence.IsDefaultOrEmpty
                                                  || !evidence.OperatingBoundaries.IsDefaultOrEmpty =>
                "Native capability evidence cannot claim auxiliary evidence or operating boundaries.",
            CapabilityRealizationKind.Composed when evidence.AuxiliaryEvidence.IsDefaultOrEmpty =>
                "Composed capability evidence requires at least one supporting evidence identity.",
            CapabilityRealizationKind.Composed when !evidence.OperatingBoundaries.IsDefaultOrEmpty =>
                "A composed strategy with operating boundaries must be classified as constrained.",
            CapabilityRealizationKind.Constrained when evidence.OperatingBoundaries.IsDefaultOrEmpty =>
                "Constrained capability evidence requires at least one operating boundary.",
            CapabilityRealizationKind.Unavailable when !evidence.AuxiliaryEvidence.IsDefaultOrEmpty
                                                       || !evidence.OperatingBoundaries.IsDefaultOrEmpty =>
                "Unavailable capability evidence cannot claim supporting evidence or operating boundaries.",
            _ => null
        };
    }

    static ProcessInterpreterRealizationDiagnostic InvalidEvidence(
        ProcessInterpreterCapabilityEvidence evidence,
        string message) => new(
            ProcessInterpreterRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
            DiagnosticSeverity.Error,
            message,
            evidence.Requirement,
            evidence.Id,
            nodes: [],
            evidence.OperatingBoundaries,
            "Correct the target profile evidence before using it for planning or execution admission.");
}

static class ProcessInterpreterRealizationCollections
{
    internal static ImmutableArray<T> NormalizeIdentitySet<T>(
        ImmutableArray<T> values,
        Func<T, string> key,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (var value in normalized)
        {
            if (!observed.Add(key(value)))
            {
                throw new ArgumentException("Interpreter realization evidence cannot repeat an identity.", parameterName);
            }
        }

        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }
}
