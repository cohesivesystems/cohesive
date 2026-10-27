using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;

namespace Cohesive.Assurance.Tests;

public sealed class EffectUniquenessTests
{
    static readonly ExecutionDefinitionReference Transition = new(new("reserve"), new("1"),
        new("sha256", "fixture/v1", "fixture-transition-content"));

    static EffectUniquenessCheck Check(ObservationScopeCompletion completion,
        params CommittedEffectObservation[] observations) => new(
            new("reservation/at-most-one-commit", "1", Transition),
            new("fixture/commit-observation", "1", "mapping-v1", "demo/tenant", "inventory/book"),
            new(AssuranceTargetKind.ReferenceModel, "fixture", "1", "fixture-content"),
            Transition, completion, [.. observations]);

    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "effect-uniqueness.json")));
        foreach (var item in document.RootElement.EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A0_fixtures_define_verdicts_independently(string name, string json)
    {
        Assert.NotEmpty(name);
        using var document = JsonDocument.Parse(json);
        var fixture = document.RootElement;
        var observations = fixture.GetProperty("effects").EnumerateArray()
            .Select(pair => new CommittedEffectObservation(pair[0].GetString(), pair[1].GetString())).ToArray();
        var input = Check(Enum.Parse<ObservationScopeCompletion>(fixture.GetProperty("completion").GetString()!), observations);
        var result = EffectUniquenessEvaluator.Evaluate(input);
        Assert.Equal(Enum.Parse<AssuranceVerdict>(fixture.GetProperty("verdict").GetString()!), result.Verdict);
        Assert.Equal(fixture.GetProperty("uniqueEffects").GetInt32(), result.UniqueEffects);
        if (fixture.TryGetProperty("diagnostic", out var diagnostic))
        {
            Assert.Equal(diagnostic.GetString(), Assert.Single(result.Validation.Diagnostics).Code);
            Assert.Null(result.FirstWitnessIndex);
            Assert.Null(result.SecondWitnessIndex);
        }
        else Assert.True(result.Validation.IsValid);
        if (result.Verdict == AssuranceVerdict.Violated)
        {
            var first = input.Observations[result.FirstWitnessIndex!.Value];
            var second = input.Observations[result.SecondWitnessIndex!.Value];
            Assert.Equal(first.OperationIdentity, second.OperationIdentity);
            Assert.NotEqual(first.EffectIdentity, second.EffectIdentity);
        }
        var retained = EffectUniquenessDocument.Serialize(input);
        var restored = EffectUniquenessDocument.Restore(retained);
        Assert.Equal(result.Verdict, restored.Verdict);
        Assert.Equal(result.Explain(), restored.Explain());
        Assert.Equal(retained, EffectUniquenessDocument.Serialize(restored.Input));
    }

    [Fact]
    public void Open_prefix_explanation_does_not_claim_proof()
    {
        var result = EffectUniquenessEvaluator.Evaluate(Check(ObservationScopeCompletion.Open,
            new("reservation-1", "commit-1"), new("reservation-1", "commit-1")));
        Assert.Equal("""
            Claim: reservation/at-most-one-commit@1
            Profile: cohesive-assurance/effect-uniqueness/v1
            Verdict: Inconclusive
            Scope: Open
            Observed operations: 1
            Distinct effects: 1
            Missing identities: 0
            Vacuous: False
            Reason: The scope is open; later effects remain possible.
            General proof state: not established by this trace check
            """, result.Explain());
        Assert.True(result.NoViolationObserved);
    }

    [Fact]
    public void Foreign_exact_transition_is_invalid_evidence()
    {
        var input = Check(ObservationScopeCompletion.ClosedComplete, new CommittedEffectObservation("op", "effect"));
        var foreign = new ExecutionDefinitionReference(Transition.DefinitionId, new("2"), Transition.Fingerprint);
        var result = EffectUniquenessEvaluator.Evaluate(new(input.Claim, input.ObservationContract,
            input.Target, foreign, input.Completion, input.Observations));
        Assert.Equal(AssuranceVerdict.Inconclusive, result.Verdict);
        Assert.Equal("assurance.observation.transition.mismatch", Assert.Single(result.Validation.Diagnostics).Code);
        Assert.False(result.NoViolationObserved);
    }

    [Fact]
    public void Exhaustive_small_sequences_agree_with_independent_set_definition()
    {
        // Finite conformance check, not a claim about all protocol executions.
        CommittedEffectObservation[] alphabet = [new("a", "1"), new("a", "2"), new("b", "3")];
        for (var length = 0; length <= 5; length++)
        {
            var count = (int)Math.Pow(alphabet.Length, length);
            for (var code = 0; code < count; code++)
            {
                var digits = code;
                var observations = new CommittedEffectObservation[length];
                for (var index = 0; index < length; index++)
                {
                    observations[index] = alphabet[digits % alphabet.Length];
                    digits /= alphabet.Length;
                }
                var violation = observations.GroupBy(x => x.OperationIdentity)
                    .Any(group => group.Select(x => x.EffectIdentity).Distinct().Count() > 1);
                foreach (var completion in Enum.GetValues<ObservationScopeCompletion>())
                {
                    var result = EffectUniquenessEvaluator.Evaluate(Check(completion, observations));
                    var expected = violation ? AssuranceVerdict.Violated
                        : completion == ObservationScopeCompletion.ClosedComplete
                            ? AssuranceVerdict.Satisfied : AssuranceVerdict.Inconclusive;
                    Assert.Equal(expected, result.Verdict);
                    Assert.Equal(expected, EffectUniquenessEvaluator.Evaluate(Check(completion, [.. observations.Reverse()])).Verdict);
                }
            }
        }
    }

    [Fact]
    public void Identity_changes_affect_evidence_fingerprint()
    {
        var input = Check(ObservationScopeCompletion.Open);
        var target = new AssuranceTarget(input.Target.Kind, input.Target.Id, "2", "new-build");
        var mapping = new EffectObservationContract("fixture/commit-observation", "2", "new-mapping", "demo/tenant", "inventory/book");
        var alternatives = new[]
        {
            new EffectUniquenessCheck(input.Claim, input.ObservationContract, target, Transition, input.Completion, []),
            new EffectUniquenessCheck(input.Claim, mapping, input.Target, Transition, input.Completion, []),
            new EffectUniquenessCheck(new(input.Claim.Id, "2", Transition), input.ObservationContract, input.Target, Transition, input.Completion, []),
            new EffectUniquenessCheck(input.Claim, input.ObservationContract, input.Target, Transition, ObservationScopeCompletion.ClosedComplete, [])
        };
        Assert.All(alternatives, alternative => Assert.NotEqual(
            EffectUniquenessDocument.Fingerprint(input), EffectUniquenessDocument.Fingerprint(alternative)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("profile")]
    [InlineData("fingerprint")]
    [InlineData("missing-completion")]
    [InlineData("tamper")]
    public void Persistence_rejects_untrusted_documents(string mutation)
    {
        var json = EffectUniquenessDocument.Serialize(Check(ObservationScopeCompletion.Open));
        var node = JsonNode.Parse(json)!.AsObject();
        switch (mutation)
        {
            case "unknown": node["unexpected"] = 1; break;
            case "duplicate":
                Assert.Throws<JsonException>(() => EffectUniquenessDocument.Restore(
                    json.Insert(1, "\"profile\":\"duplicate\",")));
                return;
            case "profile": node["profile"] = "future"; break;
            case "fingerprint": node["fingerprint"] = "forged"; break;
            case "missing-completion": node["input"]!.AsObject().Remove("completion"); break;
            case "tamper": node["input"]!["target"]!["revision"] = "changed"; break;
        }
        Assert.Throws<JsonException>(() => EffectUniquenessDocument.Restore(node.ToJsonString()));
    }

    [Fact]
    public void Constructors_reject_invalid_states()
    {
        Assert.Throws<ArgumentException>(() => new CommittedEffectObservation(" ", "effect"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check((ObservationScopeCompletion)99));
        Assert.Throws<ArgumentException>(() => Check(ObservationScopeCompletion.Open, [null!]));
    }
}
