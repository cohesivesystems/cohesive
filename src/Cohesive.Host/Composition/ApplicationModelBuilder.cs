using Cohesive.Model.Serialization;

using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Prelude;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Host.Composition;

/// <summary>Composes explicitly registered offline declarations by constructor dependency, then freezes them.</summary>
/// <remarks>No runtime services or resources are created implicitly. Registrations and instances belong to this
/// builder alone. Constructor dependencies must also be registered; cycles fail at setup. Build caches failures.
/// Model constructors must not perform IO or own disposable resources. This is not an invocation service container.</remarks>
public sealed class ApplicationModelBuilder
{
    readonly Dictionary<Type, Func<IServiceProvider, object>> registrations = [];
    readonly Dictionary<Type, Type> implementations = [];
    readonly AuthoringState<PreparedApplicationModel> state;

    /// <summary>Starts an independently owned offline composition.</summary>
    public ApplicationModelBuilder() => state = new(Prepare);

    /// <summary>Imports ordinary singleton DI registrations into an isolated declaration composition.</summary>
    /// <param name="services">Declaration registrations only; keyed, scoped, transient and disposable runtime services are unsupported.</param>
    /// <exception cref="ArgumentException">A registration has unsupported lifetime, key or duplicate contract.</exception>
    public ApplicationModelBuilder(IServiceCollection services) : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService || descriptor.Lifetime != ServiceLifetime.Singleton)
                throw new ArgumentException("Model registrations must be unkeyed singletons.", nameof(services));
            if (descriptor.ImplementationType is { } implementation)
            {
                var factory = ActivatorUtilities.CreateFactory(implementation, Type.EmptyTypes);
                registrations.Add(descriptor.ServiceType, provider => factory(provider, null));
                implementations.Add(descriptor.ServiceType, implementation);
            }
            else if (descriptor.ImplementationInstance is { } instance)
                registrations.Add(descriptor.ServiceType, _ => instance);
            else if (descriptor.ImplementationFactory is { } factory)
                registrations.Add(descriptor.ServiceType, factory);
        }
    }

    /// <summary>Registers a declaration whose public constructor consumes other registered declarations.</summary>
    /// <typeparam name="T">Concrete, non-disposable declaration type.</typeparam>
    /// <returns>This composition builder.</returns>
    /// <exception cref="ArgumentException">The type is duplicated, disposable, or has ambiguous constructors.</exception>
    /// <exception cref="InvalidOperationException">The composition is frozen.</exception>
    public ApplicationModelBuilder Add<T>() where T : class
    {
        return state.Edit(() =>
        {
            if (typeof(IDisposable).IsAssignableFrom(typeof(T)) || typeof(IAsyncDisposable).IsAssignableFrom(typeof(T)))
                throw new ArgumentException("Offline declarations cannot own disposable runtime resources.");
            var factory = ActivatorUtilities.CreateFactory(typeof(T), Type.EmptyTypes);
            registrations.Add(typeof(T), provider => factory(provider, null));
            implementations.Add(typeof(T), typeof(T));
            return this;
        });
    }

    /// <summary>Constructs all declarations once, completes their authoring and checks canonical revision uniqueness.</summary>
    /// <returns>The immutable instance registry and validated execution catalog.</returns>
    /// <exception cref="InvalidOperationException">Dependencies are missing or cyclic, or declaration preparation fails.</exception>
    /// <exception cref="ApplicationModelPreparationException">Canonical documents fail catalog validation.</exception>
    public PreparedApplicationModel Build() => state.Value;

    PreparedApplicationModel Prepare()
    {
        var services = new ServiceCollection();
        var instances = new Dictionary<Type, object>();
        var path = new List<Type>();
        var completed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (type, factory) in registrations)
            services.AddSingleton(type, provider =>
            {
                if (path.Contains(type))
                    throw new InvalidOperationException($"Cyclic declaration dependency: {string.Join(" -> ", path.Append(type).Select(t => t.Name))}.");
                path.Add(type);
                try
                {
                    var instance = factory(provider) ?? throw new InvalidOperationException($"Model factory returned null for {type}.");
                    if (instance is IDisposable or IAsyncDisposable)
                        throw new InvalidOperationException("Offline models cannot own disposable runtime resources.");
                    if (completed.Add(instance) && instance is IAuthoringModel authoring) authoring.Complete();
                    instances.Add(type, instance);
                    return instance;
                }
                finally { path.RemoveAt(path.Count - 1); }
            });
        // This container owns declarations only. No scoped runtime provider escapes preparation.
        using var provider = services.BuildServiceProvider();
        foreach (var type in registrations.Keys) provider.GetRequiredService(type);
        var documents = instances.Values.Distinct(ReferenceEqualityComparer.Instance).OfType<IExecutionDefinitionModel>().Select(model => model.Document);
        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var catalog);
        if (!validation.IsValid) throw new ApplicationModelPreparationException(validation);
        var prepared = new PreparedApplicationModel(instances, catalog!);
        foreach (var application in instances.Values.OfType<ApplicationModel>()) application.Attach(prepared);
        return prepared;
    }

    internal void Discover(Type root, HashSet<Type> visited)
    {
        if (!visited.Add(root)) return;
        if (registrations.ContainsKey(root))
        {
            if (implementations.TryGetValue(root, out var implementation))
                foreach (var parameter in RequireConstructor(implementation).GetParameters()) Discover(parameter.ParameterType, visited);
            return;
        }
        if (!root.IsClass || root.IsAbstract || root == typeof(string) || root.ContainsGenericParameters)
            throw new InvalidOperationException($"Model dependency '{root}' needs an explicit registration.");
        if (typeof(IDisposable).IsAssignableFrom(root) || typeof(IAsyncDisposable).IsAssignableFrom(root))
            throw new InvalidOperationException($"Offline model '{root}' cannot own runtime resources.");
        foreach (var parameter in RequireConstructor(root).GetParameters()) Discover(parameter.ParameterType, visited);
        var factory = ActivatorUtilities.CreateFactory(root, Type.EmptyTypes);
        registrations.Add(root, provider => factory(provider, null));
    }

    static System.Reflection.ConstructorInfo RequireConstructor(Type type)
    {
        var constructors = type.GetConstructors();
        return constructors.Length == 1 ? constructors[0]
            : throw new InvalidOperationException($"Model '{type}' requires exactly one public constructor or an explicit factory.");
    }

    /// <summary>Registers an explicit declaration factory, including an interface dependency override.</summary>
    /// <typeparam name="T">Declaration contract.</typeparam><param name="factory">Setup-only factory; must not retain the provider or perform IO.</param>
    /// <returns>This builder.</returns>
    public ApplicationModelBuilder Add<T>(Func<IServiceProvider, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return state.Edit(() => { registrations.Add(typeof(T), provider => factory(provider)); return this; });
    }
}

