using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Processes.Execution;
using Cohesive.Simulation.Scenarios;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.IR;

namespace Cohesive.Simulation.Transitions;

/// <summary>Selects an immutable source receipt and the host to which the scenario applies it.</summary>
/// <param name="Host">Registered host alias.</param>
/// <param name="Receipt">Registered source receipt alias; subsequent actions refer to the instance admitted by this pair.</param>
/// <param name="Fault">Fault at this recovery action only; other operations require None.</param>
public sealed record ReactionScenarioInput(string Host, string Receipt, ReactionScenarioFault Fault = ReactionScenarioFault.None);

/// <summary>Portable evidence from the existing durable reaction interpreter.</summary>
/// <param name="Policy">Exact policy fingerprint.</param>
/// <param name="Source">Exact source commit fingerprint.</param>
/// <param name="Instance">Admitted Process identity.</param>
/// <param name="Status">Evidence-derived reaction status.</param>
/// <param name="Prerequisite">Exact awaited instance, or null.</param>
/// <param name="Diagnostic">Structured diagnostic code, or null.</param>
/// <param name="Decision">Target receipt decision, or null before conclusive evidence.</param>
/// <param name="Operation">Serialized exact target operation identity, or null before dispatch.</param>
/// <param name="ControlDisposition">Canonical cancellation outcome for abandonment actions, otherwise null.</param>
/// <param name="Attempts">Number of retained target attempts.</param>
/// <param name="Episode">Serialized typed episode value, or null for other identity scopes.</param>
/// <param name="Fault">Fault boundary actually exercised by this action, or None.</param>
public sealed record ReactionScenarioObservation(string Policy, string Source, string Instance,
    MachineReactionStatus Status, string? Prerequisite, string? Diagnostic, TransitionDecisionKind? Decision, string? Operation, ProcessDurableRuntimeDisposition? ControlDisposition, int Attempts, string? Episode, ReactionScenarioFault Fault);

/// <summary>Interprets bounded virtual-time schedules through existing durable reaction hosts.</summary>
/// <remarks>Use a fresh interpreter and isolated deterministic stores/targets for each run. This is not a sandbox:
/// supplied hosts retain their authorization and infrastructure behavior. The environment identity must pin initial
/// entity state, target realization, authorization and fault configuration. No scheduler or reaction semantics are
/// duplicated here. State lives in the supplied stores; the scenario world supplies actors, not entity replicas.
/// Calls are sequential and single-run; timestamps come exclusively from canonical scenario actions.</remarks>
public sealed class MachineReactionScenarioInterpreter : IScenarioActionInterpreter
{
    /// <summary>Admits a source receipt without dispatching its target.</summary>
    public const string Deliver = "reaction.deliver";
    /// <summary>Advances an admitted instance using existing recovery semantics.</summary>
    public const string Recover = "reaction.recover";
    /// <summary>Inspects an admitted instance without dispatch.</summary>
    public const string Inspect = "reaction.inspect";
    /// <summary>Cancels an admitted instance through the existing authorized control operation.</summary>
    public const string Abandon = "reaction.abandon";
    static readonly DefaultClrTypeRefMapper Mapper = new();
    static readonly ValueContract InputContract = new(Mapper.Map(typeof(ReactionScenarioInput), nullability: null));
    static readonly ValueContract OutputContract = new(Mapper.Map(typeof(ReactionScenarioObservation), nullability: null));
    readonly Dictionary<string, MachineReactionHost> hosts;
    readonly Dictionary<string, EntityTransitionOperationReceipt> receipts;
    readonly Dictionary<(string Host, string Receipt), ProcessInstanceId> admitted = [];
    readonly Func<OperationContext, ProcessStartRequest, CancelProcessCommand, ValueTask<bool>> authorizeAbandon;
    readonly ReactionScenarioFaults? faults;
    int nextSequence;
    string? scenarioFingerprint;

