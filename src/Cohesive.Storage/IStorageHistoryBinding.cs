namespace Cohesive.Storage;

/// <summary>Offline history selection exposed by the storage binding that owns it.</summary>
/// <remarks>Declared intent only; this is not proof that a native store is configured or ready.</remarks>
public interface IStorageHistoryBinding
{
    /// <summary>Declared history, or null when no history attachment is configured.</summary>
    StorageHistoryDeclaration? HistoryDeclaration { get; }
}

/// <summary>Inspectable history policy derived from a native storage binding, without clients or credentials.</summary>
/// <param name="Resource">Existing database resource identity used by the storage binding.</param>
/// <param name="Authority">Authoritative representation selected by the history configuration.</param>
/// <param name="Reconstitution">Declared state reconstruction strategy.</param>
public sealed record StorageHistoryDeclaration(string Resource, EntityStateAuthority Authority,
    EntityReconstitutionMode Reconstitution);
