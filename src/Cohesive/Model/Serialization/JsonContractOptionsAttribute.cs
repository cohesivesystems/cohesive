using System.Reflection;
using System.Text.Json;

namespace Cohesive.Model.Serialization;

/// <summary>Associates a contracts assembly with its authoritative public JSON options factory.</summary>
/// <remarks>The factory is trusted authoring code, not portable model behavior. It must be deterministic,
/// parameterless and independent of services, credentials or runtime requests. Hosts and generators
/// should use the same factory. Generated schemas remain projections of the returned serializer contract.</remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class JsonContractOptionsAttribute(Type providerType, string factoryMethod) : Attribute
{
    /// <summary>Type owning the public static options factory.</summary>
    public Type ProviderType { get; } = providerType ?? throw new ArgumentNullException(nameof(providerType));
    /// <summary>Name of the parameterless public static factory.</summary>
    public string FactoryMethod { get; } = Guard.RequireNotNullOrWhiteSpace(factoryMethod);

    /// <summary>Resolves and snapshots the options explicitly declared by a contracts assembly.</summary>
    /// <param name="assembly">Assembly whose declaration identifies the serializer authority.</param>
    /// <returns>Frozen options for public contract projection.</returns>
    /// <exception cref="InvalidOperationException">The declaration or factory is missing or invalid.</exception>
    public static JsonSerializerOptions Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var declaration = assembly.GetCustomAttribute<JsonContractOptionsAttribute>()
            ?? throw new InvalidOperationException($"Contracts assembly '{assembly.GetName().Name}' does not declare JSON contract options.");
        var method = declaration.ProviderType.GetMethod(declaration.FactoryMethod,
            BindingFlags.Public | BindingFlags.Static, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (method is null || method.ContainsGenericParameters || method.ReturnType != typeof(JsonSerializerOptions))
            throw new InvalidOperationException("A JSON contract options factory must be a public static parameterless method returning JsonSerializerOptions.");
        JsonSerializerOptions options;
        try
        {
            options = method.Invoke(null, null) as JsonSerializerOptions
                ?? throw new InvalidOperationException("The JSON contract options factory returned null.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"JSON contract options factory '{declaration.ProviderType.FullName}.{declaration.FactoryMethod}' failed.",
                exception.InnerException);
        }
        var snapshot = new JsonSerializerOptions(options);
        snapshot.MakeReadOnly(populateMissingResolver: true);
        return snapshot;
    }
}
