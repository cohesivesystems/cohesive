using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Integrations.Authoring;

/// <summary>Source declaration for the existing bounded ingestion profile.</summary>
/// <typeparam name="TSelection">Acquisition selection payload.</typeparam>
public sealed class IngestionHandle<TSelection> where TSelection : class
{
    internal IntegrationAuthoringContext Owner { get; }
    internal string Name { get; }
    internal IntegrationRequest? Acquisition { get; private set; }
    IntegrationRequest? publication, advancement, settlement;
    readonly List<PropertyHandle<TSelection>> requirements = [];
    ExecutionDefinitionDocument? importedDefinition;
    ImmutableArray<ExecutionDefinitionDocument> importedDependencies = [];

    /// <summary>Exact acquisition boundary usable by scoped property declarations.</summary>
    /// <exception cref="InvalidOperationException">Acquisition has not been declared.</exception>
    public IntegrationRequest AcquisitionRequest => Acquisition ?? throw new InvalidOperationException("Declare acquisition first.");

    /// <summary>Exact publication boundary usable by scoped property declarations.</summary>
    /// <exception cref="InvalidOperationException">Publication has not been declared.</exception>
    public IntegrationRequest PublicationRequest => publication ?? throw new InvalidOperationException("Declare publication first.");

    internal static IngestionHandle<TSelection> Import(IntegrationAuthoringContext owner, string name,
        ExecutionDefinitionDocument definition, IReadOnlyCollection<ExecutionDefinitionDocument> dependencies)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dependencies);
        var validation = InteractionContractCatalog.TryCreate(dependencies.Where(d => d.Kind == InteractionContractDocuments.Kind), out var contracts);
        if (validation.IsValid) validation = IngestionDefinitionDocuments.TryLower(definition, contracts!, out _);
        if (!validation.IsValid) throw new ArgumentException(string.Join("\n", validation.Diagnostics.Select(d => d.Message)), nameof(dependencies));
        var source = definition.GetDefinition<IngestionDefinition>();
        var handle = new IngestionHandle<TSelection>(owner, name)
        {
            importedDefinition = definition,
            importedDependencies = [.. dependencies]
        };
        handle.Acquisition = Link("acquire", source.Acquire);
        handle.publication = Link("publish", source.Publish);
        handle.settlement = Link("settle", source.Settle);
        if (source.AdvanceLedger is { } advance) handle.advancement = Link("advance", advance);
        return handle;

        IntegrationRequest Link(string role, RequestContractReference reference)
        {
            contracts!.ValidateReference(reference.Definition, "/" + role, out var resolved);
            var result = ((RequestContractDefinition)resolved!).Response.TerminalOutcomes.OfType<RequestResultDefinition>().Single();
            owner.Reserve(name + "/" + role);
            var request = new ReferencedRequest(owner, name + "/" + role, result.Id.Value, reference);
            owner.Requests.Add(name + "/" + role, request);
            return request;
        }
    }

    // Retained protocols are references to existing authority, never reconstructed with new naming conventions.
    sealed class ReferencedRequest(IntegrationAuthoringContext owner, string name, string resultName, RequestContractReference reference)
        : IntegrationRequest(owner, name, resultName)
    {
        internal override (RequestContractReference Request, ImmutableArray<ExecutionDefinitionDocument> Documents) Lower() => (reference, []);
    }

    internal IngestionHandle(IntegrationAuthoringContext owner, string name) { Owner = owner; Name = name; }

    /// <summary>Declares acquisition.</summary>
    /// <typeparam name="TResult">Prepared publication payload.</typeparam>
    /// <param name="request">Acquisition Request in this model.</param>
    /// <returns>This declaration.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen or the referenced protocol is fixed.</exception>
    /// <exception cref="ArgumentException">The Request belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    public IngestionHandle<TSelection> Acquire<TResult>(RequestHandle<TSelection, TResult> request)
    { Check(request); Acquisition = request; return this; }

    /// <summary>Declares durable publication.</summary>
    /// <param name="request">Publication Request in this model.</param>
    /// <returns>This declaration.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen or the referenced protocol is fixed.</exception>
    /// <exception cref="ArgumentException">The Request belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    public IngestionHandle<TSelection> Publish(IntegrationRequest request)
    { Check(request); publication = request; return this; }

    /// <summary>Declares optional separate ledger advancement.</summary>
    /// <param name="request">Progress Request in this model.</param>
    /// <returns>This declaration.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen or the referenced protocol is fixed.</exception>
    /// <exception cref="ArgumentException">The Request belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    public IngestionHandle<TSelection> AdvanceProgress(IntegrationRequest request)
    { Check(request); advancement = request; return this; }

    /// <summary>Declares source settlement after publication and progress.</summary>
    /// <param name="request">Settlement Request in this model.</param>
    /// <returns>This declaration.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen or the referenced protocol is fixed.</exception>
    /// <exception cref="ArgumentException">The Request belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    public IngestionHandle<TSelection> Settle(IntegrationRequest request)
    { Check(request); settlement = request; return this; }

    /// <summary>Adds a required contract property.</summary>
    /// <param name="property">Scoped property in this model.</param>
    /// <returns>This declaration.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">The property belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Property is null.</exception>
    public IngestionHandle<TSelection> Requires(PropertyHandle<TSelection> property)
    { Owner.Mutable(); ArgumentNullException.ThrowIfNull(property); Owner.Owns(property.Owner); if (!requirements.Contains(property)) requirements.Add(property); return this; }

    /// <summary>Describes a provider of this acquisition operation.</summary>
    /// <param name="name">Stable provider identity within the model.</param>
    /// <param name="operation">Adapter-owned operation descriptor.</param>
    /// <param name="medium">Communication medium identity.</param>
    /// <returns>An immutable provider source, refinable with explicit contributions.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="InvalidOperationException">Acquisition has not been declared.</exception>
    public IntegrationProvider<TSelection> Provider(string name, string operation, string medium)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(medium);
        return new(this, Acquisition ?? throw new InvalidOperationException("Declare acquisition before describing its provider."), name, operation, medium, []);
    }

    /// <summary>Selects a versioned provider-operation profile independently of the configured account connection.</summary>
    /// <param name="profile">Immutable provider-operation source.</param>
    /// <returns>A provider handle whose connection must be selected explicitly.</returns>
    /// <exception cref="ArgumentNullException">Profile is null.</exception>
    /// <exception cref="ArgumentException">Profile is invalid.</exception>
    /// <exception cref="InvalidOperationException">Acquisition has not been declared.</exception>
    public IntegrationProvider<TSelection> Provider(ProviderOperationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var document = profile.Document;
        var source = document.GetDefinition<ProviderOperationProfileDefinition>();
        return new(this, AcquisitionRequest, document.Metadata.DefinitionId.Value, source.Operation, source.Medium, [], document);
    }

    /// <summary>Snapshots a concrete selection and freezes this model's declarations.</summary>
    /// <param name="selection">Structural selection copied into canonical storage.</param>
    /// <returns>An immutable binding source with independent account and consumer choices.</returns>
    /// <exception cref="ArgumentNullException">Selection is null.</exception>
    /// <exception cref="NotSupportedException">The selection cannot be represented as structural data.</exception>
    public IntegrationInstance<TSelection> For(TSelection selection)
    {
        var snapshot = ContractPropertyDocuments.DeclareContext(Owner.Identity("selection"), Owner.Revision, selection, Owner.Origin("selection"));
        // Reidentify canonical bytes without re-running application getters or requiring deserialization.
        var metadata = snapshot.Metadata;
        var scoped = new ExecutionDefinitionDocument(snapshot.Kind,
            new ExecutionDefinitionMetadata(Owner.Identity("selections/" + metadata.Fingerprint.Value),
                metadata.RevisionId, metadata.SchemaVersion, metadata.Fingerprint, metadata.Provenance,
                metadata.DisplayName, metadata.Description, metadata.SourceMap, metadata.Diagnostics),
            snapshot.Definition, snapshot.Extensions);
        Owner.Freeze();
        return new(this, scoped);
    }

    void Check(IntegrationRequest request)
    {
        Owner.Mutable();
        if (importedDefinition is not null)
            throw new InvalidOperationException("A referenced ingestion protocol is immutable; author a new protocol for semantic changes.");
        ArgumentNullException.ThrowIfNull(request);
        Owner.Owns(request.Owner);
    }

    internal IntegrationCompilation Compile(ExecutionDefinitionDocument selection, IntegrationProvider<TSelection>? provider,
        string? account, IngestionLedgerAddress? consumer, ConnectionReference? logical, ConnectionBindings? connections, int? batchSize)
    {
        try
        {
            if (Acquisition is null || publication is null || settlement is null || provider is null || consumer is null)
                throw new InvalidOperationException("Declare acquisition, publication and settlement, then select a provider, account and consumer.");
            if (!ReferenceEquals(provider.Acquisition, Acquisition)) throw new InvalidOperationException("Provider acquisition must match the ingestion Request.");
            List<ExecutionDefinitionDocument> documents = [.. importedDependencies];
            ExecutionDefinitionDocument? resolution = null;
            if (provider.Profile is { } profile)
            {
                if (account is not null)
                    return new([], null, null, IntegrationConnectionDocuments.Error("account", "A connected binding derives its resource from the connection; do not also supply an account.", "/resource"));
                if (logical is null || connections is null)
                    return new([], null, null, IntegrationConnectionDocuments.Error("unbound", "Select a logical connection and supply explicit connection bindings.", "/connection"));
                if (!connections.TryResolve(logical, out var connection))
                    return new([], null, null, IntegrationConnectionDocuments.Error("unbound", "The logical connection reference is unbound.", "/connection"));
                var configured = connection!.Document;
                var configuration = connections.ConfiguredLogical == logical ? connections.Configuration : null;
                if (configuration is not null)
                {
                    if (batchSize is not null && batchSize != connections.ConfiguredBatch)
                        return new([], null, null, IntegrationConnectionDocuments.Error("policy", "Express local batch overrides through scoped configuration before admission.", "/batchSize"));
                    batchSize = connections.ConfiguredBatch;
                    documents.AddRange(connections.ConfigurationDocuments);
                    documents.Add(configuration);
                }
                var matching = IntegrationConnectionDocuments.Match(profile, configured, batchSize, out var effective);
                if (!matching.IsValid) return new([], null, null, matching);
                account = configured.GetDefinition<IntegrationConnectionDefinition>().Resource;
                resolution = IntegrationConnectionDocuments.DeclareResolution(
                    Owner.Identity("selections/" + selection.Metadata.Fingerprint.Value + "/connections/" + Uri.EscapeDataString(logical.Name)),
                    Owner.Revision, new(logical, profile.Reference, configured.Reference, batchSize, effective, configuration?.Reference));
                documents.Add(profile); documents.Add(configured); documents.Add(resolution);
            }
            else if (logical is not null || batchSize is not null || string.IsNullOrWhiteSpace(account))
                return new([], null, null, IntegrationConnectionDocuments.Error("profile", "Declare a provider-operation profile for connection selection; legacy binding requires an account.", "/provider"));
            Dictionary<IntegrationRequest, RequestContractReference> requests = [];
            foreach (var source in Owner.Requests.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (source.Value is null) throw new InvalidOperationException($"Request '{source.Key}' needs a result.");
                var lowered = source.Value.Lower();
                requests.Add(source.Value, lowered.Request); documents.AddRange(lowered.Documents);
            }
            var ingestion = importedDefinition ?? IngestionDefinitionDocuments.Create(Owner.Identity("ingestions/" + Name), Owner.Revision,
                new(requests[Acquisition], requests[publication], requests[settlement], advancement is null ? null : requests[advancement]), Owner.Origin(Name));
            if (!documents.Any(d => d.Reference == ingestion.Reference)) documents.Add(ingestion);
            documents.Add(selection);
            Dictionary<PropertyHandle<TSelection>, ExecutionDefinitionReference> properties = [];
            foreach (var property in requirements.Concat(provider.Properties).Distinct())
                properties.Add(property, property.Lower(selection, requests, documents).Reference);
            var offer = IngestionProviderBindingDocuments.DeclareProvider(Owner.Identity("selections/" + selection.Metadata.Fingerprint.Value + "/providers/" + provider.Name), Owner.Revision,
                new(requests[Acquisition], provider.Operation, provider.Medium, [.. provider.Properties.Select(p => new ContractProperty(ContractRole.Provides, properties[p]))], provider.Profile?.Reference), Owner.Origin("providers/" + provider.Name));
            documents.Add(offer);
            var contract = new IngestionContract(ingestion.Reference, [.. requirements.Select(p => new ContractProperty(ContractRole.Requires, properties[p]))]);
            var binding = IngestionProviderBindingDocuments.DeclareBinding(Owner.Identity("selections/" + selection.Metadata.Fingerprint.Value + "/bindings/" + Uri.EscapeDataString(consumer.Flow)),
                Owner.Revision, contract.Bind(offer.Reference, account!, consumer, resolution?.Reference), Owner.Origin(Name + "/binding"));
            documents.Add(binding);
            var validation = InteractionContractCatalog.TryCreate(documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var contracts);
            if (!validation.IsValid) return new([.. documents], null, null, validation);
            var lowering = IngestionDefinitionDocuments.TryLower(ingestion, contracts!, out var process);
            if (!lowering.IsValid) return new([.. documents], null, null, lowering);
            if (!documents.Any(d => d.Reference == process!.Reference)) documents.Add(process!);
            // Validate the complete closure, including possible conflicts with an imported Process revision.
            validation = ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var definitions);
            if (!validation.IsValid) return new([.. documents], null, null, validation);
            validation = IngestionProviderBindingDocuments.Inspect(binding, definitions!, contracts!, out _);
            if (!validation.IsValid) return new([.. documents], null, null, validation);
            return new([.. documents], binding, process, validation);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or JsonException or FormatException)
        { return new([], null, null, IntegrationAuthoringContext.Error(error.Message)); }
    }
}

