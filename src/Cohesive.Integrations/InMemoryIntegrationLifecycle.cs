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
    readonly Dictionary<string, IntegrationLifecycleChangeReceipt> changes = new(StringComparer.Ordinal);
    readonly Dictionary<ProcessContinuationIdentity, IntegrationAdmission> pending = new();
    readonly Dictionary<ProcessContinuationIdentity, IntegrationCompletion> completed = new();
    long revision;
    IntegrationDesiredState desired = IntegrationDesiredState.Paused;
    IntegrationLifecycleChange? lastChange;

    /// <summary>Creates a paused local controller for an explicit consumer and host authorization authority.</summary>
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

    /// <summary>Validates and binds an immutable compilation once, outside the admission path.</summary>
    /// <param name="compilation">Exact closure for this consumer; qualification and dispatch policy remain independent.</param>
    /// <returns>A controller-owned token reusable across admissions, retries and stopped-state checks.</returns>
    /// <exception cref="ArgumentNullException">Compilation is null.</exception>
    /// <exception cref="ArgumentException">The compilation, consumer or lowered Process is invalid.</exception>
    public IntegrationAdmissionBinding Bind(IntegrationCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var validation = ValidateCompilation(compilation);
        if (!validation.IsValid) throw new ArgumentException(validation.Diagnostics[0].Message, nameof(compilation));
        return new(this, compilation.Binding!.Reference, compilation.Process!.Reference);
    }

    /// <summary>Changes desired state with revision fencing and compact exact command replay.</summary>
    /// <param name="request">Attributable command with stable retry identity.</param>
    /// <returns>A typed disposition and original compact receipt on acceptance; never constructs a pending snapshot.</returns>
    /// <exception cref="ArgumentNullException">Request or authorization is null.</exception>
    /// <exception cref="ArgumentException">Identity, desired state, revision or UTC timestamp is invalid.</exception>
    public IntegrationLifecycleResult<IntegrationLifecycleChangeReceipt> Change(IntegrationLifecycleChange request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        Validate(request.Authorization, request.AtUtc);
        if (request.ExpectedRevision < 0 || !Enum.IsDefined(request.Desired)) throw new ArgumentException("Invalid lifecycle command.");
        if (!authorize(IntegrationLifecycleAction.Change, request.Authorization)) return new(IntegrationLifecycleOutcome.Unauthorized);
        lock (sync)
        {
            if (changes.TryGetValue(request.Id, out var prior))
                return prior.Command == request ? new(IntegrationLifecycleOutcome.Replayed, prior) : new(IntegrationLifecycleOutcome.IdentityConflict);
            if (request.ExpectedRevision != revision) return new(IntegrationLifecycleOutcome.RevisionConflict);
            if (revision == long.MaxValue) return new(IntegrationLifecycleOutcome.RevisionExhausted);
            desired = request.Desired; revision++; lastChange = request;
            var receipt = new IntegrationLifecycleChangeReceipt(request, revision);
            changes.Add(request.Id, receipt);
            return new(IntegrationLifecycleOutcome.Applied, receipt);
        }
    }

    /// <summary>Admits a session using a prevalidated token, or retrieves its original admission.</summary>
    /// <remarks>Exact replay after pause or completion is not permission to repeat external effects.
    /// Admission performs no catalog construction, binding inspection, lowering or history enumeration.</remarks>
    /// <param name="request">Complete original intent, including the controller-owned binding token.</param>
    /// <returns>Typed disposition with the original receipt on acceptance or replay.</returns>
    /// <exception cref="ArgumentNullException">A required request field is null.</exception>
    /// <exception cref="ArgumentException">Revision or UTC timestamp is invalid.</exception>
    public IntegrationLifecycleResult<IntegrationAdmission> Admit(IntegrationAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Binding); ArgumentNullException.ThrowIfNull(request.Continuation);
        Validate(request.Authorization, request.AtUtc);
        if (request.ExpectedRevision < 0) throw new ArgumentException("Revision cannot be negative.", nameof(request));
        if (!authorize(IntegrationLifecycleAction.Admit, request.Authorization)) return new(IntegrationLifecycleOutcome.Unauthorized);
        if (!ReferenceEquals(request.Binding.Owner, this)) return new(IntegrationLifecycleOutcome.BindingMismatch);
        lock (sync)
        {
            var prior = pending.GetValueOrDefault(request.Continuation) ?? completed.GetValueOrDefault(request.Continuation)?.Admission;
            if (prior is not null)
            {
                var matches = prior.Binding == request.Binding.Binding && prior.Process == request.Binding.Process
                    && prior.LifecycleRevision == request.ExpectedRevision && prior.Authorization == request.Authorization && prior.AtUtc == request.AtUtc;
                return matches ? new(IntegrationLifecycleOutcome.Replayed, prior) : new(IntegrationLifecycleOutcome.IdentityConflict);
            }
            if (revision != request.ExpectedRevision) return new(IntegrationLifecycleOutcome.RevisionConflict);
            if (desired != IntegrationDesiredState.Active) return new(IntegrationLifecycleOutcome.AdmissionStopped);
            var receipt = new IntegrationAdmission(consumer, request.Continuation, request.Binding.Binding, request.Binding.Process,
                request.ExpectedRevision, request.Authorization, request.AtUtc);
            pending.Add(request.Continuation, receipt);
            return new(IntegrationLifecycleOutcome.Applied, receipt);
        }
    }

    /// <summary>Moves a successfully completed exact Process out of the pending set, retaining its original evidence.</summary>
    /// <remarks>Failed/cancelled/unresolved work stays pending. A later authorized recorder receives
    /// ReplayedWithOriginalRecorder and the first completion receipt; attribution is never silently replaced.</remarks>
    /// <param name="request">Exact admission, trusted completion evidence and authenticated recorder.</param>
    /// <returns>A typed disposition and original completion receipt on acceptance or replay.</returns>
    /// <exception cref="ArgumentNullException">A required request field is null.</exception>
    public IntegrationLifecycleResult<IntegrationCompletion> Complete(IntegrationCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Admission);
        ArgumentNullException.ThrowIfNull(request.Decision); ArgumentNullException.ThrowIfNull(request.Authorization);
        if (!authorize(IntegrationLifecycleAction.Complete, request.Authorization)) return new(IntegrationLifecycleOutcome.Unauthorized);
        var admission = request.Admission;
        var decision = request.Decision;
        lock (sync)
        {
            var prior = pending.GetValueOrDefault(admission.Continuation) ?? completed.GetValueOrDefault(admission.Continuation)?.Admission;
            if (prior != admission || decision.State.Definition != admission.Process || decision.State.Continuation != admission.Continuation)
                return new(IntegrationLifecycleOutcome.CompletionMismatch);
            if (decision.Disposition != ProcessActivationDisposition.Completed) return new(IntegrationLifecycleOutcome.Unresolved);
            if (completed.TryGetValue(admission.Continuation, out var original))
                return new(original.Authorization == request.Authorization ? IntegrationLifecycleOutcome.Replayed
                    : IntegrationLifecycleOutcome.ReplayedWithOriginalRecorder, original);
            var receipt = new IntegrationCompletion(admission, decision, request.Authorization);
            completed.Add(admission.Continuation, receipt);
            pending.Remove(admission.Continuation);
            return new(IntegrationLifecycleOutcome.Applied, receipt);
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
        [.. pending.Values
            .OrderBy(a => a.Continuation.ProcessInstanceId.Value, StringComparer.Ordinal).ThenBy(a => a.Continuation.ProcessAttemptId.Value, StringComparer.Ordinal)]);
    static void Validate(ProcessControlAuthorizationContext authorization, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (atUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Lifecycle observation must use UTC.", nameof(atUtc));
    }
    static DocumentValidationResult Valid() => new([]);
    static DocumentValidationResult Error(string code, string message) => new([new("integrations.lifecycle." + code, DiagnosticSeverity.Error, message)]);
}
