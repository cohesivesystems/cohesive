using System.Text.Json.Serialization;

namespace Cohesive.Transitions.IR;

/// <summary>Closed persisted union of algebraic sparse aggregate patch operations.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = TransitionWireNames.PatchDiscriminator)]
[JsonDerivedType(typeof(SetTransitionPatch), TransitionWireNames.SetPatch)]
[JsonDerivedType(typeof(RemoveTransitionPatch), TransitionWireNames.RemovePatch)]
[JsonDerivedType(typeof(IncrementTransitionPatch), TransitionWireNames.IncrementPatch)]
[JsonDerivedType(typeof(AddToSetTransitionPatch), TransitionWireNames.AddToSetPatch)]
[JsonDerivedType(typeof(AppendTransitionPatch), TransitionWireNames.AppendPatch)]
[JsonDerivedType(typeof(UpsertOwnedChildTransitionPatch), TransitionWireNames.UpsertOwnedChildPatch)]
[JsonDerivedType(typeof(RemoveOwnedChildTransitionPatch), TransitionWireNames.RemoveOwnedChildPatch)]
public abstract record TransitionPatchOperation
{
    /// <summary>Creates base state for a registered sparse patch variant.</summary>
    private protected TransitionPatchOperation()
    {
    }
}

/// <summary>Sets a field to the result of a pure expression, including an explicit null.</summary>
public sealed record SetTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates a set patch.</summary>
    /// <param name="value">Expression yielding the replacement value.</param>
    [JsonConstructor]
    public SetTransitionPatch(Expr value) => Value = value;

    /// <summary>Expression yielding the replacement value.</summary>
    public Expr Value { get; }
}

/// <summary>Removes the value at the target path; this is distinct from setting it to null.</summary>
public sealed record RemoveTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates a remove patch.</summary>
    public RemoveTransitionPatch()
    {
    }
}

/// <summary>Increments a numeric field by a pure expression result.</summary>
public sealed record IncrementTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates an increment patch.</summary>
    /// <param name="amount">Expression yielding the increment amount.</param>
    [JsonConstructor]
    public IncrementTransitionPatch(Expr amount) => Amount = amount;

    /// <summary>Expression yielding the increment amount.</summary>
    public Expr Amount { get; }
}

/// <summary>Adds a value to a semantic set if it is not already present.</summary>
public sealed record AddToSetTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates an add-to-set patch.</summary>
    /// <param name="value">Expression yielding the candidate set element.</param>
    [JsonConstructor]
    public AddToSetTransitionPatch(Expr value) => Value = value;

    /// <summary>Expression yielding the candidate set element.</summary>
    public Expr Value { get; }
}

/// <summary>Appends a value to an ordered collection.</summary>
public sealed record AppendTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates an append patch.</summary>
    /// <param name="value">Expression yielding the value to append.</param>
    [JsonConstructor]
    public AppendTransitionPatch(Expr value) => Value = value;

    /// <summary>Expression yielding the value to append.</summary>
    public Expr Value { get; }
}

/// <summary>Upserts an owned child selected by semantic identity within the target collection.</summary>
public sealed record UpsertOwnedChildTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates an owned-child upsert patch.</summary>
    /// <param name="identityPath">Child-relative path containing semantic identity.</param>
    /// <param name="identity">Expression yielding the child identity to match.</param>
    /// <param name="value">Expression yielding the complete child value to insert or replace.</param>
    [JsonConstructor]
    public UpsertOwnedChildTransitionPatch(
        FieldPath identityPath,
        Expr identity,
        Expr value)
    {
        IdentityPath = identityPath;
        Identity = identity;
        Value = value;
    }

    /// <summary>Child-relative path containing semantic identity.</summary>
    public FieldPath IdentityPath { get; }

    /// <summary>Expression yielding the child identity to match.</summary>
    public Expr Identity { get; }

    /// <summary>Expression yielding the complete child value to insert or replace.</summary>
    public Expr Value { get; }
}

/// <summary>Removes an owned child selected by semantic identity within the target collection.</summary>
public sealed record RemoveOwnedChildTransitionPatch : TransitionPatchOperation
{
    /// <summary>Creates an owned-child removal patch.</summary>
    /// <param name="identityPath">Child-relative path containing semantic identity.</param>
    /// <param name="identity">Expression yielding the child identity to match.</param>
    [JsonConstructor]
    public RemoveOwnedChildTransitionPatch(FieldPath identityPath, Expr identity)
    {
        IdentityPath = identityPath;
        Identity = identity;
    }

    /// <summary>Child-relative path containing semantic identity.</summary>
    public FieldPath IdentityPath { get; }

    /// <summary>Expression yielding the child identity to match.</summary>
    public Expr Identity { get; }
}
