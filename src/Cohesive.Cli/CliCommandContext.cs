using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Cli;

/// <summary>
/// Base invocation context supplied to CLI command middleware and handlers.
/// </summary>
public class CliCommandContext : ICancellationTokenContext, IServiceProvider
{
    readonly IServiceProvider? serviceProvider;

    /// <summary>Initializes a command invocation context without an attached service provider.</summary>
    /// <param name="configurationRoot">Fully merged configuration for the invocation.</param>
    /// <param name="parseResult">Parsed command-line input for the invocation.</param>
    /// <param name="cancellationToken">Cancellation token for the invocation.</param>
    /// <param name="output">Optional output channels; standard process output is used when omitted.</param>
    public CliCommandContext(
        IConfigurationRoot configurationRoot,
        ParseResult parseResult,
        CancellationToken cancellationToken,
        CliOutput? output = null)
        : this(configurationRoot, parseResult, cancellationToken, output, serviceProvider: null)
    {
    }

    internal CliCommandContext(
        IConfigurationRoot configurationRoot,
        ParseResult parseResult,
        CancellationToken cancellationToken,
        CliOutput? output,
        IServiceProvider? serviceProvider)
    {
        ConfigurationRoot = Guard.RequireNotNull(configurationRoot);
        ParseResult = Guard.RequireNotNull(parseResult);
        CancellationToken = cancellationToken;
        Output = output ?? CliOutput.Standard;
        this.serviceProvider = serviceProvider;
    }

    /// <summary>Initializes a derived command context from an existing context.</summary>
    /// <param name="source">Invocation context whose configuration, parsing, output, and service scope are retained.</param>
    protected CliCommandContext(CliCommandContext source)
        : this(
            configurationRoot: source.ConfigurationRoot,
            parseResult: source.ParseResult,
            cancellationToken: source.CancellationToken,
            output: source.Output,
            serviceProvider: source.serviceProvider
            )
    {
    }

    /// <summary>
    /// Fully merged raw configuration graph used to create the typed command configuration.
    /// </summary>
    public IConfigurationRoot ConfigurationRoot { get; }

    /// <summary>
    /// System.CommandLine parse result for the invocation.
    /// </summary>
    public ParseResult ParseResult { get; }

    /// <summary>
    /// Ambient cancellation token for the command invocation.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Output channels available to the current invocation.
    /// </summary>
    public CliOutput Output { get; }

    /// <summary>
    /// Resolves a required service from the command invocation context.
    /// </summary>
    /// <typeparam name="T">Service type to resolve.</typeparam>
    /// <returns>The resolved service.</returns>
    /// <exception cref="InvalidOperationException">
    /// The context has no attached service provider, or the provider does not contain the requested service.
    /// </exception>
    public T GetRequiredService<T>() where T : notnull =>
        GetAttachedServices().GetRequiredService<T>();

    /// <summary>
    /// Resolves an invocation dependency for handler binding.
    /// </summary>
    /// <param name="dependencyType">Requested dependency type.</param>
    /// <param name="value">Resolved dependency instance when available.</param>
    /// <returns><see langword="true"/> when the dependency was resolved; otherwise <see langword="false"/>.</returns>
    protected virtual bool TryResolveDependency(Type dependencyType, out object? value)
    {
        ArgumentNullException.ThrowIfNull(dependencyType);
        value = serviceProvider?.GetService(dependencyType);
        return value is not null;
    }

    internal bool TryResolveInvocationDependency(Type dependencyType, out object? value) =>
        TryResolveDependency(dependencyType, out value);

    internal IServiceProvider GetAttachedServices() =>
        serviceProvider ?? throw new InvalidOperationException("The CLI command context does not have an attached service provider.");

    /// <summary>Resolves an optional service from the command invocation context.</summary>
    /// <param name="serviceType">Service type to resolve.</param>
    /// <returns>The resolved service, or <see langword="null"/> when no provider or service is available.</returns>
    public object? GetService(Type serviceType) => serviceProvider?.GetService(serviceType);
}

/// <summary>
/// Invocation context supplied to a CLI command handler after configuration has been merged and bound.
/// </summary>
/// <typeparam name="TConfiguration">Typed configuration bound for the invoked command.</typeparam>
public class CliCommandContext<TConfiguration> : CliCommandContext, ICliTypedCommandContext
{
    /// <summary>Initializes a new instance of the cli command context type.</summary>
    /// <param name="configuration">Typed configuration bound for the invocation.</param>
    /// <param name="configurationRoot">Fully merged configuration for the invocation.</param>
    /// <param name="parseResult">Parsed command-line input for the invocation.</param>
    /// <param name="cancellationToken">Cancellation token for the invocation.</param>
    /// <param name="output">Optional output channels; standard process output is used when omitted.</param>
    public CliCommandContext(
        TConfiguration configuration,
        IConfigurationRoot configurationRoot,
        ParseResult parseResult,
        CancellationToken cancellationToken,
        CliOutput? output = null)
        : this(configuration, configurationRoot, parseResult, cancellationToken, output, serviceProvider: null)
    {
    }

    internal CliCommandContext(
        TConfiguration configuration,
        IConfigurationRoot configurationRoot,
        ParseResult parseResult,
        CancellationToken cancellationToken,
        CliOutput? output,
        IServiceProvider? serviceProvider)
        : base(configurationRoot, parseResult, cancellationToken, output, serviceProvider)
    {
        Configuration = configuration;
    }

    /// <summary>Initializes a derived typed command context from an existing context.</summary>
    /// <param name="source">Typed invocation context whose configuration, parsing, output, and service scope are retained.</param>
    protected CliCommandContext(CliCommandContext<TConfiguration> source)
        : base(source)
    {
        Configuration = source.Configuration;
    }

    /// <summary>
    /// Typed configuration parsed for the invoked command.
    /// </summary>
    public TConfiguration Configuration { get; }

    internal CliCommandContext<TConfiguration> WithServices(IServiceProvider sp) =>
        new(Configuration, ConfigurationRoot, ParseResult, CancellationToken, Output, serviceProvider: sp);

    object ICliTypedCommandContext.Configuration => Configuration!;

    Type ICliTypedCommandContext.ConfigurationType => typeof(TConfiguration);
}

interface ICliTypedCommandContext
{
    object Configuration { get; }
    Type ConfigurationType { get; }
}
