namespace Cohesive.Model.Serialization;

/// <summary>Prepared wire names for one closed CLR enum, using the same catalog as semantic authoring.</summary>
/// <typeparam name="TEnum">Enum whose declared values have unique, statically inferable wire names.</typeparam>
/// <remarks>Metadata is initialized once per closed enum type and retained for its CLR lifetime.
/// Requests perform only a dictionary lookup. Undeclared numeric values and flags combinations are rejected.</remarks>
public static class SerializedEnum<TEnum> where TEnum : struct, Enum
{
    static readonly IReadOnlyDictionary<TEnum, string> Names = Prepare();

    /// <summary>Gets the canonical wire name of a declared value.</summary>
    /// <param name="value">Declared enum value.</param>
    /// <returns>Its serialized member name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not declared.</exception>
    /// <exception cref="TypeInitializationException">The enum has ambiguous values or unsupported converter metadata.</exception>
    public static string GetWireName(TEnum value) => Names.TryGetValue(value, out var name)
        ? name : throw new ArgumentOutOfRangeException(nameof(value), "Expected a declared enum value.");

    static IReadOnlyDictionary<TEnum, string> Prepare()
    {
        if (!SerializedEnumMemberCatalog.TryCreate(typeof(TEnum), out var catalog, out var failure, out _))
            throw new InvalidOperationException($"Enum wire metadata cannot be prepared: {failure}.");
        var result = new Dictionary<TEnum, string>();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (!catalog!.TryGetWireName(name, out var wire) || !result.TryAdd(Enum.Parse<TEnum>(name), wire))
                throw new InvalidOperationException("Enum values must have unique declared wire identities.");
        }
        return result;
    }
}
