using System.CommandLine;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Host.Cli;

/// <summary>
/// Builds and runs a command tree whose option values are merged into an <see cref="IConfiguration"/> pipeline
/// before being parsed by <see cref="ConfigurationParameterParser"/>.
/// </summary>
/// <remarks>
/// A <see cref="CliApplication"/> can be used as a console front-end for background jobs while still allowing the
/// same executable to fall back to ordinary host startup when no registered command name is present in
/// <c>args[0]</c>. This enables one <c>Program.cs</c> entrypoint to support both command execution and the normal
/// ASP.NET or worker-service boot path.
/// </remarks>
public sealed class CliApplication(string? description = null)
{
    readonly List<CliCommandNode> commands = [];
    readonly Dictionary<Type, List<Delegate>> parameterPipelines = [];
    readonly Dictionary<Type, List<Delegate>> executionPipelines = [];
    readonly Dictionary<Type, List<Delegate>> validationPipelines = [];
    readonly Dictionary<Type, List<CliDynamicBindingRegistration>> dynamicBindingPipelines = [];
    Action<IConfigurationBuilder>? configureConfiguration;
    string? environmentVariablePrefix;

    /// <summary>
    /// Root command description used for generated help.
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    /// Descriptions of the registered root commands and subcommands.
    /// </summary>
    public IReadOnlyList<CliCommandDescriptor> Commands =>
        [.. commands.SelectMany(static command => command.Describe())];

    /// <summary>
    /// Adds shared configuration providers that apply to every CLI command before environment variables and CLI values.
    /// </summary>
    /// <param name="configure">Callback used to register configuration providers.</param>
    /// <returns>The current application.</returns>
    public CliApplication WithConfiguration(Action<IConfigurationBuilder> configure)
    {
        configureConfiguration += Guard.RequireNotNull(configure);
        return this;
    }

    /// <summary>
    /// Enables environment variable configuration for CLI commands.
    /// </summary>
    /// <param name="prefix">Optional prefix filter applied to environment variables.</param>
    /// <returns>The current application.</returns>
    public CliApplication WithEnvironmentVariablePrefix(string? prefix = null)
    {
        environmentVariablePrefix = prefix;
        return this;
    }

    /// <summary>
    /// Defines configuration parameter mapping options.
    /// </summary>
    /// <typeparam name="TConfiguration">Configuration type the conventions apply to.</typeparam>
    /// <param name="configure">Convention step that mutates parameter metadata.</param>
    /// <returns>The current application.</returns>
    public CliApplication ConfigureParameters<TConfiguration>(Action<ConfigurationParameterOptions<TConfiguration>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        GetPipeline(parameterPipelines, typeof(TConfiguration)).Add(configure);
        foreach (var command in commands)
            command.ApplyParameterConfiguration(typeof(TConfiguration), configure);
        return this;
    }

    /// <summary>
    /// Registers execution middleware applied to every command.
    /// </summary>
    /// <typeparam name="TConfiguration">Configuration type the middleware applies to.</typeparam>
    /// <param name="middleware">Middleware invoked before the command handler.</param>
    /// <returns>The current application.</returns>
    public CliApplication Use<TConfiguration>(Func<CliCommandContext<TConfiguration>, CliCommandExecutionDelegate, Task<int>> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        GetPipeline(executionPipelines, typeof(TConfiguration)).Add(middleware);
        foreach (var command in commands)
            command.ApplyExecutionMiddleware(typeof(TConfiguration), middleware);
        return this;
    }

    /// <summary>
    /// Registers a validation delegate that runs before the command handler with configuration type <typeparamref name="TConfiguration"/>.
    /// </summary>
    /// <example>
    /// The following is an example of a validation delegate:
    /// <code language="csharp">
    ///(TrainCommandConfiguration config) =>
    ///   config.Dataset == "blocked"
    ///     ? new[] {"Dataset 'blocked' is not allowed."}
    ///     : []
    /// </code>
    /// </example>
    public CliApplication Validate<TConfiguration>(Delegate validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        GetPipeline(validationPipelines, typeof(TConfiguration)).Add(validate);
        foreach (var command in commands)
            command.ApplyValidation(typeof(TConfiguration), validate);
        return this;
    }

    /// <summary>
    /// Validates that dynamically bound command handlers can resolve their declared parameters.
    /// </summary>
    public IReadOnlyList<CliDynamicHandlerResolutionError> ValidateDynamicHandlers() =>
        [.. commands.SelectMany(static command => command.ValidateDynamicHandlers())];

