using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Machines;

/// <summary>Analyzes a closed registration set without executing reactions or interpreting guards.</summary>
/// <remarks>Each policy already validates its embedded prerequisite's source and identity contract.
/// Exact prerequisites form finite embedded documents, so exact dependency cycles cannot currently be authored.
/// Repeating a logical definition across pinned revisions is reported separately from a deadlock.</remarks>
public static class MachineReactionAnalysis
{
    /// <summary>An exact policy is registered more than once.</summary>
    public const string DuplicateRegistration = "machine.reaction.analysis.duplicate";
    /// <summary>Distinct policies would route recovery through the same exact Process definition.</summary>
    public const string AmbiguousProcess = "machine.reaction.analysis.processAmbiguous";
    /// <summary>An exact prerequisite is absent from the registration set.</summary>
    public const string MissingPrerequisite = "machine.reaction.analysis.prerequisiteMissing";
    /// <summary>A logical definition recurs across revisions in a finite prerequisite chain.</summary>
    public const string RecurringDefinition = "machine.reaction.analysis.definitionRecurs";

    /// <summary>Checks registration uniqueness, exact prerequisite closure and logical recurrence.</summary>
    /// <param name="reactions">Complete policies for one authority; declaration order is non-semantic.</param>
    /// <returns>Deterministically ordered diagnostics. Validity does not prove guard satisfiability or eventual completion.</returns>
    /// <exception cref="ArgumentNullException">The collection or a policy is null.</exception>
    public static DocumentValidationResult Analyze(IEnumerable<MachineReaction> reactions)
    {
        ArgumentNullException.ThrowIfNull(reactions);
        var policies = reactions.ToArray();
        Dictionary<ExecutionDefinitionReference, MachineReaction> registered = [];
        HashSet<ExecutionDefinitionReference> processes = [];
        List<DocumentValidationDiagnostic> diagnostics = [];
        foreach (var policy in policies) ArgumentNullException.ThrowIfNull(policy);
        foreach (var policy in policies.OrderBy(p => p.Document.Metadata.DefinitionId.Value, StringComparer.Ordinal)
                     .ThenBy(p => p.Document.Metadata.RevisionId.Value, StringComparer.Ordinal)
                     .ThenBy(p => p.Document.Metadata.Fingerprint.Value, StringComparer.Ordinal))
        {
            if (!registered.TryAdd(policy.Document.Reference, policy))
                Add(DuplicateRegistration, DiagnosticSeverity.Error, policy,
                    "Register each exact reaction revision once.");
            else if (!processes.Add(policy.Payload.Process.Reference))
                Add(AmbiguousProcess, DiagnosticSeverity.Error, policy,
                    "Distinct reaction policies share an exact Process route. Give independently registered policies distinct identities.");
        }
        foreach (var policy in registered.Values)
        {
            if (policy.Payload.Prerequisite is { } prerequisite && !registered.ContainsKey(prerequisite.Reference))
                Add(MissingPrerequisite, DiagnosticSeverity.Error, policy,
                    $"Register the exact prerequisite '{prerequisite.Reference}'; a different revision or identity is not a substitute.");
            HashSet<ExecutionDefinitionId> seen = [policy.Document.Metadata.DefinitionId];
            var ancestor = policy.Payload.Prerequisite;
            while (ancestor is not null)
            {
                if (!seen.Add(ancestor.Metadata.DefinitionId))
                {
                    Add(RecurringDefinition, DiagnosticSeverity.Warning, policy,
                        $"Logical definition '{ancestor.Metadata.DefinitionId}' recurs in this pinned chain. This is finite, not an exact dependency cycle; verify the intended revision chain.");
                    break;
                }
                ancestor = ancestor.GetDefinition<MachineReactionPayloadWire>().Prerequisite;
            }
        }
        return new(DocumentValidationDiagnostics.Normalize([.. diagnostics]));

        void Add(string code, DiagnosticSeverity severity, MachineReaction policy, string message) =>
            diagnostics.Add(new(code, severity, message, Evidence: new(
                stage: "reaction-registration", subject: policy.Document.Reference.ToString())));
    }
}
