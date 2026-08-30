using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>
/// Stable semantic namespace that isolates one generated stream from other uses of the same definition and seed.
/// </summary>
/// <remarks>
/// Scope identities are compared ordinally and are not normalized. Worlds, scenarios, populations, fixtures, and
/// scripts should therefore derive stable identities from their semantic model rather than runtime object identity.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct GenerationScope
{
    /// <summary>Creates a generation scope.</summary>
    /// <param name="value">Stable nonempty semantic namespace.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public GenerationScope(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Gets the conventional scope used when a caller does not request isolation.</summary>
    public static GenerationScope Default { get; } = new("default");

    /// <summary>Gets the exact semantic namespace.</summary>
    public string Value { get; }

    /// <summary>Returns the exact semantic namespace.</summary>
    /// <returns>The value supplied when this scope was constructed.</returns>
    public override string ToString() => Value;

    internal static void Validate(GenerationScope scope, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(scope.Value))
            throw new ArgumentException("A generation scope cannot be default or empty.", parameterName);
    }
}
