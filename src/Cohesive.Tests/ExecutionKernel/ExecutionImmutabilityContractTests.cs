using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.IR;
using Cohesive.Relations.IR;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionImmutabilityContractTests
{
    [Fact]
    public void FrameworkMarkedPayloadGraphsExposeNoMutableState()
    {
        var assemblies = new[] { typeof(InteractionContractDefinition).Assembly, typeof(ProcessDefinition).Assembly,
            typeof(TransitionDefinition).Assembly, typeof(RelationQueryDefinition).Assembly };
        var roots = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IImmutableExecutionDefinition).IsAssignableFrom(type) && type.IsClass).ToArray();
        Assert.NotEmpty(roots);
        var errors = new List<string>();
        foreach (var root in roots) Inspect(root, root.Name, new(), errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void AuditRejectsMutableConsumerShapes()
    {
        var errors = new List<string>();
        Inspect(typeof(BadConsumer), nameof(BadConsumer), new(), errors);
        Assert.Contains(errors, error => error.Contains("setter"));
        Assert.Contains(errors, error => error.Contains("array"));
        Assert.Contains(errors, error => error.Contains("collection"));
    }

    static void Inspect(Type type, string path, HashSet<Type> visited, List<string> errors)
    {
        if (!visited.Add(type)) return;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(Type) || type == typeof(JsonElement)) return;
        // ObservationValue is an owned-snapshot value with read-only views, covered by model
        // ownership tests. Walking its IReadOnlyDictionary facade alone cannot prove that contract.
        if (type == typeof(ObservationValue)) return;
        if (type == typeof(object)) { errors.Add(path + ": unverifiable object graph"); return; }
        if (type.IsArray) { errors.Add(path + ": mutable array"); return; }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Nullable<>)) { Inspect(type.GetGenericArguments()[0], path, visited, errors); return; }
            if (type.Namespace == "System.Collections.Immutable" || type.Namespace == "System.Collections.Frozen")
            {
                foreach (var argument in type.GetGenericArguments()) Inspect(argument, path, visited, errors);
                return;
            }
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            { errors.Add(path + ": mutable or unverifiable collection " + type); return; }
        }
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        { errors.Add(path + ": mutable or unverifiable collection " + type); return; }
        foreach (var derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
            Inspect(derived.DerivedType, path + "/" + derived.DerivedType.Name, visited, errors);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            if (property.SetMethod is { } setter && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
                errors.Add(path + "/" + property.Name + ": mutable setter");
            Inspect(property.PropertyType, path + "/" + property.Name, visited, errors);
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.IsInitOnly) errors.Add(path + "/" + field.Name + ": mutable field");
            Inspect(field.FieldType, path + "/" + field.Name, visited, errors);
        }
    }

    public sealed class BadConsumer : IImmutableExecutionDefinition
    {
        public string Text { get; set; } = "";
        public int[] Values { get; } = [];
        public List<string> Items { get; } = [];
    }
}
