using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Host.Cli;

/// <summary>
/// Fluent builder for a typed CLI command that merges CLI values into configuration before binding.
/// </summary>
/// <typeparam name="TConfiguration">Typed configuration bound for the command.</typeparam>
public sealed class CliCommandBuilder<TConfiguration>(
    string name,
    string? description = null,
    Action<CliCommandNode>? applyRegisteredPipelines = null
    ) : CliCommandNode
{
    readonly ConfigurationParameterOptions<TConfiguration> parameterOptions = new();
    readonly List<CliCommandArgument> arguments = [];
    readonly List<CliCommandNode> subcommands = [];
    readonly List<Func<CliCommandContext<TConfiguration>, CliCommandExecutionDelegate, Task<int>>> middleware = [];
    readonly List<CliCommandValidationDelegate> validators = [];
    readonly List<Delegate> dynamicValidators = [];
    Action<IConfigurationBuilder>? configureConfiguration;
    CliCommandExecutionDelegate? handler;
    Delegate? dynamicHandler;
    Type effectiveContextType = typeof(CliCommandContext<TConfiguration>);
    Func<CliValidationServicesScope>? validationServicesScopeFactory;

    /// <summary>
    /// Command name used on the command line.
    /// </summary>
    public override string Name { get; } = Guard.RequireNotNullOrWhiteSpace(name);

    /// <summary>
    /// Optional help description for the command.
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    /// Effective expression-based configuration parameter overrides registered for the command.
    /// </summary>
    public IReadOnlyList<ConfigurationParameterOption> Options => 
        new ReadOnlyCollection<ConfigurationParameterOption>(parameterOptions.Options.ToList());

    /// <summary>
    /// Adds command-specific configuration providers that run before environment variables and CLI values.
    /// </summary>
    /// <param name="configure">Callback used to register configuration providers.</param>
    /// <returns>The current builder.</returns>
    public CliCommandBuilder<TConfiguration> ConfigureConfiguration(Action<IConfigurationBuilder> configure)
    {
        configureConfiguration += Guard.RequireNotNull(configure);
        return this;
    }

    /// <summary>
    /// Configures parameter metadata using the existing <see cref="ConfigurationParameterOptions{T}"/> fluent API.
    /// </summary>
    /// <param name="configure">Callback that mutates the command's configuration parameter options.</param>
    /// <returns>The current builder.</returns>
    public CliCommandBuilder<TConfiguration> ConfigureParameters(Action<ConfigurationParameterOptions<TConfiguration>> configure)
    {
        Guard.RequireNotNull(configure)(parameterOptions);
        return this;
    }

    /// <summary>
    /// Registers execution middleware for the command.
    /// </summary>
    /// <param name="invoke">Middleware invoked before the command handler.</param>
    /// <returns>The current builder.</returns>
    public CliCommandBuilder<TConfiguration> Use(Func<CliCommandContext<TConfiguration>, CliCommandExecutionDelegate, Task<int>> invoke)
    {
        middleware.Add(Guard.RequireNotNull(invoke));
        return this;
    }

    /// <summary>
    /// Starts configuring metadata overrides for a mapped configuration property.
    /// </summary>
    /// <param name="member">Property selector rooted at <typeparamref name="TConfiguration"/>.</param>
    /// <typeparam name="TParameter">Selected property type.</typeparam>
    /// <returns>A fluent parameter override builder.</returns>
    public ConfigurationParameterOptionBuilder Map<TParameter>(Expression<Func<TConfiguration, TParameter>> member) =>
        parameterOptions.Map(member);

    /// <summary>
    /// Maps a configuration property to a positional command argument instead of a generated option.
    /// </summary>
    /// <param name="member">Leaf property selector rooted at <typeparamref name="TConfiguration"/>.</param>
    /// <typeparam name="TParameter">Selected property type.</typeparam>
    /// <returns>A fluent argument builder.</returns>
    public CliArgumentBuilder Argument<TParameter>(Expression<Func<TConfiguration, TParameter>> member)
    {
        ArgumentNullException.ThrowIfNull(member);

        var propertyChain = CliExpressionPath.CapturePropertyChain(member);
        var path = CliExpressionPath.CreateFieldPath(propertyChain);
        var existingIndex = arguments.FindIndex(argument => argument.Path == path);
        if (existingIndex < 0)
        {
            arguments.Add(new(PropertyName: propertyChain[^1].Name, Path: path));
            existingIndex = arguments.Count - 1;
        }

        var index = existingIndex;
        return new(update => arguments[index] = update(arguments[index]));
    }

    /// <summary>
    /// Registers a subcommand rooted under the current command.
    /// </summary>
    /// <param name="name">Subcommand name.</param>
    /// <param name="description">Optional help text.</param>
    /// <param name="validate">The delegate to execute before execution.</param>
    /// <param name="execute">The delegate that executes the command</param>
    /// <typeparam name="TSubConfiguration">Typed configuration bound for the subcommand.</typeparam>
    /// <returns>A fluent builder for the subcommand.</returns>
    public CliCommandBuilder<TSubConfiguration> SubCommand<TSubConfiguration>(string name, string? description = null, Delegate? validate = null, Delegate? execute = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureUniqueChildName(subcommands, name: name);
        var command = new CliCommandBuilder<TSubConfiguration>(name: name, description: description, applyRegisteredPipelines: applyRegisteredPipelines);
        applyRegisteredPipelines?.Invoke(command);
        subcommands.Add(command);
        if (validate is not null) 
            command.Validate(validate);
        if (execute is not null) 
            command.OnExecute(execute);
        return command;
    }

    /// <summary>
    /// Sets a command handler whose parameters are resolved through the CLI binding pipeline.
    /// </summary>
    /// <param name="execute">Command handler delegate.</param>
    /// <returns>The current builder.</returns>
    public CliCommandBuilder<TConfiguration> OnExecute(Delegate execute)
    {
        dynamicHandler = Guard.RequireNotNull(execute);
        handler = BindHandler(dynamicHandler);
        return this;
    }

    internal override Command BuildCommand(CliApplication application, CliInvocationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(application);

        var command = string.IsNullOrWhiteSpace(Description) ? new Command(Name) : new Command(Name, Description);
        var descriptors = ConfigurationParameterParser.Describe(parameterOptions);
        var descriptorsByPath = descriptors.ToDictionary(descriptor => descriptor.Path, descriptor => descriptor);
        var positionalPaths = arguments.Select(argument => argument.Path).ToHashSet();
        List<CliSymbolBinding> bindings = [];

        foreach (var arg in arguments)
        {
            if (!descriptorsByPath.TryGetValue(arg.Path, out var descriptor))
                throw new InvalidOperationException($"CLI argument '{arg.Path}' does not map to a bindable configuration property.");

            var cmdArgument = CreateArgument(arg, descriptor);
            command.Arguments.Add(cmdArgument);
            bindings.Add(new(descriptor, Symbol: cmdArgument));
        }

        foreach (var descriptor in descriptors.Where(descriptor => !positionalPaths.Contains(descriptor.Path)))
        {
            var option = CreateOption(descriptor);
            command.Options.Add(option);
            bindings.Add(new(descriptor, Symbol: option));
        }

        foreach (var subcommand in subcommands)
            command.Subcommands.Add(subcommand.BuildCommand(application, options));

        if (handler is not null)
        {
            command.SetAction((parseResult, ct) => InvokeAsync(application, parseResult, bindings, options, ct));
        }
        else if (subcommands.Count > 0)
        {
            command.SetAction(parseResult =>
            {
                CliOutput.Create(parseResult, options).WriteErrorLine($"Command '{Name}' requires a subcommand.");
                return 1;
            });
        }
        else
        {
            command.SetAction((Func<ParseResult, int>)(_ => throw new InvalidOperationException($"Command '{Name}' does not have an execution handler.")));
        }

        return command;
    }

    async Task<int> InvokeAsync(CliApplication application, ParseResult parseResult, IReadOnlyList<CliSymbolBinding> bindings, CliInvocationOptions? options, CancellationToken ct)
    {
        var output = CliOutput.Create(parseResult, options);
        try
        {
            var configuration = BuildConfiguration(application, bindings, parseResult);
            var parameters = ConfigurationParameterParser.Parse(configuration, parameterOptions);
            var context = new CliCommandContext<TConfiguration>(parameters, configuration, parseResult, ct, output, serviceProvider: null);
            var pipeline = BuildExecutionPipeline();
            return handler is null ? 0 : await pipeline(context).ConfigureAwait(false);
        }
        catch (ConfigurationParameterParseException ex)
        {
            output.WriteErrorLine(ex.Message);
            foreach (var detail in ex.Errors)
                output.WriteErrorLine($"  - {detail}");
            return 1;
        }
    }

    IConfigurationRoot BuildConfiguration(CliApplication application, IReadOnlyList<CliSymbolBinding> bindings, ParseResult parseResult)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            switch (binding.Symbol)
            {
                case Option<string?> option when parseResult.GetResult(option) is not null:
                    values[binding.Descriptor.ConfigurationKey] = parseResult.GetValue(option);
                    break;
                case Option<string[]> option when parseResult.GetResult(option) is not null:
                    AddCollectionValues(values, binding.Descriptor.ConfigurationKey, parseResult.GetValue(option));
                    break;
                case Argument<string?> argument when parseResult.GetResult(argument) is not null:
                    values[binding.Descriptor.ConfigurationKey] = parseResult.GetValue(argument);
                    break;
                case Argument<string[]> argument when parseResult.GetResult(argument) is not null:
                    AddCollectionValues(values, binding.Descriptor.ConfigurationKey, parseResult.GetValue(argument));
                    break;
            }
        }

        var builder = new ConfigurationBuilder();
        application.ApplySharedConfiguration(builder);
        configureConfiguration?.Invoke(builder);

        if (application.EnvironmentVariablePrefix is null)
            builder.AddEnvironmentVariables();
        else
            builder.AddEnvironmentVariables(prefix: application.EnvironmentVariablePrefix);

        builder.AddInMemoryCollection(values);
        return builder.Build();
    }

    static void AddCollectionValues(IDictionary<string, string?> values, string configurationKey, IReadOnlyList<string>? entries)
    {
        if (entries is null)
            return;

        var index = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var value = entries[i];
            if (string.IsNullOrWhiteSpace(value))
                continue;

            values[$"{configurationKey}:{index++}"] = value;
        }
    }

    static Option CreateOption(ConfigurationParameterDescriptor descriptor)
    {
        if (IsStringCollectionType(descriptor.ParameterType))
            return CreateStringCollectionOption(descriptor);

        return CreateScalarOption(descriptor);
    }

    static Option<string?> CreateScalarOption(ConfigurationParameterDescriptor descriptor)
    {
        var aliases = descriptor.CliShortName is null
            ? Array.Empty<string>()
            : [descriptor.CliShortName];

        var option = new Option<string?>(descriptor.CliName, aliases)
        {
            Description = BuildDescription(descriptor.Description, descriptor.Required, descriptor.AllowedValues)
        };

        if (descriptor.AllowedValues.Count > 0)
            option.AcceptOnlyFromAmong([.. descriptor.AllowedValues]);

        return option;
    }

    static Option<string[]> CreateStringCollectionOption(ConfigurationParameterDescriptor descriptor)
    {
        var aliases = descriptor.CliShortName is null ? Array.Empty<string>() : [descriptor.CliShortName];
        return new(descriptor.CliName, aliases)
        {
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result => ParseStringCollectionValues(result, descriptor),
            Description = BuildDescription(descriptor.Description, descriptor.Required, descriptor.AllowedValues)
        };
    }

    static Argument CreateArgument(CliCommandArgument argumentDefinition, ConfigurationParameterDescriptor descriptor)
    {
        if (IsStringCollectionType(descriptor.ParameterType))
            return CreateStringCollectionArgument(argumentDefinition, descriptor);

        return CreateScalarArgument(argumentDefinition, descriptor);
    }

    static Argument<string?> CreateScalarArgument(CliCommandArgument argumentDefinition, ConfigurationParameterDescriptor descriptor)
    {
        var argumentName = argumentDefinition.Name ?? descriptor.ConfigurationKey.Split(':', StringSplitOptions.RemoveEmptyEntries).Last();

        var argument = new Argument<string?>(argumentName)
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = BuildDescription(
                description: argumentDefinition.Description ?? descriptor.Description,
                required: descriptor.Required,
                allowedValues: descriptor.AllowedValues
                )
        };

        if (descriptor.AllowedValues.Count > 0)
            argument.AcceptOnlyFromAmong([..descriptor.AllowedValues]);

        return argument;
    }

    static Argument<string[]> CreateStringCollectionArgument(CliCommandArgument argumentDefinition, ConfigurationParameterDescriptor descriptor)
    {
        var argumentName = argumentDefinition.Name
                           ?? descriptor.ConfigurationKey.Split(':', StringSplitOptions.RemoveEmptyEntries).Last();

        return new(argumentName)
        {
            Arity = descriptor.Required ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore,
            CustomParser = result => ParseStringCollectionValues(result, descriptor),
            Description = BuildDescription(
                argumentDefinition.Description ?? descriptor.Description,
                descriptor.Required,
                descriptor.AllowedValues
                )
        };
    }

    static string[] ParseStringCollectionValues(ArgumentResult result, ConfigurationParameterDescriptor descriptor)
    {
        var values = result.Tokens
            .Select(token => token.Value)
            .SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .WhereNotNullOrWhiteSpace()
            .ToArray();

        if (descriptor.AllowedValues.Count > 0)
        {
            foreach (var invalidValue in values
                         .Where(value => !descriptor.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                result.AddError($"Argument '{descriptor.CliName}' must contain only values from [{string.Join(", ", descriptor.AllowedValues)}], but included '{invalidValue}'.");
            }
        }

        return values;
    }

    static bool IsStringCollectionType(Type type)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        if (targetType == typeof(string))
            return false;

        if (targetType.IsArray)
            return targetType.GetElementType() == typeof(string);

        return targetType.GetInterfaces()
            .Append(targetType)
            .Any(static candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && candidate.GetGenericArguments()[0] == typeof(string)
                );
    }

    static string? BuildDescription(string? description, bool required, IReadOnlyList<string> allowedValues)
    {
        List<string> metadata = [];
        if (required)
            metadata.Add("required from any configuration source");
        if (allowedValues.Count > 0)
            metadata.Add($"allowed: {string.Join(", ", allowedValues)}");

        if (metadata.Count == 0)
            return description;

        return string.IsNullOrWhiteSpace(description) ? $"[{string.Join("; ", metadata)}]" : $"{description} [{string.Join("; ", metadata)}]";
    }

    static void EnsureUniqueChildName(IEnumerable<CliCommandNode> existing, string name)
    {
        if (existing.Any(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A command named '{name}' is already registered at this level.");
    }

    CliCommandExecutionDelegate BuildExecutionPipeline()
    {
        var execute = BuildValidatedHandler();
        foreach (var component in middleware.AsEnumerable().Reverse())
        {
            var next = execute;
            execute = context => component(RequireTypedContext(context), next);
        }
        return execute;
    }

    static CliCommandContext<TConfiguration> RequireTypedContext(CliCommandContext context) =>
        context as CliCommandContext<TConfiguration> ?? throw new InvalidOperationException($"Expected CLI context '{typeof(CliCommandContext<TConfiguration>).FullName}', but received '{context.GetType().FullName}'.");

    static CliCommandExecutionDelegate BindHandler(Delegate execute) =>
        CliCommandHandlerBinding.Bind(execute);

    static CliCommandValidationDelegate BindValidator(Delegate validate) =>
        CliCommandHandlerBinding.BindValidator(validate);

    CliCommandExecutionDelegate BuildValidatedHandler() => async context =>
    {
        var validation = await ValidateAsync(context).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                context.Output.WriteErrorLine(error);
            return 1;
        }
        return handler is null ? 0 : await handler(context).ConfigureAwait(false);
    };

    async Task<CliValidationResult> ValidateAsync(CliCommandContext context)
    {
        List<string>? errors = null;
        foreach (var validator in validators)
        {
            var result = await validator(context).ConfigureAwait(false);
            if (result.IsValid)
                continue;

            errors ??= [];
            errors.AddRange(result.Errors);
        }

        return errors is null ? CliValidationResult.Success : CliValidationResult.Failure(errors);
    }

    public CliCommandBuilder<TConfiguration> Validate(Delegate validate)
    {
        dynamicValidators.Add(Guard.RequireNotNull(validate));
        validators.Add(BindValidator(validate));
        return this;
    }

    internal override void ApplyParameterConfiguration(Type configurationType, Delegate configure)
    {
        if (configurationType == typeof(TConfiguration))
            ConfigureParameters((Action<ConfigurationParameterOptions<TConfiguration>>)configure);

        foreach (var subcommand in subcommands)
            subcommand.ApplyParameterConfiguration(configurationType, configure);
    }

    internal override void ApplyExecutionMiddleware(Type configurationType, Delegate invoke)
    {
        if (configurationType == typeof(TConfiguration))
            Use((Func<CliCommandContext<TConfiguration>, CliCommandExecutionDelegate, Task<int>>)invoke);

        foreach (var subcommand in subcommands)
            subcommand.ApplyExecutionMiddleware(configurationType, invoke);
    }

    internal override void ApplyValidation(Type configurationType, Delegate validate)
    {
        if (configurationType == typeof(TConfiguration))
            Validate(validate);

        foreach (var subcommand in subcommands)
            subcommand.ApplyValidation(configurationType, validate);
    }

    internal override void ApplyDynamicBindingMetadata(Type configurationType, Type contextType, Func<CliValidationServicesScope>? createValidationServicesScope)
    {
        if (configurationType == typeof(TConfiguration))
            ConfigureDynamicBinding(contextType, createValidationServicesScope);

        foreach (var subcommand in subcommands)
            subcommand.ApplyDynamicBindingMetadata(configurationType, contextType, createValidationServicesScope);
    }

    internal override IReadOnlyList<CliCommandDescriptor> Describe(string? parentPath = null)
    {
        var path = string.IsNullOrWhiteSpace(parentPath) ? Name : $"{parentPath} {Name}";
        return
        [
            new(Name: Name,
                Path: path,
                ConfigurationType: typeof(TConfiguration),
                EffectiveContextType: effectiveContextType,
                DynamicHandler: dynamicHandler?.Method,
                DynamicValidators: [.. dynamicValidators.Select(static validator => validator.Method)],
                Subcommands: [.. subcommands.SelectMany(subcommand => subcommand.Describe(path))]
            )
        ];
    }

    internal override IReadOnlyList<CliDynamicHandlerResolutionError> ValidateDynamicHandlers(string? parentPath = null)
    {
        List<CliDynamicHandlerResolutionError> errors = [];
        var path = string.IsNullOrWhiteSpace(parentPath) ? Name : $"{parentPath} {Name}";

        if (dynamicHandler is not null)
            ValidateDynamicCallable(errors, path, dynamicHandler, kind: "handler");

        foreach (var validator in dynamicValidators)
            ValidateDynamicCallable(errors, path, validator, kind: "validator");

        foreach (var subcommand in subcommands)
            errors.AddRange(subcommand.ValidateDynamicHandlers(path));

        return errors;
    }

    void ConfigureDynamicBinding(Type contextType, Func<CliValidationServicesScope>? createValidationServicesScope)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        if (!typeof(CliCommandContext<TConfiguration>).IsAssignableFrom(contextType))
        {
            throw new InvalidOperationException(
                $"Dynamic CLI binding context '{contextType.FullName}' must derive from '{typeof(CliCommandContext<TConfiguration>).FullName}'.");
        }

        effectiveContextType = contextType;
        validationServicesScopeFactory = createValidationServicesScope;
    }

    void ValidateDynamicCallable(List<CliDynamicHandlerResolutionError> errors, string path, Delegate execute, string kind)
    {
        try
        {
            using var servicesScope = validationServicesScopeFactory?.Invoke();
            foreach (var parameter in execute.Method.GetParameters())
            {
                if (!CanResolveDynamicParameter(parameter.ParameterType, servicesScope?.Services))
                {
                    errors.Add(new(
                        CommandPath: path,
                        ParameterName: parameter.Name,
                        ParameterType: parameter.ParameterType,
                        Message: $"Unable to resolve dynamic {kind} parameter '{parameter.Name}' of type '{parameter.ParameterType.FullName}'."
                        )
                    );
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(new(
                CommandPath: path,
                ParameterName: null,
                ParameterType: typeof(IServiceProvider),
                Message: $"Unable to create validation services for command '{path}': {ex.Message}"
                )
            );
        }
    }

    bool CanResolveDynamicParameter(Type parameterType, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(parameterType);

        if (parameterType.IsAssignableFrom(effectiveContextType))
            return true;

        if (parameterType == typeof(CancellationToken)
            || parameterType == typeof(ParseResult)
            || parameterType == typeof(CliOutput)
            || parameterType == typeof(IConfiguration)
            || typeof(IConfigurationRoot).IsAssignableFrom(parameterType))
        {
            return true;
        }

        if (parameterType.IsAssignableFrom(typeof(TConfiguration)))
            return true;

        return services?.GetService(parameterType) is not null;
    }
}

/// <summary>
/// Base type used internally to compose root commands and subcommands.
/// </summary>
public abstract class CliCommandNode
{
    /// <summary>
    /// Command name used on the command line.
    /// </summary>
    public abstract string Name { get; }

    internal abstract Command BuildCommand(CliApplication application, CliInvocationOptions? options);
    
    internal abstract void ApplyParameterConfiguration(Type configurationType, Delegate configure);
    
    internal abstract void ApplyExecutionMiddleware(Type configurationType, Delegate invoke);

    internal abstract void ApplyValidation(Type configurationType, Delegate validate);

    internal abstract void ApplyDynamicBindingMetadata(Type configurationType, Type contextType, Func<CliValidationServicesScope>? createValidationServicesScope);

    internal abstract IReadOnlyList<CliCommandDescriptor> Describe(string? parentPath = null);

    internal abstract IReadOnlyList<CliDynamicHandlerResolutionError> ValidateDynamicHandlers(string? parentPath = null);
}

static class CliExpressionPath
{
    public static FieldPath CreateFieldPath(IReadOnlyList<PropertyInfo> propertyChain) =>
        new([.. propertyChain.Select(property => FieldPathSegment.ForField(property.Name))]);

    public static IReadOnlyList<PropertyInfo> CapturePropertyChain<T, TParameter>(Expression<Func<T, TParameter>> selector)
    {
        List<PropertyInfo> reversedProperties = [];
        var current = selector.Body;

        while (true)
        {
            current = StripConvert(current);
            switch (current)
            {
                case MemberExpression { Member: PropertyInfo property, Expression: not null } member:
                    reversedProperties.Add(property);
                    current = member.Expression;
                    continue;
                case ParameterExpression parameter when ReferenceEquals(parameter, selector.Parameters[0]):
                    if (reversedProperties.Count == 0)
                        throw new ArgumentException("Selector must reference a property path.", nameof(selector));

                    reversedProperties.Reverse();
                    return reversedProperties;
                default:
                    throw new ArgumentException("Selector must be a property path rooted at the lambda parameter.", nameof(selector));
            }
        }
    }

    static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
            current = unary.Operand;

        return current;
    }
}

sealed record CliSymbolBinding(
    ConfigurationParameterDescriptor Descriptor,
    object Symbol
    );
