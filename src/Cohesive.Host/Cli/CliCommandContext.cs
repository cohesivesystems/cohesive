using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Host.Cli;

/// <summary>
/// Base invocation context supplied to CLI command middleware and handlers.
/// </summary>
public class CliCommandContext(
    IConfigurationRoot configurationRoot,
    ParseResult parseResult,
    CancellationToken cancellationToken,
    CliOutput? output = null,
    IServiceProvider? serviceProvider = null
    ) : ICancellationTokenContext, IServiceProvider
{
    readonly IServiceProvider? serviceProvider = serviceProvider;

    protected CliCommandContext(CliCommandContext source, IServiceProvider? serviceProvider = null)
        : this(
            configurationRoot: source.ConfigurationRoot,
            parseResult: source.ParseResult,
            cancellationToken: source.CancellationToken,
            output: source.Output,
            serviceProvider: serviceProvider ?? source.serviceProvider
            )
    {
    }

    /// <summary>
    /// Fully merged raw configuration graph used to create the typed command configuration.
    /// </summary>
    public IConfigurationRoot ConfigurationRoot { get; } = Guard.RequireNotNull(configurationRoot);

    /// <summary>
    /// System.CommandLine parse result for the invocation.
    /// </summary>
    public ParseResult ParseResult { get; } = Guard.RequireNotNull(parseResult);

    /// <summary>
    /// Ambient cancellation token for the command invocation.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Output channels available to the current invocation.
    /// </summary>
    public CliOutput Output { get; } = output ?? CliOutput.Standard;

    /// <summary>
    /// Resolves a required service from the command invocation context.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull
    {
        if (serviceProvider is null)
            throw new InvalidOperationException("The CLI command context does not have an attached service provider.");
        return serviceProvider.GetRequiredService<T>();
    }

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

    public object? GetService(Type serviceType)
    {
        if (serviceProvider is null)
            throw new InvalidOperationException("The CLI command context does not have an attached service provider.");
        return serviceProvider.GetService(serviceType);
    }
}

/// <summary>
/// Invocation context supplied to a CLI command handler after configuration has been merged and bound.
/// </summary>
/// <typeparam name="TConfiguration">Typed configuration bound for the invoked command.</typeparam>
public class CliCommandContext<TConfiguration> : CliCommandContext, ICliTypedCommandContext
{
    public CliCommandContext(
        TConfiguration configuration,
        IConfigurationRoot configurationRoot,
        ParseResult parseResult,
        CancellationToken cancellationToken,
        CliOutput? output = null,
        IServiceProvider? serviceProvider = null
        ) : base(configurationRoot, parseResult, cancellationToken, output, serviceProvider)
    {
        Configuration = configuration;
    }

    protected CliCommandContext(CliCommandContext<TConfiguration> source, IServiceProvider? serviceProvider = null)
        : base(source, serviceProvider)
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
