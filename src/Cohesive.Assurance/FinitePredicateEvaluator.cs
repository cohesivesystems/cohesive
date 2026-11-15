using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Assurance;

// One explicit row binding shared by finite interpretations; no ambient inputs or host callbacks survive.
internal sealed class FinitePredicateEvaluator
{
    readonly PortableExpressionReferenceEvaluator evaluator = new(AssurancePredicates.Capabilities, "finite guard checker");
    readonly PortableExpressionEvaluationContext context;
    PortableExpressionValue current = new(PortableValueState.Missing, default);

    internal FinitePredicateEvaluator(FiniteGuardCheckDocument plan)
    {
        var id = plan.Claim.Definition.Inputs[0].Id;
        PortableExpressionValue missing = new(PortableValueState.Missing, default);
        PortableExpressionValue Resolve(ValueBindingId binding) => binding == id ? current : missing;
        context = new()
        {
            ResolveBinding = Resolve,
            ResolveField = (binding, path) => binding is { } value
                ? Resolve(value).Project(path, missingFieldState: PortableValueState.Missing) : missing,
            ResolveParameter = _ => missing,
            MissingFieldState = PortableValueState.Missing
        };
    }

    internal void SetMember(ObservationValue member) => current = PortableExpressionValue.FromObservation(member);
    internal bool Evaluate(Expr expression) => PortableExpressionReferenceEvaluator.RequireBoolean(
        evaluator.Evaluate(expression, context), "finite guard predicate");
}
