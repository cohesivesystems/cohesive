using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model;

namespace Cohesive.Cli;

/// <summary>Declarative relationships between effective bound CLI parameters.</summary>
/// <remarks>Names are configuration keys, not CLI aliases. Selection means nonempty text or collection,
/// true for booleans, and non-null for other scalars. Defaults participate. Numeric rules support
/// 8–64-bit integral types and decimal, including nullable forms; absent optional numbers are skipped.</remarks>
public sealed record CliConstraint
{
    /// <summary>The semantic operation of a declarative constraint.</summary>
    public enum Rule
    {
        /// <summary>A numeric value greater than zero.</summary>
        Positive,
        /// <summary>An inclusive numeric range.</summary>
        Range,
        /// <summary>Exactly one effective value selected.</summary>
        ExactlyOne,
        /// <summary>At most one effective value selected.</summary>
        AtMostOne,
        /// <summary>Selection of one value requires another.</summary>
        Requires,
        /// <summary>At most one string value names standard input.</summary>
        StandardInput
    }
    /// <summary>Gets the operation represented by the declaration.</summary>
    public Rule Kind { get; }

    /// <summary>Gets the immutable parameter keys referenced by the rule.</summary>
    public ImmutableArray<string> Parameters { get; private init; }
    ImmutableArray<FieldPath> boundPaths { get; init; } = [];
    /// <summary>Gets an inclusive range when this is a range rule, otherwise null.</summary>
    public RangeConstraint? Bounds { get; }

    CliConstraint(Rule rule, IEnumerable<string> parameters, RangeConstraint? bounds = null)
    {
        Kind = rule;
        Parameters = [.. parameters];
        if (Parameters.Any(string.IsNullOrWhiteSpace) || Parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Parameters.Length)
            throw new ArgumentException("Constraint keys must be nonempty and distinct.", nameof(parameters));
        Bounds = bounds;
    }

    /// <summary>Requires a supplied numeric value to be greater than zero.</summary>
    /// <param name="parameter">Configuration key.</param>
    /// <returns>A positive-value rule.</returns>
    /// <exception cref="ArgumentException">The key is empty.</exception>
    public static CliConstraint Positive(string parameter) => new(Rule.Positive, [parameter]);

    /// <summary>Constrains a supplied numeric value to inclusive bounds.</summary>
    /// <param name="parameter">Configuration key.</param>
    /// <param name="minimum">Inclusive lower bound, or null.</param>
    /// <param name="maximum">Inclusive upper bound, or null.</param>
    /// <returns>A rule using the core range constraint model.</returns>
    /// <exception cref="ArgumentException">The key or bounds are invalid.</exception>
    public static CliConstraint Range(string parameter, decimal? minimum = null, decimal? maximum = null) =>
        new(Rule.Range, [parameter], new RangeConstraint(minimum, maximum));

    /// <summary>Requires exactly one parameter to be selected.</summary>
    /// <param name="parameters">At least two distinct configuration keys.</param>
    /// <returns>A selection rule.</returns>
    /// <exception cref="ArgumentException">Keys are invalid or fewer than two are supplied.</exception>
    /// <exception cref="ArgumentNullException">The keys are null.</exception>
    public static CliConstraint ExactlyOne(params string[] parameters) => Group(Rule.ExactlyOne, parameters);

    /// <summary>Allows at most one parameter to be selected.</summary>
    /// <param name="parameters">At least two distinct configuration keys.</param>
    /// <returns>A mutual-exclusion rule.</returns>
    /// <exception cref="ArgumentException">Keys are invalid or fewer than two are supplied.</exception>
    /// <exception cref="ArgumentNullException">The keys are null.</exception>
    public static CliConstraint AtMostOne(params string[] parameters) => Group(Rule.AtMostOne, parameters);

    /// <summary>Requires the second parameter when the first is selected.</summary>
    /// <param name="parameter">Configuration key activating the requirement.</param>
    /// <param name="requiredParameter">Configuration key that must then be selected.</param>
    /// <returns>A conditional requirement rule.</returns>
    /// <exception cref="ArgumentException">Keys are empty or identical.</exception>
    public static CliConstraint Requires(string parameter, string requiredParameter) =>
        new(Rule.Requires, [parameter, requiredParameter]);

