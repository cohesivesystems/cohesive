using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Authoring;

/// <summary>Response and recovery policy used when a Request joins one child Process.</summary>
/// <remarks>
/// Terminal outcomes are deliberately absent: they are derived from the child-terminal mapping when the
/// protocol is authored. The resulting <see cref="RequestResponseObligation"/> remains the persisted authority.
/// Request-level timeout and cancellation outcomes are unsupported by this four-state child protocol; child
/// cancellation is represented separately by the cancelled terminal mapping and invocation cancellation policy.
/// </remarks>
public sealed class ProcessInvocationResponsePolicy
{
    /// <summary>Creates an explicit child-Process response policy.</summary>
    /// <param name="lateResult">Disposition for a result arriving after logical completion.</param>
    /// <param name="staleResult">Disposition for a result targeting incompatible continuation state.</param>
    /// <param name="duplicateResult">Disposition for a repeated logical result.</param>
    /// <param name="retry">Semantic retry precondition.</param>
    /// <param name="ambiguousOutcome">Required resolution after an ambiguous external outcome.</param>
    /// <param name="unresolvedOutcome">Required resolution for an unresolved response obligation.</param>
    /// <param name="retentionHorizon">Minimum duration for which the response obligation remains addressable.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A policy is unspecified or unsupported, or <paramref name="retentionHorizon"/> is not positive.
    /// </exception>
    public ProcessInvocationResponsePolicy(
        RequestResultDisposition lateResult,
        RequestResultDisposition staleResult,
        RequestResultDisposition duplicateResult,
        RequestRetrySemantics retry,
        RequestResolutionSemantics ambiguousOutcome,
        RequestResolutionSemantics unresolvedOutcome,
        TimeSpan retentionHorizon)
    {
        ValidatePolicy(lateResult, nameof(lateResult));
        ValidatePolicy(staleResult, nameof(staleResult));
        ValidatePolicy(duplicateResult, nameof(duplicateResult));
        ValidatePolicy(retry, nameof(retry));
        ValidatePolicy(ambiguousOutcome, nameof(ambiguousOutcome));
        ValidatePolicy(unresolvedOutcome, nameof(unresolvedOutcome));
        if (retentionHorizon <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionHorizon),
                retentionHorizon,
                "A child Process response policy requires a positive retention horizon.");
        }

        LateResult = lateResult;
        StaleResult = staleResult;
        DuplicateResult = duplicateResult;
        Retry = retry;
        AmbiguousOutcome = ambiguousOutcome;
        UnresolvedOutcome = unresolvedOutcome;
        RetentionHorizon = retentionHorizon;
    }

    /// <summary>Disposition for results arriving after logical completion.</summary>
    public RequestResultDisposition LateResult { get; }

    /// <summary>Disposition for results targeting incompatible continuation state.</summary>
    public RequestResultDisposition StaleResult { get; }

    /// <summary>Disposition for repeated logical results.</summary>
    public RequestResultDisposition DuplicateResult { get; }

    /// <summary>Semantic retry precondition.</summary>
    public RequestRetrySemantics Retry { get; }

    /// <summary>Required resolution for an ambiguous external outcome.</summary>
    public RequestResolutionSemantics AmbiguousOutcome { get; }

    /// <summary>Required resolution for an otherwise unresolved obligation.</summary>
    public RequestResolutionSemantics UnresolvedOutcome { get; }

    /// <summary>Minimum duration for which the response obligation remains addressable.</summary>
    public TimeSpan RetentionHorizon { get; }

    /// <summary>Creates the standard durable join policy for a child Process.</summary>
    /// <param name="retentionHorizon">Minimum duration for which the join remains addressable.</param>
    /// <returns>
    /// A policy that observes late results, rejects stale results, reuses duplicate dispositions, and reconciles
    /// ambiguous or unresolved outcomes before retry or completion.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retentionHorizon"/> is not positive.
    /// </exception>
    public static ProcessInvocationResponsePolicy ReconciledJoin(TimeSpan retentionHorizon) => new(
        RequestResultDisposition.Observe,
        RequestResultDisposition.Reject,
        RequestResultDisposition.ReusePriorDisposition,
        RequestRetrySemantics.ReconcileBeforeRetry,
        RequestResolutionSemantics.Reconcile,
        RequestResolutionSemantics.Reconcile,
        retentionHorizon);

    internal RequestResponseObligation CreateObligation(
        ImmutableArray<RequestTerminalOutcomeDefinition> terminalOutcomes) => new(
        terminalOutcomes,
        RequestOptionalTerminalSemantics.Unsupported,
        RequestOptionalTerminalSemantics.Unsupported,
        LateResult,
        StaleResult,
        DuplicateResult,
        Retry,
        AmbiguousOutcome,
        UnresolvedOutcome,
        RetentionHorizon);

    static void ValidatePolicy<TPolicy>(TPolicy policy, string parameterName)
        where TPolicy : struct, Enum
    {
        if (!Enum.IsDefined(policy)
            || Convert.ToInt32(policy, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                policy,
                "A child Process response policy must be explicitly declared.");
        }
    }
}

