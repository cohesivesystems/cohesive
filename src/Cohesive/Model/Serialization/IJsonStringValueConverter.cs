namespace Cohesive.Model.Serialization;

/// <summary>Declares that a converter writes non-null values as JSON strings.</summary>
/// <remarks>Describes the emitted representation, not an exhaustive input-validation schema.
/// Readers may accept additional compatibility forms. The string domain is open; projections must
/// not infer a closed enum from CLR member names. Nullability remains the containing contract's concern.</remarks>
public interface IJsonStringValueConverter
{
}
