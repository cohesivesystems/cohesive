using System.Collections.Immutable;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Model.Expressions;
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
    public const string CompilerProfile = "cohesive.adapters.sqlite.sql/compiler-v3";

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
        readonly Dictionary<QueryNodeId, HashSet<FieldKey>> liveFields = [];
        // This is a readability budget, not a SQLite engine identifier limit. Case-insensitive allocation
        // conservatively prevents SQLite's ASCII-insensitive identifier lookup from merging distinct slots.
        const int AliasByteBudget = 63;
        readonly SqlAliasAllocator relationAliases = NewAliases();
        internal static SqlAliasAllocator NewAliases() => new(AliasByteBudget, StringComparer.OrdinalIgnoreCase);

        public PreparedBranch Compile()
        {
            if (branch.Kind != RelationQueryNativeResultKind.QueryRows)
                throw Fail("SQLITE_REL_TERMINAL", "SQLite v1 supports named query-row results only.", branch.Node);
            if (request.ProfileFeasibility.Decisions.Any(decision => selection.ContainsRequirement(decision.Requirement)
                    && decision is OverrideRelationQueryRealizationDecision))
                throw Fail("SQLITE_REL_OVERRIDE", "SQLite v1 cannot execute an explicit realization override.", branch.Node);
            if (selection.SourceInstances.Select(static source => source.ExecutionDomain).Distinct().Count() != 1)
                throw Fail("SQLITE_REL_DOMAIN", "All sources must use one database execution domain.", branch.Node);
            CollectLiveFields(branch.Node, branch.Fields.Select(field => new FieldKey(branch.Binding, field.Path)).ToHashSet());
            var scope = CompileNode(branch.Node);
            var terminal = new SqlSelectBuilder(scope.Query, scope.Name);
            var resultAliases = NewAliases();
            var fields = ImmutableArray.CreateBuilder<SqliteRelationQueryResultField>(branch.Fields.Length);
            var ordinal = 0;
            foreach (var field in branch.Fields)
            {
                var slot = scope.Fields[new(branch.Binding, field.Path)];
                var name = $"{branch.Binding.Value}_{field.Path}";
                terminal.Select(SqlExpression.Column(scope.Name, slot.Value), resultAliases.Allocate(name, $"value:{field.Path}", "value"));
                terminal.Select(slot.Present is null ? One : SqlExpression.Column(scope.Name, slot.Present),
                    resultAliases.Allocate($"{name}_present", $"presence:{field.Path}", "present"));
                fields.Add(new(field, slot.Contract, ordinal, ordinal + 1));
                ordinal += 2;
            }
            var bindingPresenceOrdinal = ordinal++;
            terminal.Select(SqlExpression.IsNotNull(SqlExpression.Column(scope.Name, scope.BindingPresence[branch.Binding])),
                resultAliases.Allocate($"{branch.Binding.Value}_binding_present", "binding-presence", "binding_present"));
            var occurrences = ImmutableArray.CreateBuilder<SqliteRelationQueryOccurrenceColumn>(scope.Identities.Count);
            foreach (var (binding, identity) in scope.Identities.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
            {
                var columns = ImmutableArray.CreateBuilder<SqliteRelationQueryIdentityColumn>(identity.Components.Length);
                foreach (var component in identity.Components)
                {
                    terminal.Select(SqlExpression.Column(scope.Name, component.Alias),
                        resultAliases.Allocate($"{binding.Value}_identity", $"identity:{binding.Value}:{columns.Count}", "identity"));
                    columns.Add(new(ordinal++, component.Contract));
                }
                occurrences.Add(new(binding, identity.Shape, columns.MoveToImmutable()));
            }
            ApplyOrdering(terminal, scope, scope.Name);
            return new(terminal.BuildTemplate(SqliteSqlDialect.Instance, SqlFormatting.Indented), fields.MoveToImmutable(), occurrences.MoveToImmutable(),
                [.. parameters.Values.OrderBy(static parameter => parameter.Id.Value, StringComparer.Ordinal)], bindingPresenceOrdinal);
        }

        // Consume canonical demand analyses instead of independently walking expression syntax.
        // Each stage carries only fields needed by its consumers; identity/presence/order slots have
        // separate ownership and remain available even after a value's final semantic use.
        void CollectLiveFields(QueryNodeId id, HashSet<FieldKey> required)
        {
            var first = !liveFields.TryGetValue(id, out var live);
            live ??= [];
            var previousCount = live.Count;
            live.UnionWith(required);
            if (!first && live.Count == previousCount) return;
            liveFields[id] = live;
            var node = nodes[id];
            HashSet<FieldKey> inputs = [.. live];
            if (node.CanonicalNode is ProjectQueryNode project)
                inputs.RemoveWhere(field => field.Binding == project.ResultBinding);
            foreach (var site in node.ExpressionSites)
                foreach (var field in site.Analysis.Requirements.Fields)
                    if (field.Binding is { } binding) inputs.Add(new(binding, field.Path));
            foreach (var input in node.LogicalPlan.EffectiveInputs)
            {
                var visible = nodes[input].OutputBindings.Select(static binding => binding.Binding).ToHashSet();
                CollectLiveFields(input, inputs.Where(field => visible.Contains(field.Binding)).ToHashSet());
            }
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
            foreach (var key in result.Fields.Keys.Where(key => !liveFields[id].Contains(key)).ToArray()) result.Fields.Remove(key);
            result.Name = relationAliases.Allocate(id.Value, $"node:{id.Value}", "stage");
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
            var identity = placement.Identity ?? throw Fail("SQLITE_REL_IDENTITY", "A non-null unique integer/text identity is required.", source.Id);
            var fields = selection.Fields.Where(field => field.Input.Producer == source.Id).ToArray();
            var identityFields = table.IdentityFields.IsEmpty
                ? fields.Where(field => field.Input.Field.Path == identity.SemanticPath).Select(static field => field.Input.Id).ToImmutableArray()
                : table.IdentityFields;
            if (identityFields.IsEmpty || identityFields.Any(id => !fields.Any(field => field.Input.Id == id))
                || (identityFields.Length > 1 && identity.SemanticPath is not null))
                throw Fail("SQLITE_REL_IDENTITY", "Identity components must name selected source fields; composite identities require a source-native selector without a single semantic path.", source.Id);
            if (table.AsciiOrderingFields.Any(id => !fields.Any(field => field.Input.Id == id
                    && field.Input.ValueContract?.Type is ScalarTypeRef { Kind: ScalarTypeKind.String })))
                throw Fail("SQLITE_REL_ORDER_ENCODING", "ASCII ordering evidence must name selected text fields from this source.", source.Id);
            var sourceAlias = relationAliases.Allocate(source.Binding.Value, $"source:{source.Id.Value}", "source");
            var builder = new SqlSelectBuilder(new SqlQualifiedTable(table.Table), sourceAlias);
            Scope result = new();
            foreach (var field in fields)
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
                var isIdentity = identityFields.Contains(field.Input.Id);
                if (isIdentity && (!Required(contract) || !(Integer(contract) || Text(contract))
                    || (identity.SemanticPath is not null
                        && (column != identity.SourceSelector || field.Input.Field.Path != identity.SemanticPath))))
                    throw Fail("SQLITE_REL_IDENTITY", "Identity components require non-null, non-missing INTEGER or TEXT fields and exact placement selectors.", source.Id);
                AddField(builder, result, new(source.Binding, field.Input.Field.Path), new(
                    SqlExpression.Column(sourceAlias, column), presence is null ? One : SqlExpression.Column(sourceAlias, presence.Column),
                    contract, new(source.Binding, field.Input.Field.Path), table.AsciiOrderingFields.Contains(field.Input.Id)));
            }
            var components = identityFields.Select(id =>
            {
                var path = fields.Single(field => field.Input.Id == id).Input.Field.Path;
                var slot = result.Fields[new(source.Binding, path)];
                return new IdentityComponent(slot.Value, slot.Contract);
            }).ToImmutableArray();
            result.Identities.Add(source.Binding, new(components, source.Shape));
            result.BindingPresence.Add(source.Binding, components[0].Alias);
            result.UniqueKeys.Add(identityFields.Select(id => new FieldKey(source.Binding,
                fields.Single(field => field.Input.Id == id).Input.Field.Path)).ToHashSet());
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Filter(RelationQueryExecutionNode node, Scope input, FilterQueryNode filter)
        {
            var (builder, result, environment) = Wrap(input, liveFields[node.Id]);
            builder.Where(Boolean(Expression(filter.Predicate, environment, node.Id), node.Id));
            var refined = Refine(filter.Predicate, environment, whenTrue: true);
            foreach (var (key, cell) in refined)
                if (result.Fields.TryGetValue(key, out var slot))
                    result.Fields[key] = slot with { Contract = cell.Contract, AbsentBinding = cell.AbsentBinding };
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Project(RelationQueryExecutionNode node, Scope input, ProjectQueryNode project)
        {
            var (builder, result, environment) = Wrap(input, liveFields[node.Id]);
            var bindingPresence = Select(builder, result, One, $"{project.ResultBinding.Value}_binding_present", $"binding:{project.ResultBinding.Value}");
            result.BindingPresence.Add(project.ResultBinding, bindingPresence);
            foreach (var assignment in node.ProjectionAssignments)
            {
                if (!liveFields[node.Id].Contains(new(project.ResultBinding, assignment.Definition.Target))) continue;
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
            var environment = Environment(left, left.Name);
            foreach (var pair in Environment(right, right.Name)) environment.Add(pair.Key, pair.Value);
            var predicate = Boolean(Expression(join.Predicate, environment, node.Id), node.Id);
            var builder = new SqlSelectBuilder(left.Query, left.Name);
            builder.Join(right.Query, right.Name, join.Kind == JoinKind.Inner ? SqlJoinKind.Inner : SqlJoinKind.Left, predicate);
            Scope result = new();
            foreach (var (key, cell) in environment)
            {
                if (!liveFields[node.Id].Contains(key)) continue;
                var outer = join.Kind == JoinKind.Left && right.Fields.ContainsKey(key);
                AddField(builder, result, key, outer ? cell with
                {
                    Present = cell.Contract.Presence == FieldPresence.Required
                        ? SqlExpression.IsNotNull(SqlExpression.Column(right.Name, right.BindingPresence[key.Binding]))
                        : SqlExpression.Binary(SqlBinaryOperator.And,
                            SqlExpression.IsNotNull(SqlExpression.Column(right.Name, right.BindingPresence[key.Binding])),
                            SqlExpression.Coalesce(cell.Present, Zero)),
                    Contract = new ValueContract(cell.Contract.Type, cell.Contract.Shape, cell.Contract.Cardinality, FieldPresence.Optional, cell.Contract.Nullability),
                    AbsentBinding = cell.Contract.Presence == FieldPresence.Required ? key.Binding : cell.AbsentBinding
                } : cell);
            }
            foreach (var (side, input) in new[] { (left.Name, left), (right.Name, right) })
            {
                foreach (var (binding, presence) in input.BindingPresence)
                {
                    result.BindingPresence.Add(binding, Select(builder, result, SqlExpression.Column(side, presence), presence, $"binding:{binding.Value}"));
                }
                foreach (var (binding, identity) in input.Identities)
                {
                    result.Identities.Add(binding, CopyIdentity(builder, result, side, binding, identity));
                }
            }
            result.Equalities.AddRange(left.Equalities);
            result.Equalities.AddRange(right.Equalities);
            var pairs = JoinEqualities(join.Predicate, environment);
            if (join.Kind == JoinKind.Inner) result.Equalities.AddRange(pairs);
            foreach (var leftKey in left.UniqueKeys)
                foreach (var rightKey in right.UniqueKeys)
                    result.UniqueKeys.Add([.. leftKey, .. rightKey]);
            var leftFields = left.Fields.Values.Select(static slot => slot.Origin).OfType<FieldKey>().ToHashSet();
            var rightFields = right.Fields.Values.Select(static slot => slot.Origin).OfType<FieldKey>().ToHashSet();
            var matchedRight = MatchedFields(pairs, leftFields);
            if (IsUnique(right, matchedRight)) result.UniqueKeys.AddRange(left.UniqueKeys);
            if (join.Kind == JoinKind.Inner && IsUnique(left, MatchedFields(pairs, rightFields)))
                result.UniqueKeys.AddRange(right.UniqueKeys);
            result.Query = builder.BuildQuery();
            return result;
        }

        Scope Representative(RelationQueryExecutionNode node, Scope input, SelectRepresentativeQueryNode definition)
        {
            var (builder, ranked, environment) = Wrap(input, liveFields[node.Id], retainOrder: false);
            List<SqlExpression> partitions = [];
            foreach (var key in definition.Keys)
            {
                var cell = Expression(key, environment, node.Id);
                RequireEquality(cell, node.Id);
                if (cell.Contract.Presence == FieldPresence.Optional) partitions.Add(cell.Present);
                partitions.Add(CanonicalValue(cell));
            }
            var partitionCells = definition.Keys.Select(key => Expression(key, environment, node.Id)).ToArray();
            var partitionFields = partitionCells.Select(static cell => cell.Origin).ToArray();
            var orderings = Ordering(definition.Orderings, environment, input, node.Id,
                partitionFields.OfType<FieldKey>().ToHashSet());
            var rank = ranked.Aliases.Allocate("representative_rank", $"rank:{node.Id.Value}", "rank");
            ranked.Name = relationAliases.Allocate($"{node.Id.Value}_ranked", $"ranked:{node.Id.Value}", "ranked");
            builder.Select(SqlExpression.RowNumber([.. partitions], orderings), rank);
            ranked.Query = builder.BuildQuery();
            var (winner, result, _) = Wrap(ranked, liveFields[node.Id], retainOrder: false);
            winner.Where(SqlExpression.Binary(SqlBinaryOperator.Equal, SqlExpression.Column(ranked.Name, rank), One));
            // Partition equality distinguishes missing from null; ordering intentionally places both
            // in the same nullish bucket. Such a partition key alone cannot prove a tie-free order.
            if (partitionCells.All(static cell => cell.Origin is not null
                    && !(cell.Contract.Presence == FieldPresence.Optional && cell.Contract.Nullability == FieldNullability.Nullable)))
                result.UniqueKeys.Add(partitionFields.OfType<FieldKey>().ToHashSet());
            result.Query = winner.BuildQuery();
            return result;
        }

        Scope Order(RelationQueryExecutionNode node, Scope input, OrderQueryNode definition)
        {
            var (builder, result, environment) = Wrap(input, liveFields[node.Id], retainOrder: false);
            foreach (var ordering in Ordering(definition.Orderings, environment, input, node.Id))
            {
                var alias = Select(builder, result, ordering.Expression, "ordering_key", $"ordering:{result.Orderings.Count}");
                builder.OrderBy(ordering.Expression, ordering.Direction, ordering.NullPlacement);
                result.Orderings.Add(new(alias, ordering.Direction, ordering.NullPlacement));
            }
            result.Query = builder.BuildQuery();
            return result;
        }

        ImmutableArray<SqlOrdering> Ordering(ImmutableArray<QueryOrdering> definitions,
            Dictionary<FieldKey, Cell> environment, Scope input, QueryNodeId node, HashSet<FieldKey>? partitionFields = null)
        {
            HashSet<FieldKey> orderedFields = partitionFields is null ? [] : [.. partitionFields];
            var result = ImmutableArray.CreateBuilder<SqlOrdering>(definitions.Length);
            foreach (var definition in definitions)
            {
                var cell = Expression(definition.Key, environment, node);
                if (!Integer(cell.Contract) && !(Text(cell.Contract) && cell.AsciiOrdering))
                    throw Fail("SQLITE_REL_ORDER_ENCODING", "Ordering requires INTEGER or TEXT with explicit ASCII-domain evidence.", node);
                if (cell.Origin is { } origin) orderedFields.Add(origin);
                result.Add(new(CanonicalValue(cell), definition.Direction == QuerySortDirection.Ascending
                    ? SqlSortDirection.Ascending : SqlSortDirection.Descending,
                    definition.NullPlacement == QueryNullPlacement.First ? SqlNullPlacement.First : SqlNullPlacement.Last));
            }
            if (!IsUnique(input, orderedFields))
                throw Fail("SQLITE_REL_UNIQUE_ORDER", "The ordering tuple (with representative partition keys) must cover a proven unique key. Supply source key evidence or an additional tie-breaker.", node);
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
            var rightEnvironment = binary.Operator is BinaryOperator.And or BinaryOperator.Or
                ? Refine(binary.Left, environment, whenTrue: binary.Operator == BinaryOperator.And) : environment;
            var right = Expression(binary.Right, rightEnvironment, node);
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
                    sql = IsNullLiteral(binary.Left) ? SqlExpression.IsNull(CanonicalValue(right))
                        : IsNullLiteral(binary.Right) ? SqlExpression.IsNull(CanonicalValue(left))
                        : SqlExpression.Binary(Required(left.Contract) && Required(right.Contract)
                            ? SqlBinaryOperator.Equal : SqlBinaryOperator.IsNotDistinctFrom, CanonicalValue(left), CanonicalValue(right));
                    if (left.Contract.Presence != FieldPresence.Required || right.Contract.Presence != FieldPresence.Required)
                        sql = SqlExpression.Binary(SqlBinaryOperator.And,
                            SqlExpression.Binary(SqlBinaryOperator.Equal,
                                left.Contract.Presence == FieldPresence.Required ? One : left.Present,
                                right.Contract.Presence == FieldPresence.Required ? One : right.Present), sql);
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

        static bool IsNullLiteral(Expr expression) => expression is ConstantExpr { Value.Kind: ObservationValueKind.Null }
            or LiteralExpr { Value.Kind: ObservationValueKind.Null };

        static IdentitySlot CopyIdentity(SqlSelectBuilder builder, Scope result, string side,
            ValueBindingId binding, IdentitySlot identity) => identity with
        {
            Components = identity.Components.Select((component, index) => component with
            {
                Alias = Select(builder, result, SqlExpression.Column(side, component.Alias),
                    component.Alias, $"identity:{binding.Value}:{index}")
            }).ToImmutableArray()
        };

        // Only direct field equalities in a conjunction establish functional dependencies.
        // In particular, predicates under OR and outer-join cross-side equalities cannot do so.
        static List<(FieldKey Left, FieldKey Right)> JoinEqualities(Expr expression, Dictionary<FieldKey, Cell> environment)
        {
            if (expression is BinaryExpr { Operator: BinaryOperator.And } and)
                return [.. JoinEqualities(and.Left, environment), .. JoinEqualities(and.Right, environment)];
            if (expression is BinaryExpr { Operator: BinaryOperator.Eq } equal
                && FieldCell(equal.Left, environment)?.Origin is { } left
                && FieldCell(equal.Right, environment)?.Origin is { } right)
                return [(left, right)];
            return [];
        }

        static HashSet<FieldKey> MatchedFields(List<(FieldKey Left, FieldKey Right)> pairs, HashSet<FieldKey> other)
        {
            HashSet<FieldKey> result = [];
            foreach (var (left, right) in pairs)
            {
                if (other.Contains(left)) result.Add(right);
                if (other.Contains(right)) result.Add(left);
            }
            return result;
        }

        static bool IsUnique(Scope scope, HashSet<FieldKey> fields)
        {
            HashSet<FieldKey> closure = [.. fields];
            bool changed;
            do
            {
                changed = false;
                foreach (var (left, right) in scope.Equalities)
                {
                    if (closure.Contains(left)) changed |= closure.Add(right);
                    if (closure.Contains(right)) changed |= closure.Add(left);
                }
            } while (changed);
            return scope.UniqueKeys.Any(key => key.IsSubsetOf(closure));
        }

        static Cell? FieldCell(Expr expression, Dictionary<FieldKey, Cell> environment) => expression switch
        {
            FieldExpr { Binding: { } binding } field => environment.GetValueOrDefault(new(binding, field.Path)),
            FieldExpr field => environment.Where(pair => pair.Key.Path == field.Path).Select(static pair => pair.Value).SingleOrDefault(),
            FieldRefExpr field => environment.Where(pair => pair.Key.Path == field.Path).Select(static pair => pair.Value).SingleOrDefault(),
            _ => null
        };

        // These are facts on the selected branch, never unconditional schema facts. SQL's total
        // comparisons may evaluate either operand, but AND/OR preserve the guarded Boolean result.
        // Unguarded canonical comparisons against nullish values still fail compilation.
        Dictionary<FieldKey, Cell> Refine(Expr predicate, Dictionary<FieldKey, Cell> environment, bool whenTrue)
        {
            Dictionary<FieldKey, Cell> result = new(environment);
            ExprGuardRefinement.Apply(predicate, whenTrue,
                expression => expression is ParameterExpr parameter
                    ? request.Plan.InputContract.Parameters.Single(p => p.Definition.Id.Value == parameter.Parameter).ValueContract
                    : FieldCell(expression, result)?.Contract,
                (expression, contract) =>
                {
                    var cell = FieldCell(expression, result)!;
                    foreach (var (key, current) in result.ToArray())
                    {
                        var same = cell.Origin is not null && current.Origin == cell.Origin;
                        var bindingPresent = contract.Presence == FieldPresence.Required && cell.AbsentBinding is not null
                            && current.AbsentBinding == cell.AbsentBinding;
                        if (!same && !bindingPresent) continue;
                        result[key] = current with
                        {
                            Contract = new(current.Contract.Type, current.Contract.Shape, current.Contract.Cardinality,
                                bindingPresent || same && contract.Presence == FieldPresence.Required ? FieldPresence.Required : current.Contract.Presence,
                                same ? contract.Nullability : current.Contract.Nullability),
                            AbsentBinding = bindingPresent ? null : current.AbsentBinding
                        };
                    }
                });
            return result;
        }

        static SqlExpression CanonicalValue(Cell cell)
        {
            // Storage evidence guarantees NULL payloads for missing fields, including absent join bindings.
            var value = cell.Value;
            return cell.Contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.String } ? SqlExpression.Collate(value, "BINARY") : value;
        }
        static SqlExpression Boolean(Cell cell, QueryNodeId node) => cell.Contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.Bool } && Required(cell.Contract)
            ? cell.Value : throw Fail("SQLITE_REL_BOOLEAN", "Predicates require non-null, non-missing boolean values.", node);
        static bool Text(ValueContract contract) => contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.String };
        static bool Integer(ValueContract contract) => contract.Type is ScalarTypeRef { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 };
        static bool Required(ValueContract contract) => contract.Presence == FieldPresence.Required && contract.Nullability == FieldNullability.NonNullable;

        static void RequireEncoding(ValueContract contract, QueryNodeId node, RelationQueryInputId? input = null)
        {
            try { _ = SqliteScalarCodec.GetStorageType(contract); }
            catch (NotSupportedException exception) { throw Fail("SQLITE_REL_ENCODING", exception.Message, node, input); }
        }

        (SqlSelectBuilder Builder, Scope Result, Dictionary<FieldKey, Cell> Environment) Wrap(Scope input, HashSet<FieldKey> retainedFields, bool retainOrder = true)
        {
            var builder = new SqlSelectBuilder(input.Query, input.Name);
            Scope result = new();
            var environment = Environment(input, input.Name);
            foreach (var (key, cell) in environment)
                if (retainedFields.Contains(key)) AddField(builder, result, key, cell);
            foreach (var (binding, identity) in input.Identities)
            {
                result.Identities.Add(binding, CopyIdentity(builder, result, input.Name, binding, identity));
            }
            foreach (var (binding, presence) in input.BindingPresence)
            {
                result.BindingPresence.Add(binding, Select(builder, result, SqlExpression.Column(input.Name, presence), presence, $"binding:{binding.Value}"));
            }
            if (retainOrder)
            {
                foreach (var order in input.Orderings)
                {
                    result.Orderings.Add(order with { Alias = Select(builder, result, SqlExpression.Column(input.Name, order.Alias), order.Alias, $"ordering:{result.Orderings.Count}") });
                }
                ApplyOrdering(builder, input, input.Name);
            }
            result.UniqueKeys.AddRange(input.UniqueKeys);
            result.Equalities.AddRange(input.Equalities);
            return (builder, result, environment);
        }

        void AddField(SqlSelectBuilder builder, Scope scope, FieldKey key, Cell cell)
        {
            var name = $"{key.Binding.Value}_{key.Path}";
            var semanticKey = $"field:{key.Binding.Value}:{key.Path}";
            var value = Select(builder, scope, cell.Value, name, semanticKey);
            // Required field presence is a compile-time fact. Carry a physical bit only where missing is possible.
            var presence = cell.Contract.Presence == FieldPresence.Required ? null : Select(builder, scope, cell.Present, $"{name}_present", $"presence:{semanticKey}");
            scope.Fields.Add(key, new(value, presence, cell.Contract, cell.Origin, cell.AsciiOrdering, cell.AbsentBinding));
        }
        static string Select(SqlSelectBuilder builder, Scope scope, SqlExpression expression, string preferredName, string semanticKey)
        {
            if (scope.Columns.TryGetValue(expression, out var alias)) return alias;
            alias = scope.Aliases.Allocate(preferredName, semanticKey, "column");
            builder.Select(expression, alias);
            scope.Columns.Add(expression, alias);
            return alias;
        }
        static Dictionary<FieldKey, Cell> Environment(Scope scope, string alias) => scope.Fields.ToDictionary(static pair => pair.Key,
            pair => new Cell(SqlExpression.Column(alias, pair.Value.Value), pair.Value.Present is null ? One
                : SqlExpression.Column(alias, pair.Value.Present), pair.Value.Contract, pair.Value.Origin, pair.Value.AsciiOrdering, pair.Value.AbsentBinding));
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
    sealed record Cell(SqlExpression Value, SqlExpression Present, ValueContract Contract, FieldKey? Origin = null,
        bool AsciiOrdering = false, ValueBindingId? AbsentBinding = null);
    sealed record Slot(string Value, string? Present, ValueContract Contract, FieldKey? Origin,
        bool AsciiOrdering, ValueBindingId? AbsentBinding);
    sealed record IdentityComponent(string Alias, ValueContract Contract);
    sealed record IdentitySlot(ImmutableArray<IdentityComponent> Components, QualifiedShapeId Shape);
    sealed record OrderingSlot(string Alias, SqlSortDirection Direction, SqlNullPlacement NullPlacement);
    sealed class Scope
    {
        public string Name { get; set; } = null!;
        public SqlAliasAllocator Aliases { get; } = BranchCompiler.NewAliases();
        public SqlSelectQuery Query { get; set; } = null!;
        public Dictionary<SqlExpression, string> Columns { get; } = [];
        public Dictionary<FieldKey, Slot> Fields { get; } = [];
        public Dictionary<ValueBindingId, IdentitySlot> Identities { get; } = [];
        public Dictionary<ValueBindingId, string> BindingPresence { get; } = [];
        public List<OrderingSlot> Orderings { get; } = [];
        public List<HashSet<FieldKey>> UniqueKeys { get; } = [];
        public List<(FieldKey Left, FieldKey Right)> Equalities { get; } = [];
    }
    sealed record Preparation(RelationQueryBoundRealizationReport Report, Dictionary<RelationQueryNativeResultBranchId, PreparedBranch> Branches);
    sealed record PreparedBranch(SqlCommandTemplate Template, ImmutableArray<SqliteRelationQueryResultField> Fields,
        ImmutableArray<SqliteRelationQueryOccurrenceColumn> Occurrences, ImmutableArray<SqliteRelationQueryParameter> Parameters, int BindingPresenceOrdinal);
}
