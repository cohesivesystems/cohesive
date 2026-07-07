using System.Linq.Expressions;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// DSL-only grouped row abstraction.
/// </summary>
/// <typeparam name="TKey">Group key type.</typeparam>
/// <typeparam name="TElement">Grouped element type.</typeparam>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class RelationGroup<TKey, TElement>
{
    /// <summary>
    /// Group key.
    /// </summary>
    public TKey Key => throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");

    /// <summary>
    /// Group items.
    /// </summary>
    public IReadOnlyList<TElement> Items => throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");

    /// <summary>
    /// Sum aggregate.
    /// </summary>
    public decimal Sum(Expression<Func<TElement, decimal>> selector)
    {
        _ = selector;
        throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");
    }

    /// <summary>
    /// Count aggregate.
    /// </summary>
    public int Count() => throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");

    /// <summary>
    /// Min aggregate.
    /// </summary>
    public decimal Min(Expression<Func<TElement, decimal>> selector)
    {
        _ = selector;
        throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");
    }

    /// <summary>
    /// Max aggregate.
    /// </summary>
    public decimal Max(Expression<Func<TElement, decimal>> selector)
    {
        _ = selector;
        throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");
    }

    /// <summary>
    /// Average aggregate.
    /// </summary>
    public decimal Average(Expression<Func<TElement, decimal>> selector)
    {
        _ = selector;
        throw new InvalidOperationException("RelationGroup is expression-only and cannot be invoked directly.");
    }
}