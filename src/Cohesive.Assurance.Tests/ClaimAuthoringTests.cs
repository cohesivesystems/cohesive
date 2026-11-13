using Cohesive.Authoring;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Cohesive.Model;

namespace Cohesive.Assurance.Tests;

public class ClaimAuthoringTests
{
    public sealed record Inventory(decimal OnHand, decimal Reserved);
    public sealed record Order(decimal Quantity);
    public sealed record Lines(int[] Quantities);
    public sealed record OrderEvent(string OrderId);
    public sealed record Renamed([property: JsonPropertyName("reserved_units")] int Reserved);

    static ClaimReference Ref(string name, string revision = "1") =>
        new(new SourceReference($"test://{name}"), revision, $"sha256:{name}-{revision}");

    static Claim Start(string id = "inventory.safe", string sourceRevision = "1") =>
        Claim.Define(id, "1", Ref("inventory"), Ref("observations"), Ref("source", sourceRevision), Ref("configuration"));

    static Claim State() => Start().AtEveryCommittedState(Ref("population"), InitialStateSelection.Include);

    static ClaimInput Input(ExpressionBinding binding, ClaimInputRole role) =>
        new(binding.Id, role, new ValueContract(binding.Type));

    [Fact]
    public void TypedAndStructuralSourcesHaveIdenticalSemanticIr()
    {
        var session = new ExpressionAuthoring();
        var stock = session.Input<Inventory>();
        var typed = State().Require(stock, ClaimInputRole.State, s => s.Reserved <= s.OnHand).Build();
        var structural = State().Require(Expr.Le(Expr.Field(stock.Id, "Reserved"), Expr.Field(stock.Id, "OnHand")),
            [Input(stock, ClaimInputRole.State)]).Build();

        Assert.Equal(structural.SemanticFingerprint, typed.SemanticFingerprint);
        Assert.Equal(structural.Definition.Predicate, typed.Definition.Predicate);
        Assert.NotEqual(structural.Fingerprint, typed.Fingerprint); // Different source producers.
        Assert.Contains(typed.SourceMap, x => x.Path == "/predicate");
        Assert.All(typed.SourceMap, x => Assert.Equal(Ref("source"), x.Source));
    }

    [Fact]
    public void CommittedTransitionRetainsThreeExplicitRoles()
    {
        var session = new ExpressionAuthoring();
        var before = session.Input<Inventory>();
        var command = session.Input<Order>();
        var after = session.Input<Inventory>();
        var claim = Start().ForEachCommitted(Ref("accept"))
            .Require(before, command, after, (b, c, a) => c.Quantity > 0m && a.Reserved == b.Reserved + c.Quantity).Build();
        Assert.Equal(ClaimForm.TransitionContract, claim.Definition.Form);
        Assert.Equal(3, claim.Definition.Inputs.Length);
        Assert.Null(claim.Definition.Witness);
        var structural = Start().ForEachCommitted(Ref("accept"))
            .Require(Expr.And(
                Expr.Gt(Expr.Field(command.Id, "Quantity"),
                    new LiteralExpr(new ScalarTypeRef(ScalarTypeKind.Decimal), ObservationValue.FromDecimal(0m))),
                Expr.Eq(Expr.Field(after.Id, "Reserved"),
                    Expr.Add(Expr.Field(before.Id, "Reserved"), Expr.Field(command.Id, "Quantity")))),
                [Input(after, ClaimInputRole.After), Input(command, ClaimInputRole.Command), Input(before, ClaimInputRole.Before)])
            .Build();
        Assert.Equal(claim.SemanticFingerprint, structural.SemanticFingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventObligationsPreserveSelectorsAndInclusiveDuration(bool bounded)
    {
        var session = new ExpressionAuthoring();
        var trigger = session.Input<OrderEvent>();
        var witness = session.Input<OrderEvent>();
        var source = bounded ? Start().RequireWithin(Ref("cancel"), Ref("release"), TimeSpan.Zero)
            : Start().RequireEarlier(Ref("ship"), Ref("capture"));
        var claim = source.Require(trigger, ClaimInputRole.Trigger, witness, ClaimInputRole.Witness,
            (t, w) => t.OrderId == w.OrderId).Build();
        Assert.Equal(bounded ? ClaimForm.BoundedResponse : ClaimForm.HistoricalRequirement, claim.Definition.Form);
        Assert.Equal(bounded ? 0L : (long?)null, claim.Definition.DeadlineTicks);
        Assert.Equal(claim.Serialize(), ClaimDocument.Deserialize(claim.Serialize()).Serialize());
        var structural = source.Require(Expr.Eq(Expr.Field(trigger.Id, "OrderId"), Expr.Field(witness.Id, "OrderId")),
            [Input(witness, ClaimInputRole.Witness), Input(trigger, ClaimInputRole.Trigger)]).Build();
        Assert.Equal(claim.SemanticFingerprint, structural.SemanticFingerprint);
    }

    [Fact]
    public void RepeatedLoweringAndRestorationAreDeterministic()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var source = State().Require(Expr.BoundValue(input.Id), [input]);
        var first = source.Build();
        Assert.Equal(first.Serialize(), source.Build().Serialize());
        var restored = ClaimDocument.Deserialize(first.Serialize());
        Assert.Equal(first.Fingerprint, restored.Fingerprint);
        Assert.True(first.SourceMap.SequenceEqual(restored.SourceMap));
    }

    [Fact]
    public void SourceEditsDoNotMutatePriorArtifacts()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var source = State().Require(Expr.BoundValue(input.Id), [input]);
        var first = source.Build();
        var changed = source.Assuming(Ref("atomicity")).RequireEvidence(Ref("policy-fixtures")).Build();
        Assert.Empty(first.Definition.Assumptions);
        Assert.Empty(first.Definition.EvidenceRequirements);
        Assert.Single(ClaimDocument.Deserialize(changed.Serialize()).Definition.EvidenceRequirements);
        Assert.NotEqual(first.SemanticFingerprint, changed.SemanticFingerprint);
    }