    /// <summary>Binds immutable receipts and isolated hosts to one declared simulation environment.</summary>
    /// <param name="environmentIdentity">Stable exact identity of the caller-owned initial state and interpretation/fault configuration.</param>
    /// <param name="store">Shared discoverable Process store; existing recovery registration checks validate all hosts against it.</param>
    /// <param name="hosts">Host aliases; every prerequisite must be registered in the same authority.</param>
    /// <param name="receipts">Immutable source evidence indexed by alias; copied on construction.</param>
    /// <param name="authorizeAbandon">Explicit simulation control authorization; null denies abandonment.</param>
    /// <param name="faults">Optional single-run instrumentation bound to this run's target hosts.</param>
    /// <exception cref="ArgumentException">Environment identity is blank, aliases are blank, or runtime registration has missing prerequisites, incompatible stores or hosts without recovery.</exception>
    /// <exception cref="ArgumentNullException">A required argument or registration value is null.</exception>
    /// <exception cref="InvalidOperationException">Fault instrumentation already belongs to another interpreter.</exception>
    public MachineReactionScenarioInterpreter(string environmentIdentity, IProcessDurableStore store,
        IReadOnlyDictionary<string, MachineReactionHost> hosts,
        IReadOnlyDictionary<string, EntityTransitionOperationReceipt> receipts,
        Func<OperationContext, ProcessStartRequest, CancelProcessCommand, ValueTask<bool>>? authorizeAbandon = null, ReactionScenarioFaults? faults = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentIdentity);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(receipts);
        EnvironmentIdentity = environmentIdentity;
        this.faults = faults;
        this.hosts = new(hosts, StringComparer.Ordinal);
        this.receipts = new(receipts, StringComparer.Ordinal);
        foreach (var (alias, host) in this.hosts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias);
            ArgumentNullException.ThrowIfNull(host);
        }
        foreach (var (alias, receipt) in this.receipts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias);
            ArgumentNullException.ThrowIfNull(receipt);
        }
        // Reuse the runtime's registration authority, including store, recovery, revision and tenant checks.
        _ = new MachineReactionRecovery(store, [.. this.hosts.Values]);
        faults?.Attach(this.hosts);
        this.authorizeAbandon = authorizeAbandon ?? ((_, _, _) => ValueTask.FromResult(false));
        var identity = JsonSerializer.Serialize(new
        {
            Environment = environmentIdentity,
            FaultTargets = faults?.Aliases.ToArray() ?? [],
            Hosts = this.hosts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { Alias = p.Key, Policy = p.Value.Policy.Document.Reference, p.Value.Authority }),
            Receipts = this.receipts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { Alias = p.Key, p.Value.Commit.Fingerprint, p.Value.CommittedAtUtc })
        });
        Identity = "cohesive-simulation-reactions/v2/sha256/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>Profile identity pinning host policies, source commits and declared initial environment.</summary>
    public string Identity { get; }

    /// <summary>Exact caller-owned environment recipe identity pinned by this interpreter.</summary>
    public string EnvironmentIdentity { get; }

    /// <summary>Declares the four operations with their shared canonical input and output contracts.</summary>
    /// <param name="scenario">Scenario source being authored.</param>
    /// <returns>The same builder for fluent composition.</returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    public static ScenarioBuilder DeclareOperations(ScenarioBuilder scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        string[] operations = [Deliver, Recover, Inspect, Abandon];
        foreach (var operation in operations)
            scenario.Operation(operation, InputContract, OutputContract);
        return scenario;
    }

    /// <summary>Runs one action at its declared virtual time and projects authoritative lifecycle evidence.</summary>
    /// <param name="context">Canonical scenario action and contracts.</param>
    /// <param name="cancellationToken">Cancels host operations.</param>
    /// <returns>Portable lifecycle evidence without a second copy of entity state in the actor world.</returns>
    /// <exception cref="ArgumentNullException">Context is null.</exception>
    /// <exception cref="InvalidOperationException">Contracts, action order, registration, receipt matching or retained instance evidence are invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">The host denies delivery, inspection, recovery or abandonment.</exception>
    /// <exception cref="OperationCanceledException">Execution is cancelled.</exception>
    public async ValueTask<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SequenceIndex != nextSequence || scenarioFingerprint is not null && scenarioFingerprint != context.Scenario.Fingerprint.Value)
            throw new InvalidOperationException("Use a fresh reaction interpreter for each sequential scenario run.");
        if (context.Operation.Input != InputContract || context.Operation.Output != OutputContract)
            throw new InvalidOperationException("Reaction scenario contracts differ from the declared profile.");
        if (context.Operation.Id is not (Deliver or Recover or Inspect or Abandon))
            throw new InvalidOperationException("Unknown reaction scenario operation.");
        var input = context.Input.Value!.Value;
        var hostAlias = input.Fields![nameof(ReactionScenarioInput.Host)].GetString()!;
        var receiptAlias = input.Fields![nameof(ReactionScenarioInput.Receipt)].GetString()!;
        if (!hosts.TryGetValue(hostAlias, out var host) || !receipts.TryGetValue(receiptAlias, out var receipt))
            throw new InvalidOperationException("The action refers to an unregistered host or receipt.");
        if (nextSequence == 0) ValidateSchedule(context.Scenario);
        var fault = ReadFault(input);
        scenarioFingerprint = context.Scenario.Fingerprint.Value;
        nextSequence++;
        var operation = OperationContext.Create(timeProvider: new ActionClock(context.Action.ScheduledAtUtc), cancellationToken: cancellationToken);
        var key = (hostAlias, receiptAlias);
        if (context.Operation.Id == Deliver)
        {
            var instances = await host.AdmitAsync(operation, receipt).ConfigureAwait(false);
            if (instances.Length != 1) throw new InvalidOperationException("The source receipt must match exactly one reaction movement.");
            admitted[key] = instances[0];
        }
        if (!admitted.TryGetValue(key, out var instance))
            throw new InvalidOperationException("Deliver the selected receipt before advancing or inspecting its reaction.");
        MachineReactionProgress? actionProgress = null;
        if (context.Operation.Id == Recover)
        {
            faults?.Begin(hostAlias, fault);
            var completed = false;
            try
            {
                actionProgress = await host.RecoverAsync(operation, instance).ConfigureAwait(false);
                completed = true;
            }
            finally { faults?.End(requireHit: completed); }
        }
        var inspection = await host.InspectLifecycleAsync(operation, instance).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Admitted reaction is missing from its store.");
        ProcessDurableRuntimeDisposition? controlDisposition = null;
        if (context.Operation.Id == Abandon)
        {
            var commandId = $"simulation/{scenarioFingerprint}/{Uri.EscapeDataString(context.Action.Id)}";
            var command = new CancelProcessCommand(ProcessControlCommand.CurrentSchemaVersion,
                new(new(commandId), new(commandId), instance, new(context.Actor.Id, host.Authority, Identity),
                    operation.UtcNow, host.Policy.Document.Metadata.Provenance),
                new(new(instance, inspection.Control.CurrentAttempt.AttemptId), inspection.Control.Revision), new("simulation.abandon"));
            controlDisposition = (await host.AbandonAsync(operation, command, authorizeAbandon).ConfigureAwait(false)).Disposition;
            inspection = (await host.InspectLifecycleAsync(operation, instance).ConfigureAwait(false))!;
        }
        var progress = actionProgress ?? inspection.Progress;
        var observation = new ReactionScenarioObservation(host.Policy.Document.Metadata.Fingerprint.Value, receipt.Commit.Fingerprint.Value,
            instance.Value, progress.Status, progress.Prerequisite?.Value, progress.Diagnostic?.Code,
            progress.Receipt?.Commit.DecisionKind, progress.Operation is null ? null : JsonSerializer.Serialize(progress.Operation),
            controlDisposition, progress.Attempts.IsDefault ? 0 : progress.Attempts.Length,
            inspection.Episode is null ? null : JsonSerializer.Serialize(inspection.Episode), fault);
        return ScenarioActionResult.Unchanged(PortableValue.Concrete(OutputContract, ObservationValue.FromObject(observation)));
    }

    void ValidateSchedule(ScenarioDefinitionDocument scenario)
    {
        HashSet<(string Host, string Receipt)> delivered = [];
        foreach (var operation in scenario.Definition.Operations)
            if (operation.Input != InputContract || operation.Output != OutputContract)
                throw new InvalidOperationException("Reaction scenario contracts differ from the declared profile.");
        foreach (var action in scenario.Definition.Actions)
        {
            var fields = action.Input.Fields!;
            var host = fields[nameof(ReactionScenarioInput.Host)].GetString()!;
            var receipt = fields[nameof(ReactionScenarioInput.Receipt)].GetString()!;
            var fault = ReadFault(action.Input);
            if (!hosts.ContainsKey(host) || !receipts.ContainsKey(receipt))
                throw new InvalidOperationException("The schedule refers to an unregistered host or receipt.");
            if (action.OperationId is not (Deliver or Recover or Inspect or Abandon))
                throw new InvalidOperationException("Unknown reaction scenario operation.");
            if (fault != ReactionScenarioFault.None && (action.OperationId != Recover || faults?.Contains(host) != true))
                throw new InvalidOperationException("A fault requires a recovery action and an instrumented target alias.");
            if (action.OperationId == Deliver) delivered.Add((host, receipt));
            else if (!delivered.Contains((host, receipt)))
                throw new InvalidOperationException("Deliver the selected receipt before advancing or inspecting its reaction.");
        }
    }

    static ReactionScenarioFault ReadFault(ObservationValue input)
    {
        var name = input.Fields![nameof(ReactionScenarioInput.Fault)].GetString();
        if (!Enum.TryParse<ReactionScenarioFault>(name, out var fault) || !Enum.IsDefined(fault))
            throw new InvalidOperationException("Unknown reaction scenario fault.");
        return fault;
    }

    sealed class ActionClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
