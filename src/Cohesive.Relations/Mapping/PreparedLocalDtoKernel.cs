using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

// Value-only realization of a restricted canonical slice. Static input rows and grouped totals are
// obtained from the canonical interpreter; field aliases become expressions rather than new rows.
sealed class PreparedLocalDtoKernel<TOutput>(
    GraphShapeId rootShape, long maximumRoots, PreparedLocalDtoKernel<TOutput>.MapRow map) where TOutput : notnull
{
    internal delegate bool MapRow(ObservationValue root, out TOutput result);

    internal static bool CanPrepare(CompiledRelationQueryPlan plan) =>
        RuntimeFeature.IsDynamicCodeSupported
        && plan.InputContract.Parameters.IsDefaultOrEmpty
        && plan.InputContract.Traversals.IsDefaultOrEmpty
        && plan.InputContract.Expansions.IsDefaultOrEmpty
        && plan.ExecutionSlice.RelationOutput is { Definition.Mode: RelationOutputMode.ZeroOrOnePerRoot, KeySite: null } terminal
        && terminal.Invariants.IsDefaultOrEmpty
        && plan.ExecutionSlice.Nodes.All(static node => node.CanonicalNode
            is SourceQueryNode or ProjectQueryNode or AggregateQueryNode or JoinQueryNode { Kind: JoinKind.Inner })
        && plan.ExecutionSlice.Nodes.Any(static node => node.CanonicalNode is JoinQueryNode);

    internal static PreparedLocalDtoKernel<TOutput>? TryCompile(
        CompiledRelationQueryPlan plan, RelationQueryEvaluationOutcome preparation,
        CompiledRelationDtoMapper<TOutput> mapper, string rootIdentityField)
    {
        var execution = preparation.PhysicalExecution;
        if (!preparation.IsSuccessful || mapper.ComposeMaterializer is not { } materialize
            || execution?.Evidence is not { } evidence
            || !ReferenceEquals(execution.Request.Plan, plan)
            || execution.Interpretation?.RequirementGapAnalysis is not { IsConclusive: true, Gaps.IsEmpty: true }
            || evidence.SuppliedFieldsForAnalysis.IsDefault)
            return null;

        var root = plan.InputContract.Sources.Single(source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var graph = plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == root.Shape.GraphId).Graph;
        var rootShape = new GraphShapeId(graph, root.Shape.ShapeId);
        var compiler = new Compiler(plan, evidence.PreparedSources, root);
        if (!compiler.HasValidRootContract(rootShape.Graph.GetShape(root.Shape), rootIdentityField)
            || !compiler.Lower(plan.ExecutionSlice.RelationOutput!.Definition.Node)
            || !compiler.CanConstruct(plan.ExecutionSlice.RelationOutput, mapper))
            return null;

        var rowLimit = execution.Request.PhysicalPlan.Policy.MaximumLocalRows;
        var maximumRoots = rowLimit;
        var acquiredRows = 0L;
        // Prepared fragments retain acquisition order. The executor needs positive remaining
        // capacity to start each read, even when that source returns zero rows.
        foreach (var source in evidence.PreparedSources)
        {
            maximumRoots = Math.Min(maximumRoots, rowLimit - acquiredRows - 1);
            acquiredRows += source.Evidence.Occurrences.Length;
        }
        maximumRoots = Math.Min(maximumRoots, rowLimit - acquiredRows);
        return new(rootShape, maximumRoots, compiler.Compile(plan.ExecutionSlice.RelationOutput.Binding, materialize));
    }

    internal bool TryMap<TRoot>(IEnumerable<TRoot> roots, string identityField, CancellationToken cancellationToken,
        out TOutput[] result, out List<(string Id, Observation Value, string Order)> admitted) where TRoot : notnull
    {
        admitted = [];
        foreach (var row in RelationQueryEvaluationBuilder.AdmitByIdentityField(rootShape, roots, identityField, cancellationToken))
            admitted.Add((row.Id, row.Value, Uri.EscapeDataString(row.Id)));
        cancellationToken.ThrowIfCancellationRequested();
        // Physical source occurrence IDs share an input prefix and escape the observation identity.
        admitted.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Order, right.Order));
        for (var index = 1; index < admitted.Count; index++)
            if (admitted[index - 1].Id == admitted[index].Id)
                throw new ArgumentException("Supplied roots cannot repeat an observation identity.", "observations");

        if (admitted.Count > maximumRoots)
        {
            result = [];
            return false;
        }

        result = admitted.Count == 0 ? [] : new TOutput[admitted.Count];
        List<string>? failures = null;
        var count = 0;
        foreach (var row in admitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (map(row.Value.Value, out var dto))
                    result[count++] = dto;
            }
            catch (RelationDtoRowMappingException exception)
            {
                (failures ??= []).Add(exception.DiagnosticMessage);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              and not OutOfMemoryException
                                              and not StackOverflowException
                                              and not AccessViolationException)
            {
                (failures ??= []).Add(CompiledRelationDtoMapper<TOutput>.ConstructionFailureMessage(exception));
            }
        }
        if (failures is not null)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        if (count != result.Length)
            Array.Resize(ref result, count);
        return true;
    }

    sealed class Compiler(CompiledRelationQueryPlan plan, ImmutableArray<PreparedRelationQuerySource> prepared,
        RelationQuerySourceInputContract root)
    {
        readonly RelationQueryCompiledPlanIndex index = RelationQueryCompiledPlanIndex.For(plan);
        readonly ParameterExpression rootValue = Expression.Parameter(typeof(ObservationValue), "root");
        readonly ParameterExpression result = Expression.Parameter(typeof(TOutput).MakeByRefType(), "result");
        readonly LabelTarget exit = Expression.Label(typeof(bool));
        readonly List<ParameterExpression> locals = [];
        readonly List<Expression> steps = [];
        readonly Dictionary<(ValueBindingId Binding, string Field), (Expression Value, ScalarTypeKind Kind)> fields = [];

        internal bool HasValidRootContract(Shape shape, string identityField)
        {
            foreach (var contract in root.Fields)
            {
                var field = contract.Input;
                if (!field.Field.Path.TryGetDirectFieldName(out var name)
                    || !shape.TryGetField(name, out var definition)
                    || definition is not { Cardinality: FieldCardinality.Single, Presence: FieldPresence.Required,
                        Nullability: FieldNullability.NonNullable, Type: ScalarTypeRef { Format: PrimitiveFormat.None, Kind: ScalarTypeKind.String or ScalarTypeKind.Decimal } scalar }
                    || field.ValueContract != ValueContract.FromField(definition)
                    || definition.Role == FieldRole.Identity && name != identityField)
                    return false;

                fields.Add((root.Binding, name), (Read(rootValue, name), scalar.Kind));
            }
            return true;
        }

        internal bool Lower(QueryNodeId id)
        {
            var execution = index.Nodes[id];
            return execution.CanonicalNode switch
            {
                SourceQueryNode => id == root.Node,
                JoinQueryNode { Kind: JoinKind.Inner } join => LowerJoin(execution, join),
                ProjectQueryNode project => LowerProjection(execution, project),
                _ => false
            };
        }

        bool LowerProjection(RelationQueryExecutionNode execution, ProjectQueryNode project)
        {
            if (!Lower(execution.LogicalPlan.EffectiveInputs[0]))
                return false;
            foreach (var assignment in execution.ProjectionAssignments)
            {
                if (!assignment.Definition.Target.TryGetDirectFieldName(out var name)
                    || assignment.ValueSite.Analysis.Site.Expression is not FieldExpr field
                    || !TryReadField(field, out var value)
                    || !fields.TryAdd((project.ResultBinding, name), value))
                    return false;
            }
            return true;
        }

        bool LowerJoin(RelationQueryExecutionNode execution, JoinQueryNode join)
        {
            var inputs = execution.LogicalPlan.EffectiveInputs;
            if (!Lower(inputs[0]) || !join.TryGetEquijoin(out var left, out var right)
                || !TryStaticRows(inputs[1], out var binding, out var rows))
                return false;

            if (left.Binding == binding.Binding)
                (left, right) = (right, left);
            if (right.Binding != binding.Binding || !right.Path.TryGetDirectFieldName(out var keyName)
                || !TryReadField(left, out var key) || key.Kind != ScalarTypeKind.String)
                return false;

            var lookup = new Dictionary<string, ObservationValue>(rows.Length, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!row.TryGetBinding(binding.Binding, out var value)
                    || !value.Value.TryGetProperty(keyName, out var id)
                    || id.Kind != ObservationValueKind.String || id.String is not { } text
                    || !lookup.TryAdd(text, value.Value))
                    return false;
            }

            var matched = Expression.Variable(typeof(ObservationValue), "matched");
            locals.Add(matched);
            var find = Expression.Call(Expression.Constant(lookup), nameof(Dictionary<string, ObservationValue>.TryGetValue),
                Type.EmptyTypes, Expression.Property(key.Value, nameof(ObservationValue.String)), matched);
            steps.Add(Expression.IfThen(Expression.Not(find), Expression.Return(exit, Expression.Constant(false))));

            if (binding.Shape is not { } shapeId)
                return false;
            var graph = plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == shapeId.GraphId).Graph;
            var shape = graph.GetShape(shapeId);
            foreach (var field in shape.Fields)
            {
                if (field is not { Cardinality: FieldCardinality.Single, Type: ScalarTypeRef { Format: PrimitiveFormat.None, Kind: ScalarTypeKind.String or ScalarTypeKind.Decimal } scalar })
                    continue;
                var name = field.Name.Value;
                if (lookup.Values.All(row => row.TryGetProperty(name, out var value)
                    && value.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
                    && ValueContract.FromField(field).IsSatisfiedByConstant(value)))
                {
                    fields.Add((binding.Binding, name), (Read(matched, name), scalar.Kind));
                }
            }
            return true;
        }

        bool TryStaticRows(QueryNodeId id, out RelationQueryBindingShape binding, out ImmutableArray<RelationQueryRuntimeRow> rows)
        {
            var execution = index.Nodes[id];
            binding = default;
            rows = default;
            if (execution.OutputBindings.Length != 1)
                return false;
            binding = execution.OutputBindings[0];
            if (execution.CanonicalNode is SourceQueryNode)
                return FindSource(id)?.RuntimeRows.TryGet(out rows) == true;
            return execution.CanonicalNode is AggregateQueryNode
                && FindSource(execution.LogicalPlan.EffectiveInputs[0])?.GroupedRows.TryGetValue(id, out rows) == true;
        }

        PreparedRelationQuerySource? FindSource(QueryNodeId id) =>
            index.SourceInputs.TryGetValue(id, out var source)
                ? prepared.FirstOrDefault(fragment => fragment.Evidence.Input == source.Id && ReferenceEquals(fragment.Plan, plan))
                : null;

        bool TryReadField(FieldExpr field, out (Expression Value, ScalarTypeKind Kind) value)
        {
            value = default;
            return field.Binding is { } binding && field.Path.TryGetDirectFieldName(out var name)
                && fields.TryGetValue((binding, name), out value);
        }

        internal bool CanConstruct(RelationQueryRelationExecutionOutput terminal, CompiledRelationDtoMapper<TOutput> mapper)
        {
            var shape = terminal.Definition.Shape;
            var graph = plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == shape.GraphId).Graph;
            var definition = graph.GetShape(shape);
            foreach (var member in mapper.Descriptor.Members)
            {
                if (!member.OutputField.Path.TryGetDirectFieldName(out var name)
                    || !fields.TryGetValue((terminal.Binding, name), out var value)
                    || !definition.TryGetField(name, out var field)
                    || field.Cardinality != FieldCardinality.Single
                    || field.Type is not ScalarTypeRef { Format: PrimitiveFormat.None } scalar || scalar.Kind != value.Kind)
                    return false;
            }
            return mapper.Descriptor.Members.Length == terminal.Fields.Length;
        }

        internal MapRow Compile(ValueBindingId output, Func<Func<string, Expression>, Expression> materialize)
        {
            steps.Add(Expression.Assign(result, materialize(name => fields[(output, name)].Value)));
            steps.Add(Expression.Label(exit, Expression.Constant(true)));
            return Expression.Lambda<MapRow>(Expression.Block(locals, steps), rootValue, result).Compile();
        }

        static MethodCallExpression Read(Expression row, string field) =>
            Expression.Call(row, nameof(ObservationValue.GetProperty), Type.EmptyTypes, Expression.Constant(field));
    }
}
