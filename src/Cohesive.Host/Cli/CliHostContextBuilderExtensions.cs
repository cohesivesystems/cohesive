using System.CommandLine;
using Cohesive.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Host.Cli;

/// <summary>
/// Helpers for creating host-backed CLI execution contexts.
/// </summary>
public static class CliHostContextBuilderExtensions
{
    extension(CliApplication app)
    {
        /// <summary>
        /// Registers host-backed execution middleware for every command bound to <typeparamref name="TConfiguration"/>.
        /// </summary>
        public CliApplication UseHostContext<TConfiguration, TContext>(
            Func<CliCommandContext<TConfiguration>, IHost> createHost,
            Func<CliCommandContext<TConfiguration>, TContext> createContext
            ) where TContext : CliCommandContext<TConfiguration> =>
            app.UseHostContext(
                createHost,
                (Func<CliCommandContext<TConfiguration>, IHost, TContext>)((context, _) => createContext(context)));

        /// <summary>
        /// Registers host-backed execution middleware for every command bound to <typeparamref name="TConfiguration"/>.
        /// </summary>
        public CliApplication UseHostContext<TConfiguration, TContext>(
            Func<CliCommandContext<TConfiguration>, IHost> createHost,
            Func<CliCommandContext<TConfiguration>, IHost, TContext> createContext
            ) where TContext : CliCommandContext<TConfiguration>
        {
            app.RegisterDynamicBinding<TConfiguration>(
                contextType: typeof(TContext),
                createValidationServicesScope: CreateValidationServicesScopeFactory(createHost)
                );
            return app.Use<TConfiguration>((context, next) => StartHostThenExecute(context, next, createHost, createContext));
        }

        /// <summary>
        /// Registers host-backed execution middleware for every command bound to <typeparamref name="TConfiguration"/>.
        /// </summary>
        public CliApplication UseHostContext<TConfiguration, TContext>(
            Func<CliCommandContext<TConfiguration>, IHost> createHost,
            Func<CliCommandContext<TConfiguration>, IServiceProvider, TContext> createContext
            ) where TContext : CliCommandContext<TConfiguration> =>
            app.UseHostContext(createHost, context => createContext(context, context.GetAttachedServices()));
    }

    extension<TConfiguration>(CliCommandBuilder<TConfiguration> cmd)
    {
        /// <summary>
        /// Registers host-backed execution middleware for a specific command.
        /// </summary>
        public CliCommandBuilder<TConfiguration> UseHostContext<TContext>(
            Func<CliCommandContext<TConfiguration>, IHost> createHost,
            Func<CliCommandContext<TConfiguration>, TContext> createContext
            ) where TContext : CliCommandContext<TConfiguration> =>
            cmd.UseHostContext(createHost, (Func<CliCommandContext<TConfiguration>, IHost, TContext>)((context, _) => createContext(context)));

        /// <summary>
        /// Registers host-backed execution middleware for a specific command.
        /// </summary>
        public CliCommandBuilder<TConfiguration> UseHostContext<TContext>(
            Func<CliCommandContext<TConfiguration>, IHost> createHost,
            Func<CliCommandContext<TConfiguration>, IHost, TContext> createContext
            ) where TContext : CliCommandContext<TConfiguration>
        {
            cmd.ApplyDynamicBindingMetadata(
                configurationType: typeof(TConfiguration),
                contextType: typeof(TContext),
                createValidationServicesScope: CreateValidationServicesScopeFactory(createHost));
            return cmd.Use((context, next) => StartHostThenExecute(context, next, createHost, createContext));
        }

        /// <summary>
        /// Registers host-backed execution middleware for a specific command.
        /// </summary>
        public CliCommandBuilder<TConfiguration> UseHostContext<TContext>(
            Func<CliCommandContext<TConfiguration>, IHost> createHost,
            Func<CliCommandContext<TConfiguration>, IServiceProvider, TContext> createContext
            ) where TContext : CliCommandContext<TConfiguration> =>
            cmd.UseHostContext(createHost, context => createContext(context, context.GetAttachedServices()));
    }

    static async Task<int> StartHostThenExecute<TConfiguration, TContext>(
        CliCommandContext<TConfiguration> context,
        CliCommandExecutionDelegate next,
        Func<CliCommandContext<TConfiguration>, IHost> createHost,
        Func<CliCommandContext<TConfiguration>, IHost, TContext> createContext
        ) where TContext : CliCommandContext<TConfiguration>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(createHost);
        ArgumentNullException.ThrowIfNull(createContext);
        using var host = createHost(context);
        await host.StartAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            if (TryCreateInvocationScope(host.Services, out var scope))
            {
                await using var invocationScope = scope;
                return await next(createContext(context.WithServices(invocationScope.ServiceProvider), host)).ConfigureAwait(false);
            }
            return await next(createContext(context.WithServices(host.Services), host)).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    static Func<CliValidationServicesScope> CreateValidationServicesScopeFactory<TConfiguration>(Func<CliCommandContext<TConfiguration>, IHost> createHost)
    {
        ArgumentNullException.ThrowIfNull(createHost);
        return () =>
        {
            var host = createHost(CreateValidationContext<TConfiguration>());
            if (TryCreateValidationScope(host.Services, out var scope))
            {
                return new(scope.ServiceProvider, new CliValidationScopeLease(host, scope));
            }

            return new(host.Services, host);
        };
    }

    static CliCommandContext<TConfiguration> CreateValidationContext<TConfiguration>()
    {
        object? configuration;
        try
        {
            configuration = Activator.CreateInstance<TConfiguration>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to create validation configuration for CLI command type '{typeof(TConfiguration).FullName}'.", ex);
        }

        if (configuration is not TConfiguration typedConfiguration)
        {
            throw new InvalidOperationException($"Unable to create validation configuration for CLI command type '{typeof(TConfiguration).FullName}'.");
        }

        return new(
            typedConfiguration,
            new ConfigurationBuilder().Build(),
            new RootCommand().Parse([]),
            CancellationToken.None,
            CommandIo.Null(),
            serviceProvider: null
            );
    }

    static bool TryCreateInvocationScope(IServiceProvider services, out AsyncServiceScope scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService(typeof(IServiceScopeFactory)) is IServiceScopeFactory scopeFactory)
        {
            scope = scopeFactory.CreateAsyncScope();
            return true;
        }
        scope = default;
        return false;
    }

    static bool TryCreateValidationScope(IServiceProvider services, out IServiceScope scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService(typeof(IServiceScopeFactory)) is IServiceScopeFactory scopeFactory)
        {
            scope = scopeFactory.CreateScope();
            return true;
        }
        scope = null!;
        return false;
    }

    sealed class CliValidationScopeLease(IHost host, IServiceScope scope) : IDisposable
    {
        public void Dispose()
        {
            scope.Dispose();
            host.Dispose();
        }
    }
}
