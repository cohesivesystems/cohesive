namespace Cohesive.Transitions.IR;

/// <summary>
/// Stable wire names used by canonical Transition IR v1.
/// </summary>
/// <remarks>
/// These constants are the single source of truth for persisted discriminators. Renaming a CLR type does
/// not change its durable representation.
/// </remarks>
public static class TransitionWireNames
{
    /// <summary>Shared execution-definition kind for Transition documents.</summary>
    public const string DefinitionKind = "transition";

    /// <summary>JSON property carrying a transition-node discriminator.</summary>
    public const string NodeDiscriminator = "$node";

    /// <summary>Sequence node discriminator.</summary>
    public const string SequenceNode = "sequence";

    /// <summary>Lexical binding node discriminator.</summary>
    public const string LetNode = "let";

    /// <summary>Predicate-choice node discriminator.</summary>
    public const string ChoiceNode = "choice";

    /// <summary>Exact-pattern match node discriminator.</summary>
    public const string MatchNode = "match";

    /// <summary>Sparse aggregate update node discriminator.</summary>
    public const string UpdateNode = "update";

    /// <summary>Pure emission-intent node discriminator.</summary>
    public const string EmitNode = "emit";

    /// <summary>Terminal typed outcome node discriminator.</summary>
    public const string OutcomeNode = "outcome";

    /// <summary>JSON property carrying a sparse-patch discriminator.</summary>
    public const string PatchDiscriminator = "$patch";

    /// <summary>Set patch discriminator.</summary>
    public const string SetPatch = "set";

    /// <summary>Remove patch discriminator.</summary>
    public const string RemovePatch = "remove";

    /// <summary>Increment patch discriminator.</summary>
    public const string IncrementPatch = "increment";

    /// <summary>Add-to-set patch discriminator.</summary>
    public const string AddToSetPatch = "addToSet";

    /// <summary>Append patch discriminator.</summary>
    public const string AppendPatch = "append";

    /// <summary>Owned-child upsert patch discriminator.</summary>
    public const string UpsertOwnedChildPatch = "upsertOwnedChild";

    /// <summary>Owned-child removal patch discriminator.</summary>
    public const string RemoveOwnedChildPatch = "removeOwnedChild";
}