    /// <summary>
    /// Registers a root command backed by a typed configuration object.
    /// </summary>
    /// <param name="name">Command name.</param>
    /// <param name="description">Optional help text.</param>
    /// <param name="execute">The delegate to execute when the command is invoked.</param>
    /// <param name="validate">The validation delegate to run before the command runs.</param>
    /// <typeparam name="TConfiguration">Typed configuration bound for the command.</typeparam>
    /// <returns>A builder used to configure the command.</returns>
    public CliCommandBuilder<TConfiguration> Command<TConfiguration>(string name, string? description = null, Delegate? execute = null, Delegate? validate = null)
    {
        var commandName = Guard.RequireNotNullOrWhiteSpace(name);
        EnsureUniqueChildName(commands, commandName);
        var command = new CliCommandBuilder<TConfiguration>(commandName, description, ApplyRegisteredPipelines);
        ApplyRegisteredPipelines(command);
        commands.Add(command);
        if (execute != null)
            command = command.OnExecute(execute);
        if (validate != null)
            command = command.Validate(validate);
        return command;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the argument list should be handled by the registered CLI command tree.
    /// </summary>
    /// <param name="args">Program arguments.</param>
    /// <returns>
    /// <see langword="true"/> when the first token matches a registered root command name; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This method is intended for mixed CLI and host executables. Host-style invocations such as
    /// <c>--urls http://localhost:5000</c> bypass the command tree and can fall through to the normal host startup.
    /// </remarks>
    public bool ShouldHandle(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
            return false;
        var candidate = args[0];
        return commands.Any(command => string.Equals(command.Name, candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses and executes the registered command tree.
    /// </summary>
    /// <param name="args">Program arguments.</param>
    /// <param name="ct">Cancellation token for async handlers.</param>
    /// <returns>The command exit code.</returns>
    public Task<int> InvokeAsync(IReadOnlyList<string> args, CancellationToken ct = default) =>
        InvokeAsync(args, options: null, ct);

    /// <summary>
    /// Parses and executes the registered command tree using invocation-specific output channels.
    /// </summary>
    public Task<int> InvokeAsync(IReadOnlyList<string> args, CliInvocationOptions? options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var rootCommand = BuildRootCommand(options);
        var parseResult = rootCommand.Parse(args);
        var invocationConfiguration = new InvocationConfiguration();
        if (options?.ErrorOutput is not null)
            invocationConfiguration.Error = options.ErrorOutput;

        return parseResult.InvokeAsync(invocationConfiguration, ct);
    }

    /// <summary>
    /// Executes the CLI command tree when the first argument matches a registered root command, otherwise runs the supplied fallback.
    /// </summary>
    /// <param name="args">Program arguments.</param>
    /// <param name="runDefaultAsync">Fallback host or application startup path.</param>
    /// <param name="ct">Cancellation token for async handlers.</param>
    /// <returns>The exit code produced by either the command handler or the fallback path.</returns>
    /// <remarks>
    /// This is the primary integration point for <c>Program.cs</c> when one executable needs to support both
    /// explicit commands and the normal ASP.NET or worker-service startup path.
    /// <code>
    /// var cli = new CliApplication("Training jobs")
    ///     .Command&lt;TrainCommandConfiguration&gt;("train", "Start a training run")
    ///     .OnExecute(async (context, cancellationToken) =&gt;
    ///     {
    ///         await RunTrainingAsync(context.Parameters, cancellationToken);
    ///         return 0;
    ///     });
    ///
    /// return await cli.RunOrFallbackAsync(args, static (remainingArgs, cancellationToken) =&gt;
    ///     RunHostAsync(remainingArgs, cancellationToken));
    /// </code>
    /// </remarks>
    public Task<int> RunOrFallbackAsync(IReadOnlyList<string> args, Func<IReadOnlyList<string>, CancellationToken, Task<int>> runDefaultAsync, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runDefaultAsync);
        return ShouldHandle(args) ? InvokeAsync(args, ct) : runDefaultAsync(args, ct);
    }

    internal void ApplySharedConfiguration(IConfigurationBuilder builder) => configureConfiguration?.Invoke(builder);

    internal string? EnvironmentVariablePrefix => environmentVariablePrefix;

    internal void RegisterDynamicBinding<TConfiguration>(Type contextType, Func<CliValidationServicesScope>? createValidationServicesScope)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        GetDynamicBindingPipeline(typeof(TConfiguration)).Add(new(contextType, createValidationServicesScope));
        foreach (var command in commands)
            command.ApplyDynamicBindingMetadata(typeof(TConfiguration), contextType, createValidationServicesScope);
    }

    void ApplyRegisteredPipelines(CliCommandNode command)
    {
        foreach (var (configurationType, configureSteps) in parameterPipelines)
        {
            foreach (var step in configureSteps)
                command.ApplyParameterConfiguration(configurationType, step);
        }

        foreach (var (configurationType, middlewareSteps) in executionPipelines)
        {
            foreach (var step in middlewareSteps)
                command.ApplyExecutionMiddleware(configurationType, step);
        }

        foreach (var (configurationType, validationSteps) in validationPipelines)
        {
            foreach (var step in validationSteps)
                command.ApplyValidation(configurationType, step);
        }

        foreach (var (configurationType, registrations) in dynamicBindingPipelines)
        {
            foreach (var registration in registrations)
                command.ApplyDynamicBindingMetadata(configurationType, registration.ContextType, registration.CreateValidationServicesScope);
        }
    }

    static List<Delegate> GetPipeline(Dictionary<Type, List<Delegate>> pipelines, Type configurationType)
    {
        if (!pipelines.TryGetValue(configurationType, out var steps))
        {
            steps = [];
            pipelines[configurationType] = steps;
        }
        return steps;
    }

    List<CliDynamicBindingRegistration> GetDynamicBindingPipeline(Type configurationType)
    {
        if (!dynamicBindingPipelines.TryGetValue(configurationType, out var steps))
        {
            steps = [];
            dynamicBindingPipelines[configurationType] = steps;
        }

        return steps;
    }

    static void EnsureUniqueChildName(IEnumerable<CliCommandNode> existing, string name)
    {
        if (existing.Any(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A command named '{name}' is already registered at this level.");
    }

    RootCommand BuildRootCommand(CliInvocationOptions? options)
    {
        var root = string.IsNullOrWhiteSpace(Description) ? new RootCommand() : new RootCommand(Description);
        foreach (var command in commands)
            root.Subcommands.Add(command.BuildCommand(this, options));
        return root;
    }

    sealed record CliDynamicBindingRegistration(
        Type ContextType,
        Func<CliValidationServicesScope>? CreateValidationServicesScope
        );
}
