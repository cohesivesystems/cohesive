using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Api.Execution.Services;

/// <summary>Native realization of an exact query with reusable semantic preparation and scoped acquisition.</summary>
/// <remarks>
/// The declared query owns filtering and result semantics. Its scope parameter must participate in the intended
/// logical ownership predicate; physical partition routing alone is not authorization. The evaluator factory
/// receives admitted scope evidence and must retain it in source placement/read bindings, not ambient state.
/// </remarks>
public sealed class ServiceQueryBinding : ServiceBinding
{
    /// <summary>Retains one immutable compilation request and a resolver called only after authorization.</summary>
    /// <exception cref="ArgumentNullException">Compilation or evaluator is null.</exception>
    /// <exception cref="ArgumentException">Identity, revision, document integrity or query semantics are invalid.</exception>
    public ServiceQueryBinding(string operationId, ExecutionRevisionId revision,
        RelationQueryCompilationRequest compilation, Func<OperationContext, ScopeRef, IRelationQueryEvaluator> evaluator)
        : base(operationId)
    {
        if (string.IsNullOrWhiteSpace(revision.Value))
            throw new ArgumentException("An exact query revision is required.", nameof(revision));
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        Evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        var validation = RelationQueryDocumentSemanticValidator.Validate(compilation.DefinitionDocument);
        if (!validation.IsValid) throw new ServiceBindingValidationException(validation);
        if (compilation.DefinitionDocument.Definition is not QueryDefinition query)
            throw ServiceBindingValidationException.Error("services.binding.queryRequired",
                "A query operation requires a canonical Query definition.", "/bindings/query");
        var fingerprint = compilation.DefinitionDocument.DefinitionFingerprint;
        Reference = new(new(query.Id.Value), revision,
            new(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value));
    }

    /// <summary>Exact native query authority; the supplied revision names this immutable semantic document.</summary>
    public ExecutionDefinitionReference Reference { get; }
    /// <summary>Reusable semantic snapshot, including native output demand and shape dependencies.</summary>
    public RelationQueryCompilationRequest Compilation { get; }
    internal Func<OperationContext, ScopeRef, IRelationQueryEvaluator> Evaluator { get; }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceQueryOperation query || query.Query != Reference)
            throw ServiceBindingValidationException.Error("services.binding.inexact",
                "The binding must realize the exact declared query.", "/bindings/query");
        var parameter = Compilation.DefinitionDocument.Definition.Body.Parameters
            .SingleOrDefault(p => p.Id == query.ScopeParameter);
        if (parameter is null || parameter.Presence != FieldPresence.Required
            || parameter.Type != new ScalarTypeRef(ScalarTypeKind.String))
            throw ServiceBindingValidationException.Error("services.binding.scopeParameter",
                "The trusted scope parameter must be a required non-null string in the canonical query.", "/bindings/query/scopeParameter");
    }
}

public sealed partial class ServiceRuntime
{
    /// <summary>Admits scope, binds canonical parameters, and delegates execution to the native query evaluator.</summary>
    /// <remarks>
    /// Caller parameters cannot override the declared scope parameter or output demand. Omission retains native
    /// required/default semantics. The immutable compilation request is reused; invocation evidence is fresh.
    /// Provider failures and cancellation propagate. No automatic retry or cross-invocation result cache is added.
    /// </remarks>
    /// <exception cref="ArgumentException">The operation, evaluation identity or supplied parameters are invalid.</exception>
    /// <exception cref="InvalidOperationException">The evaluator violates its exact evaluation contract.</exception>
    /// <exception cref="OperationCanceledException">Invocation cancellation is observed.</exception>
    public async ValueTask<ServiceOperationResult<RelationQueryEvaluationOutcome>> EvaluateAsync(OperationContext context, string operationId,
        RelationQueryEvaluationId evaluationId, IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId.Value);
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceQueryOperation operation || linked.Binding is not ServiceQueryBinding binding)
            throw new ArgumentException("The operation is not a declared query.", nameof(operationId));
        using var evidence = new ServiceInvocationEvidence(definitionReference, operationId, operation.Query, new(evaluationId.Value));
        try
        {
            context.ThrowIfCancellationRequested();
            var scope = await authorization.AdmitAsync(context, operation).ConfigureAwait(false);
            if (scope is null)
            {
                evidence.Record("invocationRejected", "services.authorization.denied");
                return new(ApiResultKind.Forbidden, null,
                    [new("services.authorization.denied", DiagnosticSeverity.Error, "The caller is not authorized to invoke this operation.", "/authorization")],
                    evidence.Complete(ApiResultKind.Forbidden));
            }
            evidence.Record("authorityAdmitted");
            var declarations = binding.Compilation.DefinitionDocument.Definition.Body.Parameters;
            if (parameters.ContainsKey(operation.ScopeParameter))
                return RejectInput("services.query.scopeOverride", "Caller parameters cannot supply the trusted scope parameter.");
            if (parameters.Keys.Any(id => !declarations.Any(p => p.Id == id)))
                return RejectInput("services.query.parameterUnknown", "Caller parameters must be declared by the query.");
            RelationQueryEvaluation evaluation;
            try
            {
                var builder = binding.Compilation.Evaluate(evaluationId);
                foreach (var parameter in declarations)
                {
                    if (parameter.Id == operation.ScopeParameter)
                        builder.Set(parameter.Id, ObservationValue.FromString(scope.Id), evidenceReference: "service/authorized-scope");
                    else if (parameters.TryGetValue(parameter.Id, out var value))
                        builder.Set(parameter.Id, value, evidenceReference: "service/caller-parameter");
                    else
                        builder.Omit(parameter.Id);
                }
                evaluation = builder.Build();
            }
            catch (ArgumentException)
            {
                return RejectInput("services.query.inputInvalid", "Caller parameters do not satisfy the canonical query contract.");
            }
            evidence.Record("parametersBound");
            context.ThrowIfCancellationRequested();
            var evaluator = binding.Evaluator(context, scope)
                ?? throw new InvalidOperationException("The query binding returned no evaluator.");
            var outcome = await evaluator.EvaluateAsync(evaluation, context.CancellationToken).ConfigureAwait(false);
            if (outcome is null || !evaluation.HasSameSemantics(outcome.Evaluation))
                throw new InvalidOperationException("The evaluator returned evidence for a different canonical evaluation.");
            evidence.Record("queryEvaluated");
            var kind = outcome.IsSuccessful ? ApiResultKind.Success : ApiResultKind.ValidationFailed;
            return new(kind, outcome, [], evidence.Complete(kind));
        }
        catch (Exception exception)
        {
            evidence.Fail(exception);
            throw;
        }

        ServiceOperationResult<RelationQueryEvaluationOutcome> RejectInput(string code, string message)
        {
            evidence.Record("invocationRejected", code);
            return new(ApiResultKind.ValidationFailed, null, [new(code, DiagnosticSeverity.Error, message, "/input")],
                evidence.Complete(ApiResultKind.ValidationFailed));
        }
    }
}
