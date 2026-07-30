using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Authoring;

/// <summary>Typed authoring value backed by one portable canonical Process expression.</summary>
/// <remarks>
/// A value is scoped to one authoring session. It contains no executable user callback; member selectors are
/// converted immediately to <see cref="FieldPath"/> and discarded.
/// </remarks>
/// <typeparam name="TValue">CLR type projected into the portable value contract.</typeparam>
public sealed class ProcessValue<TValue>
{
    readonly ProcessAuthoringContext context;

    internal ProcessValue(ProcessAuthoringContext context, Expr expression, ValueContract contract)
    {
        this.context = context;
        Expression = expression;
        Contract = contract;
    }

    /// <summary>Portable canonical expression represented by this typed value.</summary>
    public Expr Expression { get; }

    /// <summary>Portable semantic contract expected from <see cref="Expression"/>.</summary>
    public ValueContract Contract { get; }

    /// <summary>Selects a nested field using a typed semantic member path.</summary>
    /// <typeparam name="TField">CLR type projected into the selected field contract.</typeparam>
    /// <param name="selector">Member-path selector rooted at this value.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed value containing a binding-qualified canonical field expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> is not a member path rooted at its parameter.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// This value is foreign to the active authoring session or is not rooted in a canonical value binding.
    /// </exception>
    public ProcessValue<TField> Field<TField>(
        Expression<Func<TValue, TField>> selector,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(selector);
        return context.Field<TValue, TField>(
            this,
            ProcessAuthoringMemberPath.From(selector),
            context.Source(sourceFile, sourceLine, sourceMember, "Process value field"));
    }

    internal ProcessAuthoringContext Context => context;
}

/// <summary>Typed handle for one canonical Process value binding.</summary>
/// <typeparam name="TValue">CLR type projected into the binding contract.</typeparam>
public sealed class ProcessBinding<TValue>
{
    readonly ProcessAuthoringContext context;
    readonly ProcessOutputBinding? output;

    internal ProcessBinding(
        ProcessAuthoringContext context,
        ValueBindingId binding,
        ValueContract contract,
        ProcessOutputBinding? output)
    {
        this.context = context;
        Binding = binding;
        Contract = contract;
        this.output = output;
        Value = new(context, Expr.BoundValue(binding), contract);
    }

    /// <summary>Stable canonical value-binding identity.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Portable semantic contract of the complete bound value.</summary>
    public ValueContract Contract { get; }

    /// <summary>Typed whole-binding Process value.</summary>
    public ProcessValue<TValue> Value { get; }

    /// <summary>Canonical expression referencing the complete bound value.</summary>
    public Expr Expression => Value.Expression;

    /// <summary>Selects a nested field using a typed semantic member path.</summary>
    /// <typeparam name="TField">CLR type projected into the selected field contract.</typeparam>
    /// <param name="selector">Member-path selector rooted at this binding.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed value containing a binding-qualified canonical field expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> is not a member path rooted at its parameter.
    /// </exception>
    /// <exception cref="InvalidOperationException">This binding belongs to another authoring session.</exception>
    public ProcessValue<TField> Field<TField>(
        Expression<Func<TValue, TField>> selector,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Value.Field(selector, sourceFile, sourceLine, sourceMember);

    internal ProcessAuthoringContext Context => context;

    internal ProcessOutputBinding RequireOutput()
    {
        if (output is null)
        {
            throw new InvalidOperationException(
                $"Process binding '{Binding.Value}' is an input binding and cannot receive a continuation output.");
        }

        return output;
    }
}

/// <summary>Typed authoring handle for one admitted inbound Request obligation.</summary>
public sealed class ProcessRequestObligation
{
    readonly ProcessAuthoringContext context;

    internal ProcessRequestObligation(
        ProcessAuthoringContext context,
        ProcessRequestObligationBinding canonicalBinding)
    {
        this.context = context;
        CanonicalBinding = canonicalBinding;
    }

    /// <summary>Stable identity used by a later Reply to discharge the same logical Request.</summary>
    public RequestObligationBindingId Binding => CanonicalBinding.Binding;

    internal ProcessRequestObligationBinding CanonicalBinding { get; }

    internal ProcessAuthoringContext Context => context;
}

internal static class ProcessAuthoringMemberPath
{
    public static FieldPath From<TSource, TValue>(Expression<Func<TSource, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var boxed = Expression.Lambda<Func<TSource, object?>>(
            Expression.Convert(selector.Body, typeof(object)),
            selector.Parameters);
        return FieldPath.Capture(boxed);
    }
}
