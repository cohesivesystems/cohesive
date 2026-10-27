using System.Collections.Immutable;
using Cohesive.Prelude;

namespace Cohesive.Storage.Materialization;

/// <summary>Optional instance-owned materialization declarations using the existing authoring builders.</summary>
/// <remarks>Activation freezes membership without native IO. Native resources and execution lifetimes are separate.</remarks>
public abstract class MaterializationModel : IAuthoringModel
{
    /// <summary>Underlying ordered builder, usable directly without subclassing.</summary>
    protected MaterializationModelBuilder Materializations { get; } = new();
    /// <summary>Admitted preparations in declaration order, shared after completion.</summary>
    public ImmutableArray<PreparedMaterialization> Definition => Materializations.Build();
    void IAuthoringModel.Complete() => _ = Definition;
}

/// <summary>Collects independently authored materializations without introducing a second declaration format.</summary>
public sealed class MaterializationModelBuilder
{
    readonly List<PreparedMaterialization> declarations = [];
    readonly HashSet<MaterializationId> ids = [];
    readonly AuthoringState<ImmutableArray<PreparedMaterialization>> state;

    /// <summary>Creates an independent declaration lifetime.</summary>
    public MaterializationModelBuilder() => state = new(() => [.. declarations]);

    /// <summary>Includes an admitted preparation from the direct materialization builder.</summary>
    /// <param name="materialization">Exact immutable declaration and impact preparation.</param>
    /// <returns>The same preparation for assignment to a typed model property.</returns>
    /// <exception cref="ArgumentNullException">Preparation is null.</exception>
    /// <exception cref="ArgumentException">The semantic ID is already registered.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PreparedMaterialization Include(PreparedMaterialization materialization)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        return state.Edit(() =>
        {
            if (!ids.Add(materialization.Document.Definition.Id))
                throw new ArgumentException($"Materialization '{materialization.Document.Definition.Id}' is already declared.", nameof(materialization));
            declarations.Add(materialization);
            return materialization;
        });
    }

    /// <summary>Freezes membership and returns the cached ordered preparations; performs no native IO.</summary>
    /// <returns>Immutable contributions shared on repeated reads.</returns>
    public ImmutableArray<PreparedMaterialization> Build() => state.Value;
}
