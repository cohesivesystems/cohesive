using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Api;

public sealed class ServiceLookupBindingTests
{
    public sealed record Item(string Id, string Partition);
    static readonly DomainEntity<Item> Entity = new DomainModelBuilder().Entity<Item>("item");
    static HostedQuery<string, string> Query(HostedQueryEvaluationSemantics semantics = HostedQueryEvaluationSemantics.Observation) =>
        HostedQuery<string, string>.Create(new("lookup"), new("1"), new("test/lookup", "1"), "item", RunControlFixture.Provenance,
            evaluationSemantics: semantics);
    static Process<string, string> Process(HostedQuery<string, string> query) => ProcessAuthoring.Project<string, string>(
        ProcessDefinitionDocuments.Create(new("lookup-process"), new("1"), new(query.InputContract, query.ResultContract, new("read"), [
            new EvaluateRelationProcessNode(new("read"), query.Reference, Expr.BoundValue(ProcessBindingIds.Input), new(new(new("next"), new("return")))),
            new ReturnProcessNode(new("return"), Expr.Const("done"))], ProcessRecoveryPolicy.ContinueAttempt), RunControlFixture.Provenance));
    static ServiceBuilder Declaration(Process<string, string> process, ServiceDependencies dependencies) =>
        Service.Define(new("lookup-service"), new("1"), RunControlFixture.Provenance).Use(dependencies)
            .Operation("read").Run(process).ExecuteEphemerally(TimeSpan.FromSeconds(1));

    [Fact]
    public void Entity_lookup_cannot_claim_deterministic_computation()
    {
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceDependencies().Lookup(
            Query(HostedQueryEvaluationSemantics.DeterministicComputation), Entity, input => input, item => item?.Id ?? "missing"));
        Assert.Equal(ServiceBindingDiagnosticCodes.LookupSemanticsInvalid, Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Missing_hosted_query_registration_fails_before_io()
    {
        var query = Query(); var process = Process(query);
        var error = Assert.Throws<ServiceBindingValidationException>(() => Declaration(process, new()).Bind(process,
            _ => throw new InvalidOperationException("No IO at setup")));
        Assert.Equal(ServiceBindingDiagnosticCodes.QueryMissing, Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Lookup_requires_explicit_fixed_partition_and_does_not_guess_from_entity_fields()
    {
        var query = Query(); var process = Process(query);
        var dependencies = new ServiceDependencies().Lookup(query, Entity, input => input, item => item?.Id ?? "missing");
        var repository = new InMemoryEntityOutboxRepository(Entity.Definition, EntityPartitionKeyPolicy.FromField(nameof(Item.Partition)));
        var error = Assert.Throws<ServiceBindingValidationException>(() => Declaration(process, dependencies).Bind(process, repository));
        Assert.Equal(ServiceBindingDiagnosticCodes.LookupPartitionMissing, Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Explicit_repository_resolver_cannot_substitute_another_entity()
    {
        var query = Query(); var process = Process(query);
        var dependencies = new ServiceDependencies().Lookup(query, Entity, input => input, item => item?.Id ?? "missing");
        var wrong = new InMemoryEntityOutboxRepository(RunControlFixture.Entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var error = Assert.Throws<ServiceBindingValidationException>(() => Declaration(process, dependencies).Bind(process, _ => wrong));
        Assert.Equal(ServiceBindingDiagnosticCodes.ObservationMismatch, Assert.Single(error.Validation.Diagnostics).Code);
    }
}
