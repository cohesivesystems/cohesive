namespace Cohesive.Storage.Materialization;

/// <summary>Exact-ID dependency pool implementing one canonical materialization backend-pool definition.</summary>
/// <remarks>
/// Runtime routing is deliberately absent from this port. Callers must first resolve an explicit target identity
/// from canonical configuration or durable routing state and then request that exact dependency.
/// </remarks>
public interface IMaterializationTargetPool
{
    /// <summary>Gets the canonical static pool definition implemented by these dependencies.</summary>
    MaterializationBackendPoolDefinition Definition { get; }

    /// <summary>Resolves one exact target dependency by its canonical backend-IR identity.</summary>
    /// <param name="targetId">Exact declared target identity.</param>
    /// <returns>The target whose descriptor exactly implements the declared member.</returns>
    /// <exception cref="ArgumentException"><paramref name="targetId"/> is default.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="targetId"/> is not a declared pool member.</exception>
    IMaterializationTarget Resolve(MaterializationTargetId targetId);
}

/// <summary>Immutable local reference implementation of an exact-ID materialization target pool.</summary>
public sealed class InMemoryMaterializationTargetPool : IMaterializationTargetPool
{
    readonly IReadOnlyDictionary<MaterializationTargetId, IMaterializationTarget> targets;

    /// <summary>Creates a dependency pool implementing every exact member of one canonical definition.</summary>
    /// <param name="definition">Canonical static pool definition.</param>
    /// <param name="targets">Exact target dependencies; order has no semantic effect.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="targets"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A target or descriptor is null; a dependency identity is duplicated, undeclared, missing, belongs to another
    /// materialization, or its descriptor differs from the canonical pool member.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">A target descriptor cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">A target descriptor contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">A target descriptor has no canonical JSON representation.</exception>
    public InMemoryMaterializationTargetPool(
        MaterializationBackendPoolDefinition definition,
        IReadOnlyCollection<IMaterializationTarget> targets)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentNullException.ThrowIfNull(targets);

        var declared = new Dictionary<MaterializationTargetId, MaterializationTargetDescriptor>(
            definition.Members.Length);
        foreach (var member in definition.Members)
            declared.Add(member.Id, member);

        var resolved = new Dictionary<MaterializationTargetId, IMaterializationTarget>(targets.Count);
        foreach (var target in targets)
        {
            if (target is null)
                throw new ArgumentException("A materialization target pool cannot contain null dependencies.", nameof(targets));

            var descriptor = target.Descriptor
                ?? throw new ArgumentException("A materialization target dependency requires a descriptor.", nameof(targets));
            if (!declared.TryGetValue(descriptor.Id, out var expected))
            {
                throw new ArgumentException(
                    $"Target '{descriptor.Id.Value}' is not declared by backend pool '{definition.Id.Value}'.",
                    nameof(targets));
            }

            if (descriptor.MaterializationId != definition.MaterializationId)
            {
                throw new ArgumentException(
                    $"Target '{descriptor.Id.Value}' serves another materialization.",
                    nameof(targets));
            }

            if (!MaterializationContract.CanonicalEquals(descriptor, expected))
            {
                throw new ArgumentException(
                    $"Target '{descriptor.Id.Value}' does not implement its exact canonical pool descriptor.",
                    nameof(targets));
            }

            if (!resolved.TryAdd(descriptor.Id, target))
            {
                throw new ArgumentException(
                    $"Target dependency '{descriptor.Id.Value}' is duplicated.",
                    nameof(targets));
            }
        }

        if (resolved.Count != declared.Count)
        {
            var missing = definition.Members
                .Where(member => !resolved.ContainsKey(member.Id))
                .Select(static member => member.Id.Value);
            throw new ArgumentException(
                $"Backend pool '{definition.Id.Value}' is missing target dependencies: {string.Join(", ", missing)}.",
                nameof(targets));
        }

        this.targets = resolved;
    }

    /// <inheritdoc />
    public MaterializationBackendPoolDefinition Definition { get; }

    /// <inheritdoc />
    public IMaterializationTarget Resolve(MaterializationTargetId targetId)
    {
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        return targets.TryGetValue(targetId, out var target)
            ? target
            : throw new KeyNotFoundException(
                $"Target '{targetId.Value}' is not a member of backend pool '{Definition.Id.Value}'.");
    }

}