/// <summary>Typed handle for the canonical Request/Reply protocol that invokes one exact child Process.</summary>
/// <remarks>
/// <see cref="Process"/> is the authority for the child reference and portable input/result contracts. The
/// request and reply documents are the sole durable interaction-contract authority; the remaining properties are
/// typed projections used by Process authoring, linking, and host registration.
/// </remarks>
/// <typeparam name="TInput">CLR authoring type of the child Process input.</typeparam>
/// <typeparam name="TResult">CLR authoring type of the child Process terminal result.</typeparam>
public sealed class ProcessInvocationProtocol<TInput, TResult>
{
    internal ProcessInvocationProtocol(
        Process<TInput, TResult> process,
        ProcessChildOutcomeMapping outcomeMapping,
        ExecutionDefinitionDocument requestDocument,
        RequestContractReference request,
        ReplyContractReference completedReply,
        ReplyContractReference failedReply,
        ReplyContractReference cancelledReply,
        ReplyContractReference terminatedReply,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        InteractionContractCatalog catalog)
    {
        Process = process;
        OutcomeMapping = outcomeMapping;
        RequestDocument = requestDocument;
        Request = request;
        CompletedReply = completedReply;
        FailedReply = failedReply;
        CancelledReply = cancelledReply;
        TerminatedReply = terminatedReply;
        Documents = documents;
        Catalog = catalog;
    }

    /// <summary>Exact canonical child Process from which input, result, and definition evidence was derived.</summary>
    public Process<TInput, TResult> Process { get; }

    /// <summary>Total mapping from child terminal states to Request terminal outcomes.</summary>
    public ProcessChildOutcomeMapping OutcomeMapping { get; }

    /// <summary>Canonical child-invocation Request document.</summary>
    public ExecutionDefinitionDocument RequestDocument { get; }

    /// <summary>Exact child-invocation Request reference.</summary>
    public RequestContractReference Request { get; }

    /// <summary>Exact Reply emitted when the child Process completes successfully.</summary>
    public ReplyContractReference CompletedReply { get; }

    /// <summary>Exact Reply emitted when the child Process fails.</summary>
    public ReplyContractReference FailedReply { get; }

    /// <summary>Exact Reply emitted when the child Process is cancelled.</summary>
    public ReplyContractReference CancelledReply { get; }

    /// <summary>Exact Reply emitted when the child Process is forcibly terminated.</summary>
    public ReplyContractReference TerminatedReply { get; }

    /// <summary>Request followed by completed, failed, cancelled, and terminated Reply documents.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>Validated exact-reference catalog containing every generated interaction document.</summary>
    public InteractionContractCatalog Catalog { get; }
}

/// <summary>Authors canonical child-Process invocation protocols from typed Process handles.</summary>
public static class ProcessInvocationProtocolAuthoring
{
    /// <summary>Authors the Request/Reply protocol for invoking an exact canonical child Process.</summary>
    /// <typeparam name="TInput">CLR authoring type of the child Process input.</typeparam>
    /// <typeparam name="TResult">CLR authoring type of the child Process terminal result.</typeparam>
    /// <param name="process">Exact valid child Process using continue-attempt recovery.</param>
    /// <param name="requestDefinitionId">Stable identity shared by revisions of the invocation Request.</param>
    /// <param name="requestRevisionId">Exact semantic revision of the invocation Request.</param>
    /// <param name="responsePolicy">Explicit response, retry, reconciliation, and retention policy.</param>
    /// <param name="provenance">Producer and root-source attribution for the generated protocol documents.</param>
    /// <param name="inputSchemaRevision">
    /// Optional input schema revision; defaults deterministically from the Request identity and revision.
    /// </param>
    /// <param name="resultSchemaRevision">
    /// Optional result schema revision; defaults deterministically from the Request identity and revision.
    /// </param>
    /// <param name="outcomeMapping">
    /// Optional total terminal mapping; defaults to completed, failed, cancelled, and terminated.
    /// </param>
    /// <param name="replyDefinitionPrefix">
    /// Optional stable Reply identity prefix; defaults to the Request identity followed by <c>/reply</c>.
    /// </param>
    /// <param name="replyRevisionId">Optional Reply revision; defaults to the Request revision.</param>
    /// <returns>
    /// A typed handle containing the exact child Process, canonical interaction documents, references, mapping,
    /// and validated interaction catalog.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="process"/>, <paramref name="responsePolicy"/>, or <paramref name="provenance"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or schema revision is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// The Process is invalid, does not use continue-attempt recovery, or generated interaction documents do not
    /// form a valid exact-reference catalog.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical Process or interaction content cannot be decoded or encoded by the strict wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    public static ProcessInvocationProtocol<TInput, TResult> InvocationProtocol<TInput, TResult>(
        this Process<TInput, TResult> process,
        ExecutionDefinitionId requestDefinitionId,
        ExecutionRevisionId requestRevisionId,
        ProcessInvocationResponsePolicy responsePolicy,
        ExecutionProvenance provenance,
        InteractionValueSchemaRevision? inputSchemaRevision = null,
        InteractionValueSchemaRevision? resultSchemaRevision = null,
        ProcessChildOutcomeMapping? outcomeMapping = null,
        ExecutionDefinitionId? replyDefinitionPrefix = null,
        ExecutionRevisionId? replyRevisionId = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(responsePolicy);
        ArgumentNullException.ThrowIfNull(provenance);
        if (!process.IsValid)
        {
            throw new InvalidOperationException(
                "A child invocation protocol requires a valid Process: "
                + Format(process.Validation));
        }

