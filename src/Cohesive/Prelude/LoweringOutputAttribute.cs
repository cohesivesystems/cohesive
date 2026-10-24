namespace Cohesive.Prelude;

/// <summary>
/// Marks a canonical IR type that is produced by lowering source, deserializing persisted IR, or compiler tests,
/// rather than constructed directly by application or tooling code.
/// </summary>
/// <remarks>
/// <para>
/// Source is the authoring authority and the canonical IR derived from it is the semantic authority, so code outside
/// the declaring assembly should obtain marked types from an authoring API or a serializer. The Cohesive analyzer
/// reports <c>COHIR001</c> when a marked type, or a type derived from one, is constructed or copied with
/// <c>with</c> in any other assembly, unless that assembly declares <see cref="IrProducerAttribute"/>. Generic
/// construction through a type parameter, reflection, and deserialization are not covered.
/// </para>
/// <para>
/// Mark definition roots and graph nodes. Do not mark small value objects that authoring APIs accept as arguments
/// (for example page or ordering definitions); the authoring core validates and normalizes those.
/// </para>
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LoweringOutputAttribute : Attribute
{
}