/// <summary>Optional constructor-authored application root with access to its validated composition.</summary>
public abstract class ApplicationModel
{
    PreparedApplicationModel? composition;
    internal void Attach(PreparedApplicationModel value) => composition = value;
    /// <summary>Validated declarations; available after ModelActivator returns, never during constructors.</summary>
    public PreparedApplicationModel Composition => composition ?? throw new InvalidOperationException("Activate the application before inspecting its composition.");
}

/// <summary>Completed offline declarations, with no runtime dependency resolution or invocation state.</summary>
public sealed class PreparedApplicationModel
{
    readonly IReadOnlyDictionary<Type, object> instances;
    internal PreparedApplicationModel(IReadOnlyDictionary<Type, object> instances, ExecutionDefinitionDocumentCatalog catalog)
    { this.instances = instances; Executions = catalog; }
    /// <summary>Validated canonical execution documents, rejecting repeated identity/revision pairs.</summary>
    public ExecutionDefinitionDocumentCatalog Executions { get; }
    /// <summary>Returns the exact instance created for one registered declaration type.</summary>
    /// <typeparam name="T">Registered declaration type.</typeparam>
    /// <returns>The composition-owned, completed declaration.</returns>
    /// <exception cref="KeyNotFoundException">The type was not registered.</exception>
    public T Get<T>() where T : class => (T)instances[typeof(T)];
}

/// <summary>Discovers concrete constructor dependencies and activates one independently owned offline graph.</summary>
public static class ModelActivator
{
    /// <summary>Constructs dependencies before consumers, completes each declaration, and validates the whole catalog.</summary>
    /// <typeparam name="T">Application or model root.</typeparam>
    /// <param name="configure">Optional explicit interface/factory registrations; direct builder usage remains supported.</param>
    /// <returns>The completed root; constructors capture declarations, never runtime resources.</returns>
    /// <exception cref="InvalidOperationException">Dependencies are unsupported, missing, cyclic, or preparation fails.</exception>
    /// <exception cref="ApplicationModelPreparationException">Canonical catalog validation fails.</exception>
    public static T Create<T>(Action<IServiceCollection>? configure = null) where T : class
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        var builder = new ApplicationModelBuilder(services);
        builder.Discover(typeof(T), []);
        return builder.Build().Get<T>();
    }
}

/// <summary>Structured canonical catalog failure during application composition.</summary>
/// <param name="validation">Failed catalog validation.</param>
public sealed class ApplicationModelPreparationException(DocumentValidationResult validation)
    : PreparationException("application composition", "application.definition.invalid", "Application execution definitions are invalid.")
{
    /// <summary>Original canonical catalog diagnostics, including duplicate revision evidence.</summary>
    public DocumentValidationResult Validation { get; } = validation;
}
