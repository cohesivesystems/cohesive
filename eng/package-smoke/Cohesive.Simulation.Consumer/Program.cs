using System.Text.Json;
using Cohesive.Simulation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

if (args.Length != 2)
    throw new ArgumentException("Expected 'emit <world-path>' or 'verify <json-lines-path>'.");

switch (args[0])
{
    case "emit":
        var customers = Simulation.Define<SmokeCustomer>(customer => customer
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Ada", weight: 1d),
                Gen.Weighted("Grace", weight: 1d)))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        var world = Simulation.DefineWorld("world/package-smoke", "r1", builder => builder
            .Population("customers", count: 2, customers)
            .Exemplar("customer-for-ui", "customers", sequenceIndex: 1));
        await File.WriteAllTextAsync(args[1], WorldDefinitionJsonSerializer.Serialize(world));
        break;
    case "verify":
        var lines = await File.ReadAllLinesAsync(args[1]);
        if (lines.Length != 2)
            throw new InvalidOperationException($"Expected two provisioned items but found '{lines.Length}'.");
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            Require(root.GetProperty("format").GetString(), WorldJsonLinesSink.Format, "format");
            Require(root.GetProperty("targetId").GetString(), "package-smoke/cli", "targetId");
            Require(root.GetProperty("worldId").GetString(), "world/package-smoke", "worldId");
            Require(root.GetProperty("rootSeed").GetString(), "42", "rootSeed");
            if (root.GetProperty("populationCount").GetInt32() != 2
                || root.GetProperty("sequenceIndex").GetInt64() != index)
            {
                throw new InvalidOperationException($"Provisioned item '{index}' has invalid population coordinates.");
            }

            var exemplars = root.GetProperty("exemplars").EnumerateArray().ToArray();
            if ((index == 0 && exemplars.Length != 0)
                || (index == 1
                    && (exemplars.Length != 1
                        || !string.Equals(exemplars[0].GetString(), "customer-for-ui", StringComparison.Ordinal))))
            {
                throw new InvalidOperationException($"Provisioned item '{index}' has invalid exemplar evidence.");
            }
        }
        break;
    default:
        throw new ArgumentException($"Unknown smoke-test command '{args[0]}'.");
}

return 0;

static void Require(string? actual, string expected, string property)
{
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected {property} '{expected}' but found '{actual}'.");
}

sealed record SmokeCustomer(string Name, int Age);
