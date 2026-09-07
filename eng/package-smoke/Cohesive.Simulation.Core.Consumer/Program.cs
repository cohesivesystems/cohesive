using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Scenarios;
using Cohesive.Simulation.Worlds;

var customers = Simulation.Define<Customer>(customer => customer
    .Member(value => value.Name, Gen.Categorical(
        Gen.Weighted("Ada", weight: 1d),
        Gen.Weighted("Grace", weight: 1d)))
    .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
var generator = customers.Compile();
var generated = generator.Generate(seed: 42);
if (generated.Value.Age is < 18 or > 90)
    throw new InvalidOperationException("Generated customer age is outside the authored contract.");

var propertyRun = generator.CheckProperty(
    seed: 42,
    property: static customer => customer.Age >= 18,
    options: new(requiredPassedCases: 32));
if (propertyRun.Status != PropertyCaseRunStatus.Passed)
    throw new InvalidOperationException($"Expected passing property cases but found '{propertyRun.Status}'.");

var world = Simulation.DefineWorld("world/core-package-quickstart", "r1", builder => builder
    .Population("customers", count: 2, customers)
    .Exemplar("customer-for-scenario", "customers", sequenceIndex: 1));
var manifest = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 42);
var retainedManifest = WorldArtifactManifestJsonSerializer.Deserialize(
    WorldArtifactManifestJsonSerializer.Serialize(manifest));
var scenario = Simulation.DefineScenario(
    id: "scenario/core-package-quickstart",
    revision: "r1",
    initialWorld: retainedManifest,
    startsAtUtc: DateTimeOffset.UnixEpoch,
    configure: builder => builder
        .Operation<AdvanceCustomer, AdvanceReceipt>("customer.advance-age")
        .Actor("customer", "customer-for-scenario")
        .Action(
            id: "advance-customer",
            afterStart: TimeSpan.FromMinutes(1),
            actorId: "customer",
            operationId: "customer.advance-age",
            input: new AdvanceCustomer(Years: 1)));
var retainedScenario = ScenarioDefinitionJsonSerializer.Deserialize(
    ScenarioDefinitionJsonSerializer.Serialize(scenario));
var initialWorld = ScenarioWorldSnapshot.FromCoreWorld(retainedScenario);
var trace = await ScenarioRunner.ExecuteAsync(initialWorld, new AdvanceCustomerInterpreter(customers.OutputShape));
var retainedTrace = ScenarioExecutionTraceJsonSerializer.Deserialize(
    ScenarioExecutionTraceJsonSerializer.Serialize(trace));
var before = initialWorld.GetActor("customer").Observation.Materialize<Customer>();
var after = retainedTrace.ToFinalWorldSnapshot().GetActor("customer").Observation.Materialize<Customer>();
if (after != before with { Age = before.Age + 1 })
    throw new InvalidOperationException("The retained scenario trace did not reconstruct the evolved actor state.");

Console.WriteLine(
    $"Cohesive.Simulation core package generated, checked, retained, evolved, and replayed '{after.Name}'.");

sealed record Customer(string Name, int Age);

sealed record AdvanceCustomer(int Years);

sealed record AdvanceReceipt(bool Accepted);

sealed class AdvanceCustomerInterpreter(GraphShapeId customerShape) : IScenarioActionInterpreter
{
    public string Identity => "package-smoke/core-scenario/v1";

    public ValueTask<ScenarioActionResult> ExecuteAsync(
        ScenarioActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var customer = context.ActorSnapshot.Observation.Materialize<Customer>();
        var input = context.Input.Value?.Deserialize<AdvanceCustomer>()
            ?? throw new InvalidOperationException("A concrete advance input is required.");
        var after = Observation.Create(
            customerShape,
            ObservationValue.FromObject(customer with { Age = customer.Age + input.Years }));
        var output = PortableValue.Concrete(
            context.Operation.Output,
            ObservationValue.FromObject(new AdvanceReceipt(Accepted: true)));
        return ValueTask.FromResult(new ScenarioActionResult(
            output,
            [ScenarioActorStateChange.Replace(context.ActorSnapshot, after)]));
    }
}
