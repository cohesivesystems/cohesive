using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Integrations.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Cardinality of explicitly supplied, already configured and authorized candidates.</summary>
public enum IntegrationCandidateSelector
{
    /// <summary>Select one named exact binding; never substitute another candidate.</summary>
    Exactly,
    /// <summary>Require exactly one admissible candidate.</summary>
    ExactlyOne,
    /// <summary>Require every supplied instance; preserve individual failures rather than dropping them.</summary>
    All
}

/// <summary>Selection results with every per-instance qualification retained.</summary>
/// <param name="Candidates">All assessments in stable binding-identity order.</param>
/// <param name="Selected">Admitted plans; empty when selector requirements fail.</param>
/// <param name="Validation">Cardinality or per-instance failure diagnostics.</param>
public sealed record IntegrationCandidateSelection(ImmutableArray<IntegrationQualificationResult> Candidates,
    ImmutableArray<IntegrationCompilation> Selected, DocumentValidationResult Validation)
{
    /// <summary>Selects among supplied assessments without inventing preference or authorization.</summary>
    /// <param name="candidates">Candidates already compiled through explicit or scoped binding.</param>
    /// <param name="selector">Required cardinality.</param>
    /// <param name="exact">Required for Exactly and prohibited otherwise.</param>
    /// <returns>Deterministic selection and all assessment explanations.</returns>
    /// <exception cref="ArgumentNullException">Candidates is null.</exception>
    public static IntegrationCandidateSelection Resolve(IEnumerable<IntegrationQualificationResult> candidates,
        IntegrationCandidateSelector selector, ExecutionDefinitionReference? exact = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = candidates.OrderBy(c => c.Candidate.Binding?.Metadata.DefinitionId.Value, StringComparer.Ordinal)
            .ThenBy(c => c.Candidate.Binding?.Metadata.Fingerprint.Value, StringComparer.Ordinal).ToImmutableArray();
        IntegrationCandidateSelection Fail(string code, string message) => new(ordered, [], IntegrationQualification.Error(code, message));
        if (!Enum.IsDefined(selector) || (selector == IntegrationCandidateSelector.Exactly) != (exact is not null))
            return Fail("selector", "Exactly requires an exact binding; other selectors do not accept one.");
        if (ordered.Any(c => c.Candidate.Binding is null) || ordered.Select(c => c.Candidate.Binding!.Reference).Distinct().Count() != ordered.Length)
            return Fail("candidates", "Supply distinct bound candidates.");
        var eligible = ordered.Where(c => c.IsAdmissible).ToArray();
        if (selector == IntegrationCandidateSelector.Exactly)
        {
            var selected = ordered.SingleOrDefault(c => c.Candidate.Binding!.Reference == exact);
            return selected is null ? Fail("missing", "The exact binding was not supplied.")
                : !selected.IsAdmissible ? new(ordered, [], selected.Validation) : new(ordered, [selected.Compilation!], DocumentValidationResult.Valid);
        }
        if (selector == IntegrationCandidateSelector.ExactlyOne && eligible.Length != 1)
            return Fail("cardinality", "ExactlyOne requires one admissible candidate; zero or multiple candidates are invalid.");
        if (selector == IntegrationCandidateSelector.All && (ordered.IsEmpty || eligible.Length != ordered.Length))
            return Fail("all", "All requires a nonempty set of individually admissible instances; inspect each candidate's diagnostics.");
        return new(ordered, [.. eligible.Select(c => c.Compilation!)], DocumentValidationResult.Valid);
    }
}
