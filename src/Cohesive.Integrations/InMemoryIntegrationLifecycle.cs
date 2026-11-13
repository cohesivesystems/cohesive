using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Integrations.Authoring;
using Cohesive.Processes.Execution;

namespace Cohesive.Integrations;

/// <summary>Thread-safe local reference implementation of revision-fenced admission and drain control.</summary>
/// <remarks>One instance owns one stable consumer. Lifecycle changes and admission linearize under the same lock.
/// Admitted work is never cancelled by this controller: Process control owns suspension/cancellation and current
/// provider policy still gates new effects. This in-memory implementation is not durable or distributed.
/// Authorization is evaluated before the lock; the host owns authentication and policy freshness. Published
/// snapshots are observations, not capabilities that can authorize an effect. No health is inferred from Active.</remarks>
public sealed class InMemoryIntegrationLifecycle
{
    readonly object sync = new();
    readonly IngestionLedgerAddress consumer;
    readonly Func<IntegrationLifecycleAction, ProcessControlAuthorizationContext, bool> authorize;
    readonly Dictionary<string, (IntegrationLifecycleChange Command, IntegrationLifecycleSnapshot Result)> changes = new(StringComparer.Ordinal);
    readonly Dictionary<ProcessContinuationIdentity, IntegrationAdmission> admissions = new();
    readonly Dictionary<ProcessContinuationIdentity, IntegrationCompletion> completed = new();
    long revision;
    IntegrationDesiredState desired = IntegrationDesiredState.Deactivated;
    IntegrationLifecycleChange? lastChange;

    /// <summary>Creates a deactivated local controller for an explicit consumer and host authorization authority.</summary>
    /// <param name="consumer">Stable consumer; every identity component must be nonempty and bounded.</param>
    /// <param name="authorize">Host decision for the requested action; exceptions propagate without mutation.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A consumer identity is invalid.</exception>
    public InMemoryIntegrationLifecycle(IngestionLedgerAddress consumer,
        Func<IntegrationLifecycleAction, ProcessControlAuthorizationContext, bool> authorize)
    {
        ArgumentNullException.ThrowIfNull(consumer); ArgumentNullException.ThrowIfNull(authorize);
        consumer.Validate(); this.consumer = consumer; this.authorize = authorize;
    }

    /// <summary>Inspects current desired state and unresolved admissions without changing either.</summary>
    /// <returns>An immutable snapshot; Pending is ordered by Process instance and attempt identity.</returns>
    public IntegrationLifecycleSnapshot Inspect() { lock (sync) return Snapshot(); }

    /// <summary>Inspects original completion attribution for one exact session.</summary>
    /// <param name="continuation">Exact Process instance and attempt to inspect.</param>
    /// <returns>Original completion evidence, or null when the session has no accepted successful completion.</returns>
    /// <exception cref="ArgumentNullException">Continuation is null.</exception>
    public IntegrationCompletion? InspectCompletion(ProcessContinuationIdentity continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        lock (sync) return completed.GetValueOrDefault(continuation);
    }

    /// <summary>Changes desired state with optimistic revision fencing and exact command replay.</summary>
    /// <param name="command">Attributable command with a stable retry identity.</param>
    /// <param name="snapshot">Accepted original result on replay; otherwise current state.</param>
    /// <returns>Structured authorization, conflict or successful validation.</returns>
    /// <exception cref="ArgumentNullException">Command or its authorization is null.</exception>
    /// <exception cref="ArgumentException">Identity, desired state, revision or UTC timestamp is invalid.</exception>
    public DocumentValidationResult Change(IntegrationLifecycleChange command, out IntegrationLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(command); ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        Validate(command.Authorization, command.AtUtc);
        if (command.ExpectedRevision < 0 || !Enum.IsDefined(command.Desired)) throw new ArgumentException("Invalid lifecycle command.");
        var permitted = authorize(IntegrationLifecycleAction.Change, command.Authorization);
        lock (sync)
        {
            snapshot = Snapshot();
            if (!permitted) return Error("authorization", "Host authorization rejected the lifecycle change.");
            if (changes.TryGetValue(command.Id, out var prior))
            {
                if (prior.Command != command) return Error("command-conflict", "Command identity was reused with different intent.");
                snapshot = prior.Result;
                return Valid();
            }
            if (command.ExpectedRevision != revision) return Error("revision", "Desired-state revision changed; inspect and reconcile intent.");
            if (revision == long.MaxValue) return Error("revision-exhausted", "Lifecycle revision cannot advance.");
            desired = command.Desired; revision++; lastChange = command;
            snapshot = Snapshot(); changes.Add(command.Id, (command, snapshot));
            return Valid();
        }
    }

