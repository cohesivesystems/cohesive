using System.Reflection;
using Cohesive.Adapters.Aspire.Pulumi;
using Pulumi.Automation.Commands;
using Pulumi.Automation.Commands.Exceptions;
using Pulumi.Automation.Events;
using Semver;

namespace Cohesive.Adapters.Aspire.Pulumi.Tests;

public sealed partial class AspirePulumiDeploymentTests
{
    [Fact]
    public async Task Destroy_missing_stack_never_creates_or_destroys_a_replacement()
    {
        var command = new RecordingPulumiCommand { SelectionFailure = MissingStackFailure() };
        await WithAutomationRequest(AspirePulumiDeploymentOperation.Destroy, async request =>
        {
            var error = await Assert.ThrowsAsync<StackNotFoundException>(() =>
                new PulumiAutomationDeploymentExecutor(command).ExecuteAsync(request, CancellationToken.None));
            Assert.Same(command.SelectionFailure, error);
            var select = Assert.Single(command.Calls);
            Assert.Equal(new[] { "stack", "select", "--stack", request.Handoff.PulumiStackName }, select.Take(4));
        });
    }

    [Fact]
    public async Task Destroy_existing_stack_preserves_exact_target_refresh_output_and_secret_policy()
    {
        var command = new RecordingPulumiCommand();
        using var cancellation = new CancellationTokenSource();
        await WithAutomationRequest(AspirePulumiDeploymentOperation.Destroy, async request =>
        {
            var progress = new List<AspirePulumiDeploymentProgress>();
            request = new(request.Handoff, request.RepositoryRoot, request.HandoffPath, request.Operation, progress.Add);
            var result = await new PulumiAutomationDeploymentExecutor(command).ExecuteAsync(request, cancellation.Token);
            Assert.Equal(AspirePulumiDeploymentOperation.Destroy, result.Operation);
            Assert.Equal("Succeeded", result.Outcome);
            Assert.Equal("select", command.Calls[0][1]);
            Assert.DoesNotContain(command.Calls, args => args.Contains("init"));
            var destroy = Assert.Single(command.Calls, args => args[0] == "destroy");
            Assert.Contains("--refresh", destroy);
            Assert.All(command.Calls, args =>
            {
                Assert.DoesNotContain("--show-secrets", args);
                var stackIndex = Array.IndexOf(args, "--stack");
                Assert.True(stackIndex >= 0);
                Assert.Equal(request.Handoff.PulumiStackName, args[stackIndex + 1]);
            });
            Assert.Contains(progress, p => p.Stream == AspirePulumiOutputStream.StandardOutput && p.Message == "progress");
            Assert.Contains(progress, p => p.Stream == AspirePulumiOutputStream.StandardError && p.Message == "diagnostic");
            Assert.All(command.Tokens, token => Assert.Equal(cancellation.Token, token));
            Assert.All(command.Environments, env =>
            {
                Assert.Equal(request.HandoffPath, env[CohesivePulumiEnvironmentVariables.HandoffPath]);
                Assert.Equal(request.Handoff.Fingerprint.Value, env[CohesivePulumiEnvironmentVariables.HandoffFingerprint]);
            });
        });
    }

