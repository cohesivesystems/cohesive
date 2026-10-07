using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Storage.Processes;

namespace AspireFirst.Orders;

/// <summary>Host-lifetime preparation of the declared process against receipt-capable local repositories.</summary>
/// <remarks>This is single-partition demo composition, not a production authorization policy or recovery worker.</remarks>
public sealed class FulfillmentProcessBindings
{
    /// <summary>Prepares exact transition links and the finite process interpreter once.</summary>
    /// <param name="orders">Local order authority with atomic receipt support.</param>
    /// <param name="inventory">Local inventory authority with atomic receipt support.</param>
    /// <exception cref="InvalidOperationException">Compilation or required persistence capability is invalid.</exception>
    public FulfillmentProcessBindings(IEntityRepository<Order> orders, IEntityRepository<InventoryItem> inventory)
    {
        var validation = InteractionContractCatalog.TryCreate([], out var contracts);
        if (!validation.IsValid) throw new InvalidOperationException("The empty interaction catalog is invalid.");
        var bindings = new[]
        {
            new ProcessTransitionOperationBinding(Require(InventoryTransitions.Reserve.Compile()), inventory, contracts!, partitionKey: FulfillmentDemo.LocalPartition),
            new ProcessTransitionOperationBinding(Require(InventoryTransitions.Release.Compile()), inventory, contracts!, partitionKey: FulfillmentDemo.LocalPartition),
            new ProcessTransitionOperationBinding(Require(OrderTransitions.Submit.Compile()), orders, contracts!, partitionKey: FulfillmentDemo.LocalPartition)
        };
        var compilation = FulfillmentProcess.Definition.Compile(new ProcessDefinitionValidationContext(
            bindings.Select(binding => binding.CreateProcessDefinitionLink()).Append(FulfillmentProcess.Stock.CreateProcessDefinitionLink())));
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var plan = compilation.Plan!;
        Declaration = Service.Define(new("fulfillment"), new("1"), FulfillmentProcess.Provenance)
            .Operation("fulfill").Run(plan).ExecuteEphemerally(TimeSpan.FromSeconds(15)).Build();
        var byReference = bindings.ToDictionary(binding => binding.Plan.DefinitionReference);
        var transitions = new EntityTransitionProcessOperationAdapter(invocation => byReference.GetValueOrDefault(invocation.Definition));
        var queries = new ProcessRelationHandlerCatalog([
            ProcessRelationHandlerRegistration.Create(FulfillmentProcess.Stock, async (context, _, input) =>
            {
                var item = await inventory.TryGetEntity(context, input.Sku, new(partitionKey: FulfillmentDemo.LocalPartition));
                return new InventoryAvailability(item is not null, item?.Available ?? 0);
            })]);
        var host = new RegisteredAsyncProcessReferenceHost(queries, transitions.ExecuteAsync);
        Runtime = new ServiceRuntime(Declaration, [new ServiceEphemeralProcessBinding("fulfill", plan,
            "aspire-first", (_, _) => host)], new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
    }

    /// <summary>Service policy declares finite, invocation-local execution.</summary>
    public ExecutionDefinitionDocument Declaration { get; }
    /// <summary>Prepared runtime; invocation identities and state are supplied per call.</summary>
    public ServiceRuntime Runtime { get; }

    /// <summary>Maps the local demonstration route through the canonical service binding.</summary>
    /// <param name="app">Native endpoint builder.</param>
    public void Map(WebApplication app) => app.MapServiceEphemeralProcess(Declaration, _ => Runtime, "fulfill",
        FulfillmentProcess.Definition, new("POST", "/fulfillment", [], new(typeof(FulfillOrder))));

    static CompiledTransitionPlan Require(TransitionCompilationResult compilation) => compilation.Plan
        ?? throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics.Select(d => d.Code + ": " + d.Message)));

}
