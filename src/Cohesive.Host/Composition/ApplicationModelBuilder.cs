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
    readonly AuthoringState<ApplicationModel> state;

    /// <summary>Starts an independently owned offline composition.</summary>
    public ApplicationModelBuilder() => state = new(Prepare);

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
            return this;
        });
    }

    /// <summary>Constructs all declarations once, completes their authoring and checks canonical revision uniqueness.</summary>
    /// <returns>The immutable instance registry and validated execution catalog.</returns>
    /// <exception cref="InvalidOperationException">Dependencies are missing or cyclic, or declaration preparation fails.</exception>
    /// <exception cref="ApplicationModelPreparationException">Canonical documents fail catalog validation.</exception>
    public ApplicationModel Build() => state.Value;

    ApplicationModel Prepare()
    {
        var resolver = new Resolver(registrations);
        foreach (var type in registrations.Keys) resolver.GetService(type);
        foreach (var instance in resolver.Instances.Values)
            if (instance is IAuthoringModel authoring) authoring.Complete();
        var documents = resolver.Instances.Values.OfType<IExecutionDefinitionModel>().Select(model => model.Document);
        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var catalog);
        if (!validation.IsValid) throw new ApplicationModelPreparationException(validation);
        return new(resolver.Instances, catalog!);
    }

    sealed class Resolver(Dictionary<Type, Func<IServiceProvider, object>> registrations) : IServiceProvider
    {
        readonly HashSet<Type> constructing = [];
        internal Dictionary<Type, object> Instances { get; } = [];
        public object? GetService(Type serviceType)
        {
            if (Instances.TryGetValue(serviceType, out var instance)) return instance;
            if (!registrations.TryGetValue(serviceType, out var factory)) return null;
            if (!constructing.Add(serviceType)) throw new InvalidOperationException($"Cyclic declaration dependency at '{serviceType}'.");
            try { instance = factory(this); Instances.Add(serviceType, instance); return instance; }
            finally { constructing.Remove(serviceType); }
        }
    }
}

/// <summary>Completed offline declarations, with no runtime dependency resolution or invocation state.</summary>
public sealed class ApplicationModel
{
    readonly IReadOnlyDictionary<Type, object> instances;
    internal ApplicationModel(IReadOnlyDictionary<Type, object> instances, ExecutionDefinitionDocumentCatalog catalog)
    { this.instances = instances; Executions = catalog; }
    /// <summary>Validated canonical execution documents, rejecting repeated identity/revision pairs.</summary>
    public ExecutionDefinitionDocumentCatalog Executions { get; }
    /// <summary>Returns the exact instance created for one registered declaration type.</summary>
    /// <typeparam name="T">Registered declaration type.</typeparam>
    /// <returns>The composition-owned, completed declaration.</returns>
    /// <exception cref="KeyNotFoundException">The type was not registered.</exception>
    public T Get<T>() where T : class => (T)instances[typeof(T)];
}

/// <summary>Structured canonical catalog failure during application composition.</summary>
/// <param name="validation">Failed catalog validation.</param>
public sealed class ApplicationModelPreparationException(DocumentValidationResult validation)
    : PreparationException("application composition", "application.definition.invalid", "Application execution definitions are invalid.")
{
    /// <summary>Original canonical catalog diagnostics, including duplicate revision evidence.</summary>
    public DocumentValidationResult Validation { get; } = validation;
}