    [Theory]
    [InlineData(AspirePulumiDeploymentOperation.Preview, false)]
    [InlineData(AspirePulumiDeploymentOperation.Preview, true)]
    [InlineData(AspirePulumiDeploymentOperation.Apply, false)]
    [InlineData(AspirePulumiDeploymentOperation.Apply, true)]
    public async Task Preview_and_apply_retain_create_capable_acquisition(AspirePulumiDeploymentOperation operation, bool missing)
    {
        var command = new RecordingPulumiCommand { StopAtOperation = true, SelectionFailure = missing ? MissingStackFailure() : null };
        await WithAutomationRequest(operation, async request =>
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new PulumiAutomationDeploymentExecutor(command).ExecuteAsync(request, CancellationToken.None));
            Assert.Equal("stack", command.Calls[0][0]);
            Assert.Equal("select", command.Calls[0][1]);
            Assert.Contains(request.Handoff.PulumiStackName, command.Calls[0]);
            if (missing)
            {
                Assert.Equal("init", command.Calls[1][1]);
                Assert.Contains(request.Handoff.PulumiStackName, command.Calls[1]);
            }
            else Assert.DoesNotContain(command.Calls, args => args.Contains("init"));
            Assert.Equal(operation == AspirePulumiDeploymentOperation.Preview ? "preview" : "up", command.Calls[^1][0]);
        });
    }

    [Fact]
    public async Task Selection_cancellation_propagates_without_destroy()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var command = new RecordingPulumiCommand { SelectionFailure = failure };
        await WithAutomationRequest(AspirePulumiDeploymentOperation.Destroy, async request =>
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new PulumiAutomationDeploymentExecutor(command).ExecuteAsync(request, cancellation.Token));
            Assert.Same(failure, error);
            Assert.Single(command.Calls);
            Assert.Equal(cancellation.Token, Assert.Single(command.Tokens));
        });
    }

    [Fact]
    public async Task Wrong_project_fails_before_any_stack_command()
    {
        var command = new RecordingPulumiCommand();
        await WithAutomationRequest(AspirePulumiDeploymentOperation.Destroy, async request =>
        {
            await File.WriteAllTextAsync(Path.Combine(request.RepositoryRoot, "infra/test/Pulumi.yaml"), "name: wrong\nruntime: dotnet\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PulumiAutomationDeploymentExecutor(command).ExecuteAsync(request, CancellationToken.None));
            Assert.Empty(command.Calls);
        });
    }

    static async Task WithAutomationRequest(AspirePulumiDeploymentOperation operation, Func<AspirePulumiDeploymentRequest, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohesive-stack-selection-{Guid.NewGuid():N}");
        try
        {
            var program = Path.Combine(root, "infra/test");
            Directory.CreateDirectory(program);
            await File.WriteAllTextAsync(Path.Combine(program, "Pulumi.yaml"), "name: test-infrastructure\nruntime: dotnet\n");
            var handoff = CreateHandoff(CompletePlan());
            var path = Path.Combine(root, "handoff.json");
            await File.WriteAllBytesAsync(path, handoff.ToCanonicalBytes());
            await action(new(handoff, root, path, operation));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Pulumi exposes the exception type but keeps its constructor internal. This fixture only
    // constructs that pinned SDK exception; production classification remains entirely Pulumi-owned.
    static StackNotFoundException MissingStackFailure() => (StackNotFoundException)Activator.CreateInstance(
        typeof(StackNotFoundException), BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
        args: [new CommandResult(255, "", "no stack named test found; secret-provider-marker")], culture: null)!;

    sealed class RecordingPulumiCommand : PulumiCommand
    {
        public override SemVersion Version => new(3, 113, 1);
        public List<string[]> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<Dictionary<string, string?>> Environments { get; } = [];
        public Exception? SelectionFailure { get; init; }
        public bool StopAtOperation { get; init; }

        public override Task<CommandResult> RunAsync(IList<string> args, string workingDir,
            IDictionary<string, string?> additionalEnv, Action<string>? onStandardOutput = null,
            Action<string>? onStandardError = null, Action<EngineEvent>? onEngineEvent = null,
            CancellationToken cancellationToken = default) =>
            RunInputAsync(args, workingDir, additionalEnv, onStandardOutput, onStandardError, null, onEngineEvent, cancellationToken);

        public override Task<CommandResult> RunInputAsync(IList<string> args, string workingDir,
            IDictionary<string, string?> additionalEnv, Action<string>? onStandardOutput = null,
            Action<string>? onStandardError = null, string? stdIn = null, Action<EngineEvent>? onEngineEvent = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args.ToArray());
            Tokens.Add(cancellationToken);
            Environments.Add(new(additionalEnv));
            if (args.Take(2).SequenceEqual(new[] { "stack", "select" }) && SelectionFailure is not null)
                return Task.FromException<CommandResult>(SelectionFailure);
            if (args[0] is "destroy" or "preview" or "up")
            {
                if (StopAtOperation) return Task.FromException<CommandResult>(new OperationCanceledException());
                onStandardOutput?.Invoke("progress");
                onStandardError?.Invoke("diagnostic");
            }
            var output = args.Take(2).SequenceEqual(new[] { "stack", "history" })
                ? """[{"kind":"destroy","startTime":"2026-09-18T00:00:00Z","endTime":"2026-09-18T00:00:01Z","message":"test","environment":{},"config":{},"result":"succeeded","version":1,"deployment":"","resourceChanges":{}}]"""
                : "";
            return Task.FromResult(new CommandResult(0, output, ""));
        }
    }
}
