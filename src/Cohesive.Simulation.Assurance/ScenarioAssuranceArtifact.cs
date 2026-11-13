using System.Text;
using System.Text.Json;
using Cohesive.Assurance;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Assurance;

/// <summary>Portable retention of a scenario attempt using the existing trace and evaluation wire contracts.</summary>
/// <remarks>
/// Method and candidate identity are recorded attribution, not authenticated execution receipts.
/// Reading an artifact always produces trace re-evaluation evidence, never a fresh candidate execution.
/// A satisfied finite trace is not a universal proof. Property campaign/replay tokens remain separate artifacts.
/// </remarks>
public static class ScenarioAssuranceArtifact
{
    const string Schema = "cohesive-simulation-assurance/v1";
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    // Embedded documents retain their own exact wire representations and validation authorities.
    sealed record Envelope(string Schema, ScenarioAssuranceMethod RecordedMethod,
        ScenarioAssuranceBinding Binding, string Trace, string Evaluation);

    /// <summary>Retains the binding, recorded method, full scenario trace and exact evaluation together.</summary>
    /// <param name="result">Completed bridge attempt to retain, including inconclusive or invalid evaluation results.</param>
    /// <returns>Canonical envelope containing independently versioned trace and evaluation documents.</returns>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    /// <exception cref="JsonException">An embedded document cannot be represented portably.</exception>
    /// <exception cref="NotSupportedException">An embedded representation is unsupported.</exception>
    public static string Serialize(ScenarioAssuranceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(new Envelope(Schema,
            result.Method, result.Binding, ScenarioExecutionTraceJsonSerializer.Serialize(result.Trace),
            result.Evaluation.Serialize()), Options));
    }

    /// <summary>Validates retained documents, recomputes the mapping and evaluation, and checks exact agreement.</summary>
    /// <param name="json">Envelope previously produced by <see cref="Serialize"/>.</param>
    /// <returns>A trace re-evaluation result. No candidate operation is invoked, regardless of the recorded method.</returns>
    /// <exception cref="JsonException">The envelope is malformed, incompatible, or its binding, trace and evaluation disagree.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Retained evaluation bounds are invalid.</exception>
    public static ScenarioAssuranceResult Reevaluate(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, Options, "scenario assurance artifact",
                out Envelope? retained, out var error) || retained is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        if (retained.Schema != Schema || retained.Binding is null || retained.Trace is null || retained.Evaluation is null)
            throw new JsonException("An artifact requires a supported schema, binding, trace and evaluation.");

        var evaluation = TraceEvaluation.Reevaluate(retained.Evaluation);
        try
        {
            var trace = ScenarioExecutionTraceJsonSerializer.Deserialize(retained.Trace);
            var result = ScenarioAssurance.Reevaluate(evaluation.Claim, trace, retained.Binding, evaluation.Limits);
            if (!string.Equals(result.Evaluation.Serialize(), evaluation.Serialize(), StringComparison.Ordinal))
                throw new JsonException("Retained binding and scenario trace do not reproduce the retained evaluation.");
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new JsonException("Retained scenario or mapping is invalid for this interpretation.", exception);
        }
    }
}
