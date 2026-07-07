using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Configuration.Tests;

/// <summary>
/// A configuration key tagged by the type it resolves to.
/// </summary>
/// <param name="Name">The configuration key.</param>
/// <typeparam name="T"></typeparam>
// ReSharper disable once UnusedTypeParameter
public sealed record ConfigKey<T>(string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// A configuration environment that resolves configuration keys.
/// </summary>
public interface IConfigEnv
{
    /// <summary>
    /// Resolves a configuration value corresponding to the given key.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <typeparam name="T">The configured value type to resolve.</typeparam>
    /// <exception cref="InvalidOperationException">The configuration key was not found.</exception>
    /// <returns>The resolved configuration value.</returns>
    T Resolve<T>(ConfigKey<T> key);
    
    /// <summary>
    /// Tries to resolve a configuration value corresponding to the given key.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The resulting configuration value.</param>
    /// <typeparam name="T">The configured value type to resolve.</typeparam>
    /// <returns>True if the value was resolved; false otherwise.</returns>
    bool TryResolve<T>(ConfigKey<T> key, out T value);
}

/// <summary>
/// A registry of configuration keys.
/// </summary>
public interface IConfigRegistry
{
    /// <summary>
    /// Gets all configuration keys of the given type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    IReadOnlyList<ConfigKey<T>> GetAll<T>();
}

/// <summary>
/// A configuration environment with caching and cycle detection.
/// </summary>
sealed class ConfigEnv : IConfigEnv, IConfigRegistry
{
    readonly Dictionary<object, Binding> bindings;
    readonly Dictionary<object, object> cache = [];
    readonly HashSet<object> resolving = [];

    public ConfigEnv(IEnumerable<Binding> bindings)
    {
        this.bindings = bindings.ToDictionary(b => b.Key);
    }
    
    public T Resolve<T>(ConfigKey<T> key)
    {
        if (TryResolve(key, out var value))
            return value;

        throw new InvalidOperationException($"Missing config: {key}");
    }

    public bool TryResolve<T>(ConfigKey<T> key, out T value)
    {
        if (cache.TryGetValue(key, out var cached))
        {
            value = (T)cached;
            return true;
        }

        if (!bindings.TryGetValue(key, out var binding))
        {
            value = default!;
            return false;
        }

        if (!resolving.Add(key))
            throw new InvalidOperationException($"Cycle detected at {key}");

        var result = binding.Factory(this);

        resolving.Remove(key);
        cache[key] = result;

        value = (T)result;
        return true;
    }

    public IReadOnlyList<ConfigKey<T>> GetAllWithTag<T>(string tag) => bindings.Values
        .Where(b => b.Key is ConfigKey<T> && b.Tags.Contains(tag))
        .Select(b => (ConfigKey<T>)b.Key)
        .ToList();
    
    public IReadOnlyList<ConfigKey<T>> GetAll<T>() => 
        bindings.Keys.OfType<ConfigKey<T>>().ToList();
}

/// <summary>
/// A configuration key reference.
/// </summary>
/// <param name="key"></param>
/// <typeparam name="T"></typeparam>
public readonly struct Ref<T>(ConfigKey<T> key)
{
    /// <summary>
    /// The configuration key.
    /// </summary>
    public ConfigKey<T> Key { get; } = key;
}

/// <summary>
/// A configuration key reference factory.
/// </summary>
public static class Ref
{
    /// <summary>
    /// Creates a configuration key reference.
    /// </summary>
    /// <param name="key"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Ref<T> To<T>(ConfigKey<T> key) => new(key);
}

/// <summary>
/// A binding of a configuration key to an instance factory.
/// </summary>
/// <param name="key"></param>
/// <param name="factory"></param>
sealed class Binding(object key, Func<IConfigEnv, object> factory, IEnumerable<string>? tags = null)
{
    /// <summary>
    /// The configuration key.
    /// </summary>
    public object Key { get; } = key;
    
    /// <summary>
    /// The instance factory parameterized by the configuration environment.
    /// </summary>
    public Func<IConfigEnv, object> Factory { get; } = factory;
    
    /// <summary>
    /// Optional tags for targeted selection.
    /// </summary>
    public IReadOnlySet<string> Tags { get; } = tags?.ToHashSet() ?? [];
}

/// <summary>
/// A configuration bindings builder.
/// </summary>
public sealed class ConfigBuilder
{
    readonly List<Binding> bindings = [];

    /// <summary>
    /// Binds a configuration key to an instance factory.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="factory"></param>
    /// <param name="tags"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public ConfigBuilder Bind<T>(ConfigKey<T> key, Func<IConfigEnv, T> factory, IEnumerable<string>? tags = null)
    {
        bindings.Add(new(key: key, env => factory(env)!, tags: tags));
        return this;
    }

    /// <summary>
    /// Binds a configuration key to a constant value.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public ConfigBuilder BindValue<T>(ConfigKey<T> key, T value)
    {
        bindings.Add(new(key: key, _ => value!));
        return this;
    }

    /// <summary>
    /// Applies the given configuration fragment to the configuration builder.
    /// </summary>
    /// <param name="fragment"></param>
    /// <returns></returns>
    public ConfigBuilder Include(ConfigFragment fragment)
    {
        fragment(this);
        return this;
    }
    
    public IConfigEnv Build() => new ConfigEnv([..bindings]);
}

/// <summary>
/// A configuration fragment/module that acts on a <see cref="ConfigBuilder"/>.
/// </summary>
public delegate void ConfigFragment(ConfigBuilder builder);


public static class ConfigEnvExtensions
{
    public static void RegisterConfigEnv(this IConfigEnv env, IEnumerable<object> serviceKeys, IServiceCollection services)
    {
        foreach (var key in serviceKeys)
            services.AddSingleton(key.GetType(), _ => ResolveUntyped(env, key));
    }
    
    public static IServiceProvider ToServiceProvider(this IConfigEnv env, IEnumerable<object> serviceKeys)
    {
        var services = new ServiceCollection();
        env.RegisterConfigEnv(serviceKeys, services);
        return services.BuildServiceProvider();
    }

    static object ResolveUntyped(IConfigEnv env, object key)
    {
        var method = typeof(IConfigEnv)
            .GetMethod(nameof(IConfigEnv.Resolve))!
            .MakeGenericMethod(key.GetType().GenericTypeArguments[0]);

        return method.Invoke(env, [key])!;
    }
}

/// <summary>
/// A raw configuration value source.
/// </summary>
public interface IConfigSource
{
    /// <summary>
    /// Tries to get a configuration value.
    /// </summary>
    /// <param name="key">The configuration key to resolve.</param>
    /// <param name="value">The resolved configuration value.</param>
    /// <returns>True if the configuration key was found, otherwise false.</returns>
    bool TryGet(string key, out string value);
}

/// <summary>
/// A composite configuration value source.
/// </summary>
/// <param name="sources"></param>
public sealed class CompositeConfigSource(params IConfigSource[] sources) : IConfigSource
{
    readonly IReadOnlyList<IConfigSource> sources = sources;

    public bool TryGet(string key, out string value)
    {
        foreach (var source in sources)
        {
            if (source.TryGet(key, out value))
                return true;
        }

        value = null!;
        return false;
    }
}

/// <summary>
/// Extension methods for <see cref="IConfigSource"/>.
/// </summary>
public static class ConfigSourceExtensions
{
    /// <summary>
    /// Gets and parses a required configuration value.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="key"></param>
    /// <param name="parse"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static T GetRequired<T>(this IConfigSource source, string key, Func<string, T> parse)
    {
        if (!source.TryGet(key, out var value))
            throw new InvalidOperationException($"Missing config: {key}");

        return parse(value);
    }

    /// <summary>
    /// Tries to get and parse a configuration value.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="key"></param>
    /// <param name="parse"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T? GetOptional<T>(this IConfigSource source, string key, Func<string, T> parse) => 
        source.TryGet(key, out var value) ? parse(value) : default;
}

/// <summary>
/// Selects a value of type <typeparamref name="T"/> from a configuration environment.
/// </summary>
/// <typeparam name="T">The selected value type.</typeparam>
public interface ISelector<out T>
{
    /// <summary>
    /// Resolves a value of type <typeparamref name="T"/> from a configuration environment.
    /// </summary>
    /// <param name="env">The configuration environment to resolve from.</param>
    /// <returns>The resolved value.</returns>
    T Resolve(IConfigEnv env);
}

public static class Selector
{
    public static Func<IConfigEnv, T> ToFunc<T>(this ISelector<T> selector)
        => selector.Resolve;
}


public sealed class KeySelector<T>(ConfigKey<T> key) : ISelector<T>
{
    public T Resolve(IConfigEnv env) => 
        env.Resolve(key);
}

public sealed class UniqueSelector<T> : ISelector<T>
{
    public T Resolve(IConfigEnv env)
    {
        var registry = (IConfigRegistry)env;
        var keys = registry.GetAll<T>();

        if (keys.Count == 0)
            throw new InvalidOperationException($"No instances of {typeof(T)} found");

        if (keys.Count > 1)
            throw new InvalidOperationException($"Multiple instances of {typeof(T)} found");

        return env.Resolve(keys[0]);
    }
}

public static class KeyNaming
{
    /// <summary>
    /// Gets the name of the given configuration key.
    /// </summary>
    /// <param name="key"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static string GetName<T>(ConfigKey<T> key) => key.Name;
}


public sealed class ByNameSelector<T>(string name) : ISelector<T>
{
    public T Resolve(IConfigEnv env)
    {
        var registry = (IConfigRegistry)env;

        var matches = registry.GetAll<T>()
            .Where(k => KeyNaming.GetName(k).Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException($"No {typeof(T)} named '{name}'");

        if (matches.Count > 1)
            throw new InvalidOperationException($"Multiple {typeof(T)} named '{name}'");

        return env.Resolve(matches[0]);
    }
}

public sealed class ByTagSelector<T>(string tag) : ISelector<T>
{
    public T Resolve(IConfigEnv env)
    {
        var registry = (IConfigRegistry)env;
        var matches = ((ConfigEnv)env).GetAllWithTag<T>(tag);

        if (matches.Count == 0)
            throw new InvalidOperationException($"No {typeof(T)} with tag '{tag}'");

        if (matches.Count > 1)
            throw new InvalidOperationException($"Multiple {typeof(T)} with tag '{tag}'");

        return env.Resolve(matches[0]);
    }
}

public sealed class FirstAvailableSelector<T>(params ISelector<T>[] selectors) : ISelector<T>
{
    public T Resolve(IConfigEnv env)
    {
        foreach (var selector in selectors)
        {
            try
            {
                return selector.Resolve(env);
            }
            catch
            {
                // ignore and continue
            }
        }

        throw new InvalidOperationException($"No selector could resolve {typeof(T)}");
    }
}

public sealed class AllSelector<T> : ISelector<IReadOnlyList<T>>
{
    public IReadOnlyList<T> Resolve(IConfigEnv env)
    {
        var registry = (IConfigRegistry)env;
        var keys = registry.GetAll<T>();

        return keys.Select(env.Resolve).ToList();
    }
}

/// <summary>
/// Selector constructors.
/// </summary>
public static class Select
{
    /// <summary>
    /// Selects the given configuration key.
    /// </summary>
    /// <param name="key"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ISelector<T> Key<T>(ConfigKey<T> key)
        => new KeySelector<T>(key);

    /// <summary>
    /// Selects the unique instance of the given type, throwing if there's more than one binding.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ISelector<T> Unique<T>()
        => new UniqueSelector<T>();

    /// <summary>
    /// Selects the unique instance whose name key contains the given value.
    /// </summary>
    /// <param name="name"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ISelector<T> ByName<T>(string name)
        => new ByNameSelector<T>(name);

    public static ISelector<T> ByTag<T>(string tag)
        => new ByTagSelector<T>(tag);

    public static ISelector<T> First<T>(params ISelector<T>[] selectors)
        => new FirstAvailableSelector<T>(selectors);

    public static ISelector<IReadOnlyList<T>> All<T>()
        => new AllSelector<T>();
}

/// <summary>
/// An automatic wiring rule for values of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IAutoWireRule<T>
{
    bool CanSatisfy(ConfigKey<T> key);

    ISelector<T> Select(ConfigKey<T> key, IConfigRegistry registry);
}

public sealed class UniqueByTypeRule<T> : IAutoWireRule<T>
{
    public bool CanSatisfy(ConfigKey<T> key) => true;

    public ISelector<T> Select(ConfigKey<T> key, IConfigRegistry registry)
        => Tests.Select.Unique<T>();
}

public sealed class NameConventionRule<T> : IAutoWireRule<T>
{
    public bool CanSatisfy(ConfigKey<T> key) => true;

    public ISelector<T> Select(ConfigKey<T> key, IConfigRegistry registry)
    {
        var name = key.Name.Split(':').Last(); // "StorageClient"
        return Tests.Select.ByName<T>(name.Replace("Client", ""));
    }
}

public sealed class TagRule<T>(string tag) : IAutoWireRule<T>
{
    public bool CanSatisfy(ConfigKey<T> key) => true;

    public ISelector<T> Select(ConfigKey<T> key, IConfigRegistry registry) => 
        Tests.Select.ByTag<T>(tag);
}

public sealed class AssignableTypeRule<T> : IAutoWireRule<T>
{
    public bool CanSatisfy(ConfigKey<T> key) => true;

    public ISelector<T> Select(ConfigKey<T> key, IConfigRegistry registry)
    {
        var matches = registry.GetAll<T>();
        if (matches.Length != 1)
            throw new InvalidOperationException("Ambiguous match");

        return Tests.Select.Key(matches.Single());
    }
}