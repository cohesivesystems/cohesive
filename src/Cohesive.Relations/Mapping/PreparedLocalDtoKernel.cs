using System.Buffers;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    GraphShapeId rootShape, Observation.CompleteProjectionAdmission rootAdmission, string identityField,
    int identityOrdinal, long maximumRoots, PreparedLocalDtoKernel<TOutput>.MapRow map) where TOutput : notnull
{
    // Keep the optional rental bounded even when an evaluator has a much larger row limit.
    const int MaximumPooledRootSlots = 64 * 1024;
    const int MinimumPackedSortRoots = 128;

    readonly WeakPreparationCache<Type, ObservationValue.ScalarProjection?> rootProjections = new();

    internal delegate bool MapRow(ObservationValue root, ObservationValue[]? slots, int offset, out TOutput result);

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
        var rootLayout = ObservationLayout.Create(rootShape);
        if (!rootLayout.TryGetOrdinal(rootIdentityField, out var identityOrdinal))
            return null;
        var compiler = new Compiler(plan, evidence.PreparedSources, root, rootLayout);
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

        return new(rootShape, new(rootLayout), rootIdentityField, identityOrdinal, maximumRoots,
            compiler.Compile(plan.ExecutionSlice.RelationOutput.Binding, mapper.Descriptor.Members, materialize));
    }

    internal bool TryMap<TRoot>(IEnumerable<TRoot> roots, CancellationToken cancellationToken,
        out TOutput[] result, out IEnumerable<RelationQuerySuppliedRoot> budgetFallback) where TRoot : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = typeof(TRoot);
        if (!rootProjections.TryGet(type, out var project))
            project = rootProjections.Get(type, owner => ObservationValue.CompileCompleteScalarProjection(owner, rootAdmission.Layout));

        // Arrays provide an exact extent without consulting caller-defined enumeration or count logic.
        // A batch that may need canonical budget handling retains the ordinary owned observations.
        var capacity = roots is TRoot[] array && array.Length <= maximumRoots && array.Length <= MaximumPooledRootSlots
            ? array.Length
            : 0;
        var width = rootAdmission.Layout.Count;
        var slots = project is not null && capacity > 0 && capacity <= MaximumPooledRootSlots / width
            ? ArrayPool<ObservationValue>.Shared.Rent(capacity * width)
            : null;
        AdmittedRoot[]? rootBuffer = null;
        string[]? orderKeys = null;
        int[]? orderBuffer = null;
        budgetFallback = [];
        try
        {
            if (slots is not null && capacity > 1)
            {
                rootBuffer = ArrayPool<AdmittedRoot>.Shared.Rent(capacity);
                orderKeys = ArrayPool<string>.Shared.Rent(capacity);
                orderBuffer = ArrayPool<int>.Shared.Rent(capacity);
            }
            var ownedRoots = rootBuffer is null ? new List<AdmittedRoot>(capacity) : null;
            var count = 0;
            foreach (var value in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AdmittedRoot admitted;
                if (project is not null && value?.GetType() == type)
                {
                    var offset = slots is null ? 0 : count * width;
                    var projected = slots ?? new ObservationValue[width];
                    project(value, projected, offset);
                    var rowValues = projected.AsSpan(offset, width);
                    Observation? observation = null;
                    if (slots is null)
                        observation = rootAdmission.Admit(ImmutableCollectionsMarshal.AsImmutableArray(projected));
                    else
                        rootAdmission.Validate(rowValues);
                    var id = RelationQueryEvaluationBuilder.RequireRootIdentity(rowValues[identityOrdinal], identityField);
                    admitted = new(id, Uri.EscapeDataString(id), observation, offset);
                }
                else
                {
                    var row = RelationQueryEvaluationBuilder.AdmitRootByIdentityField(rootShape, value!, identityField);
                    admitted = new(row.Id, Uri.EscapeDataString(row.Id), row.Value, 0);
                }

                if (rootBuffer is null)
                    ownedRoots!.Add(admitted);
                else
                {
                    rootBuffer[count] = admitted;
                    orderKeys![count] = admitted.Order;
                    orderBuffer![count] = count;
                }
                count++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var rows = rootBuffer is null ? CollectionsMarshal.AsSpan(ownedRoots) : rootBuffer.AsSpan(0, count);
            var order = orderBuffer is null ? Span<int>.Empty : orderBuffer.AsSpan(0, count);
            var keys = orderKeys is null ? Span<string>.Empty : orderKeys.AsSpan(0, count);
            if (count > 1 && SortAndFindDuplicate(rows, keys, order))
                throw new ArgumentException("Supplied roots cannot repeat an observation identity.", "observations");

            if (rows.Length > maximumRoots)
            {
                budgetFallback = ownedRoots!.Select(static row => RelationQuerySuppliedRoot.FromObservation(row.Id, row.Value!));
                result = [];
                return false;
            }

            result = MapAdmitted(rows, order, slots, cancellationToken);
            return true;
        }
        finally
        {
            if (orderBuffer is not null)
                ArrayPool<int>.Shared.Return(orderBuffer);
            if (orderKeys is not null)
                ArrayPool<string>.Shared.Return(orderKeys, clearArray: true);
            if (rootBuffer is not null)
                ArrayPool<AdmittedRoot>.Shared.Return(rootBuffer, clearArray: true);
            if (slots is not null)
                ArrayPool<ObservationValue>.Shared.Return(slots, clearArray: true);
        }
    }

    static bool SortAndFindDuplicate(Span<AdmittedRoot> rows, Span<string> keys, Span<int> order)
    {
        // Physical source occurrence IDs share an input prefix and escape the observation identity.
        if (order.IsEmpty)
            rows.Sort();
        else
        {
            if (TrySortPackedKeys(rows, keys, order, out var duplicate))
                return duplicate;
            keys.Sort(order, StringComparer.Ordinal);
        }

        for (var index = 1; index < rows.Length; index++)
        {
            if (!keys.IsEmpty && keys[index - 1] != keys[index])
                continue;
            var left = order.IsEmpty ? index - 1 : order[index - 1];
            var right = order.IsEmpty ? index : order[index];
            if (rows[left].Id == rows[right].Id)
                return true;
        }
        return false;
    }

    static bool TrySortPackedKeys(ReadOnlySpan<AdmittedRoot> rows, ReadOnlySpan<string> keys,
        Span<int> order, out bool duplicate)
    {
        duplicate = false;
        if (keys.Length < MinimumPackedSortRoots)
            return false;

        ReadOnlySpan<char> shared = keys[0];
        var longest = shared.Length;
        for (var index = 1; index < keys.Length; index++)
        {
            var key = keys[index].AsSpan();
            longest = Math.Max(longest, key.Length);
            shared = shared[..shared.CommonPrefixLength(key)];
            if (longest - shared.Length > sizeof(ulong))
                return false;
        }

        var buffer = ArrayPool<ulong>.Shared.Rent(keys.Length);
        try
        {
            var packed = buffer.AsSpan(0, keys.Length);
            for (var index = 0; index < keys.Length; index++)
            {
                var suffix = keys[index].AsSpan(shared.Length);
                ulong value = 0;
                for (var character = 0; character < suffix.Length; character++)
                {
                    var code = suffix[character];
                    if (code > byte.MaxValue)
                        return false;
                    value |= (ulong)code << ((sizeof(ulong) - 1 - character) * 8);
                }
                packed[index] = value;
            }

            packed.Sort(order);
            for (var index = 1; index < packed.Length; index++)
            {
                if (packed[index - 1] != packed[index])
                    continue;
                if (rows[order[index - 1]].Id == rows[order[index]].Id)
                    duplicate = true;
                else
                {
                    // An escaped-key collision must retain the ordinary sort's tie behavior.
                    for (var original = 0; original < order.Length; original++)
                        order[original] = original;
                    return false;
                }
            }
            return true;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(buffer);
        }
    }

    TOutput[] MapAdmitted(ReadOnlySpan<AdmittedRoot> admitted, ReadOnlySpan<int> order,
        ObservationValue[]? slots, CancellationToken cancellationToken)
    {
        var result = admitted.IsEmpty ? [] : new TOutput[admitted.Length];
        List<string>? failures = null;
        var count = 0;
        for (var index = 0; index < admitted.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ref readonly var row = ref admitted[order.IsEmpty ? index : order[index]];
            try
            {
                if (map(row.Value?.Value ?? default, row.Value is null ? slots : null, row.Offset, out var dto))
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
        return result;
    }

    readonly record struct AdmittedRoot(string Id, string Order, Observation? Value, int Offset) : IComparable<AdmittedRoot>
    {
        public int CompareTo(AdmittedRoot other) => string.CompareOrdinal(Order, other.Order);
    }

    static ObservationValue ReadRoot(ObservationValue row, ObservationValue[]? slots, int offset,
        ObservationLayout layout, int ordinal, string field)
    {
        if (slots is not null)
            return slots[offset + ordinal];

        return row.Fields is OrdinalObservationFields values && ReferenceEquals(values.Layout, layout)
            && values.TryGetField(ordinal, out var value)
            ? value
            : row.GetProperty(field);
    }

    sealed class Compiler(CompiledRelationQueryPlan plan, ImmutableArray<PreparedRelationQuerySource> prepared,
        RelationQuerySourceInputContract root, ObservationLayout rootLayout)
    {
        readonly RelationQueryCompiledPlanIndex index = RelationQueryCompiledPlanIndex.For(plan);
        readonly ParameterExpression rootValue = Expression.Parameter(typeof(ObservationValue), "root");
        readonly ParameterExpression rootSlots = Expression.Parameter(typeof(ObservationValue[]), "slots");
        readonly ParameterExpression rootOffset = Expression.Parameter(typeof(int), "offset");
        readonly ParameterExpression result = Expression.Parameter(typeof(TOutput).MakeByRefType(), "result");
        readonly LabelTarget exit = Expression.Label(typeof(bool));
        readonly List<ParameterExpression> locals = [];
        readonly List<Expression> steps = [];
        readonly Dictionary<(ValueBindingId Binding, string Field), (Expression Value, ScalarTypeKind Kind)> fields = [];
        readonly List<StaticJoin> joins = [];

        sealed record StaticJoin(ValueBindingId Binding, Expression Key, ParameterExpression Match,
            Dictionary<string, ObservationValue> Rows);

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

                fields.Add((root.Binding, name), (Expression.Call(
                    typeof(PreparedLocalDtoKernel<TOutput>), nameof(ReadRoot), Type.EmptyTypes, rootValue, rootSlots, rootOffset,
                    Expression.Constant(rootLayout), Expression.Constant(rootLayout.GetOrdinal(name)), Expression.Constant(name)),
                    scalar.Kind));
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
            joins.Add(new(binding.Binding, key.Value, matched, lookup));
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

        internal MapRow Compile(ValueBindingId output, ImmutableArray<RelationDtoMapperMemberDescriptor> members,
            Func<Func<string, Expression>, Expression> materialize)
        {
            // Projection aliases retain the same expression instance. Track the actual consumers
            // after lowering so an unused alias does not widen the prepared join payload.
            var demand = new Dictionary<Expression, Type?>(ReferenceEqualityComparer.Instance);
            foreach (var join in joins)
                Demand(join.Key, typeof(string));
            foreach (var member in members)
                Demand(fields[(output, member.OutputField.Path.Segments[0].Segment!)].Value, member.TargetType);

            var typed = new Dictionary<Expression, Expression>(ReferenceEqualityComparer.Instance);
            foreach (var join in joins)
            {
                var key = Resolve(join.Key);
                if (key.Type != typeof(string))
                    key = Expression.Property(key, nameof(ObservationValue.String));
                if (!TryEmitScalarLookup(join, key, demand, typed))
                    EmitLookup(join.Rows, key, join.Match);
            }

            steps.Add(Expression.Assign(result, materialize(name => Resolve(fields[(output, name)].Value))));
            steps.Add(Expression.Label(exit, Expression.Constant(true)));
            return Expression.Lambda<MapRow>(Expression.Block(locals, steps), rootValue, rootSlots, rootOffset, result).Compile();

            Expression Resolve(Expression value) => typed.GetValueOrDefault(value, value);

            void Demand(Expression read, Type destination)
            {
                if (!demand.TryAdd(read, destination) && demand[read] != destination)
                    demand[read] = null;
            }
        }

        bool TryEmitScalarLookup(StaticJoin join, Expression key, Dictionary<Expression, Type?> demand,
            Dictionary<Expression, Expression> typed)
        {
            var consumed = fields.Where(field => field.Key.Binding == join.Binding && demand.ContainsKey(field.Value.Value))
                .Take(2).ToArray();
            if (consumed.Length != 1)
                return false;

            var field = consumed[0];
            var destination = demand[field.Value.Value];
            return field.Value.Kind switch
            {
                ScalarTypeKind.String when destination == typeof(string) =>
                    TryEmitScalarLookup<string>(join.Rows, key, field.Key.Field, field.Value.Value, typed, RelationDtoValueReader.TryConvertString),
                ScalarTypeKind.Decimal when destination == typeof(decimal) =>
                    TryEmitScalarLookup<decimal>(join.Rows, key, field.Key.Field, field.Value.Value, typed, RelationDtoValueReader.TryConvertDecimal),
                _ => false
            };
        }

        delegate bool ReadScalar<T>(in ObservationValue value, out T result);

        bool TryEmitScalarLookup<T>(Dictionary<string, ObservationValue> rows, Expression key, string field,
            Expression originalRead, Dictionary<Expression, Expression> typed, ReadScalar<T> convert)
        {
            var lookup = new Dictionary<string, T>(rows.Count, StringComparer.Ordinal);
            foreach (var (identity, row) in rows)
            {
                if (!row.TryGetProperty(field, out var value) || !convert(in value, out var scalar))
                    return false;
                lookup.Add(identity, scalar);
            }

            var matched = Expression.Variable(typeof(T), "matchedScalar");
            typed.Add(originalRead, matched);
            EmitLookup(lookup, key, matched);
            return true;
        }

        void EmitLookup<T>(Dictionary<string, T> lookup, Expression key, ParameterExpression matched)
        {
            locals.Add(matched);
            var find = Expression.Call(Expression.Constant(lookup), nameof(Dictionary<string, T>.TryGetValue),
                Type.EmptyTypes, key, matched);
            steps.Add(Expression.IfThen(Expression.Not(find), Expression.Return(exit, Expression.Constant(false))));
        }

        static MethodCallExpression Read(Expression row, string field) =>
            Expression.Call(row, nameof(ObservationValue.GetProperty), Type.EmptyTypes, Expression.Constant(field));
    }
}
