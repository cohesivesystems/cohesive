using System.Collections.Immutable;
using Cohesive.Model;
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
    public void NamedExemplar_ResolvesTheExactRawAndTypedPopulationMember()
    {
        var customers = CustomerGeneration();
        var plan = Simulation.DefineWorld("world/exemplars", "r1", world => world
                .Population("customers", count: 3, customers)
                .Exemplar("customer-for-ui", "customers", sequenceIndex: 2))
            .Compile();
        var population = plan.GetPopulation("customers");
        var typedGenerator = customers.Compile();

        var direct = population.Generate(seed: 42)[2];
        var exemplar = plan.GenerateExemplar("customer-for-ui", seed: 42);
        var typed = plan.GenerateExemplar("customer-for-ui", seed: 42, generator: typedGenerator);

        Assert.Equal(["customer-for-ui"], plan.Exemplars.Select(static item => item.Id));
        Assert.Equal(direct, exemplar);
        Assert.Equal(direct.Observation, typed.Observation);
        Assert.Equal(direct.Replay, typed.Replay);
        Assert.Equal(typedGenerator.Materializer.Materialize(direct.Observation), typed.Value);
        Assert.Throws<ArgumentException>(() => plan.GenerateExemplar(
            "customer-for-ui",
            seed: 42,
            generator: OrderGeneration().Compile()));
        Assert.Throws<KeyNotFoundException>(() => plan.GenerateExemplar("missing", seed: 42));
    }

    [Fact]
    public void AddingAnUnrelatedExemplar_DoesNotPerturbExistingGenerationCoordinates()
    {
        var customers = CustomerGeneration();
        var baseline = Simulation.DefineWorld("world/exemplar-stability", "r1", world => world
                .Population("customers", count: 3, customers)
                .Exemplar("primary-customer", "customers", sequenceIndex: 0))
            .Compile();
        var extended = Simulation.DefineWorld("world/exemplar-stability", "r2", world => world
                .Population("customers", count: 3, customers)
                .Exemplar("secondary-customer", "customers", sequenceIndex: 1)
                .Exemplar("primary-customer", "customers", sequenceIndex: 0))
            .Compile();

        Assert.Equal(
            baseline.GenerateExemplar("primary-customer", seed: 42),
            extended.GenerateExemplar("primary-customer", seed: 42));
        Assert.NotEqual(baseline.Fingerprint, extended.Fingerprint);
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

        Assert.Equal(generated.Generated, replayed);
    }

    [Fact]
    public void PopulationSequenceIdentity_IsStableAcrossSeedsAndCarriedByGeneratedItems()
    {
        var population = Simulation.DefineWorld("world/identity", "r1", world => world
                .Population("customers", count: 2, CustomerGeneration()))
            .Compile()
            .GetPopulation("customers");

        var first = population.Generate(seed: 41);
        var second = population.Generate(seed: 42);

        Assert.Equal(
            first.Select(static item => item.EntityId),
            second.Select(static item => item.EntityId));
        Assert.Equal(
            WorldEntitySequenceIdentityConvention.Create(population.Scope, sequenceIndex: 0),
            first[0].EntityId);
        Assert.NotEqual(first[0].Replay, second[0].Replay);
    }

    [Fact]
    public void UniqueObservationFieldIdentity_ResolvesDuringPureWorldGeneration()
    {
        var population = Simulation.DefineWorld("world/external-identity", "r1", world => world
                .Population(
                    "customers",
                    count: 1,
                    WorldEntityIdentityPolicy.FromUniqueObservationField("Name"),
                    ConstantCustomerGeneration("customer-42")))
            .Compile()
            .GetPopulation("customers");

        var generated = Assert.Single(population.Generate(seed: 42));

        Assert.Equal(new EntityId("customer-42"), generated.EntityId);
        Assert.Equal("customer-42", generated.Observation.GetField("Name").GetString());
    }

    [Fact]
    public void DuplicateUniqueObservationFieldIdentity_FailsWithStructuredGenerationEvidence()
    {
        var customers = ConstantCustomerGeneration("duplicate");
        var population = Simulation.DefineWorld("world/duplicate-identity", "r1", world => world
                .Population(
                    "customers",
                    count: 2,
                    WorldEntityIdentityPolicy.FromUniqueObservationField("Name"),
                    customers))
            .Compile()
            .GetPopulation("customers");

        var exception = Assert.Throws<WorldGenerationException>(() => population.Generate(seed: 42));

        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal("simulation.world.entityIdentityDuplicate", diagnostic.Code);
        Assert.Equal("/populations/customers/items/1/entityId", diagnostic.Location);
        Assert.Equal("world-generation", diagnostic.Evidence?.Stage);
        Assert.Throws<WorldGenerationException>(() => population.Generate(seed: 42, customers.Compile()));
    }

    [Fact]
    public void InvalidUniqueObservationFieldValues_FailWithStructuredGenerationEvidence()
    {
        var missing = Simulation.DefineWorld("world/missing-identity", "r1", world => world
                .Population(
                    "customers",
                    count: 1,
                    WorldEntityIdentityPolicy.FromUniqueObservationField("Missing"),
                    ConstantCustomerGeneration("customer-42")))
            .Compile()
            .GetPopulation("customers");
        var empty = Simulation.DefineWorld("world/empty-identity", "r1", world => world
                .Population(
                    "customers",
                    count: 1,
                    WorldEntityIdentityPolicy.FromUniqueObservationField("Name"),
                    ConstantCustomerGeneration(string.Empty)))
            .Compile()
            .GetPopulation("customers");
        var unsupported = Simulation.DefineWorld("world/unsupported-identity", "r1", world => world
                .Population(
                    "customers/with~key",
                    count: 1,
                    WorldEntityIdentityPolicy.FromUniqueObservationField("Key"),
                    NestedIdentityGeneration()))
            .Compile()
            .GetPopulation("customers/with~key");

        AssertIdentityFailure(
            missing,
            "simulation.world.entityIdentityValueMissing",
            "/populations/customers/items/0/entityId");
        AssertIdentityFailure(
            empty,
            "simulation.world.entityIdentityValueInvalid",
            "/populations/customers/items/0/entityId");
        AssertIdentityFailure(
            unsupported,
            "simulation.world.entityIdentityValueInvalid",
            "/populations/customers~1with~0key/items/0/entityId");
        Assert.Throws<WorldGenerationException>(() => unsupported.Generate(
            seed: 42,
            generator: NestedIdentityGeneration().Compile()));
    }

    [Fact]
    public void IdentityPolicy_IsFingerprintSignificantAndInvalidCombinationsProduceDiagnostics()
    {
        var generation = ConstantCustomerGeneration("customer-42").Definition;
        var sequence = new WorldDefinition(
            "world/identity-policy",
            "r1",
            [new("customers", 1, WorldEntityIdentityPolicy.PopulationSequence, generation)]);
        var unique = new WorldDefinition(
            "world/identity-policy",
            "r1",
            [new(
                "customers",
                1,
                WorldEntityIdentityPolicy.FromUniqueObservationField("Name"),
                generation)]);
        var unexpectedField = new WorldDefinition(
            "world/invalid-sequence-identity",
            "r1",
            [new(
                "customers",
                1,
                new(WorldEntityIdentitySource.PopulationSequence, FieldPath.FromField("Name")),
                generation)]);
        var missingField = new WorldDefinition(
            "world/invalid-unique-identity",
            "r1",
            [new(
                "customers",
                1,
                new(WorldEntityIdentitySource.UniqueObservationField),
                generation)]);
        var unsupportedSource = new WorldDefinition(
            "world/unsupported-identity-source",
            "r1",
            [new(
                "customers",
                1,
                new((WorldEntityIdentitySource)int.MaxValue),
                generation)]);

        Assert.NotEqual(sequence.Compile().Fingerprint, unique.Compile().Fingerprint);
        AssertDiagnostic(
            unexpectedField.CompileResult(),
            "simulation.world.entityIdentityFieldUnexpected",
            "/populations/0/entityIdentity/observationField");
        AssertDiagnostic(
            missingField.CompileResult(),
            "simulation.world.entityIdentityFieldMissing",
            "/populations/0/entityIdentity/observationField");
        AssertDiagnostic(
            unsupportedSource.CompileResult(),
            "simulation.world.entityIdentitySourceInvalid",
            "/populations/0/entityIdentity/source");
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

    [Fact]
    public void InvalidExemplars_ProducePreciseStructuredDiagnostics()
    {
        WorldPopulationDefinition population = new(
            "customers",
            count: 2,
            CustomerGeneration().Definition);
        var duplicate = new WorldDefinition(
            "world/duplicate-exemplar",
            "r1",
            [population],
            [
                new("customer-for-ui", "customers", sequenceIndex: 0),
                new("customer-for-ui", "customers", sequenceIndex: 1)
            ]);
        var unknownPopulation = new WorldDefinition(
            "world/unknown-exemplar-population",
            "r1",
            [population],
            [new("customer-for-ui", "missing", sequenceIndex: 0)]);
        var outOfRange = new WorldDefinition(
            "world/out-of-range-exemplar",
            "r1",
            [population],
            [new("customer-for-ui", "customers", sequenceIndex: 2)]);
        var negative = new WorldDefinition(
            "world/negative-exemplar",
            "r1",
            [population],
            [new("customer-for-ui", "customers", sequenceIndex: -1)]);
        var empty = new WorldDefinition(
            "world/empty-exemplar-population",
            "r1",
            [new("customers", count: 0, CustomerGeneration().Definition)],
            [new("customer-for-ui", "customers", sequenceIndex: 0)]);

        AssertDiagnostic(
            duplicate.CompileResult(),
            "simulation.world.exemplarIdentityDuplicate",
            "/exemplars/1/id");
        AssertDiagnostic(
            unknownPopulation.CompileResult(),
            "simulation.world.exemplarPopulationUnknown",
            "/exemplars/0/populationId");
        AssertDiagnostic(
            outOfRange.CompileResult(),
            "simulation.world.exemplarSequenceIndexInvalid",
            "/exemplars/0/sequenceIndex");
        AssertDiagnostic(
            negative.CompileResult(),
            "simulation.world.exemplarSequenceIndexInvalid",
            "/exemplars/0/sequenceIndex");
        AssertDiagnostic(
            empty.CompileResult(),
            "simulation.world.exemplarSequenceIndexInvalid",
            "/exemplars/0/sequenceIndex");
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

    static void AssertIdentityFailure(
        CompiledWorldPopulation population,
        string code,
        string location)
    {
        var exception = Assert.Throws<WorldGenerationException>(() => population.Generate(seed: 42));
        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(location, diagnostic.Location);
        Assert.Equal("world-generation", diagnostic.Evidence?.Stage);
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

    static PocoGenerationDefinition<WorldCustomer> ConstantCustomerGeneration(string name) =>
        Simulation.Define<WorldCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant(name))
            .Member(value => value.Age, Gen.Constant(42)));

    static PocoGenerationDefinition<WorldNestedIdentity> NestedIdentityGeneration() =>
        Simulation.Define<WorldNestedIdentity>(value => value
            .Member(item => item.Key, Gen.Constant(new WorldIdentityKey("nested"))));

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

    public sealed record WorldNestedIdentity(WorldIdentityKey Key);

    public sealed record WorldIdentityKey(string Value);
}