/// <summary>Immutable provider source. Contributions describe claims requiring runtime qualification.</summary>
/// <typeparam name="TSelection">Acquisition selection payload.</typeparam>
public sealed class IntegrationProvider<TSelection> where TSelection : class
{
    internal IngestionHandle<TSelection> Ingestion { get; }
    internal IntegrationRequest Acquisition { get; }
    internal string Name { get; }
    internal string Operation { get; }
    internal string Medium { get; }
    internal ImmutableArray<PropertyHandle<TSelection>> Properties { get; }
    internal ExecutionDefinitionDocument? Profile { get; }
    internal IntegrationProvider(IngestionHandle<TSelection> ingestion, IntegrationRequest acquisition, string name, string operation,
        string medium, ImmutableArray<PropertyHandle<TSelection>> properties, ExecutionDefinitionDocument? profile = null)
    { Ingestion = ingestion; Acquisition = acquisition; Name = name; Operation = operation; Medium = medium; Properties = properties; Profile = profile; }

    /// <summary>Adds an explicit offered property without changing the original provider source.</summary>
    /// <param name="property">Property declared in the same model.</param>
    /// <returns>A provider source containing the contribution.</returns>
    /// <exception cref="ArgumentException">The property belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Property is null.</exception>
    public IntegrationProvider<TSelection> Provides(PropertyHandle<TSelection> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Ingestion.Owner.Owns(property.Owner);
        return new(Ingestion, Acquisition, Name, Operation, Medium, Properties.Contains(property) ? Properties : Properties.Add(property), Profile);
    }
}

