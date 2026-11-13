using Cohesive.Authoring;
using System.Linq.Expressions;
using Cohesive.Model;

namespace Cohesive.Assurance;

/// <summary>Typed claim predicate authoring through the shared expression lowerer.</summary>
/// <remarks>
/// Bindings contain authoring-time metadata only; exact evaluation roles remain explicit in Assurance.
/// </remarks>
public static class ClaimExpressions
{
    /// <summary>Lowers a one-input typed predicate into structural claim source.</summary>
    /// <typeparam name="T">CLR projection of the input contract.</typeparam>
    /// <param name="claim">Source declaration being refined.</param>
    /// <param name="input">Existing typed semantic binding.</param>
    /// <param name="role">Evaluation role of the binding.</param>
    /// <param name="predicate">Reviewed typed predicate; never compiled or invoked.</param>
    /// <returns>A new declaration containing only portable predicate source.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ExpressionAuthoringException">The expression is unsupported or captures host state.</exception>
    public static Claim Require<T>(this Claim claim, IExpressionInput<T> input,
        ClaimInputRole role, Expression<Func<T, bool>> predicate) where T : notnull =>
        Lower(claim, predicate, [Binding(input)], [role]);

    /// <summary>Lowers a two-input typed predicate, including event correlation.</summary>
    /// <typeparam name="TLeft">First input CLR projection.</typeparam>
    /// <typeparam name="TRight">Second input CLR projection.</typeparam>
    /// <param name="claim">Source declaration.</param>
    /// <param name="left">First semantic binding.</param>
    /// <param name="leftRole">First evaluation role.</param>
    /// <param name="right">Second semantic binding.</param>
    /// <param name="rightRole">Second evaluation role.</param>
    /// <param name="predicate">Reviewed correlation or transition predicate.</param>
    /// <returns>A new portable source declaration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ExpressionAuthoringException">The expression cannot be lowered faithfully.</exception>
    public static Claim Require<TLeft, TRight>(this Claim claim,
        IExpressionInput<TLeft> left, ClaimInputRole leftRole,
        IExpressionInput<TRight> right, ClaimInputRole rightRole,
        Expression<Func<TLeft, TRight, bool>> predicate) where TLeft : notnull where TRight : notnull =>
        Lower(claim, predicate, [Binding(left), Binding(right)], [leftRole, rightRole]);

    /// <summary>Lowers a before/command/after predicate with three typed inputs.</summary>
    /// <typeparam name="TBefore">Before-state CLR projection.</typeparam>
    /// <typeparam name="TCommand">Command CLR projection.</typeparam>
    /// <typeparam name="TAfter">After-state CLR projection.</typeparam>
    /// <param name="claim">Source declaration.</param>
    /// <param name="before">Before-state binding.</param>
    /// <param name="command">Command binding.</param>
    /// <param name="after">After-state binding.</param>
    /// <param name="predicate">Reviewed transition predicate.</param>
    /// <returns>A new portable source declaration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ExpressionAuthoringException">The expression cannot be lowered faithfully.</exception>
    public static Claim Require<TBefore, TCommand, TAfter>(this Claim claim,
        IExpressionInput<TBefore> before,
        IExpressionInput<TCommand> command,
        IExpressionInput<TAfter> after,
        Expression<Func<TBefore, TCommand, TAfter, bool>> predicate)
        where TBefore : notnull where TCommand : notnull where TAfter : notnull =>
        Lower(claim, predicate, [Binding(before), Binding(command), Binding(after)],
            [ClaimInputRole.Before, ClaimInputRole.Command, ClaimInputRole.After]);

    static ExpressionBinding Binding<T>(IExpressionInput<T> input) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Binding;
    }

    static Claim Lower(Claim claim, LambdaExpression predicate,
        ExpressionBinding[] bindings, ClaimInputRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(predicate);
        foreach (var binding in bindings) ArgumentNullException.ThrowIfNull(binding);
        ClrAuthoringContext context = new();
        ExpressionLowerer lowerer = new(context.ResolveMemberPath);
        var lowered = lowerer.LowerValue(predicate, bindings, claim.AuthoringSource.Source.Value + "/predicate").RequireValue();
        return claim.Require(lowered.Value,
            bindings.Select((binding, index) => new ClaimInput(binding.Id, roles[index], new ValueContract(binding.Type))),
            ExpressionLowerer.Producer);
    }
}
