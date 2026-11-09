using System.Text.Json.Serialization;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Xunit;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    [Fact]
    public void RepeatedAndConcurrentCompilationHasIdenticalCanonicalClosure()
    {
        var instance = Bound(new TestModel(), Selection());
        var expected = instance.Compile();
        Valid(expected);
        var independent = Bound(new TestModel(), Selection()).Compile();
        Valid(independent);
        Assert.Equal(Json(expected), Json(independent));
        Parallel.For(0, 8, _ => Assert.Equal(Json(expected), Json(instance.Compile())));
        foreach (var document in expected.Documents)
        {
            Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(document), out var reopened).IsValid);
            Assert.Equal(document.Reference, reopened!.Reference);
        }
    }

    [Fact]
    public void SelectionAndPredicateAreSnapshotsAndDeclarationFreezes()
    {
        var model = new TestModel();
        var selection = Selection();
        var instance = Bound(model, selection);
        var before = instance.Compile();
        Valid(before);
        selection.Identity.Symbol = "changed";
        model.PredicateSource.Required = false;
        Assert.Equal(Json(before), Json(instance.Compile()));
        Assert.Throws<InvalidOperationException>(() => model.Coverage.TimeBasis("changed"));
        Assert.Throws<InvalidOperationException>(() => model.Collect.Requires(model.Coverage));
        Assert.Throws<InvalidOperationException>(() => model.Acquisition.Failure<string>("later"));
    }

    [Fact]
    public void NestedJsonNamedSelectorsResolveAndScopeIsPinned()
    {
        var compiled = Bound(new TestModel(), Selection()).Compile();
        Valid(compiled);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents, out var catalog).IsValid);
        Assert.True(InteractionContractCatalog.TryCreate(compiled.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var requests).IsValid);
        Assert.True(IngestionProviderBindingDocuments.Inspect(compiled.Binding!, catalog!, requests!, out var binding).IsValid);
        var requirement = Assert.Single(binding!.Properties);
        Assert.Equal("SPY", Assert.Single(requirement.Definition.Scope.Subjects));
        Assert.Equal(requirement.Use.Property, Assert.Single(binding.ProviderProperties).Use.Property);
        Assert.True(catalog!.TryResolve(requirement.Definition.Parameters, out var parameters));
        Assert.Equal("SPY", parameters!.GetDefinition<SelectionSource>().Identity.Symbol);
        Assert.Contains("/requests/publish/", requirement.Definition.Scope.Boundary);
        Assert.Contains(compiled.Validation.Diagnostics, d => d.Code == "integrations.ingestion.realization.unqualified");
    }

    [Fact]
    public void AuthoringMatchesTheNativeRequestAndPropertySurfaces()
    {
        var compiled = Bound(new TestModel(), Selection()).Compile();
        Valid(compiled);
        var request = compiled.Documents.Single(d => d.Metadata.DefinitionId.Value == "test/integration/requests/acquire");
        var policy = new RequestProtocolResponsePolicy(RequestOptionalTerminalSemantics.Unsupported,
            RequestOptionalTerminalSemantics.Unsupported, RequestResultDisposition.Observe, RequestResultDisposition.Reject,
            RequestResultDisposition.ReusePriorDisposition, RequestRetrySemantics.StableIdentity,
            RequestResolutionSemantics.Reconcile, RequestResolutionSemantics.Escalate, TimeSpan.FromDays(30));
        var native = InteractionContractAuthoring.CreateRequestProtocol<SelectionSource, RequestProtocolOutcome<string>>(
            request.Metadata.DefinitionId, request.Metadata.RevisionId,
            new("test/integration/payload/selection/v1"),
            builder => builder.Result<string>(new("ready"), new("test/integration/payload/text/v1")), policy,
            new(new("cohesive.integrations.authoring", "1"), new("integration-model:test/integration/requests/acquire"), DocumentOrigin.Generated));
        Assert.True(native.IsValid);
        Assert.Equal(native.Request.Definition, request.Reference);
        foreach (var property in compiled.Documents.Where(d => d.Metadata.DefinitionId.Value.EndsWith("/properties/coverage", StringComparison.Ordinal)))
        {
            var definition = property.GetDefinition<ContractPropertyDefinition>();
            var nativeProperty = ContractPropertyDocuments.Declare(property.Metadata.DefinitionId, property.Metadata.RevisionId, definition,
                new(new("cohesive.integrations.authoring", "1"), new("integration-model:test/integration/properties/coverage"), DocumentOrigin.Generated));
            Assert.Equal(property.Reference, nativeProperty.Reference);
        }
    }

    [Fact]
    public void CrossModelHandlesAndExecutableSelectorsAreRejected()
    {
        var model = new TestModel();
        var other = new TestModel();
        Assert.Throws<ArgumentException>(() => model.Collect.Requires(other.Coverage));
        Assert.Throws<ArgumentException>(() => model.Collect.Publish(other.Acquisition));
        Assert.Throws<ArgumentException>(() => model.Coverage.Subject(s => s.Identity.Symbol.ToLowerInvariant()));
        var captured = "external";
        Assert.Throws<ArgumentException>(() => model.Coverage.Subject(s => captured));
        var instance = model.Collect.For(Selection());
        Assert.Throws<ArgumentException>(() => instance.Use(other.Collect.Provider("p", "get", "HTTPS")));
    }

    [Fact]
    public void MissingBindingAndInvalidIntervalReturnDiagnostics()
    {
        var model = new TestModel();
        var missing = model.Collect.For(Selection()).Compile();
        Assert.False(missing.IsSuccessful);
        Assert.Contains(missing.Validation.Diagnostics, d => d.Code == "integrations.authoring.invalid");
        var selection = Selection();
        selection.End = selection.Start.AddDays(-1);
        var invalid = Bound(new TestModel(), selection).Compile();
        Assert.False(invalid.IsSuccessful);
        Assert.Contains(invalid.Validation.Diagnostics, d => d.Message.Contains("reversed interval"));
    }

    [Fact]
    public void ExistingCompilerRejectsIncompatibleStepSchemas()
    {
        var compiled = Bound(new TestModel(incompatiblePublication: true), Selection()).Compile();
        Assert.False(compiled.IsSuccessful);
        Assert.Contains(compiled.Validation.Diagnostics, d => d.Code == "integrations.ingestion.payload");
    }

    [Fact]
    public void MissingRecoveryIsExplicitAndConsumerChoicesAreIndependent()
    {
        var missing = Bound(new TestModel(omitRecovery: true), Selection()).Compile();
        Assert.False(missing.IsSuccessful);
        Assert.Contains(missing.Validation.Diagnostics, d => d.Message.Contains("recovery policy"));
        var instance = Bound(new TestModel(), Selection());
        var research = instance.Compile();
        var risk = instance.ForConsumer(new("risk", "source", "risk-store", "SPY")).Compile();
        Valid(research); Valid(risk);
        Assert.NotEqual(research.Binding!.Reference, risk.Binding!.Reference);
        Assert.Equal(research.Process!.Reference, risk.Process!.Reference);
        Assert.Equal(research.Binding.Reference, instance.Compile().Binding!.Reference);
    }

    [Fact]
    public void ExistingProtocolRetainsExactRequestsRepliesAndProcess()
    {
        var original = Bound(new TestModel(), Selection()).Compile();
        Valid(original);
        var definition = original.Documents.Single(d => d.Kind == IngestionDefinitionDocuments.Kind);
        var dependencies = original.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind
            || d.Reference == original.Process!.Reference || d.Reference == definition.Reference).ToArray();
        var model = new ImportedModel(definition, dependencies);
        Assert.Throws<InvalidOperationException>(() => model.Collect.Publish(model.Collect.AcquisitionRequest));
        var instance = model.Collect.For(Selection()).Use(model.Collect.Provider("retained", "get", "HTTPS"))
            .WithAccount("account").ForConsumer(new("flow", "source", "sink", "SPY"));
        var compiled = instance.Compile();
        Valid(compiled);
        Assert.Equal(original.Process!.Reference, compiled.Process!.Reference);
        foreach (var document in dependencies)
            Assert.Equal(ExecutionDefinitionJsonSerializer.Serialize(document), ExecutionDefinitionJsonSerializer.Serialize(
                compiled.Documents.Single(d => d.Reference == document.Reference)));
        Assert.Equal(definition.Reference, compiled.Binding!.GetDefinition<IngestionProviderBinding>().Contract.Ingestion);
        Array.Clear(dependencies);
        Assert.Equal(Json(compiled), Json(instance.Compile()));
    }

    [Fact]
    public void ReferencedProtocolRequiresItsExactContracts()
    {
        var original = Bound(new TestModel(), Selection()).Compile();
        var definition = original.Documents.Single(d => d.Kind == IngestionDefinitionDocuments.Kind);
        Assert.Throws<ArgumentException>(() => new ImportedModel(definition, []));
    }

    [Fact]
    public void CompiledClosureRejectsConflictingImportedProcessRevision()
    {
        var original = Bound(new TestModel(), Selection()).Compile();
        var definition = original.Documents.Single(d => d.Kind == IngestionDefinitionDocuments.Kind);
        var process = original.Process!;
        var conflicting = ExecutionDefinitionDocument.Create(process.Kind, process.Metadata.DefinitionId,
            process.Metadata.RevisionId, new PredicateSource(), process.Metadata.Provenance);
        var dependencies = original.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind).Append(conflicting).ToArray();
        var model = new ImportedModel(definition, dependencies);
        var compiled = model.Collect.For(Selection()).Use(model.Collect.Provider("retained", "get", "HTTPS"))
            .WithAccount("account").ForConsumer(new("flow", "source", "sink", "SPY")).Compile();
        Assert.False(compiled.IsSuccessful);
        Assert.False(compiled.Validation.IsValid);
    }

    sealed class ImportedModel : IntegrationModel<SelectionSource>
    {
        public IngestionHandle<SelectionSource> Collect { get; }
        public ImportedModel(ExecutionDefinitionDocument definition, IReadOnlyCollection<ExecutionDefinitionDocument> dependencies)
            : base("test/imported", "v1")
        {
            Collect = Ingestion("existing", definition, dependencies);
            var property = Property("coverage").Kind(ContractPropertyKinds.DataIntegrity).Invariant()
                .Predicate(new PredicateSource()).Assume(new PredicateSource())
                .Subject(s => s.Identity.Symbol).KeyDomain("instrument/session").Boundary(Collect.PublicationRequest)
                .Interval(s => s.Start, s => s.End, TemporalBoundaryInclusion.Inclusive).TimeBasis("exchange session");
            Collect.Requires(property);
        }
    }

    static IntegrationInstance<SelectionSource> Bound(TestModel model, SelectionSource selection) => model.Collect.For(selection)
        .Use(model.Collect.Provider("provider", "get", "HTTPS").Provides(model.Coverage))
        .WithAccount("account").ForConsumer(new("research", "source", "store", "SPY"));
    static SelectionSource Selection() => new() { Identity = new() { Symbol = "SPY" }, Start = new(2026, 9, 1), End = new(2026, 9, 2) };
    static string[] Json(IntegrationCompilation result) => [.. result.Documents.Select(d => ExecutionDefinitionJsonSerializer.Serialize(d))];
    static void Valid(IntegrationCompilation result) => Assert.True(result.IsSuccessful, string.Join("\n", result.Validation.Diagnostics.Select(d => d.Message)));

    public sealed class SymbolIdentity
    {
        [JsonPropertyName("ticker")]
        public string Symbol { get; set; } = "";
    }
    public sealed class SelectionSource
    {
        public SymbolIdentity Identity { get; set; } = new();
        public DateOnly Start { get; set; }
        public DateOnly End { get; set; }
    }
    public sealed class PredicateSource { public bool Required { get; set; } = true; }
    sealed class TestModel : IntegrationModel<SelectionSource>
    {
        public PredicateSource PredicateSource { get; } = new();
        public PropertyHandle<SelectionSource> Coverage { get; }
        public IngestionHandle<SelectionSource> Collect { get; }
        public RequestHandle<SelectionSource, string> Acquisition { get; }
        public TestModel(bool incompatiblePublication = false, bool omitRecovery = false) : base("test/integration", "v1")
        {
            Payload<SelectionSource>("selection"); Payload<string>("text");
            var recovery = Recovery("recovery").StableRequestIdentity().ReconcileAmbiguousOutcomes()
                .ReusePriorDisposition().RetainEvidenceFor(TimeSpan.FromDays(30));
            Acquisition = Request<SelectionSource>("acquire").Result<string>("ready");
            if (!omitRecovery) Acquisition.Recovery(recovery);
            IntegrationRequest publish = incompatiblePublication
                ? Request<int>("publish").Result<string>("published").Recovery(recovery)
                : Request<string>("publish").Result<string>("published").Recovery(recovery);
            var settle = Request<string>("settle").Result<string>("settled").Recovery(recovery);
            Coverage = Property("coverage").Kind(ContractPropertyKinds.DataIntegrity).Invariant()
                .Predicate(PredicateSource).Assume(new PredicateSource())
                .Subject(s => s.Identity.Symbol).KeyDomain("instrument/session").Boundary(publish)
                .Interval(s => s.Start, s => s.End, TemporalBoundaryInclusion.Inclusive).TimeBasis("exchange session");
            Collect = Ingestion("collect").Acquire(Acquisition).Publish(publish).Settle(settle).Requires(Coverage);
        }
    }
}