/// <summary>Immutable selection and binding source; compilation derives the complete document closure.</summary>
/// <typeparam name="TSelection">Acquisition selection payload.</typeparam>
public sealed record IntegrationInstance<TSelection> where TSelection : class
{
    readonly IngestionHandle<TSelection> ingestion;
    readonly ExecutionDefinitionDocument selection;
    IntegrationProvider<TSelection>? provider;
    string? account;
    ConnectionReference? logical;
    int? batchSize;
    IngestionLedgerAddress? consumer;
    internal IntegrationInstance(IngestionHandle<TSelection> ingestion, ExecutionDefinitionDocument selection)
    { this.ingestion = ingestion; this.selection = selection; }

    /// <summary>Selects a provider explicitly.</summary>
    /// <param name="value">Provider in the same model.</param>
    /// <returns>A new binding source.</returns>
    /// <exception cref="ArgumentException">The provider belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Value is null.</exception>
    public IntegrationInstance<TSelection> Use(IntegrationProvider<TSelection> value)
    { ArgumentNullException.ThrowIfNull(value); ingestion.Owner.Owns(value.Ingestion.Owner); return this with { provider = value }; }

    /// <summary>Selects the external account or resource.</summary>
    /// <param name="value">Account identity; credentials are resolved separately.</param>
    /// <returns>A new binding source.</returns>
    public IntegrationInstance<TSelection> WithAccount(string value) => this with { account = value };

