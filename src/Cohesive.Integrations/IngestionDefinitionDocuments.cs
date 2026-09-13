using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Integrations;

/// <summary>Shared document persistence and target-independent lowering for the bounded ingestion profile.</summary>
/// <remarks>Successful lowering proves typed sequencing only. Runtime realization must separately qualify atomic
/// publication, scoped idempotency, retained input/receipts, source completeness, and settlement behavior.</remarks>
public static class IngestionDefinitionDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<IngestionDefinition> Projection = new(
        new("integration.ingestion.atomic.v1"), "integrations.ingestion.kind",
        "integrations.ingestion.projection", "integrations.ingestion.wire",
        "The definition must use the canonical bounded ingestion wire representation.");
    static readonly ExecutionDefinitionDocumentProjection<IngestionDefinition> LedgerProjection = new(
        new("integration.ingestion.separate-ledger.v1"), "integrations.ingestion.kind",
        "integrations.ingestion.projection", "integrations.ingestion.wire",
        "The definition must use the canonical separate-ledger ingestion wire representation.");
    static readonly ValueContract CompletionContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));

    /// <summary>Exact execution-document kind of this bounded ingestion profile.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Exact kind for publication followed by a separate recoverable ledger advancement and settlement.</summary>
    public static ExecutionDefinitionKind SeparateLedgerKind => LedgerProjection.Kind;

    static ExecutionDefinitionDocumentProjection<IngestionDefinition> For(ExecutionDefinitionDocument? document) =>
        document?.Kind == SeparateLedgerKind ? LedgerProjection : Projection;

    /// <summary>Creates a fingerprinted ingestion declaration using the existing execution document envelope.</summary>
    /// <param name="definitionId">Stable application-binding identity; distinct consumers use distinct identities.</param>
    /// <param name="revisionId">Exact accepted semantic revision.</param>
    /// <param name="definition">Complete Request composition.</param>
    /// <param name="provenance">Producer and source attribution.</param>
    /// <returns>A canonical execution document; use TryLower to validate linked contracts.</returns>
    /// <exception cref="ArgumentNullException">Definition or provenance is null.</exception>
    /// <exception cref="ArgumentException">An identity or shared document field is invalid.</exception>
    public static ExecutionDefinitionDocument Create(ExecutionDefinitionId definitionId, ExecutionRevisionId revisionId,
        IngestionDefinition definition, ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(provenance);
        return ExecutionDefinitionDocument.Create(definition.AdvanceLedger is null ? Kind : SeparateLedgerKind, definitionId, revisionId, definition, provenance);
    }

    /// <summary>Reads and validates the strict shared envelope and typed declaration without resolving Requests.</summary>
    /// <param name="json">Persisted execution-document JSON.</param>
    /// <param name="document">Receives the parsed document when the envelope can be read.</param>
    /// <param name="definition">Receives the declaration only when structural validation succeeds.</param>
    /// <returns>Shared integrity, wire, and declaration diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(string json, out ExecutionDefinitionDocument? document,
        out IngestionDefinition? definition)
    {
        var validation = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        var kind = document?.Kind ?? Kind;
        return For(document).ValidateAndProject(validation, document, value => Validate(value, kind), out definition);
    }

    /// <summary>Lowers the declaration to existing durable Request nodes and explicit outcome routing.</summary>
    /// <param name="document">Exact persisted ingestion declaration.</param>
    /// <param name="contracts">Catalog resolving the exact Request identities, revisions, fingerprints, and shapes.</param>
    /// <param name="process">Receives the canonical Process document only when all structural and link checks pass.</param>
    /// <returns>Structured diagnostics; success is not physical target qualification.</returns>
    /// <exception cref="ArgumentNullException">Document or catalog is null.</exception>
    public static DocumentValidationResult TryLower(ExecutionDefinitionDocument document, InteractionContractCatalog contracts,
        out ExecutionDefinitionDocument? process)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(contracts);
        process = null;
        var validation = For(document).ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document),
            document, value => Validate(value, document.Kind), out var definition);
        if (!validation.IsValid) return validation;
        if (!document.Extensions.IsDefaultOrEmpty)
            return Error("extension", "This bounded profile cannot interpret semantic extensions.", "/extensions");

        (string Role, RequestContractReference Request)[] steps = definition!.AdvanceLedger is null
            ? [("acquire", definition.Acquire), ("publish", definition.Publish), ("settle", definition.Settle)]
            : [("acquire", definition.Acquire), ("publish", definition.Publish), ("advanceLedger", definition.AdvanceLedger), ("settle", definition.Settle)];
        var requests = new RequestContractDefinition[steps.Length];
        var successes = new RequestResultDefinition[steps.Length];
        for (var index = 0; index < steps.Length; index++)
        {
            var location = "/definition/" + steps[index].Role;
            var linked = contracts.ValidateReference(steps[index].Request.Definition, location, out var resolved);
            if (!linked.IsValid) return linked;
            if (resolved is not RequestContractDefinition request)
                return Error("request", "The operation must resolve to a Request contract.", location);
            RequestResultDefinition? success = null;
            foreach (var outcome in request.Response.TerminalOutcomes)
            {
                if (outcome is not RequestResultDefinition result) continue;
                if (success is not null)
                    return Error("result", "The bounded profile requires exactly one successful Request result.", location);
                success = result;
            }
            if (success is null)
                return Error("result", "The bounded profile requires a successful Request result.", location);
            if (request.Response.Retry != RequestRetrySemantics.StableIdentity
                || request.Response.AmbiguousOutcome != RequestResolutionSemantics.Reconcile
                || request.Response.DuplicateResult != RequestResultDisposition.ReusePriorDisposition)
                return Error("recovery", "Declare stable retry identity, ambiguity reconciliation, and reuse of duplicate outcomes; then qualify those requirements against the adapter.", location);
            if (index > 0 && successes[index - 1].Schema != request.Payload)
                return Error("payload", "The preceding result schema and revision must exactly match this Request payload; use an explicit mapping in the operation rather than an implicit conversion.", location);
            requests[index] = request;
            successes[index] = success;
        }

        var nodes = ImmutableArray.CreateBuilder<ProcessNode>();
        var hasFailureBranch = false;
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index].Role;
            var branches = ImmutableArray.CreateBuilder<ProcessRequestOutcomeBranch>(requests[index].Response.TerminalOutcomes.Length);
            foreach (var outcome in requests[index].Response.TerminalOutcomes)
            {
                var accepted = outcome.Id == successes[index].Id;
                hasFailureBranch |= !accepted;
                var branch = step + "/" + outcome.Id.Value;
                var target = accepted ? (index + 1 < steps.Length ? steps[index + 1].Role : "complete") : "failed";
                var edge = new ProcessEdge(new(branch + "/next"), new(target));
                var output = new ProcessOutputBinding(new(branch), outcome.Schema.Contract);
                branches.Add(new(new(branch), outcome.Id, new(edge, output)));
            }
            var input = index == 0 ? ProcessBindingIds.Input : new ValueBindingId(steps[index - 1].Role + "/" + successes[index - 1].Id.Value);
            nodes.Add(new RequestProcessNode(new(step), steps[index].Request, Expr.BoundValue(input), branches.MoveToImmutable()));
        }
        nodes.Add(new ReturnProcessNode(new("complete"), Expr.Const(true)));
        if (hasFailureBranch)
            nodes.Add(new FailProcessNode(new("failed"), Expr.Const(false)));
        var graph = new ProcessDefinition(requests[0].Payload.Contract, CompletionContract, new(steps[0].Role),
            nodes.ToImmutable(), ProcessRecoveryPolicy.ContinueAttempt);
        var source = $"ingestion:{Uri.EscapeDataString(document.Metadata.DefinitionId.Value)}/{Uri.EscapeDataString(document.Metadata.RevisionId.Value)}/{document.Metadata.Fingerprint.Value}";
        var mappings = ImmutableArray.CreateBuilder<ExecutionSourceProvenance>(steps.Length);
        for (var index = 0; index < graph.Nodes.Length; index++)
        {
            if (graph.Nodes[index] is RequestProcessNode request)
                mappings.Add(new(source + "#/definition/" + request.Id.Value,
                    new(["nodes", index.ToString(CultureInfo.InvariantCulture)])));
        }
        var candidate = ProcessDefinitionDocuments.Create(
            new(document.Metadata.DefinitionId.Value + "/process"), document.Metadata.RevisionId, graph,
            new(new("cohesive.integrations.ingestion", "1"), new(source), DocumentOrigin.Generated),
            sourceMap: new(mappings.MoveToImmutable()));
        var graphValidation = ProcessDefinitionDocuments.Validate(candidate,
            new ProcessDefinitionValidationContext(interactionContracts: contracts));
        if (!graphValidation.IsValid) return graphValidation;
        process = candidate;
        return new([.. graphValidation.Diagnostics,
            new("integrations.ingestion.realization.unqualified", DiagnosticSeverity.Warning,
                "Typed sequencing is valid, but no physical realization has been qualified. Validate atomic publication, scoped idempotency, retention, source completeness, and settlement before execution. Separate-ledger bindings additionally require receipt-before-CAS reconciliation and publication-before-progress ordering.",
                "/definition")]);
    }

    static DocumentValidationResult Validate(IngestionDefinition definition, ExecutionDefinitionKind kind) =>
        definition.Acquire is null || definition.Publish is null || definition.Settle is null
            ? Error("request", "Acquire, Publish, and Settle must all be declared.", "/")
            : (kind == SeparateLedgerKind) != (definition.AdvanceLedger is not null)
                ? Error("profile", "The document kind must match the declared ledger protocol.", "/advanceLedger")
                : DocumentValidationResult.Valid;

    static DocumentValidationResult Error(string code, string message, string location) =>
        new([new("integrations.ingestion." + code, DiagnosticSeverity.Error, message, location)]);
}