    /// <summary>Allows at most one string parameter to select standard input.</summary>
    /// <param name="parameters">At least two distinct string parameter keys.</param>
    /// <returns>A standard-input exclusivity rule.</returns>
    /// <exception cref="ArgumentException">Keys are invalid or fewer than two are supplied.</exception>
    /// <exception cref="ArgumentNullException">The keys are null.</exception>
    public static CliConstraint AtMostOneStandardInput(params string[] parameters) => Group(Rule.StandardInput, parameters);

    static CliConstraint Group(Rule rule, string[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Length < 2)
            throw new ArgumentException("A selection group requires at least two parameters.", nameof(parameters));
        return new(rule, parameters);
    }

    internal CliConstraint Bind(IReadOnlyDictionary<string, Configuration.ConfigurationParameterDescriptor> descriptors)
    {
        ValidateDeclaration(descriptors);
        return this with { boundPaths = [.. Parameters.Select(key => descriptors[key].Path)] };
    }

    internal CliConstraint Resolve(IReadOnlyList<Configuration.ConfigurationParameterDescriptor> descriptors)
    {
        if (boundPaths.IsDefaultOrEmpty) return this;
        var byPath = descriptors.ToDictionary(descriptor => descriptor.Path);
        return this with
        {
            Parameters = [.. boundPaths.Select(path => byPath.TryGetValue(path, out var descriptor)
                ? descriptor.ConfigurationKey
                : throw new ArgumentException($"Constraint parameter path '{path}' is no longer declared."))],
            boundPaths = []
        };
    }

    internal void ValidateDeclaration(IReadOnlyDictionary<string, Configuration.ConfigurationParameterDescriptor> descriptors)
    {
        foreach (var key in Parameters)
        {
            if (!descriptors.TryGetValue(key, out var descriptor))
                throw new ArgumentException($"Constraint parameter '{key}' was not declared.");
            var type = Nullable.GetUnderlyingType(descriptor.ParameterType) ?? descriptor.ParameterType;
            if (Kind is Rule.Positive or Rule.Range && !IsNumeric(type))
                throw new ArgumentException($"Numeric constraint for '{key}' requires an 8–64-bit integral or decimal parameter.");
            if (Kind == Rule.StandardInput && type != typeof(string))
                throw new ArgumentException($"Standard input constraint for '{key}' requires a string parameter.");
        }
    }

    static bool IsNumeric(Type type) => !type.IsEnum && Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or
        TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Decimal;

    internal string Describe(Func<string, string> display)
    {
        var names = Parameters.Select(display).ToArray();
        return Kind switch
        {
            Rule.Positive => $"{names[0]} must be positive.",
            Rule.Range => $"{names[0]} must be in [{Format(Bounds!.Minimum, "unbounded")}, {Format(Bounds.Maximum, "unbounded")}].",
            Rule.ExactlyOne => $"Specify exactly one of {Join(names)}.",
            Rule.AtMostOne => $"Specify at most one of {Join(names)}.",
            Rule.Requires => $"{names[0]} requires {names[1]}.",
            Rule.StandardInput => $"Only one of {Join(names)} can read from standard input.",
            _ => throw new InvalidOperationException("Unknown constraint rule.")
        };
    }

    static string Join(string[] names) => names.Length == 2 ? $"{names[0]} and {names[1]}" : string.Join(", ", names);
    static string Format(decimal? value, string fallback) => value?.ToString(CultureInfo.InvariantCulture) ?? fallback;

    internal bool IsSatisfied(IReadOnlyDictionary<string, object?> values)
    {
        object? Read(string key) => values.TryGetValue(key, out var value) ? value : null;
        if (Kind is Rule.Positive or Rule.Range)
        {
            var value = Read(Parameters[0]);
            if (value is null) return true;
            var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return Kind == Rule.Positive ? number > 0 :
                (Bounds!.Minimum is null || number >= Bounds.Minimum) && (Bounds.Maximum is null || number <= Bounds.Maximum);
        }
        if (Kind == Rule.Requires)
            return !Selected(Read(Parameters[0])) || Selected(Read(Parameters[1]));
        var count = Parameters.Count(key => Kind == Rule.StandardInput
            ? Read(key) is string text && text == CommandIo.StandardStreamPath
            : Selected(Read(key)));
        return Kind == Rule.ExactlyOne ? count == 1 : count <= 1;
    }

    static bool Selected(object? value) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        bool flag => flag,
        System.Collections.IEnumerable collection => collection.Cast<object?>().Any(),
        _ => true
    };
}
