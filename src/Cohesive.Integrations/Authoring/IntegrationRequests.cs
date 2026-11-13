using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Integrations.Authoring;

/// <summary>Named authoring policy lowered into native Request response obligations.</summary>
/// <remarks>The bounded profile uses unsupported timeout/cancellation, observed late results,
/// rejected stale results and escalation for unresolved outcomes. Retention declares a response
/// obligation; the runtime must qualify how evidence is durably retained.</remarks>
public sealed class IntegrationRecovery
{
    internal IntegrationAuthoringContext Owner { get; }
    bool stableIdentity;
    bool reconcile;
    bool reuse;
    TimeSpan retention;
    internal IntegrationRecovery(IntegrationAuthoringContext owner) => Owner = owner;

    /// <summary>Requires stable logical identity for retries.</summary>
    /// <returns>This policy builder.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public IntegrationRecovery StableRequestIdentity() { Owner.Mutable(); stableIdentity = true; return this; }
    /// <summary>Requires reconciliation after an ambiguous external outcome.</summary>
    /// <returns>This policy builder.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public IntegrationRecovery ReconcileAmbiguousOutcomes() { Owner.Mutable(); reconcile = true; return this; }
    /// <summary>Requires reuse of a duplicate result's original disposition.</summary>
    /// <returns>This policy builder.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public IntegrationRecovery ReusePriorDisposition() { Owner.Mutable(); reuse = true; return this; }
    /// <summary>Declares the minimum response-obligation retention horizon.</summary>
    /// <param name="horizon">Positive retention duration.</param>
    /// <returns>This policy builder.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The horizon is not positive.</exception>
    public IntegrationRecovery RetainEvidenceFor(TimeSpan horizon)
    {
        Owner.Mutable();
        if (horizon <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(horizon));
        retention = horizon;
        return this;
    }

    internal RequestProtocolResponsePolicy Lower()
    {
        if (!stableIdentity || !reconcile || !reuse || retention <= TimeSpan.Zero)
            throw new InvalidOperationException("Declare stable retry identity, ambiguity reconciliation, duplicate disposition and evidence retention.");
        return new(RequestOptionalTerminalSemantics.Unsupported, RequestOptionalTerminalSemantics.Unsupported,
            RequestResultDisposition.Observe, RequestResultDisposition.Reject, RequestResultDisposition.ReusePriorDisposition,
            RequestRetrySemantics.StableIdentity, RequestResolutionSemantics.Reconcile, RequestResolutionSemantics.Escalate, retention);
    }
}

/// <summary>Starts one typed Request before its successful result is declared.</summary>
/// <typeparam name="TInput">Request payload type.</typeparam>
public sealed class RequestBuilder<TInput>
{
    readonly IntegrationAuthoringContext owner;
    readonly string name;
    internal RequestBuilder(IntegrationAuthoringContext owner, string name) { this.owner = owner; this.name = name; }

    /// <summary>Declares the bounded ingestion profile's single successful result.</summary>
    /// <typeparam name="TResult">Successful result payload type.</typeparam>
    /// <param name="name">Stable result identity.</param>
    /// <returns>A typed Request handle supporting failures and recovery policy.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen or the result is already declared.</exception>
    /// <exception cref="ArgumentException">The result name is empty.</exception>
    public RequestHandle<TInput, TResult> Result<TResult>(string name)
    {
        owner.Mutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (owner.Requests[this.name] is not null) throw new InvalidOperationException("A successful result is already declared.");
        var handle = new RequestHandle<TInput, TResult>(owner, this.name, name);
        owner.Requests[this.name] = handle;
        return handle;
    }
}

/// <summary>Untyped linkage of a typed Request within one authoring model.</summary>
public abstract class IntegrationRequest
{
    internal IntegrationAuthoringContext Owner { get; }
    internal string Name { get; }
    internal string ResultName { get; }
    /// <summary>Initializes a source handle owned by this assembly's typed request builder.</summary>
    /// <param name="owner">Model owning the declaration.</param>
    /// <param name="name">Local Request identity.</param>
    /// <param name="resultName">Successful result identity.</param>
    private protected IntegrationRequest(IntegrationAuthoringContext owner, string name, string resultName)
    { Owner = owner; Name = name; ResultName = resultName; }
    internal abstract (RequestContractReference Request, ImmutableArray<ExecutionDefinitionDocument> Documents) Lower();
}

/// <summary>Typed source handle whose native Request/Reply protocol is materialized during compilation.</summary>
/// <typeparam name="TInput">Request payload type.</typeparam>
/// <typeparam name="TResult">Successful result type.</typeparam>
public sealed class RequestHandle<TInput, TResult> : IntegrationRequest
{
    readonly List<Action<RequestProtocolOutcomeBuilder>> failures = [];
    IntegrationRecovery? recovery;
    internal RequestHandle(IntegrationAuthoringContext owner, string name, string resultName) : base(owner, name, resultName) { }

    /// <summary>Adds a declared terminal failure.</summary>
    /// <typeparam name="TFailure">Failure payload type.</typeparam>
    /// <param name="name">Stable terminal outcome identity.</param>
    /// <returns>This Request handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">The outcome identity is empty.</exception>
    public RequestHandle<TInput, TResult> Failure<TFailure>(string name)
    {
        Owner.Mutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        failures.Add(builder => builder.Failure<TFailure>(new(name), Owner.Schema<TFailure>()));
        return this;
    }

    /// <summary>Attaches the explicit response recovery policy.</summary>
    /// <param name="policy">Policy declared in this model.</param>
    /// <returns>This Request handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">The policy belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Policy is null.</exception>
    public RequestHandle<TInput, TResult> Recovery(IntegrationRecovery policy)
    {
        Owner.Mutable();
        ArgumentNullException.ThrowIfNull(policy);
        Owner.Owns(policy.Owner);
        recovery = policy;
        return this;
    }

    internal override (RequestContractReference Request, ImmutableArray<ExecutionDefinitionDocument> Documents) Lower()
    {
        if (recovery is null) throw new InvalidOperationException($"Request '{Name}' needs an explicit recovery policy.");
        var protocol = InteractionContractAuthoring.CreateRequestProtocol<TInput, RequestProtocolOutcome<TResult>>(
            Owner.Identity("requests/" + Name), Owner.Revision, Owner.Schema<TInput>(), builder =>
            {
                var result = builder.Result<TResult>(new(ResultName), Owner.Schema<TResult>());
                foreach (var failure in failures) failure(builder);
                return result;
            }, recovery.Lower(), Owner.Origin("requests/" + Name));
        if (!protocol.IsValid) throw new InvalidOperationException(string.Join("\n", protocol.Validation.Diagnostics.Select(d => d.Message)));
        return (protocol.Request, protocol.Documents);
    }
}
