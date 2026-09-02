using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;
using Xunit.Sdk;

namespace Cohesive.Simulation.Xunit;

/// <summary>Translates runner-neutral Cohesive property-case results into xUnit assertion outcomes.</summary>
public static class PropertyCaseAssert
{
    const int MaximumObservationCharacters = 4_096;

    /// <summary>Requires a completed property-case run to have passed.</summary>
    /// <param name="result">Completed runner-neutral property-case result.</param>
    /// <remarks>
    /// This assertion does not generate or evaluate cases. A non-passing result produces a deterministic report;
    /// counterexample observations are capped at 4,096 characters while exact replay tokens remain untruncated.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="XunitException">
    /// <paramref name="result"/> found a counterexample, was invalid, or exhausted a run bound.
    /// </exception>
    public static void Passed(PropertyCaseRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == PropertyCaseRunStatus.Passed)
        {
            return;
        }

        throw new XunitException(FormatFailure(result));
    }

    static string FormatFailure(PropertyCaseRunResult result)
    {
        StringBuilder message = new();
        message.Append("Cohesive property check did not pass.\n");
        message.Append("Status: ").Append(result.Status).Append('\n');
        AppendEvidence(message, result.Evidence);
        AppendCoverage(message, result.Evidence.Coverage);
        AppendDiagnostics(message, result.Validation.Diagnostics);
        AppendCounterexample(message, result.BestCounterexample);
        return message.ToString().TrimEnd('\r', '\n');
    }

    static void AppendEvidence(StringBuilder message, PropertyCaseRunEvidence evidence)
    {
        message.Append("Evidence:\n");
        message.Append("  generatedCases: ")
            .Append(evidence.GeneratedCaseCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        message.Append("  passedCases: ")
            .Append(evidence.PassedCaseCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        message.Append("  discardedCases: ")
            .Append(evidence.DiscardedCaseCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        message.Append("  shrinkCandidates: ")
            .Append(evidence.ShrinkCandidateCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        message.Append("  discardedShrinkCandidates: ")
            .Append(evidence.DiscardedShrinkCandidateCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    static void AppendCoverage(StringBuilder message, IReadOnlyList<PropertyCaseCoverage> coverage)
    {
        message.Append("Coverage:\n");
        if (coverage.Count == 0)
        {
            message.Append("  <none>\n");
            return;
        }

        foreach (var item in coverage)
        {
            message.Append("  ")
                .Append(JsonSerializer.Serialize(item.Classification.Value))
                .Append(": ")
                .Append(item.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
    }

    static void AppendDiagnostics(
        StringBuilder message,
        IReadOnlyList<DocumentValidationDiagnostic> diagnostics)
    {
        message.Append("Diagnostics:\n");
        if (diagnostics.Count == 0)
        {
            message.Append("  <none>\n");
            return;
        }

        foreach (var diagnostic in diagnostics)
        {
            message.Append("  [")
                .Append(diagnostic.Severity)
                .Append("] code=")
                .Append(JsonSerializer.Serialize(diagnostic.Code))
                .Append(" location=")
                .Append(JsonSerializer.Serialize(diagnostic.Location))
                .Append(" schemaLocation=")
                .Append(JsonSerializer.Serialize(diagnostic.SchemaLocation))
                .Append(" message=")
                .Append(JsonSerializer.Serialize(diagnostic.Message))
                .Append('\n');
        }
    }

    static void AppendCounterexample(StringBuilder message, PropertyCase? counterexample)
    {
        message.Append("Counterexample:\n");
        if (counterexample is null)
        {
            message.Append("  <none>");
            return;
        }

        message.Append("  Classifications:\n");
        if (counterexample.Classifications.IsEmpty)
        {
            message.Append("    <none>\n");
        }
        else
        {
            foreach (var classification in counterexample.Classifications)
            {
                message.Append("    ")
                    .Append(JsonSerializer.Serialize(classification.Value))
                    .Append('\n');
            }
        }

        message.Append("  ReplayToken: ").Append(counterexample.Replay.ToToken()).Append('\n');
        var observation = counterexample.Observation.ToCanonicalJson();
        message.Append("  Observation: ");
        if (observation.Length <= MaximumObservationCharacters)
        {
            message.Append(observation);
            return;
        }

        message.Append(observation.AsSpan(0, MaximumObservationCharacters))
            .Append("\n  [observation truncated after ")
            .Append(MaximumObservationCharacters.ToString(CultureInfo.InvariantCulture))
            .Append(" of ")
            .Append(observation.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" characters; replay token is exact]");
    }
}
