namespace Cohesive.Model;

/// <summary>
/// Contributes metadata while <see cref="ClrShapeGraphBuilder"/> derives shape graphs from CLR types.
/// </summary>
public interface IClrShapeMetadataProvider
{
    /// <summary>
    /// Returns metadata for the CLR shape artifact described by <paramref name="context"/>.
    /// </summary>
    ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context);
}
