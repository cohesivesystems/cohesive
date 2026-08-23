namespace Cohesive.Execution;

/// <summary>
/// Projects canonical interaction authority into the physical context supplied to an impure operation boundary.
/// </summary>
/// <remarks>
/// Canonical interactions name a generic authority and optional tenant, but Cohesive cannot infer a product's
/// semantic scope vocabulary, claims, grants, or storage placement from those strings. A host may implement this
/// contract to derive its typed effective scopes while retaining the canonical authority tuple as the source of
/// truth. Implementations must preserve the supplied context's time, start instant, trace, and cancellation.
/// </remarks>
public interface IInteractionAuthorityOperationContextProjector
{
    /// <summary>Projects one exact authority boundary into a physical operation context.</summary>
    /// <param name="context">Physical context carrying time, trace, and cancellation.</param>
    /// <param name="authorityScope">Canonical authority and optional tenant boundary.</param>
    /// <returns>A context enriched with the host's semantic identity and scope interpretation.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    OperationContext Project(OperationContext context, InteractionAuthorityScope authorityScope);
}

/// <summary>Default projection that leaves an identity-free physical context unchanged.</summary>
public sealed class PassthroughInteractionAuthorityOperationContextProjector
    : IInteractionAuthorityOperationContextProjector
{
    PassthroughInteractionAuthorityOperationContextProjector() { }

    /// <summary>Shared stateless projection.</summary>
    public static PassthroughInteractionAuthorityOperationContextProjector Instance { get; } = new();

    /// <inheritdoc />
    public OperationContext Project(OperationContext context, InteractionAuthorityScope authorityScope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        return context;
    }
}