    [Fact]
    public void SourceRevisionAffectsProvenanceButNotSemanticIdentity()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        ClaimDocument Build(string sourceRevision) => Start(sourceRevision: sourceRevision)
            .AtEveryCommittedState(Ref("population"), InitialStateSelection.Include)
            .Require(Expr.BoundValue(input.Id), [input]).Build();
        Assert.Equal(Build("1").SemanticFingerprint, Build("2").SemanticFingerprint);
        Assert.NotEqual(Build("1").Fingerprint, Build("2").Fingerprint);
    }

    [Fact]
    public void CanonicalSetsIgnoreContributionOrder()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var source = State().Require(Expr.BoundValue(input.Id), [input]);
        var a = source.Attach("implementation", Ref("impl")).Attach("model", Ref("model"))
            .Assuming(Ref("b")).Assuming(Ref("a")).Build();
        var b = source.Attach("model", Ref("model")).Attach("implementation", Ref("impl"))
            .Assuming(Ref("a")).Assuming(Ref("b")).Build();
        Assert.Equal(a.Serialize(), b.Serialize());
    }

    [Fact]
    public void CallerOwnedInputListIsCopied()
    {
        List<ClaimInput> inputs = [new(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)))];
        var source = State().Require(Expr.BoundValue(inputs[0].Id), inputs);
        inputs.Clear();
        Assert.Single(source.Build().Definition.Inputs);
    }

    [Fact]
    public void CapturedCodeIsRejectedWithoutInvocation()
    {
        var session = new ExpressionAuthoring();
        var stock = session.Input<Inventory>();
        var probe = new GetterProbe();
        Assert.Throws<ExpressionAuthoringException>(() =>
            State().Require(stock, ClaimInputRole.State, s => s.Reserved < probe.Value));
        Assert.Equal(0, probe.Calls);
    }

    sealed class GetterProbe
    {
        public int Calls;
        public int Value { get { Calls++; return 10; } }
    }

    [Fact]
    public void SerializedMemberNamesUseExistingMetadataPolicy()
    {
        var session = new ExpressionAuthoring();
        var value = session.Input<Renamed>();
        var claim = State().Require(value, ClaimInputRole.State, s => s.Reserved >= 0).Build();
        Assert.Contains("reserved_units", claim.Serialize());
    }

    [Fact]
    public void FiniteQuantificationUsesSharedCollectionLowering()
    {
        var session = new ExpressionAuthoring();
        var lines = session.Input<Lines>();
        var document = State().Require(lines, ClaimInputRole.State,
            s => s.Quantities.All(q => q > 0)).Build();
        Assert.True(document.Definition.Analyze().IsValid);
        Assert.Equal(document.Fingerprint, ClaimDocument.Deserialize(document.Serialize()).Fingerprint);
    }

    [Fact]
    public void IntegerArithmeticIsNotSilentlyReinterpretedAsDecimal()
    {
        var session = new ExpressionAuthoring();
        var value = session.Input<Renamed>();
        Assert.Throws<ExpressionAuthoringException>(() =>
            State().Require(value, ClaimInputRole.State, s => s.Reserved + 1 > 0));
    }

    [Fact]
    public void CanonicalClaimHasNoPublicConstructionOrMutationSurface()
    {
        Assert.Empty(typeof(ClaimDefinition).GetConstructors());
        Assert.All(typeof(ClaimDefinition).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.Empty(typeof(ClaimDocument).GetConstructors());
    }

    [Fact]
    public void ExactReferenceResolutionRejectsRevisionDrift()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var document = State().Require(Expr.BoundValue(input.Id), [input]).Build();
        ClaimReference[] catalog = [Ref("inventory"), Ref("population"), Ref("observations"), Ref("source"), Ref("configuration")];
        Assert.True(document.ValidateReferences(catalog).IsValid);
        catalog[0] = Ref("inventory", "2");
        var result = document.ValidateReferences(catalog);
        Assert.False(result.IsValid);
        Assert.Single(result.Diagnostics);
        Assert.Equal("assurance.reference.unresolved", result.Diagnostics[0].Code);
    }

    [Fact]
    public void SubjectAndConfigurationChangesHaveDistinctFingerprintScopes()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        ClaimDocument Build(string subjectRevision, string configurationRevision) =>
            Claim.Define("inventory.safe", "1", Ref("inventory", subjectRevision), Ref("observations"), Ref("source"),
                Ref("configuration", configurationRevision))
                .AtEveryCommittedState(Ref("population"), InitialStateSelection.Include)
                .Require(Expr.BoundValue(input.Id), [input]).Build();
        Assert.NotEqual(Build("1", "1").SemanticFingerprint, Build("2", "1").SemanticFingerprint);
        Assert.Equal(Build("1", "1").SemanticFingerprint, Build("1", "2").SemanticFingerprint);
        Assert.NotEqual(Build("1", "1").Fingerprint, Build("1", "2").Fingerprint);
    }

    [Fact]
    public void PersistedProfileVersionMustBeKnown()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var json = State().Require(Expr.BoundValue(input.Id), [input]).Build().Serialize();
        Assert.Throws<JsonException>(() => ClaimDocument.Deserialize(json.Replace(AssurancePredicates.ProfileId, "future/v99")));
    }

    [Fact]
    public void InvalidFormsAndBindingsCannotProduceIr()
    {
        Assert.Throws<InvalidOperationException>(() => Start().Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => Start().RequireWithin(Ref("t"), Ref("w"), TimeSpan.FromTicks(-1)));
        var input = new ClaimInput(new("before"), ClaimInputRole.Before, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        Assert.Throws<ArgumentException>(() => State().Require(Expr.BoundValue(input.Id), [input]).Build());
        Assert.Throws<InvalidOperationException>(() => State().ForEachCommitted(Ref("transition")));
    }

    [Fact]
    public void UnsupportedExpressionsHaveStructuredDiagnostics()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var failure = Assert.Throws<ClaimValidationException>(() => State()
            .Require(Expr.And(Expr.Const(false), Expr.Call("execute.host")), [input]).Build());
        Assert.False(failure.Validation.IsValid);
        Assert.NotEmpty(failure.Validation.Diagnostics);
    }

    [Fact]
    public void PersistenceRejectsTamperingUnknownFieldsAndDuplicateKeys()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var document = State().Require(Expr.BoundValue(input.Id), [input]).Build();
        var json = document.Serialize();
        Assert.Throws<JsonException>(() => ClaimDocument.Deserialize(json.Replace("inventory.safe", "inventory.unsafe")));
        var extra = JsonNode.Parse(json)!.AsObject();
        extra["executable"] = "callback";
        Assert.Throws<JsonException>(() => ClaimDocument.Deserialize(extra.ToJsonString()));
        Assert.Throws<JsonException>(() => ClaimDocument.Deserialize(json.Insert(1, "\"schema\":\"duplicate\",")));
    }
}
