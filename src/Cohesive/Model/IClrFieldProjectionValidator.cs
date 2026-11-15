using System.Reflection;

namespace Cohesive.Model;

/// <summary>Checks a field after all metadata providers and type mapping have run.</summary>
/// <remarks>Runs only during authoring; no validator is retained in the portable graph.</remarks>
public interface IClrFieldProjectionValidator
{
    /// <summary>Rejects unsupported combinations in the final field projection.</summary>
    void ValidateFieldProjection(ClrFieldProjectionContext context);
}

/// <summary>The composed metadata and resolved portable contract of a CLR property.</summary>
/// <param name="Property">Authored CLR property.</param>
/// <param name="Type">Final portable field or collection element type.</param>
/// <param name="Cardinality">Final field cardinality.</param>
/// <param name="Metadata">Metadata composed from all providers, including explicit overrides.</param>
public sealed record ClrFieldProjectionContext(PropertyInfo Property, TypeRef Type,
    FieldCardinality Cardinality, ClrShapeMetadata Metadata);
