using Aspire.Hosting;
using Cohesive.Adapters.Aspire.Application;
using AspireFirst;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class AspireApplicationTests
{
    [Fact]
    public async Task Explain_help_never_configures_native_resources()
    {
        Assert.Equal(0, await AspireApplication.RunAsync(["explain", "--help"],
            _ => throw new InvalidOperationException("must not activate")));
    }

    [Fact]
    public async Task Precancelled_invocation_throws_before_configuration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AspireApplication.RunAsync([], 
            _ => throw new InvalidOperationException("must not activate"), cancellation.Token));
    }

    [Fact]
    public async Task Native_run_rejects_incomplete_association_before_start()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => AspireApplication.RunAsync([], builder =>
        {
            calls++;
            var model = TestModels.Infrastructure();
            var incomplete = builder.AttachCohesive(model, new("test/incomplete"), new("development"), _ => { });
            return model.Explain(incomplete);
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Explain_rejects_incomplete_association_after_one_configuration()
    {
        var calls = 0;
        Assert.NotEqual(0, await AspireApplication.RunAsync(["explain"], builder =>
        {
            calls++;
            var model = TestModels.Infrastructure();
            var incomplete = builder.AttachCohesive(model, new("test/incomplete"), new("development"), _ => { });
            return model.Explain(incomplete);
        }));
        Assert.Equal(1, calls);
    }
}
