using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using E = System.Linq.Expressions.Expression;

namespace Cohesive.Transitions.Execution;

public static partial class TransitionReferenceInterpreter
{
    // Bind expressions and destination slots while keeping validation and evidence in Engine.
    internal static Action<Engine>? CompileSpecializedBody(CompiledTransitionPlan plan, ObservationLayout layout)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported) return null;
        var steps = plan.Definition.Body.Steps;
        if (steps.Length < 2 || steps[^1] is not OutcomeTransitionNode) return null;
        var engine = E.Parameter(typeof(Engine), "activation");
        var expressions = new List<Expression>();
        foreach (var step in steps.AsSpan()[..^1])
        {
            if (step is not UpdateTransitionNode { Operation: SetTransitionPatch set } update
                || !update.Path.TryGetDirectFieldName(out var destination)) return null;
            if (string.IsNullOrWhiteSpace(destination) || !layout.TryGetOrdinal(destination, out var destinationOrdinal)) return null;
            var rhs = Value(set.Value, update.Id);
            if (rhs is null) return null;
            var contract = E.Variable(typeof(ValueContract), "contract");
            var before = E.Variable(typeof(PortableExpressionValue), "before");
            expressions.Add(E.Block([contract, before],
                Call(nameof(Engine.BeginCompiledSet), E.Constant(update), contract, before),
                Call(nameof(Engine.EndCompiledSet), E.Constant(update), contract, before, rhs,
                    E.Constant(destinationOrdinal))));
        }
        return E.Lambda<Action<Engine>>(E.Block(expressions), engine).Compile();

        MethodCallExpression Call(string name, params Expression[] args) => E.Call(engine, name, Type.EmptyTypes, args);
        Expression? Value(Expr expression, ExecutionNodeId node)
        {
            if (expression is ConstantExpr constant) return E.Constant(PortableExpressionValue.FromObservation(constant.Value));
            if (expression is LiteralExpr literal) return E.Constant(PortableExpressionValue.FromObservation(literal.Value));
            if (expression is ParameterExpr parameter) return Call(nameof(Engine.ReadCompiledParameter), E.Constant(parameter.Parameter));
            FieldPath path;
            ValueBindingId binding;
            switch (expression)
            {
                case FieldExpr field:
                    path = field.Path;
                    binding = field.Binding ?? TransitionBindingIds.Observation;
                    break;
                case FieldRefExpr field:
                    path = field.Path;
                    binding = TransitionBindingIds.Observation;
                    break;
                default: return null;
            }
            if (!path.TryGetDirectFieldName(out var fieldName) || string.IsNullOrWhiteSpace(fieldName) || fieldName == ExprFieldRoots.CurrentItem
                || (binding != TransitionBindingIds.Input && binding != TransitionBindingIds.Observation)) return null;
            var ordinal = binding == TransitionBindingIds.Observation && layout.TryGetOrdinal(fieldName, out var slot)
                ? slot : -1;
            return Call(nameof(Engine.ReadCompiledField), E.Constant(binding), E.Constant(path), E.Constant(node),
                E.Constant(layout, typeof(ObservationLayout)), E.Constant(ordinal));
        }
    }

    internal sealed partial class Engine
    {
        // The generated method keeps the reference order: resolve contract, read before, require known,
        // evaluate RHS, validate after, create patch, update candidate and append trace.
        internal void BeginCompiledSet(UpdateTransitionNode update, out ValueContract contract,
            out PortableExpressionValue before)
        {
            contract = ResolveObservationContract(update.Path);
            before = ReadCandidate(update.Path, update.Id);
            RequireKnownPriorValue(before, update.Path, update.Id);
        }

        internal void EndCompiledSet(UpdateTransitionNode update, ValueContract contract,
            PortableExpressionValue before, PortableExpressionValue value, int destinationOrdinal)
        {
            var after = ValidateTyped(value, contract, update.Id);
            var patch = CreateSetPatch(update, contract, before, after);
            RecordUpdate(patch);
            if (projectionSource is not null && patch.Changed)
                ProjectCompiledSet(destinationOrdinal, patch);
        }

        void ProjectCompiledSet(int ordinal, TransitionExecutedPatch patch)
        {
            var source = projectionSource!;
            ObservationValue before;
            if (projectedValues is null) source.TryGetField(ordinal, out before);
            else before = projectedValues[ordinal];
            if (!TransitionStateProjector.MatchesEvidence(patch.Before, before))
            {
                // Defer a projection mismatch until the decision has finished. A rejected or
                // conflicting decision discards these writes, just as the ordinary projector does.
                projectionSource = null;
                projectedValues = null;
                return;
            }

            if (projectedValues is null)
            {
                projectedValues = OrdinalObservationFields.CopySlots(source);
                projectedCount = source.Count;
            }
            var after = PortableExpressionValue.FromPortable(patch.After).RequireObservation("candidate-state projection");
            OrdinalObservationFields.SetSlot(ref projectedValues[ordinal], after, ref projectedCount);
        }

        internal ObservationValue? ProjectedWrites(TransitionDecision decision) =>
            projectionSource is not null && projectedValues is not null && !decision.Patch.IsEmpty
                ? ObservationValue.FromOrdinalFields(new OrdinalObservationFields(projectionSource.Layout,
                    ImmutableCollectionsMarshal.AsImmutableArray(projectedValues), projectedCount))
                : null;

        internal PortableExpressionValue ReadCompiledParameter(string parameter) => ResolveParameter(parameter);

        internal PortableExpressionValue ReadCompiledField(ValueBindingId binding, FieldPath path, ExecutionNodeId node,
            ObservationLayout? layout, int ordinal)
        {
            if (ordinal >= 0 && observation.TryGetExact(TransitionObservationAccess.Whole, out var whole)
                && whole.State == PortableValueState.Concrete
                && whole.Value!.Value.CompositeStorage is IOrdinalObservationStorage storage
                && ReferenceEquals(storage.Layout, layout))
            {
                storage.TryGetField(ordinal, out var value);
                var portable = PortableExpressionValue.FromObservation(value).ToPortable(ResolveObservationContract(path));
                return RecordRead(portable, TransitionObservationAccess.At(path), node);
            }
            return ResolveField(binding, path, node, candidateState: false, bindings);
        }
    }
}
