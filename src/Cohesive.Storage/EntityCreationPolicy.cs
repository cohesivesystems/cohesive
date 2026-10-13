namespace Cohesive.Storage;

/// <summary>Explicit treatment of existing state when acquiring a new entity.</summary>
public enum EntityCreationPolicy
{
    /// <summary>Reject an existing identity atomically.</summary>
    IfAbsent,
    /// <summary>Replace existing state; no absence guarantee is requested.</summary>
    ReplaceExisting
}

/// <summary>Native creation guarantees advertised for setup-time admission.</summary>
/// <param name="SupportsAtomicAbsence">Whether creation can reject an existing identity atomically.</param>
public sealed record EntityCreationCapabilities(bool SupportsAtomicAbsence)
{
    /// <summary>Only unconditional replacement is available.</summary>
    public static EntityCreationCapabilities ReplacementOnly { get; } = new(false);
    /// <summary>Both absence-fenced creation and unconditional replacement are available.</summary>
    public static EntityCreationCapabilities AtomicAbsence { get; } = new(true);

    /// <summary>Checks a requested creation policy before admitting traffic.</summary>
    /// <param name="policy">Requested behavior for an existing identity.</param>
    /// <exception cref="ArgumentOutOfRangeException">The policy is unknown.</exception>
    /// <exception cref="NotSupportedException">Atomic absence is not available.</exception>
    public void Require(EntityCreationPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy == EntityCreationPolicy.IfAbsent && !SupportsAtomicAbsence)
            throw new NotSupportedException("Creation requires an atomic absence-fenced repository.");
    }
}
