using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Machines;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

// An interactive load-detail/actions consumer. Only the service declaration supplies action IDs,
// contracts and permissions; the menu consumes inspection results instead of duplicating guards.
var role = args.FirstOrDefault() ?? "dispatcher";
var capabilities = role switch
{
    "dispatcher" => ImmutableArray.Create("loads.release"),
    "supervisor" => ImmutableArray.Create("loads.release", "loads.cancel"),
    "viewer" => ImmutableArray<string>.Empty,
    _ => throw new ArgumentException("Role must be dispatcher, supervisor or viewer.")
};
var documentsComplete = !args.Contains("--missing-documents");
var author = new EntityTransitionAuthoring<Load, Command>(new("load"));
var source = Machine.Define("load.lifecycle", "1");
var ready = source.State("ready").Initial();
var released = source.State("released").Final();
var cancelled = source.State("cancelled").Final();
var release = source.Edge("release").From(ready).To(released)
    .When(author.Guard((load, command) => load.DocumentsComplete));
var cancel = source.Edge("cancel").From(ready).To(cancelled);
var links = author.Discriminator(source.Build(), load => load.Status)
    .Map(ready, LoadStatus.Ready).Map(released, LoadStatus.Released).Map(cancelled, LoadStatus.Cancelled).Build();
var entity = author.Entity;
CompiledTransitionPlan Plan(MachineMovement movement)
{
    var metadata = new TransitionAuthoringMetadata(new("load." + movement.Id), new("1"), new("body"),
        new(new(TransitionAuthoring.Producer), new("examples/load-actions"), DocumentOrigin.User));
    var transition = author.Create<string>(metadata, t => t.Move(movement, (load, command) => "Action unavailable")
        .Set(new("record-reason"), load => load.LastReason, (load, command) => command.Reason)
        .Return(new("result"), TransitionOutcomeDisposition.Applied, "Updated"));
    var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
    return compiled.Plan ?? throw new InvalidOperationException(string.Join("; ", compiled.Validation.Diagnostics.Select(d => d.Message)));
}
var plans = new[] { (Movement: release, Plan: Plan(release)), (Movement: cancel, Plan: Plan(cancel)) };
var declaration = new ServiceDefinition([.. plans.Select(item => new ServiceTransitionOperation(item.Movement.Id,
    entity.StateShape.QualifiedId, item.Plan.DefinitionReference, [new("loads." + item.Movement.Id)]))]);
var service = ServiceDefinitionDocuments.Create(new("loads"), new("1"), declaration,
    new(new("examples"), new("load-actions"), DocumentOrigin.User));
var repository = new InMemoryEntityOutboxRepository(entity, _ => "org-a");
await repository.Upsert(OperationContext.Create(), new(entity.CreateState("load-1",
    new Load("org-a", LoadStatus.Ready, documentsComplete, "")).Snapshot));
var runtime = new ServiceRuntime(service,
    plans.Select(item => new ServiceTransitionBinding(item.Movement.Id, item.Plan, entity, _ => repository)),
    new IdentityServiceInvocationAuthorization("organization", new(nameof(Load.OrganizationId))));

// A production host resolves roles into grants from its trusted identity boundary. This local demo
// uses an explicit role argument and one organization; these arguments are not an authentication API.
var actor = new PrincipalRef("demo-operator", PrincipalKind.User);
var scope = new ScopeRef("org-a", "organization");
var identity = new IdentityContext(actor, EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
    Grants: [new IdentityScopeGrant(actor, scope, capabilities, "demo-role-resolver")]);
var context = OperationContext.Create().WithIdentityContext(identity);
Console.WriteLine($"Load load-1: Ready; documents {(documentsComplete ? "complete" : "missing")}; role {role}");
var available = await runtime.InspectTransitionsAsync(context, entity.StateShape.QualifiedId, "load-1");
foreach (var action in available) Console.WriteLine($"  {action.OperationId}: {action.Kind}");
if (available.IsEmpty) { Console.WriteLine("No discoverable actions."); return; }

// The command form supplies complete input and asks again; omission never means a guessed value.
var inputs = available.ToDictionary(action => action.OperationId,
    action => PortableValue.Concrete(action.Input, ObservationValue.FromObject(new Command("Customer request"))));
var evaluated = await runtime.InspectTransitionsAsync(context, entity.StateShape.QualifiedId, "load-1", inputs);
Console.WriteLine("With reason: Customer request");
foreach (var action in evaluated) Console.WriteLine($"  {action.OperationId}: {action.Kind}");
var executeIndex = Array.IndexOf(args, "--execute");
if (executeIndex < 0) return;
if (executeIndex + 1 >= args.Length) throw new ArgumentException("Supply an action after --execute.");
var selected = evaluated.SingleOrDefault(action => action.OperationId == args[executeIndex + 1]);
if (selected is null || selected.Kind != ServiceTransitionAvailabilityKind.Permitted)
{
    Console.WriteLine("Selected action is not permitted.");
    Environment.ExitCode = 2;
    return;
}
var result = await runtime.InvokeAsync(context, selected.OperationId, "load-1", selected.ConcurrencyToken,
    new("demo/" + Guid.NewGuid().ToString("N")), inputs[selected.OperationId]);
Console.WriteLine($"Execution: {result.Kind}");
var retained = await repository.TryGet(context, "load-1", EntityReadOptions.Full.WithPartitionKey("org-a"));
Console.WriteLine($"Current status: {retained!.Entity.Observation.GetField(nameof(Load.Status)).GetString()}");

/// <summary>Typed lifecycle representation used by the load entity.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LoadStatus>))]
public enum LoadStatus
{
    /// <summary>Waiting for dispatch.</summary>
    Ready,
    /// <summary>Released to the carrier.</summary>
    Released,
    /// <summary>Cancelled by an authorized operator.</summary>
    Cancelled
}
/// <summary>A load shown in the action menu.</summary>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Status">Lifecycle discriminator.</param>
/// <param name="DocumentsComplete">Business precondition for release.</param>
/// <param name="LastReason">Reason recorded by the last command.</param>
public sealed record Load(string OrganizationId, LoadStatus Status, bool DocumentsComplete, string LastReason);
/// <summary>Input collected before inspecting or submitting an action.</summary>
/// <param name="Reason">Operator's explanation.</param>
public sealed record Command(string Reason);
