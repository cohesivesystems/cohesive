using System.Collections.Immutable;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class WorldDefinitionTests
{
    [Fact]
    public void TypedAuthoring_CompilesNamedPopulationsIntoStableScopedStreams()
    {
        var world = DemoWorld(["orders", "customers"]);

        var plan = world.Compile();
        var customers = plan.GetPopulation("customers");
        var orders = plan.GetPopulation("orders");
        var generatedCustomers = customers.Generate(seed: 1729);
        var generatedOrders = orders.Generate(seed: 1729);

        Assert.Equal(["customers", "orders"], plan.Populations.Select(static item => item.Definition.Id));
        Assert.Equal(3, generatedCustomers.Length);
        Assert.Equal(4, generatedOrders.Length);
        Assert.Equal(
            WorldPopulationScopeConvention.Create(world.Id, "customers"),
            customers.Scope);
        Assert.NotEqual(customers.Scope, orders.Scope);
        Assert.All(generatedCustomers, item => Assert.Equal(customers.Scope, item.Replay.Scope));
        Assert.All(generatedOrders, item => Assert.Equal(orders.Scope, item.Replay.Scope));
    }

    [Fact]
    public void RepeatedWorldGeneration_IsDeterministicWithoutCouplingPopulations()
    {
        var plan = DemoWorld(["customers", "orders"]).Compile();
        var customers = plan.GetPopulation("customers");
        var orders = plan.GetPopulation("orders");

        var first = customers.Generate(seed: 42);
        var second = customers.Generate(seed: 42);
        var orderValues = orders.Generate(seed: 42);

        Assert.Equal(
            first.Select(static item => item.Observation),
            second.Select(static item => item.Observation));
        Assert.Equal(
            first.Select(static item => item.Replay),
            second.Select(static item => item.Replay));
        Assert.Contains(first.Zip(orderValues), static pair =>
            !pair.First.Observation.Equals(pair.Second.Observation));
    }

    [Fact]
    public void AddingAnUnrelatedPopulation_DoesNotPerturbExistingPopulationAddresses()
    {
        var customers = CustomerGeneration();
        var baseWorld = Simulation.DefineWorld("world/demo", "r1", world => world
            .Population("customers", count: 3, customers));
        var extendedWorld = Simulation.DefineWorld("world/demo", "r2", world => world
            .Population("operators", count: 2, CustomerGeneration())
            .Population("customers", count: 3, customers));

        var before = baseWorld.Compile().GetPopulation("customers").Generate(seed: 91);
        var after = extendedWorld.Compile().GetPopulation("customers").Generate(seed: 91);

        Assert.Equal(
            before.Select(static item => item.Observation),
            after.Select(static item => item.Observation));
        Assert.Equal(
            before.Select(static item => item.Replay),
            after.Select(static item => item.Replay));
        Assert.NotEqual(baseWorld.Compile().Fingerprint, extendedWorld.Compile().Fingerprint);
    }

    [Fact]
    public void DifferentWorldIdentities_IsolateOtherwiseEquivalentPopulationStreams()
    {
        var first = Simulation.DefineWorld("world/first", "r1", world => world
                .Population("customers", count: 4, CustomerGeneration()))
            .Compile();
        var second = Simulation.DefineWorld("world/second", "r1", world => world
                .Population("customers", count: 4, CustomerGeneration()))
            .Compile();

        var firstPopulation = first.GetPopulation("customers");
        var secondPopulation = second.GetPopulation("customers");
        var firstValues = firstPopulation.Generate(seed: 700);
        var secondValues = secondPopulation.Generate(seed: 700);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(firstPopulation.Scope, secondPopulation.Scope);
        Assert.Contains(firstValues.Zip(secondValues), static pair =>
            !pair.First.Observation.Equals(pair.Second.Observation));
    }

    [Fact]
    public void Population_CanMaterializeThroughAnExactTypedGenerationInterpretation()
    {
        var customers = CustomerGeneration();
        var population = Simulation.DefineWorld("world/typed", "r1", world => world
                .Population("customers", count: 3, customers))
            .Compile()
            .GetPopulation("customers");

        var generated = population.Generate(seed: 12, generator: customers.Compile());

        Assert.Equal(3, generated.Length);
        Assert.All(generated, item => Assert.IsType<WorldCustomer>(item.Value));
        Assert.All(generated, item => Assert.Equal(population.Scope, item.Replay.Scope));
        Assert.Throws<ArgumentException>(() => population.Generate(
            seed: 12,
            generator: OrderGeneration().Compile()));
    }

    [Fact]
    public void PopulationEnumeration_IsLazyAcrossItsDeclaredBound()
    {
        var world = Simulation.DefineWorld("world/preview", "r1", builder => builder
            .Population("customers", count: int.MaxValue, CustomerGeneration()));
        var population = world.Compile().GetPopulation("customers");

        var preview = population.Enumerate(seed: 100).Take(2).ToArray();

        Assert.Equal(2, preview.Length);
        Assert.Equal(0, preview[0].Replay.SequenceIndex);
        Assert.Equal(1, preview[1].Replay.SequenceIndex);
    }

    [Fact]
    public void PopulationReplay_RemainsExactAgainstItsNestedGenerationPlan()
    {
        var population = DemoWorld(["customers", "orders"])
            .Compile()
            .GetPopulation("customers");
        var generated = population.Enumerate(seed: 301).ElementAt(2);

        var replayed = ReferenceGenerationInterpreter.Replay(
            population.GenerationPlan,
            generated.Replay.ToToken());

        Assert.Equal(generated, replayed);
    }

    [Fact]
    public void InvalidWorlds_ProducePreciseNestedDiagnostics()
    {
        var empty = new WorldDefinition("world/empty", "r1", []);
        var duplicate = new WorldDefinition(
            "world/duplicate",
            "r1",
            [
                new("customers", 1, CustomerGeneration().Definition),
                new("customers", 1, CustomerGeneration().Definition)
            ]);
        var negative = new WorldDefinition(
            "world/negative",
            "r1",
            [new("customers", -1, CustomerGeneration().Definition)]);
        var invalidGeneration = new WorldDefinition(
            "world/invalid-generation",
            "r1",
            [new("customers", 1, InvalidCustomerGeneration())]);

        AssertDiagnostic(empty.CompileResult(), "simulation.world.populationsMissing", "/populations");
        AssertDiagnostic(
            duplicate.CompileResult(),
            "simulation.world.populationIdentityDuplicate",
            "/populations/1/id");
        AssertDiagnostic(
            negative.CompileResult(),
            "simulation.world.populationCountInvalid",
            "/populations/0/count");
        AssertDiagnostic(
            invalidGeneration.CompileResult(),
            "simulation.generation.int32RangeInvalid",
            "/populations/0/generation/root/members/1/generator");
    }

    static void AssertDiagnostic(
        WorldCompilationResult result,
        string code,
        string location)
    {
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, diagnostic =>
            diagnostic.Code == code && diagnostic.Location == location);
    }

    static WorldDefinition DemoWorld(IReadOnlyList<string> order)
    {
        var customers = CustomerGeneration();
        var orders = OrderGeneration();
        return Simulation.DefineWorld("world/demo", "r1", world =>
        {
            foreach (var population in order)
            {
                switch (population)
                {
                    case "customers":
                        world.Population("customers", count: 3, customers);
                        break;
                    case "orders":
                        world.Population("orders", count: 4, orders);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown population '{population}'.");
                }
            }
        });
    }

    static PocoGenerationDefinition<WorldCustomer> CustomerGeneration() =>
        Simulation.Define<WorldCustomer>(customer => customer
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Ada", weight: 1d),
                Gen.Weighted("Grace", weight: 1d)))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));

    static PocoGenerationDefinition<WorldOrder> OrderGeneration() =>
        Simulation.Define<WorldOrder>(order => order
            .Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 1_000_000))
            .Member(value => value.Expedited, Gen.Bernoulli(probability: 0.2)));

    static GenerationDefinition InvalidCustomerGeneration()
    {
        var valid = CustomerGeneration().Definition;
        var members = valid.Root.Members
            .Select(static member => member.Identity.Value == "Age"
                ? new RecordGenerationMember(member.Identity, new Int32GenerationNode(minimum: 90, maximum: 18))
                : member)
            .ToImmutableArray();
        return new(
            valid.Id,
            valid.Revision,
            valid.ShapeGraph,
            new(valid.Root.ShapeId, members));
    }

    public sealed record WorldCustomer(string Name, int Age);

    public sealed record WorldOrder(int Number, bool Expedited);
}
