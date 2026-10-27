using Cohesive.Examples.ReplayAssurance;
using Cohesive.Storage;

namespace Cohesive.Assurance.Tests;

public sealed class PostgresReservationReplayTests
{
    [PostgresTheory]
    [InlineData(ReservationRealization.AtomicReceipt, AssuranceVerdict.Satisfied, 8, 1, 1)]
    [InlineData(ReservationRealization.UnprotectedRetry, AssuranceVerdict.Violated, 6, 2, 0)]
    public async Task Lost_acknowledgement_is_checked_against_native_effects(
        ReservationRealization realization, AssuranceVerdict verdict, int available, int effects, long receipts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var result = await ReservationReplayDemo.RunAsync(
            Environment.GetEnvironmentVariable(ReservationReplayDemo.ConnectionVariable)!, realization, timeout.Token);
        Assert.Equal(available, result.Available);
        Assert.Equal(effects, result.Effects.Length);
        Assert.Equal(receipts, result.ReceiptCount);
        Assert.Equal(ReservationReplayDemo.InitialAvailable, result.Effects[0].Before);
        Assert.All(result.Effects, effect => Assert.Equal(ReservationReplayDemo.Quantity, effect.Before - effect.After));
        Assert.Equal(available, result.Effects[^1].After);
        Assert.Equal(effects, result.Effects.Select(effect => effect.Id).Distinct().Count());
        if (realization == ReservationRealization.AtomicReceipt)
            Assert.Equal(EntityTransitionOperationDisposition.Replayed, result.ReplayDisposition);
        else
            Assert.Null(result.ReplayDisposition);
        var evaluation = EffectUniquenessEvaluator.Evaluate(result.Check);
        Assert.True(evaluation.Validation.IsValid);
        Assert.Equal(verdict, evaluation.Verdict);
        Assert.Equal(effects, evaluation.UniqueEffects);
        Assert.False(evaluation.IsVacuous);
        Assert.Equal(AssuranceTargetKind.ImplementationCandidate, result.Check.Target.Kind);
        var retained = EffectUniquenessDocument.Serialize(result.Check);
        Assert.Equal(verdict, EffectUniquenessDocument.Restore(retained).Verdict);
        Assert.DoesNotContain("Password", retained, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", retained, StringComparison.OrdinalIgnoreCase);
    }

    sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReservationReplayDemo.ConnectionVariable)))
                Skip = $"Set {ReservationReplayDemo.ConnectionVariable} to a disposable database, or run the demo's run-local.sh.";
        }
    }
}
