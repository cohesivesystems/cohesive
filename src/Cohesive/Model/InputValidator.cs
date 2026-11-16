using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>Validates inputs using instance-scoped, cached validators for their declared CLR types.</summary>
public interface IInputValidator
{
    /// <summary>Validates a fresh projection of the input; never caches input data or results.</summary>
    [RequiresUnreferencedCode("Typed input projection requires preserved CLR members.")]
    [RequiresDynamicCode("Typed input projection may compile CLR projection delegates.")]
    DocumentValidationResult Validate<T>(T input) where T : notnull;

    /// <summary>Gets or prepares a validator for the declared type. Authoring errors propagate and are cached.</summary>
    [RequiresUnreferencedCode("CLR shape authoring requires preserved members and attributes.")]
    IInputValidator<T> For<T>() where T : notnull;
}

/// <summary>A reusable validator for one declared input contract.</summary>
public interface IInputValidator<T> where T : notnull
{
    /// <summary>Validates a fresh projection; null input yields a diagnostic.</summary>
    [RequiresUnreferencedCode("Typed input projection requires preserved CLR members.")]
    [RequiresDynamicCode("Typed input projection may compile CLR projection delegates.")]
    DocumentValidationResult Validate(T input);

    /// <summary>Validates an existing portable value without CLR projection.</summary>
    DocumentValidationResult ValidatePortable(ObservationValue input);
}

/// <summary>Authoring policy for a shared input validator, snapshotted when it is created.</summary>
/// <remarks>Callbacks and metadata providers must be deterministic and thread-safe; do not mutate their captured state.</remarks>
public sealed class InputValidationOptions
{
    internal bool DataAnnotations;
    internal readonly List<Action<ClrShapeGraphBuilder>> Configuration = [];
    internal readonly Dictionary<Type, Registration> Types = [];
    internal sealed record Registration(Action<ClrShapeGraphBuilder>? Configure, Action<InputValidator> Prepare);

    /// <summary>Enables the supported DataAnnotations subset for every input type.</summary>
    public InputValidationOptions UseDataAnnotations() { DataAnnotations = true; return this; }

    /// <summary>Adds common authoring configuration, applied before type-specific configuration.</summary>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public InputValidationOptions Configure(Action<ClrShapeGraphBuilder> configure)
    { ArgumentNullException.ThrowIfNull(configure); Configuration.Add(configure); return this; }

    /// <summary>Registers a type for eager preparation, with optional per-type authoring configuration.</summary>
    /// <remarks>The type is registered as the root automatically. Do not add other roots in the callback.</remarks>
    /// <exception cref="ArgumentException">The type was already registered.</exception>
    [RequiresUnreferencedCode("CLR shape authoring requires preserved members and attributes.")]
    public InputValidationOptions For<T>(Action<ClrShapeGraphBuilder>? configure = null) where T : notnull
    { Types.Add(typeof(T), new(configure, static validator => validator.For<T>())); return this; }
}

/// <summary>A shared cache of validators and authored shapes, isolated by configuration instance.</summary>
/// <remarks>
/// Dispatch uses typeof(T), never the runtime subtype. The finite application type set should bound the cache;
/// use separate short-lived instances for dynamically generated types. No request or validation result is retained.
/// </remarks>
public sealed class InputValidator : IInputValidator
{
    readonly bool dataAnnotations;
    readonly Action<ClrShapeGraphBuilder>[] configuration;
    readonly Dictionary<Type, InputValidationOptions.Registration> types;
    readonly ConcurrentDictionary<Type, Lazy<object>> validators = new();

    InputValidator(InputValidationOptions options)
    {
        dataAnnotations = options.DataAnnotations;
        configuration = options.Configuration.ToArray();
        types = new(options.Types);
    }

    /// <summary>Creates an independent cache and prepares all explicitly registered types immediately.</summary>
    /// <exception cref="Exception">Exceptions from authoring configuration or unsupported declarations propagate.</exception>
    [RequiresUnreferencedCode("CLR shape authoring requires preserved members and attributes.")]
    public static InputValidator Create(Action<InputValidationOptions>? configure = null)
    {
        var options = new InputValidationOptions();
        configure?.Invoke(options);
        var validator = new InputValidator(options);
        foreach (var registration in validator.types.Values) registration.Prepare(validator);
        return validator;
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("Typed input projection requires preserved CLR members.")]
    [RequiresDynamicCode("Typed input projection may compile CLR projection delegates.")]
    public DocumentValidationResult Validate<T>(T input) where T : notnull => For<T>().Validate(input);

    /// <inheritdoc />
    [RequiresUnreferencedCode("CLR shape authoring requires preserved members and attributes.")]
    public IInputValidator<T> For<T>() where T : notnull => (IInputValidator<T>)validators.GetOrAdd(typeof(T),
        static (_, owner) => new Lazy<object>(() => owner.Prepare<T>(), LazyThreadSafetyMode.ExecutionAndPublication), this).Value;

    [RequiresUnreferencedCode("CLR shape authoring requires preserved members and attributes.")]
    IInputValidator<T> Prepare<T>() where T : notnull
    {
        var builder = new ClrShapeGraphBuilder();
        if (dataAnnotations) builder.UseDataAnnotations();
        foreach (var configure in configuration) configure(builder);
        if (types.TryGetValue(typeof(T), out var registration)) registration.Configure?.Invoke(builder);
        var shapes = builder.AddShape<T>().BuildResult();
        return new PreparedInputValidator<T>(shapes.GetShape<T>());
    }

    sealed class PreparedInputValidator<T>(GraphShapeId shape) : IInputValidator<T> where T : notnull
    {
        [RequiresUnreferencedCode("Typed input projection requires preserved CLR members.")]
        [RequiresDynamicCode("Typed input projection may compile CLR projection delegates.")]
        public DocumentValidationResult Validate(T input) => ValidatePortable(ObservationValue.FromObject(input));
        public DocumentValidationResult ValidatePortable(ObservationValue input) => ObservationValidator.ValidateInput(input, shape);
    }
}
