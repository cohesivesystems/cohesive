using Cohesive.Api;
using Cohesive.Model.Authoring;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Identity;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Api;

public sealed class ServiceProcessBindingAuthoringTests
{
    public sealed record Input(string Token);

    [Fact]
    public void Hosting_returns_independent_binding_phases_without_mutating_the_operation()
    {
        var process = ProcessAuthoring.Project<StartRun, string>(RunControlFixture.ProcessDocument);
        var operation = Service.Define(new("service"), new("1"), RunControlFixture.Provenance).Operation("run");
        ServiceProcessHostBuilder<StartRun, string> first = operation.Run(process, _ => { });
        ServiceProcessHostBuilder<StartRun, string> second = operation.Run(process, _ => { });
        Assert.NotSame(first, second);
        // Each returned phase exposes association and Build, not Run. Reusing the immutable source
        // for an ordinary declaration must remain legal as it is for all other authoring branches.
        Assert.NotNull(operation.Run(process));
    }

    [Fact]
    public void Binding_exception_requires_error_evidence_and_selects_the_first_error_code()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessTransitionBindingException(null!));
        Assert.Throws<ArgumentException>(() => new ProcessTransitionBindingException(DocumentValidationResult.Valid));
        var warning = new DocumentValidationDiagnostic("warning", DiagnosticSeverity.Warning, "warning");
        Assert.Throws<ArgumentException>(() => new ProcessTransitionBindingException(new([warning])));
        var validation = new DocumentValidationResult([warning, new("error", DiagnosticSeverity.Error, "failed", "/binding")]);
        var exception = new ProcessTransitionBindingException(validation);
        Assert.Equal("error", exception.Code);
        Assert.Same(validation, exception.Validation);
    }

    [Fact]
    public void Observation_mismatch_retains_service_code_and_location()
    {
        var process = ProcessAuthoring.Project<StartRun, string>(RunControlFixture.ProcessDocument);
        var wrong = new InMemoryEntityOutboxRepository(ObjectEntityDefinition.For<Other>(new("other")),
            EntityPartitionKeyPolicy.FromField(nameof(Other.Tenant)));
        var binding = new ProcessTransitionOperationBinding(RunControlFixture.Start.Compile().Plan!, wrong, RunControlFixture.Contracts());
        var native = Assert.Throws<ProcessTransitionBindingException>(() => binding.CreateProcessDefinitionLink());
        Assert.Equal("storage.processes.binding.observationMismatch", Assert.Single(native.Validation.Diagnostics).Code);
        var failure = Assert.Throws<ServiceBindingValidationException>(() => Service.Define(new("service"), new("1"), RunControlFixture.Provenance)
            .Operation("run").Run(process, bindings => bindings.Transition(binding)));
        var diagnostic = Assert.Single(failure.Validation.Diagnostics);
        Assert.Equal("services.binding.observationMismatch", diagnostic.Code);
        Assert.Equal("/binding/entity", diagnostic.Location);
        var direct = Assert.Throws<ServiceBindingValidationException>(() => new ServiceTransitionBinding("run",
            binding.Plan, wrong.EntityDefinition, _ => wrong));
        Assert.Equal(Assert.Single(direct.Validation.Diagnostics), diagnostic);
    }

    public static TheoryData<string, string> BindingCodeMappings => new()
    {
        { ProcessTransitionBindingDiagnosticCodes.ObservationMismatch, ServiceBindingDiagnosticCodes.ObservationMismatch },
        { ProcessTransitionBindingDiagnosticCodes.ReceiptCapabilityMissing, ServiceBindingDiagnosticCodes.ReceiptCapabilityMissing }
    };

    [Fact]
    public void Every_native_binding_code_has_an_explicit_mapping_test()
    {
        var catalog = typeof(ProcessTransitionBindingDiagnosticCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!).OrderBy(code => code).ToArray();
        Assert.Equal(catalog, BindingCodeMappings.Select(row => (string)row[0]).OrderBy(code => code).ToArray());
    }

    [Theory]
    [MemberData(nameof(BindingCodeMappings))]
    public void Native_admission_failures_project_the_declared_service_code(string nativeCode, string serviceCode)
    {
        var process = ProcessAuthoring.Project<StartRun, string>(RunControlFixture.ProcessDocument);
        IEntityRepository repository = nativeCode == ProcessTransitionBindingDiagnosticCodes.ReceiptCapabilityMissing
            ? new NonAtomicRepository()
            : new InMemoryEntityOutboxRepository(ObjectEntityDefinition.For<Other>(new("other")), EntityPartitionKeyPolicy.FromField(nameof(Other.Tenant)));
        var binding = new ProcessTransitionOperationBinding(RunControlFixture.Start.Compile().Plan!, repository, RunControlFixture.Contracts());
        var native = Assert.Throws<ProcessTransitionBindingException>(() => binding.CreateProcessDefinitionLink());
        Assert.Equal(nativeCode, Assert.Single(native.Validation.Diagnostics).Code);
        var projected = Assert.Throws<ServiceBindingValidationException>(() => Service.Define(new("service"), new("1"), RunControlFixture.Provenance)
            .Operation("run").Run(process, bindings => bindings.Transition(binding)));
        Assert.Equal(serviceCode, Assert.Single(projected.Validation.Diagnostics).Code);
        Assert.Equal(native.Validation.Diagnostics[0].Location, projected.Validation.Diagnostics[0].Location);
    }

    sealed class NonAtomicRepository : IEntityRepository
    {
        public Cohesive.Transitions.Model.EntityDefinition EntityDefinition => RunControlFixture.Entity;
        public string? IdentityField => nameof(RunControl.Id);
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => throw new InvalidOperationException("No setup IO");
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => throw new InvalidOperationException("No setup IO");
    }

    public sealed record Other(string Id, string Tenant);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Service_definition_hosts_token_fenced_transitions_in_both_binding_forms(bool stale, bool prepared)
    {
        var entity = RunControlFixture.Entity;
        var native = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var repository = new TypedEntityRepository<RunControl>(native);
        var actor = new PrincipalRef("tester", PrincipalKind.User);
        var scope = new ScopeRef("tenant/a", "tenant", PartitionKey: "tenant/a");
        var context = OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient), Grants: [new(actor, scope, ["run"], "tests")]));
        var original = await native.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var transition = TransitionAuthoring.Create<RunControl, Input, string>(entity.Shape,
            id: new("token-fence"), revision: new("1"), body => body.Set(new("status"), item => item.Status, "processed").Return("done"));
        var document = ProcessDefinitionDocuments.Create(new("token-process"), new("1"),
            new(transition.Definition.Input, transition.Definition.Outcome, new("invoke"), [
                new InvokeTransitionProcessNode(new("invoke"), transition.Reference, Expr.Const("run/1"), Expr.BoundValue(ProcessBindingIds.Input),
                    new(new(new("next"), new("return")))),
                new ReturnProcessNode(new("return"), Expr.Const("done"))], ProcessRecoveryPolicy.ContinueAttempt), RunControlFixture.Provenance);
        var process = ProcessAuthoring.Project<Input, string>(document);
        var hosted = Service.Define(new("service"), new("1"), RunControlFixture.Provenance).Operation("run").Require(new("run"))
            .Run(process, bindings =>
            {
                if (prepared)
                    bindings.Transition(new ProcessTransitionOperationBinding(transition.Compile().Plan!, repository,
                        RunControlFixture.Contracts(), expectedConcurrencyTokenField: nameof(Input.Token), partitionKey: "tenant/a"));
                else bindings.Transition(transition, repository, expectedConcurrencyTokenField: nameof(Input.Token));
            }).Build("tests", TimeSpan.FromSeconds(2), new IdentityServiceInvocationAuthorization("tenant", new(nameof(RunControl.Tenant))));
        if (stale) await native.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "newer" }));
        var before = await native.TryGet(context, "run/1");
        var result = await hosted.Runtime.ExecuteProcessAsync(context, "run", new(new("run"), new("attempt")),
            ObservationValue.FromObject(new Input(original.ConcurrencyToken.Value)));
        Assert.Equal(stale ? ApiResultKind.DomainError : ApiResultKind.Success, result.Kind);
        var after = await native.TryGet(context, "run/1");
        if (stale) Assert.Equal(before, after);
        else Assert.Equal("processed", after!.Entity.Observation.GetField(nameof(RunControl.Status)).GetRequiredString());
    }
}
