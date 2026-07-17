using Cohesive.Relations.IR;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Creates canonical semantic-input identities shared by relation/query compilation and invocation authoring.
/// </summary>
public static class RelationQueryInputIds
{
    const string ParameterPrefix = "input/parameter/";

    /// <summary>Creates the canonical compiled-input identity for a query parameter.</summary>
    /// <param name="parameter">Canonical query-parameter identity.</param>
    /// <returns>
    /// The stable parameter-input identity used by requirement graphs and runtime parameter evidence.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="parameter"/> is default.</exception>
    public static RelationQueryInputId ForParameter(QueryParameterId parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter.Value))
            throw new ArgumentException("A parameter input requires a non-empty parameter identifier.", nameof(parameter));

        return new($"{ParameterPrefix}{Uri.EscapeDataString(parameter.Value)}");
    }
}