        var definition = process.Definition;
        if (definition.RecoveryPolicy != ProcessRecoveryPolicy.ContinueAttempt)
        {
            throw new InvalidOperationException(
                "A child invocation protocol requires Process recovery policy ContinueAttempt, but observed "
                + $"{definition.RecoveryPolicy}.");
        }

        var mapping = outcomeMapping ?? new(
            new("completed"),
            new("failed"),
            new("cancelled"),
            new("terminated"));
        var inputSchema = new InteractionValueSchema(
            definition.Input,
            inputSchemaRevision ?? new($"{requestDefinitionId.Value}/input/{requestRevisionId.Value}"));
        var resultSchema = new InteractionValueSchema(
            definition.Result,
            resultSchemaRevision ?? new($"{requestDefinitionId.Value}/result/{requestRevisionId.Value}"));
        ImmutableArray<RequestTerminalOutcomeDefinition> terminalOutcomes =
        [
            new RequestResultDefinition(mapping.Completed, resultSchema),
            new RequestFailureDefinition(mapping.Failed, resultSchema),
            new RequestFailureDefinition(mapping.Cancelled, resultSchema),
            new RequestFailureDefinition(mapping.Terminated, resultSchema)
        ];
        var requestDocument = InteractionContractDocuments.Create(
            requestDefinitionId,
            requestRevisionId,
            new RequestContractDefinition(
                inputSchema,
                responsePolicy.CreateObligation(terminalOutcomes)),
            provenance);
        RequestContractReference request = new(Reference(requestDocument));
        var replyPrefix = replyDefinitionPrefix ?? new($"{requestDefinitionId.Value}/reply");
        var exactReplyRevision = replyRevisionId ?? requestRevisionId;
        var completedDocument = ReplyDocument(
            replyPrefix,
            exactReplyRevision,
            request,
            mapping.Completed,
            provenance);
        var failedDocument = ReplyDocument(
            replyPrefix,
            exactReplyRevision,
            request,
            mapping.Failed,
            provenance);
        var cancelledDocument = ReplyDocument(
            replyPrefix,
            exactReplyRevision,
            request,
            mapping.Cancelled,
            provenance);
        var terminatedDocument = ReplyDocument(
            replyPrefix,
            exactReplyRevision,
            request,
            mapping.Terminated,
            provenance);
        ImmutableArray<ExecutionDefinitionDocument> documents =
        [
            requestDocument,
            completedDocument,
            failedDocument,
            cancelledDocument,
            terminatedDocument
        ];
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        if (!validation.IsValid || catalog is null)
        {
            throw new InvalidOperationException(
                "Generated child invocation interaction contracts are invalid: " + Format(validation));
        }

        return new(
            process,
            mapping,
            requestDocument,
            request,
            new(Reference(completedDocument)),
            new(Reference(failedDocument)),
            new(Reference(cancelledDocument)),
            new(Reference(terminatedDocument)),
            documents,
            catalog);
    }

    static ExecutionDefinitionDocument ReplyDocument(
        ExecutionDefinitionId prefix,
        ExecutionRevisionId revision,
        RequestContractReference request,
        RequestTerminalOutcomeId outcome,
        ExecutionProvenance provenance) => InteractionContractDocuments.Create(
        new($"{prefix.Value}/{outcome.Value}"),
        revision,
        new ReplyContractDefinition(request, outcome),
        provenance);

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static string Format(DocumentValidationResult validation) => string.Join(
        "; ",
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
