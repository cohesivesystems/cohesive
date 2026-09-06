using System.Collections.Immutable;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.SQLite;

/// <summary>Compiles canonical row-query branches into reusable, parameterized SQLite statements.</summary>
public sealed class SqliteRelationQueryCompiler
{
    /// <summary>Version of exact lowering and result layout semantics.</summary>
    public const string CompilerProfile = "cohesive.adapters.sqlite.sql/compiler-v1";

    /// <summary>Creates a stateless compiler that can be shared across independent compilation requests.</summary>
    public SqliteRelationQueryCompiler() { }

    /// <summary>Inspects exact storage evidence and produces canonical contextual realization proof.</summary>
    /// <param name="request">Static plan, profile feasibility, placement and selected branches.</param>
    /// <param name="storage">Physical evidence pinned to the same plan and placement.</param>
    /// <returns>Bound realization with attributable success or failure assessments.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public RelationQueryBoundRealizationReport Realize(RelationQueryBoundRealizationRequest request,
        SqliteRelationQueryStorageBinding storage) => Prepare(request, storage).Report;

    /// <summary>Validates and lowers all selected branches without constructing SQL on subsequent invocations.</summary>
    /// <param name="request">Exact plan, feasibility and placement request.</param>
    /// <param name="storage">Validated schema/ingestion guarantees for the placed tables.</param>
    /// <returns>All branch artifacts on success; no executable artifacts when any branch fails.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SqliteRelationQueryCompilationResult Compile(RelationQueryBoundRealizationRequest request,
        SqliteRelationQueryStorageBinding storage)
    {
        var prepared = Prepare(request, storage);
        if (!prepared.Report.IsRealizable)
            return new(prepared.Report, [], RelationQueryNativeCompilationDiagnostic.FromBoundRealizationFailure(prepared.Report));
        var native = new RelationQueryNativeCompilationRequest(request.Plan, prepared.Report, request.Placement);
        var artifacts = ImmutableArray.CreateBuilder<SqliteRelationQueryCompiledArtifact>(request.Branches.Length);
        foreach (var branch in request.Branches)
        {
            var compiled = prepared.Branches[branch.Id];
            var selected = request.Selection.GetBranch(branch.Id);
            var provenance = RelationQueryNativeCompilationProvenanceFactory.Create(native, branch.Id, CompilerProfile,
                SqliteRelationQueryTargetProfile.ConventionSet, selected.ReachableNodes,
                [.. request.Plan.ExecutionSlice.Nodes.Where(node => selected.ContainsNode(node.Id))
                    .SelectMany(static node => node.ProjectionAssignments.Select(static assignment => assignment.Definition.Id))],
                [.. selected.Fields.Select(static field => field.Input.Id)]);
            artifacts.Add(new(branch, compiled.Template, compiled.Fields, compiled.Occurrences, compiled.Parameters, provenance, compiled.BindingPresenceOrdinal));
        }
        return new(prepared.Report, artifacts.MoveToImmutable(), []);
    }

    static Preparation Prepare(RelationQueryBoundRealizationRequest request, SqliteRelationQueryStorageBinding storage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storage);
        Dictionary<RelationQueryNativeResultBranchId, PreparedBranch> branches = [];
        Dictionary<RelationQueryNativeResultBranchId, RelationQueryContextualBranchFailure> failures = [];
        var invalid = request.ValidateInputs().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)?.Message;
        if (storage.Placement.Fingerprint != request.Placement.Fingerprint)
            invalid = "SQLite storage evidence is pinned to a different placement or compiled plan.";
        if (request.ProfileFeasibility.TargetProfile.Id != SqliteRelationQueryTargetProfile.ProfileId
            || request.ProfileFeasibility.TargetProfile.Target != SqliteRelationQueryTargetProfile.Target)
            invalid = "The request must use the SQLite canonical-v1 capability profile.";
        if (request.ProfileFeasibility.IsRealizable)
        {
            foreach (var branch in request.Branches)
            {
                try
                {
                    if (invalid is not null) throw new LoweringFailure("SQLITE_REL_INVALID", invalid, branch.Node, invalid: true);
                    branches.Add(branch.Id, new BranchCompiler(request, storage, branch).Compile());
                }
                catch (LoweringFailure failure)
                {
                    failures.Add(branch.Id, new(
                        failure.Invalid ? RelationQueryBoundAssessmentStatus.Invalid : RelationQueryBoundAssessmentStatus.Unavailable,
                        RelationQueryUnavailableReason.OperatingBoundaryInvalid, new(failure.Code), failure.Message,
                        "Supply the required physical evidence or use the reference interpreter for this branch.",
                        node: failure.Node, input: failure.Input, failedOperatingBoundary: SqliteRelationQueryTargetProfile.StorageBoundary));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
                {
                    failures.Add(branch.Id, new(RelationQueryBoundAssessmentStatus.Invalid,
                        RelationQueryUnavailableReason.OperatingBoundaryInvalid, new("SQLITE_REL_INVALID"),
                        exception.Message, "Repair the exact plan or storage binding before compiling.", node: branch.Node));
                }
            }
        }
        var evidence = new RelationQueryContextualEvidenceProjection(storage.Reference(request.Selection),
            request.ProfileFeasibility.IsRealizable
                ? RelationQueryContextualAssessmentProjector.Project(request, "sqlite/context",
                    branch => failures.GetValueOrDefault(branch.Id),
                    (_, _, failure) => new(EffectiveConfigurationOrigin.AdapterConvention, CompilerProfile,
                        node: failure?.Node, input: failure?.Input,
                        field: request.Selection.Fields.FirstOrDefault(field => field.Input.Id == failure?.Input)?.Input.Field.Path))
                : []);
        return new(RelationQueryBoundRealizationCompiler.Compile(request, evidence), branches);
    }

    sealed class BranchCompiler(RelationQueryBoundRealizationRequest request,
        SqliteRelationQueryStorageBinding storage, RelationQueryNativeResultBranch branch)
    {
        readonly RelationQueryBranchSelection selection = request.Selection.GetBranch(branch.Id);
        readonly Dictionary<QueryNodeId, RelationQueryExecutionNode> nodes = request.Plan.ExecutionSlice.Nodes.ToDictionary(static node => node.Id);
        readonly Dictionary<QueryNodeId, Scope> scopes = [];
        readonly Dictionary<QueryParameterId, SqliteRelationQueryParameter> parameters = [];
        int aliasOrdinal;
        string Alias() => $"c{aliasOrdinal++}";

        public PreparedBranch Compile()
        {
            if (branch.Kind != RelationQueryNativeResultKind.QueryRows)
                throw Fail("SQLITE_REL_TERMINAL", "SQLite v1 supports named query-row results only.", branch.Node);
            if (request.ProfileFeasibility.Decisions.Any(decision => selection.ContainsRequirement(decision.Requirement)
                    && decision is OverrideRelationQueryRealizationDecision))
                throw Fail("SQLITE_REL_OVERRIDE", "SQLite v1 cannot execute an explicit realization override.", branch.Node);
            if (selection.SourceInstances.Select(static source => source.ExecutionDomain).Distinct().Count() != 1)
                throw Fail("SQLITE_REL_DOMAIN", "All sources must use one database execution domain.", branch.Node);
            var scope = CompileNode(branch.Node);
            var terminal = new SqlSelectBuilder(scope.Query, "result");
            var fields = ImmutableArray.CreateBuilder<SqliteRelationQueryResultField>(branch.Fields.Length);
            var ordinal = 0;
            foreach (var field in branch.Fields)
            {
                var slot = scope.Fields[new(branch.Binding, field.Path)];
                terminal.Select(SqlExpression.Column("result", slot.Value), $"value_{ordinal}");
                terminal.Select(slot.Present is null ? One : SqlExpression.Column("result", slot.Present), $"present_{ordinal}");
                fields.Add(new(field, slot.Contract, ordinal, ordinal + 1));
                ordinal += 2;
            }
            var bindingPresenceOrdinal = ordinal++;
            terminal.Select(SqlExpression.IsNotNull(SqlExpression.Column("result", scope.BindingPresence[branch.Binding])), "binding_present");
            var occurrences = ImmutableArray.CreateBuilder<SqliteRelationQueryOccurrenceColumn>(scope.Identities.Count);
            foreach (var (binding, identity) in scope.Identities.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
            {
                terminal.Select(SqlExpression.Column("result", identity.Alias), $"identity_{ordinal}");
                occurrences.Add(new(binding, identity.Shape, ordinal++));
            }
            ApplyOrdering(terminal, scope, "result");
            return new(terminal.BuildTemplate(SqliteSqlDialect.Instance), fields.MoveToImmutable(), occurrences.MoveToImmutable(),
                [.. parameters.Values.OrderBy(static parameter => parameter.Id.Value, StringComparer.Ordinal)], bindingPresenceOrdinal);
        }

        Scope CompileNode(QueryNodeId id)
        {
            if (scopes.TryGetValue(id, out var existing)) return existing;
            var node = nodes[id];
            // Static demand owns effective topology. Bypassed nodes must never be reintroduced by native lowering.
            var inputs = node.LogicalPlan.EffectiveInputs;
            Scope result = node.CanonicalNode switch
            {
                SourceQueryNode source => Source(source),
                FilterQueryNode filter => Filter(node, CompileNode(inputs[0]), filter),
                JoinQueryNode join => Join(node, CompileNode(inputs[0]), CompileNode(inputs[1]), join),
                ProjectQueryNode project => Project(node, CompileNode(inputs[0]), project),
                SelectRepresentativeQueryNode representative => Representative(node, CompileNode(inputs[0]), representative),
                OrderQueryNode order => Order(node, CompileNode(inputs[0]), order),
                _ => throw Fail("SQLITE_REL_NODE", $"Node '{node.CanonicalNode.GetType().Name}' is outside the SQLite v1 slice.", id)
            };
            scopes.Add(id, result);
            return result;
        }

        Scope Source(SourceQueryNode source)
        {
            var placement = selection.PlacementBindings.Single(binding => binding.Node == source.Id);
            if (placement.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
                || placement.Acquisition != RelationQuerySourceAcquisitionKind.BoundedEnumeration
                || placement.Partition is not null || !placement.RelationshipKeys.IsDefaultOrEmpty)
                throw Fail("SQLITE_REL_SOURCE", "Source must be a complete table enumeration without partition or relationship-key selectors.", source.Id);
            var table = storage.Tables.Single(table => table.Placement == placement.Id);
            var identity = placement.Identity ?? throw Fail("SQLITE_REL_IDENTITY", "A non-null unique INTEGER identity selector is required.", source.Id);
            if (identity.SemanticPath is not { } identityPath)
                throw Fail("SQLITE_REL_IDENTITY", "The identity must name its canonical semantic field for ordering proof.", source.Id);
            var builder = new SqlSelectBuilder(new SqlQualifiedTable(table.Table), "source");
            Scope result = new();
            foreach (var field in selection.Fields.Where(field => field.Input.Producer == source.Id))
            {
                var contract = field.Input.ValueContract ?? throw Fail("SQLITE_REL_ENCODING", "Field has no resolved scalar contract.", source.Id);
                if (field.Input.Field.Path.Segments.Length != 1)
                    throw Fail("SQLITE_REL_PATH", "Only top-level scalar fields are supported.", source.Id);
                RequireEncoding(contract, source.Id, field.Input.Id);
                var column = placement.Fields.Single(mapped => mapped.Input == field.Input.Id).SourceSelector;
                var presence = table.Presence.SingleOrDefault(mapped => mapped.Input == field.Input.Id);
                if (contract.Presence == FieldPresence.Optional && presence is null)
                    throw Fail("SQLITE_REL_PRESENCE", $"Optional field '{field.Input.Field.Path}' requires an explicit presence column.", source.Id, field.Input.Id);
                if (contract.Presence == FieldPresence.Required && presence is not null)
                    throw Fail("SQLITE_REL_PRESENCE", "Required fields cannot be assigned optional presence bits.", source.Id);
                var isIdentity = field.Input.Field.Path == identityPath;
                if (isIdentity && (column != identity.SourceSelector || !Required(contract)
                    || !Integer(contract)))
                    throw Fail("SQLITE_REL_IDENTITY", "Identity field must use the same unique non-null INTEGER column as placement identity.", source.Id);
                AddField(builder, result, new(source.Binding, field.Input.Field.Path), new(
                    SqlExpression.Column("source", column), presence is null ? One : SqlExpression.Column("source", presence.Column),
                    contract, isIdentity ? source.Binding : null));
            }
            var identityAlias = Select(builder, result, SqlExpression.Column("source", identity.SourceSelector));
            result.Identities.Add(source.Binding, new(identityAlias, source.Shape));
            result.BindingPresence.Add(source.Binding, identityAlias);
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Filter(RelationQueryExecutionNode node, Scope input, FilterQueryNode filter)
        {
            var (builder, result, environment) = Wrap(input);
            builder.Where(Boolean(Expression(filter.Predicate, environment, node.Id), node.Id));
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Project(RelationQueryExecutionNode node, Scope input, ProjectQueryNode project)
        {
            var (builder, result, environment) = Wrap(input);
            var bindingPresence = Select(builder, result, One);
            result.BindingPresence.Add(project.ResultBinding, bindingPresence);
            foreach (var assignment in node.ProjectionAssignments)
            {
                if (assignment.Definition.Target.Segments.Length != 1
                    || assignment.Definition.Target.Segments[0].Kind != SegmentKind.Field
                    || assignment.Definition.Target.Segments[0].Segment is not { } targetName)
                    throw Fail("SQLITE_REL_PATH", "Projection targets must be top-level fields.", node.Id);
                var cell = Expression(assignment.Definition.Value, environment, node.Id);
                if (cell.Contract.Type is null)
                {
                    // Untyped null/missing literals have no payload encoding. Their target supplies the scalar domain.
                    var graph = request.Plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == project.ResultShape.GraphId).Graph;
                    if (!graph.TryGetShape(project.ResultShape.ShapeId, out var shape)
                        || !shape.TryGetField(targetName, out var field))
                        throw Fail("SQLITE_REL_FIELD", "Projection target has no exact shape-field contract.", node.Id);
                    var target = ValueContract.FromField(field);
                    RequireEncoding(target, node.Id);
                    cell = cell with { Contract = new(target.Type, target.Shape, target.Cardinality, cell.Contract.Presence, cell.Contract.Nullability) };
                }
                AddField(builder, result, new(project.ResultBinding, assignment.Definition.Target), cell);
            }
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Join(RelationQueryExecutionNode node, Scope left, Scope right, JoinQueryNode join)
        {
            if (join.Kind is not (JoinKind.Inner or JoinKind.Left))
                throw Fail("SQLITE_REL_JOIN", "Only inner and left joins are supported.", node.Id);
            if (left.Identities.Keys.Intersect(right.Identities.Keys).Any())
                throw Fail("SQLITE_REL_JOIN", "Joined sources require distinct canonical bindings.", node.Id);
            var environment = Environment(left, "left");
            foreach (var pair in Environment(right, "right")) environment.Add(pair.Key, pair.Value);
            var predicate = Boolean(Expression(join.Predicate, environment, node.Id), node.Id);
            var builder = new SqlSelectBuilder(left.Query, "left");
            builder.Join(right.Query, "right", join.Kind == JoinKind.Inner ? SqlJoinKind.Inner : SqlJoinKind.Left, predicate);
            Scope result = new();
            foreach (var (key, cell) in environment)
            {
                var outer = join.Kind == JoinKind.Left && right.Fields.ContainsKey(key);
                AddField(builder, result, key, outer ? cell with
                {
                    Present = SqlExpression.Binary(SqlBinaryOperator.And,
                        SqlExpression.IsNotNull(SqlExpression.Column("right", right.BindingPresence[key.Binding])),
                        SqlExpression.Coalesce(cell.Present, Zero)),
                    Contract = new ValueContract(cell.Contract.Type, cell.Contract.Shape, cell.Contract.Cardinality, FieldPresence.Optional, cell.Contract.Nullability)
                } : cell);
            }
            foreach (var (side, input) in new[] { ("left", left), ("right", right) })
            {
                foreach (var (binding, presence) in input.BindingPresence)
                {
                    result.BindingPresence.Add(binding, Select(builder, result, SqlExpression.Column(side, presence)));
                }
                foreach (var (binding, identity) in input.Identities)
                {
                    result.Identities.Add(binding, identity with { Alias = Select(builder, result, SqlExpression.Column(side, identity.Alias)) });
                }
            }
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Representative(RelationQueryExecutionNode node, Scope input, SelectRepresentativeQueryNode definition)
        {
            var (builder, ranked, environment) = Wrap(input, retainOrder: false);
            List<SqlExpression> partitions = [];
            foreach (var key in definition.Keys)
            {
                var cell = Expression(key, environment, node.Id);
                RequireEquality(cell, node.Id);
                if (cell.Contract.Presence == FieldPresence.Optional) partitions.Add(cell.Present);
                partitions.Add(CanonicalValue(cell));
            }
            var orderings = Ordering(definition.Orderings, environment, input, node.Id);
            var rank = Alias();
            builder.Select(SqlExpression.RowNumber([.. partitions], orderings), rank);
            ranked.Query = builder.BuildQuery();
            var (winner, result, _) = Wrap(ranked, retainOrder: false);
            winner.Where(SqlExpression.Binary(SqlBinaryOperator.Equal, SqlExpression.Column("input", rank), One));
            result.Query = winner.BuildQuery();
            return result;
        }

        Scope Order(RelationQueryExecutionNode node, Scope input, OrderQueryNode definition)
        {
            var (builder, result, environment) = Wrap(input, retainOrder: false);
            foreach (var ordering in Ordering(definition.Orderings, environment, input, node.Id))
            {
                var alias = Select(builder, result, ordering.Expression);
                builder.OrderBy(ordering.Expression, ordering.Direction, ordering.NullPlacement);
                result.Orderings.Add(new(alias, ordering.Direction, ordering.NullPlacement));
            }
            result.Query = builder.BuildQuery();
            return result;
        }

        ImmutableArray<SqlOrdering> Ordering(ImmutableArray<QueryOrdering> definitions,
            Dictionary<FieldKey, Cell> environment, Scope input, QueryNodeId node)
        {
            HashSet<ValueBindingId> orderedIdentities = [];
            var result = ImmutableArray.CreateBuilder<SqlOrdering>(definitions.Length);
            foreach (var definition in definitions)
            {
                var cell = Expression(definition.Key, environment, node);
                if (!Integer(cell.Contract))
                    throw Fail("SQLITE_REL_ORDER_ENCODING", "SQLite v1 ordering requires INTEGER scalar encodings; text, decimal and temporal order need further domain evidence.", node);
                if (cell.Identity is { } identity) orderedIdentities.Add(identity);
                result.Add(new(CanonicalValue(cell), definition.Direction == QuerySortDirection.Ascending
                    ? SqlSortDirection.Ascending : SqlSortDirection.Descending,
                    definition.NullPlacement == QueryNullPlacement.First ? SqlNullPlacement.First : SqlNullPlacement.Last));
            }
            if (!input.Identities.Keys.All(orderedIdentities.Contains))
                throw Fail("SQLITE_REL_UNIQUE_ORDER", "Ordering must include each contributing source identity. A source identity alone may repeat after a join; SQL tie-breaking cannot preserve unique-best semantics.", node);
            return result.MoveToImmutable();
        }

        Cell Expression(Expr expression, Dictionary<FieldKey, Cell> environment, QueryNodeId node) => expression switch
        {
            FieldExpr field => ReadField(field.Binding, field.Path, environment, node),
            FieldRefExpr field => ReadField(null, field.Path, environment, node),
            ParameterExpr parameter => Parameter(parameter, node),
            ConstantExpr constant => Constant(constant.Value, null, node),
            LiteralExpr literal => Constant(literal.Value, literal.Type, node),
            UnaryExpr { Operator: UnaryOperator.Not } unary => new(
                SqlExpression.Unary(SqlUnaryOperator.Not, Boolean(Expression(unary.Operand, environment, node), node)), One, BooleanContract),
            BinaryExpr binary => Binary(binary, environment, node),
            _ => throw Fail("SQLITE_REL_EXPRESSION", $"Expression '{expression.GetType().Name}' is outside the SQLite v1 slice.", node)
        };

        Cell Parameter(ParameterExpr parameter, QueryNodeId node)
        {
            var contract = request.Plan.InputContract.Parameters.Single(p => p.Definition.Id.Value == parameter.Parameter);
            if (!Required(contract.ValueContract) || contract.Definition.DefaultKind != QueryParameterDefaultKind.None)
                throw Fail("SQLITE_REL_PARAMETER", "SQLite v1 parameters must be required and non-null, without defaults.", node);
            RequireEncoding(contract.ValueContract, node, contract.Input.Id);
            parameters.TryAdd(contract.Definition.Id, new(contract.Definition.Id, contract.ValueContract));
            return new(SqlExpression.RuntimeParameter(parameter.Parameter), One, contract.ValueContract);
        }

        static Cell ReadField(ValueBindingId? binding, FieldPath path, Dictionary<FieldKey, Cell> environment, QueryNodeId node)
        {
            var matches = environment.Where(pair => pair.Key.Path == path && (binding is null || pair.Key.Binding == binding)).ToArray();
            if (matches.Length != 1) throw Fail("SQLITE_REL_FIELD", "A field must resolve to exactly one visible binding.", node);
            return matches[0].Value;
        }

        static Cell Constant(ObservationValue value, TypeRef? type, QueryNodeId node)
        {
            type ??= value.Kind switch
            {
                ObservationValueKind.Int64 => new ScalarTypeRef(ScalarTypeKind.Int64),
                ObservationValueKind.Bool => new ScalarTypeRef(ScalarTypeKind.Bool),
                ObservationValueKind.String => new ScalarTypeRef(ScalarTypeKind.String),
                ObservationValueKind.Null or ObservationValueKind.Undefined => null,
                _ => throw Fail("SQLITE_REL_CONSTANT", "Only integer, boolean, string and null/missing constants are supported.", node)
            };
            var contract = new ValueContract(type, presence: value.Kind == ObservationValueKind.Undefined ? FieldPresence.Optional : FieldPresence.Required,
                nullability: value.Kind == ObservationValueKind.Null ? FieldNullability.Nullable : FieldNullability.NonNullable);
            return new(value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined ? SqlExpression.Constant(null)
                : SqlExpression.Constant(SqliteScalarCodec.Encode(contract, value)),
                value.Kind == ObservationValueKind.Undefined ? Zero : One, contract);
        }

        Cell Binary(BinaryExpr binary, Dictionary<FieldKey, Cell> environment, QueryNodeId node)
        {
            var left = Expression(binary.Left, environment, node);
            var right = Expression(binary.Right, environment, node);
            SqlExpression sql;
            if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
                sql = SqlExpression.Binary(binary.Operator == BinaryOperator.And ? SqlBinaryOperator.And : SqlBinaryOperator.Or,
                    Boolean(left, node), Boolean(right, node));
            else
            {
                RequireEquality(left, node);
                RequireEquality(right, node);
                if (left.Contract.Type is not null && right.Contract.Type is not null
                    && left.Contract.Type != right.Contract.Type && !(Integer(left.Contract) && Integer(right.Contract)))
                    throw Fail("SQLITE_REL_COMPARISON", "Operands must have the same exact scalar comparison domain.", node);
                if (binary.Operator is BinaryOperator.Eq or BinaryOperator.Ne)
                {
                    sql = SqlExpression.Binary(SqlBinaryOperator.And,
                        SqlExpression.Binary(SqlBinaryOperator.Equal, left.Present, right.Present),
                        SqlExpression.Binary(SqlBinaryOperator.IsNotDistinctFrom, CanonicalValue(left), CanonicalValue(right)));
                    if (binary.Operator == BinaryOperator.Ne) sql = SqlExpression.Unary(SqlUnaryOperator.Not, sql);
                }
                else
                {
                    if (!Required(left.Contract) || !Required(right.Contract) || !Integer(left.Contract) || !Integer(right.Contract))
                        throw Fail("SQLITE_REL_COMPARISON", "Ordered comparisons require non-null, non-missing INTEGER operands.", node);
                    var op = binary.Operator switch
                    {
                        BinaryOperator.Gt => SqlBinaryOperator.GreaterThan, BinaryOperator.Ge => SqlBinaryOperator.GreaterThanOrEqual,
                        BinaryOperator.Lt => SqlBinaryOperator.LessThan, BinaryOperator.Le => SqlBinaryOperator.LessThanOrEqual,
                        _ => throw Fail("SQLITE_REL_EXPRESSION", $"Operator '{binary.Operator}' is unsupported.", node)
                    };
                    sql = SqlExpression.Binary(op, left.Value, right.Value);
                }
            }
            return new(sql, One, BooleanContract);
        }

        static void RequireEquality(Cell cell, QueryNodeId node)
        {
            if (cell.Contract.Type is not null && !Integer(cell.Contract)
                && cell.Contract.Type is not ScalarTypeRef { Kind: ScalarTypeKind.Bool or ScalarTypeKind.String })
                throw Fail("SQLITE_REL_EQUALITY_ENCODING", "Equality/grouping requires an exact INTEGER or ordinal TEXT scalar encoding.", node);
        }

        static SqlExpression CanonicalValue(Cell cell)
        {
            // Storage evidence guarantees NULL payloads for missing fields, including absent join bindings.
            var value = cell.Value;
            return cell.Contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.String } ? SqlExpression.Collate(value, "BINARY") : value;
        }
        static SqlExpression Boolean(Cell cell, QueryNodeId node) => cell.Contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.Bool } && Required(cell.Contract)
            ? cell.Value : throw Fail("SQLITE_REL_BOOLEAN", "Predicates require non-null, non-missing boolean values.", node);
        static bool Integer(ValueContract contract) => contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 };
        static bool Required(ValueContract contract) => contract.Presence == FieldPresence.Required && contract.Nullability == FieldNullability.NonNullable;

        static void RequireEncoding(ValueContract contract, QueryNodeId node, RelationQueryInputId? input = null)
        {
            try { _ = SqliteScalarCodec.GetStorageType(contract); }
            catch (NotSupportedException exception) { throw Fail("SQLITE_REL_ENCODING", exception.Message, node, input); }
        }

        (SqlSelectBuilder Builder, Scope Result, Dictionary<FieldKey, Cell> Environment) Wrap(Scope input, bool retainOrder = true)
        {
            var builder = new SqlSelectBuilder(input.Query, "input");
            Scope result = new();
            var environment = Environment(input, "input");
            foreach (var (key, cell) in environment) AddField(builder, result, key, cell);
            foreach (var (binding, identity) in input.Identities)
            {
                result.Identities.Add(binding, identity with { Alias = Select(builder, result, SqlExpression.Column("input", identity.Alias)) });
            }
            foreach (var (binding, presence) in input.BindingPresence)
            {
                result.BindingPresence.Add(binding, Select(builder, result, SqlExpression.Column("input", presence)));
            }
            if (retainOrder)
            {
                foreach (var order in input.Orderings)
                {
                    result.Orderings.Add(order with { Alias = Select(builder, result, SqlExpression.Column("input", order.Alias)) });
                }
                ApplyOrdering(builder, input, "input");
            }
            return (builder, result, environment);
        }

        void AddField(SqlSelectBuilder builder, Scope scope, FieldKey key, Cell cell)
        {
            var value = Select(builder, scope, cell.Value);
            // Required field presence is a compile-time fact. Carry a physical bit only where missing is possible.
            var presence = cell.Contract.Presence == FieldPresence.Required ? null : Select(builder, scope, cell.Present);
            scope.Fields.Add(key, new(value, presence, cell.Contract, cell.Identity));
        }
        string Select(SqlSelectBuilder builder, Scope scope, SqlExpression expression)
        {
            if (scope.Columns.TryGetValue(expression, out var alias)) return alias;
            alias = Alias();
            builder.Select(expression, alias);
            scope.Columns.Add(expression, alias);
            return alias;
        }
        static Dictionary<FieldKey, Cell> Environment(Scope scope, string alias) => scope.Fields.ToDictionary(static pair => pair.Key,
            pair => new Cell(SqlExpression.Column(alias, pair.Value.Value), pair.Value.Present is null ? One
                : SqlExpression.Column(alias, pair.Value.Present), pair.Value.Contract, pair.Value.Identity));
        static void ApplyOrdering(SqlSelectBuilder builder, Scope scope, string alias)
        {
            foreach (var order in scope.Orderings) builder.OrderBy(SqlExpression.Column(alias, order.Alias), order.Direction, order.NullPlacement);
        }
    }

    static readonly SqlExpression One = SqlExpression.Constant(1L);
    static readonly SqlExpression Zero = SqlExpression.Constant(0L);
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static LoweringFailure Fail(string code, string message, QueryNodeId node, RelationQueryInputId? input = null) => new(code, message, node, input: input);
    sealed class LoweringFailure(string code, string message, QueryNodeId node, bool invalid = false, RelationQueryInputId? input = null) : Exception(message)
    {
        public string Code { get; } = code;
        public QueryNodeId Node { get; } = node;
        public bool Invalid { get; } = invalid;
        public RelationQueryInputId? Input { get; } = input;
    }
    readonly record struct FieldKey(ValueBindingId Binding, FieldPath Path);
    sealed record Cell(SqlExpression Value, SqlExpression Present, ValueContract Contract, ValueBindingId? Identity = null);
    sealed record Slot(string Value, string? Present, ValueContract Contract, ValueBindingId? Identity);
    sealed record IdentitySlot(string Alias, QualifiedShapeId Shape);
    sealed record OrderingSlot(string Alias, SqlSortDirection Direction, SqlNullPlacement NullPlacement);
    sealed class Scope
    {
        public SqlSelectQuery Query { get; set; } = null!;
        public Dictionary<SqlExpression, string> Columns { get; } = [];
        public Dictionary<FieldKey, Slot> Fields { get; } = [];
        public Dictionary<ValueBindingId, IdentitySlot> Identities { get; } = [];
        public Dictionary<ValueBindingId, string> BindingPresence { get; } = [];
        public List<OrderingSlot> Orderings { get; } = [];
    }
    sealed record Preparation(RelationQueryBoundRealizationReport Report, Dictionary<RelationQueryNativeResultBranchId, PreparedBranch> Branches);
    sealed record PreparedBranch(SqlCommandTemplate Template, ImmutableArray<SqliteRelationQueryResultField> Fields,
        ImmutableArray<SqliteRelationQueryOccurrenceColumn> Occurrences, ImmutableArray<SqliteRelationQueryParameter> Parameters, int BindingPresenceOrdinal);
}
