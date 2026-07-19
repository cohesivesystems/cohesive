using Cohesive.Api;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Relations;

sealed class RelationQueryEvaluationApiOperationBinding : RelationQueryApiOperationBinding
{
    readonly Func<RelationQueryApiRequestContext, object?, ValueTask<RelationQueryEvaluation>> createEvaluation;
    readonly Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, ValueTask<IResult>> createResult;

    internal RelationQueryEvaluationApiOperationBinding(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, ValueTask<RelationQueryEvaluation>> createEvaluation,
        Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, ValueTask<IResult>> createResult)
        : base(operationName)
    {
        this.createEvaluation = createEvaluation ?? throw new ArgumentNullException(nameof(createEvaluation));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal RelationQueryEvaluationApiOperationBinding(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, ValueTask<RelationQueryEvaluation>> createEvaluation,
        Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, ValueTask<IResult>> createResult)
        : base(endpoint)
    {
        this.createEvaluation = createEvaluation ?? throw new ArgumentNullException(nameof(createEvaluation));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal override Delegate CreateHandler(ApiOperation operation, RelationQueryApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            CancellationTokenSource? linkedCancellation = null;
            var cancellationToken = operationContext.CancellationToken == httpContext.RequestAborted
                ? operationContext.CancellationToken
                : !operationContext.CancellationToken.CanBeCanceled
                    ? httpContext.RequestAborted
                    : !httpContext.RequestAborted.CanBeCanceled
                        ? operationContext.CancellationToken
                        : (linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            operationContext.CancellationToken,
                            httpContext.RequestAborted)).Token;
            using var linkedCancellationScope = linkedCancellation;
            cancellationToken.ThrowIfCancellationRequested();

            var evaluator = options.ResolveEvaluator(httpContext.RequestServices);
            var evaluationId = options.CreateEvaluationId(httpContext, operation);
            var request = await HttpRequestBindingSupport
                .ReadOperationRequestAsync(httpContext, operation, cancellationToken)
                .ConfigureAwait(false);
            var requestContext = new RelationQueryApiRequestContext(
                operationContext,
                httpContext,
                operation,
                evaluationId);
            var evaluation = await createEvaluation(requestContext, request).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Relation/query binding for operation '{operation.Name}' returned a null evaluation.");
            if (evaluation.Evaluation != evaluationId)
            {
                throw new InvalidOperationException(
                    $"Relation/query binding for operation '{operation.Name}' returned evaluation " +
                    $"'{evaluation.Evaluation}' instead of the request-scoped identity '{evaluationId}'.");
            }

            var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Relation/query evaluator returned a null outcome for operation '{operation.Name}'.");
            if (!evaluation.HasSameSemantics(outcome.Evaluation))
            {
                throw new InvalidOperationException(
                    $"Relation/query evaluator returned an outcome for a different evaluation of operation " +
                    $"'{operation.Name}'.");
            }

            var result = await createResult(
                    new(
                        OperationContext: operationContext,
                        HttpContext: httpContext,
                        Operation: operation,
                        Request: request,
                        Evaluation: evaluation),
                    outcome)
                .ConfigureAwait(false);
            return result ?? throw new InvalidOperationException(
                $"Relation/query result mapper returned null for operation '{operation.Name}'.");
        };
}
