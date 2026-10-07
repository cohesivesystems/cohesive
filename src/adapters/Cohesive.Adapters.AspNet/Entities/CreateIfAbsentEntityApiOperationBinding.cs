using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

// The initializer is input acquisition. Its materialized observation becomes canonical creation input;
// the interpreter, candidate validation and atomic repository fence own the mutation semantics.
sealed class CreateIfAbsentEntityApiOperationBinding(ApiEndpoint endpoint,
    Func<EntityApiRequestContext, object?, EntityState> initialize,
    Func<EntityApiCommitContext, EntitySnapshot, IResult> respond) : EntityApiOperationBinding(endpoint)
{
    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options)
    {
        var stateContract = ValueContract.FromShape(options.Entity.Shape);
        var definition = new TransitionDefinition(stateContract, stateContract,
            new(new ScalarTypeRef(ScalarTypeKind.Bool)), [],
            new(new("create/body"), [new OutcomeTransitionNode(new("create/result"), TransitionOutcomeDisposition.Applied, Expr.Const(true))]),
            subjectCreation: new(new("create/initialize"), Expr.BoundValue(TransitionBindingIds.Input)));
        var document = TransitionDefinitionDocuments.Create(new(operation.Id.Value + "/create-if-absent"), new("1"), definition,
            new(new("cohesive.api.crud", "1"), new("entity-api/create-if-absent"), DocumentOrigin.Generated));
        var compilation = TransitionStaticCompiler.Compile(document);
        var plan = compilation.Plan ?? throw new TransitionApiPreparationException(operation.Id.Value, compilation);
        return async (OperationContext context, HttpContext http) =>
        {
            var repository = EntityApiRequestSupport.ResolveRepository(http, options);
            if (!repository.SupportsCreateIfAbsent)
                throw new NotSupportedException("Creation requires an atomic absence-fenced repository.");
            var request = await EntityApiRequestSupport.ReadBodyRequestAsync(http, operation, context.CancellationToken).ConfigureAwait(false);
            var acquired = initialize(new(context, http, operation, options.Entity, repository, EntityId: null), request);
            var decision = TransitionReferenceInterpreter.DecideCreation(plan, options.CreateActivationId(http, operation),
                PortableValue.Concrete(stateContract, ObservationValue.FromObject(acquired.Fields)));
            try
            {
                var candidate = TransitionStateProjector.ApplyToEntity(options.Entity, acquired.EntityId.Value, decision);
                var committed = await repository.CreateIfAbsent(context, candidate.Snapshot).ConfigureAwait(false);
                return respond(new(context, http, operation, options.Entity, repository, acquired.EntityId.Value, request,
                    OldSnapshot: null, NewState: candidate, Decision: decision), committed);
            }
            catch (TransitionStatePreparationException failure)
            {
                return TypedResults.Problem(CohesiveHttpProblems.StatePreparationFailure(http, failure));
            }
            catch (ObservationConcurrencyConflictException)
            {
                return TypedResults.Problem(CohesiveHttpProblems.ConcurrencyConflict(http));
            }
        };
    }
}
