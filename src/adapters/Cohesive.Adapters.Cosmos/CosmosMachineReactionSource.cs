using Cohesive.Execution;
using Cohesive.Prelude;
using Cohesive.Storage.Processes;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Observes immutable entity transition receipts through Cosmos manual-checkpoint change delivery.</summary>
/// <remarks>Uses the existing SDK processor lifecycle and repository receipt decoder. It does not infer
/// movements from mutable entity snapshots. Receipt retention must cover the selected observation window. Source checkpointing waits for durable
/// Process admission, not target availability; a separately supervised recovery worker discovers those starts.</remarks>
public sealed class CosmosMachineReactionSource
{
    readonly CosmosEntityOutboxRepository repository;
    readonly MachineReactionHost host;
    readonly ICosmosManagedMaterializationChangeFeedProcessor processor;

    /// <summary>Binds the source repository's own container and a policy-specific lease namespace.</summary>
    /// <param name="repository">Source receipt authority; its configured container and decoder are reused.</param>
    /// <param name="leaseContainer">SDK lease/checkpoint authority, owned by the caller.</param>
    /// <param name="host">Registered semantic policy and target realization.</param>
    /// <param name="processorName">Stable deployment seed.</param>
    /// <param name="instanceName">Ephemeral worker identity.</param>
    /// <param name="fromUtc">Explicit initial observation boundary, used only before leases exist.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or the initial boundary is not UTC.</exception>
    /// <exception cref="InvalidOperationException">The repository does not own the policy's source entity machine.</exception>
    public CosmosMachineReactionSource(CosmosEntityOutboxRepository repository, Container leaseContainer,
        MachineReactionHost host, string processorName, string instanceName, DateTimeOffset fromUtc)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(leaseContainer);
        if (repository.EntityDefinition.Shape.Id != host.Policy.Payload.Source.Id)
            throw new InvalidOperationException("Source repository entity differs from the reaction policy.");
        Cohesive.Transitions.Machines.EntityMachineBinding.RequireSame(repository.EntityDefinition,
            Cohesive.Transitions.Machines.EntityMachineBinding.Read(host.Policy.Payload.Source));
        var policy = new CosmosManagedMaterializationChangeSourcePolicy(processorName, instanceName,
            CosmosManagedMaterializationInitialPosition.AtTime, fromUtc);
        var source = repository.TransitionReceiptContainer;
        var identity = new Scope(processorName, host.Policy.Document.Reference, host.Authority,
            CosmosPhysicalAffinity.CanonicalAccountEndpointText(source.Database.Client.Endpoint), source.Database.Id, source.Id,
            CosmosPhysicalAffinity.CanonicalAccountEndpointText(leaseContainer.Database.Client.Endpoint), leaseContainer.Database.Id,
            leaseContainer.Id, fromUtc);
        ProcessorName = "reaction-v1-" + CosmosEntityOutboxRepository.HashIdentity(identity);
        processor = new CosmosManagedMaterializationChangeFeedProcessor(source, leaseContainer, policy, ProcessorName);
    }

    /// <summary>Effective processor namespace, derived from policy revision, authority, physical placement and initial boundary.</summary>
    public string ProcessorName { get; }

    /// <summary>Runs until cancellation or failed delivery; failed callbacks are never acknowledged.</summary>
    /// <param name="createContext">Creates a trusted worker context bound to callback cancellation.</param>
    /// <param name="cancellationToken">Processor lifetime cancellation.</param>
    /// <returns>Processor lifetime; exceptions propagate after stopping the SDK processor.</returns>
    /// <exception cref="ArgumentNullException">Context factory is null.</exception>
    /// <exception cref="OperationCanceledException">The worker is cancelled.</exception>
    public Task RunAsync(Func<CancellationToken, OperationContext> createContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        return processor.RunAsync(async (batch, token) =>
        {
            var context = createContext(token) with { CancellationToken = token };
            await DeliverBatchAsync(repository, host, context, batch).ConfigureAwait(false);
        }, cancellationToken);
    }

    internal static async Task DeliverBatchAsync(CosmosEntityOutboxRepository repository, MachineReactionHost host,
        OperationContext context, CosmosManagedMaterializationProviderBatch batch)
    {
        foreach (var document in batch.Documents)
        {
            context.ThrowIfCancellationRequested();
            if (repository.ReadTransitionFeedReceipt(document) is { } receipt)
                await host.AdmitAsync(context, receipt).ConfigureAwait(false);
        }
        context.ThrowIfCancellationRequested();
        await batch.CheckpointAsync().ConfigureAwait(false);
    }

    sealed record Scope(string Seed, ExecutionDefinitionReference Policy, InteractionAuthorityScope Authority, string SourceAccount, string SourceDatabase,
        string SourceContainer, string LeaseAccount, string LeaseDatabase, string LeaseContainer, DateTimeOffset FromUtc);
}
