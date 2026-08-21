using Cohesive.Infra;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureDefinitionTests
{
    [Fact]
    public void Fluent_and_direct_authoring_produce_identical_normalized_ir_and_fingerprints()
    {
        InfrastructureDefinitionId definitionId = new("ari-platform");
        InfrastructureRevisionId revisionId = new("2026-08-20");
        InfrastructureNodeId api = new("api");
        InfrastructureNodeId worker = new("worker");
        InfrastructureNodeId inbox = new("domain-event-inbox");
        InfrastructureNodeId scheduler = new("durable-scheduler");
        InfrastructureCapabilityId receivesEvents = new("receives-domain-events");
        InfrastructureCapabilityId durableScheduling = new("durable-process-scheduling");
        InfrastructureCapabilityId durableStorage = new("durable-storage");
        InfrastructureRequirementId apiInboxRequirement = new("requirements/api/inbox");
        InfrastructureRequirementId workerInboxRequirement = new("requirements/worker/inbox");
        InfrastructureRequirementId workerSchedulerRequirement = new("requirements/worker/scheduler");
        InfrastructureRequirementId inboxStorageRequirement = new("requirements/inbox/durable-storage");
        InfrastructureRequirementId schedulerStorageRequirement = new("requirements/scheduler/durable-storage");
        InfrastructureBindingContractId eventPublication = new("domain-event-publication");
        InfrastructureBindingContractId processClient = new("durable-process-client");
        InfrastructureBindingId apiInboxBinding = new("bindings/api/inbox");
        InfrastructureBindingId workerInboxBinding = new("bindings/worker/inbox");
        InfrastructureBindingId workerSchedulerBinding = new("bindings/worker/scheduler");

        var directDefinition = new InfrastructureDefinition(
            definitionId,
            revisionId,
            workloads:
            [
                new(worker,
                [
                    new(workerSchedulerRequirement, durableScheduling),
                    new(workerInboxRequirement, receivesEvents)
                ]),
                new(api, [new(apiInboxRequirement, receivesEvents)])
            ],
            resources:
            [
                new(
                    scheduler,
                    InfrastructureResourceLifecycle.Persistent,
                    [new(schedulerStorageRequirement, durableStorage)]),
                new(
                    inbox,
                    InfrastructureResourceLifecycle.Persistent,
                    [new(inboxStorageRequirement, durableStorage)])
            ],
            bindings:
            [
                new(workerSchedulerBinding, worker, scheduler, processClient),
                new(workerInboxBinding, worker, inbox, eventPublication),
                new(apiInboxBinding, api, inbox, eventPublication)
            ]);
        var directDocument = InfrastructureDefinitionDocument.FromDefinition(directDefinition);

        var fluentDocument = Infrastructure.Define(definitionId, revisionId, infrastructure =>
        {
            var schedulerResource = infrastructure.Resource(scheduler)
                .Persistent()
                .Requires(schedulerStorageRequirement, durableStorage);
            var inboxResource = infrastructure.Resource(inbox)
                .Persistent()
                .Requires(inboxStorageRequirement, durableStorage);
            var apiWorkload = infrastructure.Workload(api)
                .Requires(apiInboxRequirement, receivesEvents);
            var workerWorkload = infrastructure.Workload(worker)
                .Requires(workerInboxRequirement, receivesEvents)
                .Requires(workerSchedulerRequirement, durableScheduling);

            infrastructure.Bind(apiInboxBinding, apiWorkload).To(inboxResource).As(eventPublication);
            infrastructure.Bind(workerInboxBinding, workerWorkload).To(inboxResource).As(eventPublication);
            infrastructure.Bind(workerSchedulerBinding, workerWorkload).To(schedulerResource).As(processClient);
        });

        Assert.Equal(directDefinition, fluentDocument.Definition);
        Assert.Equal(directDocument.Fingerprint, fluentDocument.Fingerprint);
        Assert.Equal(["api", "worker"], fluentDocument.Definition.Workloads.Select(static value => value.Id.Value));
        Assert.Equal(
            ["domain-event-inbox", "durable-scheduler"],
            fluentDocument.Definition.Resources.Select(static value => value.Id.Value));
        Assert.Equal(
            ["bindings/api/inbox", "bindings/worker/inbox", "bindings/worker/scheduler"],
            fluentDocument.Definition.Bindings.Select(static value => value.Id.Value));
        Assert.Equal(
            ["requirements/worker/inbox", "requirements/worker/scheduler"],
            fluentDocument.Definition.Workloads[1].Requirements.Select(static value => value.Id.Value));
    }

    [Fact]
    public void Fingerprint_fences_definition_identity_and_exact_revision()
    {
        var first = Document("system", "v1");
        var renamed = Document("renamed-system", "v1");
        var revised = Document("system", "v2");

        Assert.NotEqual(first.Fingerprint, renamed.Fingerprint);
        Assert.NotEqual(first.Fingerprint, revised.Fingerprint);

        static InfrastructureDefinitionDocument Document(string id, string revision) =>
            Infrastructure.Define(new(id), new(revision), infrastructure =>
                infrastructure.Workload(new("api")));
    }

    [Fact]
    public void Typed_binding_overloads_reject_builders_owned_by_another_definition()
    {
        InfrastructureWorkloadBuilder? foreignApi = null;
        _ = Infrastructure.Define(new("foreign"), new("v1"), infrastructure =>
            foreignApi = infrastructure.Workload(new("api")));
        ArgumentException? rejection = null;

        _ = Infrastructure.Define(new("local"), new("v1"), infrastructure =>
        {
            _ = infrastructure.Workload(new("api"));
            _ = infrastructure.Resource(new("store")).Persistent();
            rejection = Assert.Throws<ArgumentException>(() =>
                infrastructure.Bind(new("bindings/api/store"), foreignApi!));
        });

        Assert.Equal("source", Assert.IsType<ArgumentException>(rejection).ParamName);
    }

    [Fact]
    public void Definition_rejects_a_node_identity_reused_across_node_kinds()
    {
        InfrastructureNodeId duplicatedNode = new("api");

        Assert.Throws<ArgumentException>(() => new InfrastructureDefinition(
            new("duplicated-node"),
            new("v1"),
            workloads: [new(duplicatedNode)],
            resources: [new(duplicatedNode, InfrastructureResourceLifecycle.Persistent)]));
    }

    [Fact]
    public void Definition_rejects_a_requirement_identity_reused_across_nodes()
    {
        InfrastructureRequirementId duplicatedRequirement = new("requirements/shared");

        Assert.Throws<ArgumentException>(() => new InfrastructureDefinition(
            new("duplicated-requirement"),
            new("v1"),
            workloads:
            [
                new(
                    new("api"),
                    [new(duplicatedRequirement, new InfrastructureCapabilityId("request-ingress"))]),
                new(
                    new("worker"),
                    [new(duplicatedRequirement, new InfrastructureCapabilityId("background-execution"))])
            ]));
    }

    [Fact]
    public void Definition_rejects_duplicate_binding_identities()
    {
        InfrastructureBindingId duplicatedBinding = new("bindings/api/store");
        InfrastructureNodeId api = new("api");
        InfrastructureNodeId store = new("store");

        Assert.Throws<ArgumentException>(() => new InfrastructureDefinition(
            new("duplicated-binding-id"),
            new("v1"),
            workloads: [new(api)],
            resources: [new(store, InfrastructureResourceLifecycle.Persistent)],
            bindings:
            [
                new(duplicatedBinding, api, store, new("read")),
                new(duplicatedBinding, api, store, new("write"))
            ]));
    }

    [Fact]
    public void Definition_rejects_duplicate_semantic_binding_slots()
    {
        InfrastructureNodeId api = new("api");
        InfrastructureNodeId store = new("store");
        InfrastructureBindingContractId contract = new("document-client");

        Assert.Throws<ArgumentException>(() => new InfrastructureDefinition(
            new("duplicated-binding-slot"),
            new("v1"),
            workloads: [new(api)],
            resources: [new(store, InfrastructureResourceLifecycle.Persistent)],
            bindings:
            [
                new(new("bindings/api/store/one"), api, store, contract),
                new(new("bindings/api/store/two"), api, store, contract)
            ]));
    }
}
