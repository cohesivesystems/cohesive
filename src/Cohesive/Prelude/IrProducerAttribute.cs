namespace Cohesive.Prelude;

/// <summary>
/// Declares that an assembly may construct <see cref="LoweringOutputAttribute"/>-marked IR types directly.
/// </summary>
/// <remarks>
/// Reserved for lowering pipelines, IR serialization and persistence, and compiler tests, fixtures, and benchmarks.
/// The repository applies it to test, fixture, and benchmark projects through <c>Directory.Build.targets</c>.
/// Importers, generators, and application code emit source instead.
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class IrProducerAttribute : Attribute
{
    /// <summary>Creates the declaration with the reason the assembly constructs IR directly.</summary>
    /// <param name="reason">
    /// Why this assembly is an IR producer, such as "compiler tests"; shown in reviews of the declaration.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is empty or whitespace.</exception>
    public IrProducerAttribute(string reason)
    {
        Reason = Guard.RequireNotNullOrWhiteSpace(value: reason);
    }

    /// <summary>Gets why this assembly constructs IR directly.</summary>
    public string Reason { get; }
}
