using Cohesive.Execution;
using Cohesive.Api.Execution.Services;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Storage;

var model = new LoadApplication();
if (args.Contains("--generate") || args.Contains("--check-generated"))
{
    LoadClientGeneration.Write(model, Path.Combine(LoadApplication.ExampleDirectory, "ui/generated"), check: args.Contains("--check-generated"));
    return;
}
if (args.Contains("--serve"))
{
    await LoadWebHost.Run(model, args.Where(arg => arg != "--serve").ToArray());
    return;
}
// Keep the original console entry point alongside the browser consumer.
var role = args.FirstOrDefault() ?? "dispatcher";
await model.Seed("load-1", !args.Contains("--missing-documents"));
var context = OperationContext.Create().WithIdentityContext(model.Identity(role));
var available = await model.Runtime.InspectTransitionsAsync(context, model.Entity.StateShape.QualifiedId, "load-1");
foreach (var action in available) Console.WriteLine($"{action.OperationId}: {action.Kind}");
var inputs = available.ToDictionary(action => action.OperationId,
    action => PortableValue.Concrete(action.Input, ObservationValue.FromObject(new Command("Customer request"))));
var evaluated = await model.Runtime.InspectTransitionsAsync(context, model.Entity.StateShape.QualifiedId, "load-1", inputs);
foreach (var action in evaluated) Console.WriteLine($"With reason: {action.OperationId}: {action.Kind}");
var executeIndex = Array.IndexOf(args, "--execute");
if (executeIndex < 0) return;
if (executeIndex + 1 >= args.Length) throw new ArgumentException("Supply an action after --execute.");
var selected = evaluated.SingleOrDefault(action => action.OperationId == args[executeIndex + 1]);
if (selected?.Kind != ServiceTransitionAvailabilityKind.Permitted)
{
    Console.WriteLine("Selected action is not permitted.");
    Environment.ExitCode = 2;
    return;
}
var result = await model.Runtime.InvokeAsync(context, selected.OperationId, "load-1", selected.ConcurrencyToken,
    new("demo/" + Guid.NewGuid().ToString("N")), inputs[selected.OperationId]);
Console.WriteLine($"Execution: {result.Kind}");
var retained = await model.Repository.TryGet(context, "load-1", EntityReadOptions.Full.WithPartitionKey(LoadApplication.Organization));
Console.WriteLine($"Current status: {retained!.Entity.Observation.GetField(nameof(Load.Status)).GetString()}");