    /// <summary>Selects a logical connection independently of environment or account configuration.</summary>
    /// <param name="reference">Logical application reference.</param>
    /// <returns>A new binding source.</returns>
    /// <exception cref="ArgumentNullException">Reference is null.</exception>
    public IntegrationInstance<TSelection> Through(ConnectionReference reference)
    { ArgumentNullException.ThrowIfNull(reference); return this with { logical = reference }; }

    /// <summary>Explicitly overrides the profile batch default, subject to every hard limit.</summary>
    /// <param name="size">Requested items per operation.</param>
    /// <returns>A new source; eligibility is checked during compilation.</returns>
    public IntegrationInstance<TSelection> WithBatchSize(int size) => this with { batchSize = size };

    /// <summary>Selects independently tracked consumer progress.</summary>
    /// <param name="value">Flow, source, destination and partition identity.</param>
    /// <returns>A new binding source.</returns>
    public IntegrationInstance<TSelection> ForConsumer(IngestionLedgerAddress value) => this with { consumer = value };

    /// <summary>Deterministically lowers source and runs the existing structural validators.</summary>
    /// <returns>Canonical documents and diagnostics. Success does not prove domain predicates or qualify physical guarantees.</returns>
    public IntegrationCompilation Compile() => ingestion.Compile(selection, provider, account, consumer, logical, null, batchSize);

    /// <summary>Resolves the selected logical connection explicitly and pins its exact configuration.</summary>
    /// <param name="connections">Immutable resolution scope.</param>
    /// <returns>Canonical closure and structured eligibility diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Connections is null.</exception>
    public IntegrationCompilation Compile(ConnectionBindings connections)
    { ArgumentNullException.ThrowIfNull(connections); return ingestion.Compile(selection, provider, account, consumer, logical, connections, batchSize); }
}
