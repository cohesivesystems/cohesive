using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;
using Cohesive.Storage;

namespace Cohesive.Assurance.Tests;

public sealed class ReplayAssuranceTests
{
    public sealed record ReplayCase(int Attempts);

    // Pin the tested binaries rather than labeling arbitrary future builds with a fixed content hash.
    static readonly string TargetFingerprint = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", new[]
        {
            typeof(ReplayAssuranceTests).Assembly,
            typeof(InMemoryEntityOutboxRepository).Assembly,
            typeof(RunControlFixture).Assembly,
            typeof(ExecutionDefinitionReference).Assembly
        }.Select(assembly => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))))))));

    static EffectUniquenessCheck Check(IEnumerable<CommittedEffectObservation> observations,
        AssuranceTargetKind kind = AssuranceTargetKind.ReferenceModel) => new(
            new("replay/at-most-one-effect", "1", RunControlFixture.Start.Reference),
            new("fixture/isolated-versioned-effects", "1", "isolated-version-increments/v1", "adoption/tenant-a", "run-control/run-1"),
            new(kind, "replay-fixture", "1", TargetFingerprint),
            RunControlFixture.Start.Reference,
            // Only the finite fixture scope is closed: all calls are completed and there are no other writers.
            ObservationScopeCompletion.ClosedComplete, [.. observations]);

    [Fact]
    public void Existing_simulation_generator_checks_duplicate_observations_and_shrinks_a_defect()
    {
        var generator = Cohesive.Simulation.Simulation.Define<ReplayCase>(sample => sample
            .Member(x => x.Attempts, Gen.Int32(minimum: 1, maximum: 20))).Compile();
        EffectUniquenessEvidence Observe(ReplayCase sample, bool defective) => EffectUniquenessEvaluator.Evaluate(Check(
            Enumerable.Range(0, sample.Attempts).Select(index => new CommittedEffectObservation("exact-occurrence",
                defective ? index.ToString(CultureInfo.InvariantCulture) : "single-effect"))));

        var valid = generator.CheckProperty(seed: 42,
            property: sample => Observe(sample, defective: false).Verdict == AssuranceVerdict.Satisfied,
            options: new(requiredPassedCases: 100));
        Assert.Equal(PropertyCaseRunStatus.Passed, valid.Status);
        Assert.Equal(100, valid.Evidence.PassedCaseCount);

        var defective = generator.CheckProperty(seed: 42,
            property: sample => Observe(sample, defective: true).Verdict != AssuranceVerdict.Violated,
            options: new(requiredPassedCases: 100));
        Assert.NotEqual(PropertyCaseRunStatus.Passed, defective.Status);
        var witness = Assert.IsType<PropertyCase>(defective.BestCounterexample);
        var replay = generator.ReplayPropertyCase(witness.Replay.ToToken());
        Assert.Equal(2, replay.Attempts);
        Assert.Equal(AssuranceVerdict.Violated, Observe(replay, defective: true).Verdict);
    }

    [Fact]
    public async Task Real_in_memory_commit_protocol_replays_after_acknowledgement_loss()
    {
        var repository = new InMemoryEntityOutboxRepository(RunControlFixture.Entity,
            EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var prepared = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(prepared, prepared.Decision,
            RunControlFixture.Lower(prepared, prepared.Decision, RunControlFixture.Contracts()));
        // The occurrence, not request fingerprint, owns replay correlation. Scope and exact transition
        // are pinned independently by the observation contract and claim.
        var operation = Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(
            commit.Request.Operation, StrictDocumentJson.CreateOptions()));
        List<CommittedEffectObservation> observations = [];
        async Task<EntityTransitionOperationResult> Invoke(bool loseAcknowledgement)
        {
            var result = await repository.CommitTransitionOperation(context, commit);
            var stored = await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a"));
            Assert.NotNull(stored);
            // This fixture alone guarantees every effect increments Version with no unrelated writers.
            // Version is not proposed as a general production effect identity.
            observations.Add(new(operation, stored.Entity.Version.ToString(CultureInfo.InvariantCulture)));
            if (loseAcknowledgement) throw new IOException("test acknowledgement lost after commit");
            return result;
        }
        await Assert.ThrowsAsync<IOException>(() => Invoke(loseAcknowledgement: true));
        var replay = await Invoke(loseAcknowledgement: false);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
        Assert.Equal(commit.Fingerprint, replay.Receipt!.Commit.Fingerprint);
        var after = await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a"));
        Assert.Equal(before.Entity.Version + 1, after!.Entity.Version);
        var result = EffectUniquenessEvaluator.Evaluate(Check(observations, AssuranceTargetKind.ImplementationCandidate));
        Assert.Equal(AssuranceVerdict.Satisfied, result.Verdict);
        Assert.Equal(1, result.UniqueEffects);
    }
}