    /// <summary>Admits a session against the current desired-state revision, or recovers its original admission.</summary>
    /// <remarks>An exact retry after pause returns the original receipt; it is not a new admission. The host must
    /// resume the existing Process and must not treat this receipt as permission to repeat external effects.</remarks>
    /// <param name="compilation">Validated exact integration closure; capability and dispatch checks remain independent.</param>
    /// <param name="continuation">Stable Process session identity.</param>
    /// <param name="expectedRevision">Desired-state revision used for this original admission.</param>
    /// <param name="authorization">Host-authenticated admission context.</param>
    /// <param name="atUtc">Original UTC admission observation, unchanged on retry.</param>
    /// <param name="admission">Original or newly accepted receipt; null on rejection.</param>
    /// <returns>Structured admission decision without dispatching any operation.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Revision or UTC timestamp is invalid.</exception>
    public DocumentValidationResult Admit(IntegrationCompilation compilation, ProcessContinuationIdentity continuation,
        long expectedRevision, ProcessControlAuthorizationContext authorization, DateTimeOffset atUtc, out IntegrationAdmission? admission)
    {
        ArgumentNullException.ThrowIfNull(compilation); ArgumentNullException.ThrowIfNull(continuation);
        Validate(authorization, atUtc);
        if (expectedRevision < 0) throw new ArgumentException("Revision cannot be negative.", nameof(expectedRevision));
        admission = null;
        if (!authorize(IntegrationLifecycleAction.Admit, authorization)) return Error("authorization", "Host authorization rejected admission.");
        var validation = ValidateCompilation(compilation);
        if (!validation.IsValid) return validation;
        var candidate = new IntegrationAdmission(consumer, continuation, compilation.Binding!.Reference, compilation.Process!.Reference,
            expectedRevision, authorization, atUtc);
        lock (sync)
        {
            if (admissions.TryGetValue(continuation, out var prior))
            {
                if (prior != candidate) return Error("admission-conflict", "Session identity already has a different admission.");
                admission = prior; return Valid();
            }
            if (revision != expectedRevision) return Error("revision", "Admission requires the current desired-state revision.");
            if (desired != IntegrationDesiredState.Active) return Error("admission-stopped", "Integration is not accepting new sessions.");
            admissions.Add(continuation, candidate); admission = candidate;
            return Valid();
        }
    }

    /// <summary>Removes a successfully completed exact Process from the draining set, retaining its admission.</summary>
    /// <remarks>Failed, cancelled and unresolved Processes remain pending because terminal status alone cannot prove
    /// that every external effect is resolved. Operator resolution for those cases is deliberately not inferred.</remarks>
    /// <param name="admission">Exact receipt returned by this controller.</param>
    /// <param name="completion">Trusted reference/runtime decision for the admitted Process and continuation.</param>
    /// <param name="authorization">Host-authenticated completion recorder.</param>
    /// <returns>Structured authorization, lineage or completion decision.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public DocumentValidationResult Complete(IntegrationAdmission admission, ProcessActivationDecision completion,
        ProcessControlAuthorizationContext authorization)
    {
        ArgumentNullException.ThrowIfNull(admission); ArgumentNullException.ThrowIfNull(completion); ArgumentNullException.ThrowIfNull(authorization);
        if (!authorize(IntegrationLifecycleAction.Complete, authorization)) return Error("authorization", "Host authorization rejected completion.");
        lock (sync)
        {
            if (!admissions.TryGetValue(admission.Continuation, out var prior) || prior != admission
                || completion.State.Definition != admission.Process || completion.State.Continuation != admission.Continuation)
                return Error("completion-lineage", "Completion does not belong to the exact admitted session.");
            if (completion.Disposition != ProcessActivationDisposition.Completed)
                return Error("unresolved", "Only confirmed successful Process completion can leave the draining set.");
            completed.TryAdd(admission.Continuation, new(admission, completion, authorization));
            return Valid();
        }
    }

    DocumentValidationResult ValidateCompilation(IntegrationCompilation compilation)
    {
        if (!compilation.IsSuccessful) return Error("binding", "A successful integration compilation is required.");
        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(compilation.Documents, out var catalog);
        if (!validation.IsValid) return validation;
        validation = InteractionContractCatalog.TryCreate(compilation.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var contracts);
        if (!validation.IsValid) return validation;
        validation = IngestionProviderBindingDocuments.Inspect(compilation.Binding!, catalog!, contracts!, out var inspected);
        if (!validation.IsValid) return validation;
        if (inspected!.Binding.Consumer != consumer) return Error("consumer", "Binding belongs to another controlled consumer.");
        catalog!.TryResolve(inspected.Binding.Contract.Ingestion, out var ingestion);
        validation = IngestionDefinitionDocuments.TryLower(ingestion!, contracts!, out var process);
        if (!validation.IsValid) return validation;
        return process!.Reference == compilation.Process!.Reference ? Valid() : Error("process", "Process differs from the admitted binding's lowering.");
    }

    IntegrationLifecycleSnapshot Snapshot() => new(consumer, revision, desired, lastChange,
        [.. admissions.Values.Where(a => !completed.ContainsKey(a.Continuation))
            .OrderBy(a => a.Continuation.ProcessInstanceId.Value, StringComparer.Ordinal).ThenBy(a => a.Continuation.ProcessAttemptId.Value, StringComparer.Ordinal)]);
    static void Validate(ProcessControlAuthorizationContext authorization, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (atUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Lifecycle observation must use UTC.", nameof(atUtc));
    }
    static DocumentValidationResult Valid() => new([]);
    static DocumentValidationResult Error(string code, string message) => new([new("integrations.lifecycle." + code, DiagnosticSeverity.Error, message)]);
}
