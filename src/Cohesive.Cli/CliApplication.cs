using System.CommandLine;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli;

/// <summary>
/// Builds and runs a command tree whose option values are merged into an <see cref="IConfiguration"/> pipeline
/// before being parsed by <see cref="ConfigurationParameterParser"/>.
/// </summary>
/// <remarks>
/// A <see cref="CliApplication"/> can be used as a console front-end for background jobs while still allowing the
/// same executable to fall back to ordinary host startup when no registered command name is present in
/// <c>args[0]</c>. This enables one <c>Program.cs</c> entrypoint to support both command execution and the normal
/// ASP.NET or worker-service boot path. Supplied standard streams and writers remain caller-owned and are not
/// disposed by the application.
/// </remarks>
/// <param name="description">Optional root command description used for generated help.</param>
/// <param name="standardInput">Raw standard input stream, or <see langword="null"/> to open the process stream.</param>
/// <param name="standardOutput">Raw standard output stream, or <see langword="null"/> to open the process stream.</param>
/// <param name="standardError">Standard error writer, or <see langword="null"/> to use <see cref="Console.Error"/>.</param>
/// <exception cref="ArgumentException">
/// <paramref name="standardInput"/> is not readable, or <paramref name="standardOutput"/> is not writable.
/// </exception>
public sealed class CliApplication(
    string? description = null,
    Stream? standardInput = null,
    Stream? standardOutput = null,
    TextWriter? standardError = null)
{
    readonly List<CliCommandNode> commands = [];
    readonly Dictionary<Type, List<Delegate>> parameterPipelines = [];
    readonly Dictionary<Type, List<Delegate>> executionPipelines = [];
    readonly Dictionary<Type, List<Delegate>> validationPipelines = [];
    readonly Dictionary<Type, List<CliDynamicBindingRegistration>> dynamicBindingPipelines = [];
    Action<IConfigurationBuilder>? configureConfiguration;
    bool useConsoleCancellation;
    string? environmentVariablePrefix;

    /// <summary>
    /// Root command description used for generated help.
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>Raw standard input stream available to every command context.</summary>
    public Stream StandardInput { get; } = RequireReadable(
        standardInput ?? Console.OpenStandardInput(),
        nameof(standardInput));

    /// <summary>Raw standard output stream available to every command context.</summary>
    public Stream StandardOutput { get; } = RequireWritable(
        standardOutput ?? Console.OpenStandardOutput(),
        nameof(standardOutput));

    /// <summary>Standard error writer used when an invocation does not override error output.</summary>
    public TextWriter StandardError { get; } = standardError ?? Console.Error;

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
    /// Cancels command invocations when the process receives <see cref="Console.CancelKeyPress"/>.
    /// </summary>
    /// <returns>The current application.</returns>
    /// <remarks>
    /// The event handler is attached only for the lifetime of an invocation, prevents immediate process termination,
    /// and forwards the signal through <see cref="CliCommandContext.CancellationToken"/>. An invocation token supplied
    /// to <see cref="InvokeAsync(IReadOnlyList{string}, CancellationToken)"/> remains linked to the same context token.
    /// </remarks>
    public CliApplication UseConsoleCancellation()
    {
        useConsoleCancellation = true;
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
        {
            command.ApplyParameterConfiguration(typeof(TConfiguration), configure);
        }

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
        {
            command.ApplyExecutionMiddleware(typeof(TConfiguration), middleware);
        }

        return this;
    }

    /// <summary>
    /// Registers a typed configuration validator without requiring a delegate cast.
    /// </summary>
    /// <typeparam name="TConfiguration">Configuration type the validator applies to.</typeparam>
    /// <param name="validate">Validator that receives the bound command configuration.</param>
    /// <returns>The current application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="validate"/> is <see langword="null"/>.</exception>
    public CliApplication Validate<TConfiguration>(
        Func<TConfiguration, IReadOnlyList<string>> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        return Validate<TConfiguration>((Delegate)validate);
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
    /// <typeparam name="TConfiguration">Configuration type the validator applies to.</typeparam>
    /// <param name="validate">Validator whose parameters and result are adapted by the CLI binding pipeline.</param>
    /// <returns>The current application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="validate"/> is <see langword="null"/>.</exception>
    public CliApplication Validate<TConfiguration>(Delegate validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        GetPipeline(validationPipelines, typeof(TConfiguration)).Add(validate);
        foreach (var command in commands)
        {
            command.ApplyValidation(typeof(TConfiguration), validate);
        }

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
        {
            command = command.OnExecute(execute);
        }

        if (validate != null)
        {
            command = command.Validate(validate);
        }

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
        {
            return false;
        }

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
    /// <param name="args">Program arguments.</param>
    /// <param name="options">Optional output and serialization settings for this invocation.</param>
    /// <param name="ct">Cancellation token for parsing and command execution.</param>
    /// <returns>The command exit code.</returns>
    public async Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        CliInvocationOptions? options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        StreamWriter? standardOutputWriter = null;
        var effectiveStandardOutput = options?.StandardOutput;
        if (effectiveStandardOutput is null)
        {
            standardOutputWriter = CliStandardStreams.OpenUtf8Writer(StandardOutput);
            effectiveStandardOutput = standardOutputWriter;
        }

        var effectiveOptions = new CliInvocationOptions
        {
            StandardOutput = effectiveStandardOutput,
            ErrorOutput = options?.ErrorOutput ?? StandardError,
            JsonSerializerOptions = options?.JsonSerializerOptions
        };

        try
        {
            if (!useConsoleCancellation)
            {
                return await InvokeCoreAsync(args, effectiveOptions, ct).ConfigureAwait(false);
            }

            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                return await InvokeCoreAsync(args, effectiveOptions, cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        finally
        {
            if (standardOutputWriter is not null)
            {
                await standardOutputWriter.DisposeAsync().ConfigureAwait(false);
            }
        }
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

    async Task<int> InvokeCoreAsync(
        IReadOnlyList<string> args,
        CliInvocationOptions options,
        CancellationToken ct)
    {
        var rootCommand = BuildRootCommand(options);
        var parseResult = rootCommand.Parse(args);
        var invocationConfiguration = new InvocationConfiguration
        {
            Output = options.StandardOutput!,
            Error = options.ErrorOutput!
        };
        return await parseResult.InvokeAsync(invocationConfiguration, ct).ConfigureAwait(false);
    }

    internal void RegisterDynamicBinding<TConfiguration>(Type contextType, Func<CliValidationServicesScope>? createValidationServicesScope)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        GetDynamicBindingPipeline(typeof(TConfiguration)).Add(new(contextType, createValidationServicesScope));
        foreach (var command in commands)
        {
            command.ApplyDynamicBindingMetadata(typeof(TConfiguration), contextType, createValidationServicesScope);
        }
    }

    void ApplyRegisteredPipelines(CliCommandNode command)
    {
        foreach (var (configurationType, configureSteps) in parameterPipelines)
        {
            foreach (var step in configureSteps)
            {
                command.ApplyParameterConfiguration(configurationType, step);
            }
        }

        foreach (var (configurationType, middlewareSteps) in executionPipelines)
        {
            foreach (var step in middlewareSteps)
            {
                command.ApplyExecutionMiddleware(configurationType, step);
            }
        }

        foreach (var (configurationType, validationSteps) in validationPipelines)
        {
            foreach (var step in validationSteps)
            {
                command.ApplyValidation(configurationType, step);
            }
        }

        foreach (var (configurationType, registrations) in dynamicBindingPipelines)
        {
            foreach (var registration in registrations)
            {
                command.ApplyDynamicBindingMetadata(configurationType, registration.ContextType, registration.CreateValidationServicesScope);
            }
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
        {
            throw new InvalidOperationException($"A command named '{name}' is already registered at this level.");
        }
    }

    static Stream RequireReadable(Stream stream, string parameterName)
    {
        if (!stream.CanRead)
        {
            throw new ArgumentException("The standard input stream must be readable.", parameterName);
        }

        return stream;
    }

    static Stream RequireWritable(Stream stream, string parameterName)
    {
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The standard output stream must be writable.", parameterName);
        }

        return stream;
    }

    RootCommand BuildRootCommand(CliInvocationOptions? options)
    {
        var root = string.IsNullOrWhiteSpace(Description) ? new RootCommand() : new RootCommand(Description);
        foreach (var command in commands)
        {
            root.Subcommands.Add(command.BuildCommand(this, options));
        }

        return root;
    }

    sealed record CliDynamicBindingRegistration(
        Type ContextType,
        Func<CliValidationServicesScope>? CreateValidationServicesScope
        );
}
