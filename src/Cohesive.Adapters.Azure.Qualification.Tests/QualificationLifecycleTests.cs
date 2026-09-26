using Cohesive.Adapters.Azure.Qualification;

namespace Cohesive.Adapters.Azure.Qualification.Tests;

public class QualificationLifecycleTests
{
    static RuntimeQualificationOptions Options() => new(Guid.NewGuid(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Collision_never_reads_or_deletes_existing_data()
    {
        var result = await StorageQualification.RunAsync(Options(), _ => Task.FromResult<string?>(null),
            (_, _) => throw new Exception("must not read"), (_, _) => throw new Exception("must not delete"), default);
        Assert.Equal(QualificationOutcome.Collision, result.Outcome);
        Assert.Equal(QualificationCleanup.NotRequired, result.Cleanup);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Lost_creation_receipt_is_not_ownership_and_never_triggers_delete()
    {
        var result = await StorageQualification.RunAsync(Options(), _ => throw new IOException("secret payload"),
            (_, _) => throw new Exception(), (_, _) => throw new Exception(), default);
        Assert.Equal(QualificationCleanup.Unresolved, result.Cleanup);
        Assert.Equal("qualification.createUnknown", result.Diagnostic);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Original_receipt_is_used_for_cleanup_even_after_failed_verification(bool verified)
    {
        var deleted = false;
        var result = await StorageQualification.RunAsync(Options(), _ => Task.FromResult<string?>("original"),
            (etag, _) => { Assert.Equal("original", etag); return Task.FromResult(verified); },
            (etag, _) => { Assert.Equal("original", etag); deleted = true; return Task.CompletedTask; }, default);
        Assert.True(deleted);
        Assert.Equal(verified, result.Succeeded);
    }

    [Fact]
    public async Task Cancellation_after_create_still_uses_independent_cleanup_budget()
    {
        using var cancel = new CancellationTokenSource();
        var cleaned = false;
        var result = await StorageQualification.RunAsync(Options(), _ => { cancel.Cancel(); return Task.FromResult<string?>("etag"); },
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); },
            (_, ct) => { Assert.False(ct.IsCancellationRequested); cleaned = true; return Task.CompletedTask; }, cancel.Token);
        Assert.True(cleaned);
        Assert.False(result.Succeeded);
        Assert.Equal(QualificationCleanup.Deleted, result.Cleanup);
    }

    [Fact]
    public async Task Cleanup_conflict_cannot_report_success_or_retry_delete()
    {
        var calls = 0;
        var result = await StorageQualification.RunAsync(Options(), _ => Task.FromResult<string?>("etag"),
            (_, _) => Task.FromResult(true), (_, _) => { calls++; throw new InvalidOperationException("changed object"); }, default);
        Assert.Equal(1, calls);
        Assert.Equal(QualificationOutcome.Verified, result.Outcome);
        Assert.Equal(QualificationCleanup.Unresolved, result.Cleanup);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Precancelled_attempt_does_not_create()
    {
        var result = await StorageQualification.RunAsync(Options(), _ => throw new Exception(),
            (_, _) => throw new Exception(), (_, _) => throw new Exception(), new CancellationToken(true));
        Assert.Equal(QualificationOutcome.NotStarted, result.Outcome);
        Assert.Equal(QualificationCleanup.NotRequired, result.Cleanup);
    }

    [Fact]
    public void Invalid_budgets_and_run_ids_fail_before_IO()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeQualificationOptions(Guid.Empty, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeQualificationOptions(Guid.NewGuid(), TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeQualificationOptions(Guid.NewGuid(), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)));
    }
}
