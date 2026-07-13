using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.IR;

/// <summary>
/// Validates cross-node invariants for canonical relation/query definitions.
/// </summary>
public static partial class RelationQueryDefinitionValidator
{
    /// <summary>
    /// Validates a canonical relation or query definition without selecting a physical interpretation.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to validate.</param>
    /// <returns>Structured semantic validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(RelationQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Body is null)
        {
            return DocumentValidationResult.FromDiagnostics([
                new(
                    Code: "relationQuery.body.missing",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A relation/query definition must contain a logical query body.",
                    Location: "/definition/body")
            ]);
        }

        ValidationContext context = new(definition);
        context.Validate();
        return DocumentValidationResult.FromDiagnostics(context.Diagnostics);
    }

    sealed class ValidationContext(RelationQueryDefinition definition)
    {
        readonly Dictionary<QueryNodeId, LogicalQueryNode> nodes = [];
        readonly Dictionary<QueryNodeId, HashSet<ValueBindingId>> bindingsByNode = [];
        readonly HashSet<QueryNodeId> visiting = [];
        readonly HashSet<QueryNodeId> cycleReported = [];
        readonly HashSet<string> parameters = new(StringComparer.Ordinal);

        public List<DocumentValidationDiagnostic> Diagnostics { get; } = [];

        ImmutableArray<LogicalQueryNode> DefinitionNodes =>
            definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes;

        ImmutableArray<QueryParameterDefinition> DefinitionParameters =>
            definition.Body.Parameters.IsDefault ? [] : definition.Body.Parameters;

        public void Validate()
        {
            if (DefinitionNodes.IsDefaultOrEmpty)
            {
                Add(
                    code: "relationQuery.body.nodesEmpty",
                    message: "A logical query body must contain at least one node.",
                    location: "/definition/body/nodes");
            }

            IndexParameters();
            IndexNodes();
            ValidateNodeReferences();
            ValidateDefinitionRoots();
            ValidateReachability();
        }

        void IndexParameters()
        {
            foreach (var parameter in DefinitionParameters)
            {
                ValidateIdentifier(
                    parameter.Id.Value,
                    code: "relationQuery.parameter.idMissing",
                    message: "A query parameter must have a non-empty id.",
                    location: "/definition/body/parameters");

                if (!parameters.Add(parameter.Id.Value))
                {
                    Add(
                        code: "relationQuery.parameter.duplicateId",
                        message: $"Duplicate query parameter id '{parameter.Id.Value}'.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}");
                }

                if (parameter.Type is null)
                {
                    Add(
                        code: "relationQuery.parameter.typeMissing",
                        message: $"Query parameter '{parameter.Id.Value}' must declare a semantic type.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}/type");
                }
                else
                {
                    ValidatePortableType(
                        parameter.Type,
                        $"/definition/body/parameters/{parameter.Id.Value}/type");
                }

                if (parameter.Presence == FieldPresence.Required && parameter.DefaultValue is not null)
                {
                    Add(
                        code: "relationQuery.parameter.requiredHasDefault",
                        message: $"Required query parameter '{parameter.Id.Value}' cannot declare a default value.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}/defaultValue");
                }

                if (parameter.DefaultValue is { } defaultValue)
                {
                    ValidatePortableObservationValue(
                        defaultValue,
                        $"/definition/body/parameters/{parameter.Id.Value}/defaultValue");
                }
            }
        }

        void IndexNodes()
        {
            HashSet<ValueBindingId> sourceBindings = [];
            foreach (var node in DefinitionNodes)
            {
                ValidateNodeLocal(node);
                if (!nodes.TryAdd(node.Id, node))
                {
                    Add(
                        code: "relationQuery.node.duplicateId",
                        message: $"Duplicate logical query node id '{node.Id.Value}'.",
                        location: NodeLocation(node.Id));
                }

                if (node is SourceQueryNode source && !sourceBindings.Add(source.Binding))
                {
                    Add(
                        code: "relationQuery.binding.duplicateSource",
                        message: $"Source binding '{source.Binding.Value}' is declared by more than one source node.",
                        location: NodeLocation(node.Id));
                }
            }
        }

        void ValidateNodeReferences()
        {
            foreach (var node in DefinitionNodes)
            {
                foreach (var input in node.Inputs)
                {
                    ValidateIdentifier(
                        input.Value,
                        code: "relationQuery.node.inputIdMissing",
                        message: $"Node '{node.Id.Value}' contains an empty input node reference.",
                        location: NodeLocation(node.Id));

                    if (!nodes.ContainsKey(input))
                    {
                        Add(
                            code: "relationQuery.node.inputMissing",
                            message: $"Node '{node.Id.Value}' references unknown input node '{input.Value}'.",
                            location: NodeLocation(node.Id));
                    }
                }
            }

            foreach (var node in DefinitionNodes)
                _ = ResolveBindings(node.Id);
        }

        void ValidateNodeLocal(LogicalQueryNode node)
        {
            ValidateIdentifier(
                node.Id.Value,
                code: "relationQuery.node.idMissing",
                message: "A logical query node must have a non-empty id.",
                location: "/definition/body/nodes");

            switch (node)
            {
                case SourceQueryNode source:
                    ValidateBinding(source.Binding, source.Id, "source");
                    ValidateShape(source.Shape, source.Id, "shape");
                    break;
                case TraverseRelationshipQueryNode traversal:
                    ValidateBinding(traversal.From, traversal.Id, "from");
                    ValidateBinding(traversal.Result, traversal.Id, "result");
                    ValidateIdentifier(
                        traversal.Relationship.Value,
                        code: "relationQuery.traversal.relationshipMissing",
                        message: $"Relationship traversal '{traversal.Id.Value}' must reference a relationship id.",
                        location: $"{NodeLocation(traversal.Id)}/relationship");
                    if (!Enum.IsDefined(traversal.Direction))
                    {
                        Add(
                            code: "relationQuery.traversal.directionInvalid",
                            message: $"Relationship traversal '{traversal.Id.Value}' declares unsupported direction '{traversal.Direction}'.",
                            location: $"{NodeLocation(traversal.Id)}/direction");
                    }
                    if (!Enum.IsDefined(traversal.JoinKind)
                        || traversal.JoinKind is JoinKind.Right or JoinKind.Full)
                    {
                        Add(
                            code: "relationQuery.traversal.joinKindInvalid",
                            message: $"Relationship traversal '{traversal.Id.Value}' supports only inner or left join semantics.",
                            location: $"{NodeLocation(traversal.Id)}/joinKind");
                    }
                    if (!Enum.IsDefined(traversal.Requirement))
                    {
                        Add(
                            code: "relationQuery.traversal.requirementInvalid",
                            message: $"Relationship traversal '{traversal.Id.Value}' declares unsupported input requirement '{traversal.Requirement}'.",
                            location: $"{NodeLocation(traversal.Id)}/requirement");
                    }
                    break;
                case ExpandCollectionQueryNode expansion:
                    ValidateBinding(expansion.ItemBinding, expansion.Id, "itemBinding");
                    if (expansion.ItemType is null)
                    {
                        Add(
                            code: "relationQuery.expandCollection.itemTypeMissing",
                            message: $"Collection-expansion node '{expansion.Id.Value}' must declare an item type.",
                            location: $"{NodeLocation(expansion.Id)}/itemType");
                    }
                    else
                    {
                        ValidatePortableType(expansion.ItemType, $"{NodeLocation(expansion.Id)}/itemType");
                    }
                    break;
                case ProjectQueryNode project:
                    ValidateBinding(project.ResultBinding, project.Id, "resultBinding");
                    ValidateShape(project.ResultShape, project.Id, "resultShape");
                    if (project.Assignments.IsDefaultOrEmpty)
                    {
                        Add(
                            code: "relationQuery.project.assignmentsEmpty",
                            message: $"Projection node '{project.Id.Value}' must contain at least one assignment.",
                            location: $"{NodeLocation(project.Id)}/assignments");
                    }
                    break;
                case AggregateQueryNode aggregate:
                    ValidateBinding(aggregate.ResultBinding, aggregate.Id, "resultBinding");
                    ValidateShape(aggregate.ResultShape, aggregate.Id, "resultShape");
                    if (aggregate.Aggregates.IsDefaultOrEmpty)
                    {
                        Add(
                            code: "relationQuery.aggregate.assignmentsEmpty",
                            message: $"Aggregate node '{aggregate.Id.Value}' must contain at least one aggregate assignment.",
                            location: $"{NodeLocation(aggregate.Id)}/aggregates");
                    }

                    foreach (var assignment in aggregate.Aggregates.IsDefault
                                 ? []
                                 : aggregate.Aggregates)
                    {
                        if (assignment.Operation != AggregateOperator.Count && assignment.Value is null)
                        {
                            Add(
                                code: "relationQuery.aggregate.valueMissing",
                                message: $"Aggregate assignment '{assignment.Id.Value}' requires a value expression.",
                                location: $"{NodeLocation(aggregate.Id)}/aggregates/{assignment.Id.Value}/value");
                        }
                    }
                    break;
                case OrderQueryNode order when order.Orderings.IsDefaultOrEmpty:
                    Add(
                        code: "relationQuery.order.orderingsEmpty",
                        message: $"Order node '{order.Id.Value}' must contain at least one ordering.",
                        location: $"{NodeLocation(order.Id)}/orderings");
                    break;
                case PageQueryNode { Page: null } page:
                    Add(
                        code: "relationQuery.page.definitionMissing",
                        message: $"Page node '{page.Id.Value}' must contain a page definition.",
                        location: $"{NodeLocation(page.Id)}/page");
                    break;
                case PageQueryNode page:
                    if (page.Page.Limit <= 0)
                    {
                        Add(
                            code: "relationQuery.page.limitInvalid",
                            message: $"Page node '{page.Id.Value}' must have a positive limit.",
                            location: $"{NodeLocation(page.Id)}/page/limit");
                    }
                    if (page.Page is OffsetPageDefinition { Offset: < 0 })
                    {
                        Add(
                            code: "relationQuery.page.offsetInvalid",
                            message: $"Page node '{page.Id.Value}' must have a non-negative offset.",
                            location: $"{NodeLocation(page.Id)}/page/offset");
                    }
                    break;
            }
        }

        HashSet<ValueBindingId> ResolveBindings(QueryNodeId nodeId)
        {
            if (bindingsByNode.TryGetValue(nodeId, out var cached))
                return cached;

            if (!nodes.TryGetValue(nodeId, out var node))
                return [];

            if (!visiting.Add(nodeId))
            {
                if (cycleReported.Add(nodeId))
                {
                    Add(
                        code: "relationQuery.node.cycle",
                        message: $"Logical query graph contains a cycle involving node '{nodeId.Value}'.",
                        location: NodeLocation(nodeId));
                }
                return [];
            }

            HashSet<ValueBindingId> bindings = node switch
            {
                SourceQueryNode source => [source.Binding],
                FilterQueryNode filter => PreserveAndValidate(filter, filter.Input, filter.Predicate),
                TraverseRelationshipQueryNode traversal => ValidateTraversal(traversal),
                JoinQueryNode join => ValidateJoin(join),
                ExpandCollectionQueryNode expansion => ValidateExpandCollection(expansion),
                ProjectQueryNode project => ValidateProject(project),
                DistinctQueryNode distinct => ValidateDistinct(distinct),
                AggregateQueryNode aggregate => ValidateAggregate(aggregate),
                OrderQueryNode order => ValidateOrder(order),
                PageQueryNode page => ValidatePage(page),
                _ => []
            };

            visiting.Remove(nodeId);
            bindingsByNode[nodeId] = bindings;
            return bindings;
        }

        HashSet<ValueBindingId> PreserveAndValidate(LogicalQueryNode node, QueryNodeId input, Expr expression)
        {
            var bindings = CopyBindings(input);
            ValidateExpression(expression, bindings, NodeLocation(node.Id));
            return bindings;
        }

        HashSet<ValueBindingId> ValidateTraversal(TraverseRelationshipQueryNode traversal)
        {
            var bindings = CopyBindings(traversal.Input);
            if (!bindings.Contains(traversal.From))
            {
                Add(
                    code: "relationQuery.traversal.sourceBindingMissing",
                    message: $"Relationship traversal '{traversal.Id.Value}' references binding '{traversal.From.Value}' that is not visible from its input.",
                    location: NodeLocation(traversal.Id));
            }

            if (!bindings.Add(traversal.Result))
            {
                Add(
                    code: "relationQuery.traversal.resultBindingDuplicate",
                    message: $"Relationship traversal '{traversal.Id.Value}' redeclares visible binding '{traversal.Result.Value}'.",
                    location: NodeLocation(traversal.Id));
            }
            return bindings;
        }

        HashSet<ValueBindingId> ValidateJoin(JoinQueryNode join)
        {
            var left = CopyBindings(join.Left);
            var right = CopyBindings(join.Right);
            foreach (var duplicate in left.Intersect(right))
            {
                Add(
                    code: "relationQuery.join.bindingCollision",
                    message: $"Join '{join.Id.Value}' receives binding '{duplicate.Value}' from both inputs.",
                    location: NodeLocation(join.Id));
            }

            left.UnionWith(right);
            ValidateExpression(join.Predicate, left, NodeLocation(join.Id));
            return left;
        }

        HashSet<ValueBindingId> ValidateExpandCollection(ExpandCollectionQueryNode expansion)
        {
            var bindings = CopyBindings(expansion.Input);
            ValidateExpression(expansion.Collection, bindings, NodeLocation(expansion.Id));
            if (!bindings.Add(expansion.ItemBinding))
            {
                Add(
                    code: "relationQuery.expandCollection.itemBindingDuplicate",
                    message: $"Collection-expansion node '{expansion.Id.Value}' redeclares visible binding '{expansion.ItemBinding.Value}'.",
                    location: NodeLocation(expansion.Id));
            }
            return bindings;
        }

        HashSet<ValueBindingId> ValidateProject(ProjectQueryNode project)
        {
            var inputs = CopyBindings(project.Input);
            ValidateAssignments(
                (project.Assignments.IsDefault ? [] : project.Assignments)
                .Select(static assignment => (assignment.Id, assignment.Target, assignment.Value)),
                inputs,
                project.Id);
            return [project.ResultBinding];
        }

        HashSet<ValueBindingId> ValidateDistinct(DistinctQueryNode distinct)
        {
            var bindings = CopyBindings(distinct.Input);
            foreach (var key in distinct.Keys)
                ValidateExpression(key, bindings, NodeLocation(distinct.Id));
            return bindings;
        }

        HashSet<ValueBindingId> ValidateAggregate(AggregateQueryNode aggregate)
        {
            var inputs = CopyBindings(aggregate.Input);
            HashSet<QueryAssignmentId> ids = [];
            HashSet<FieldPath> targets = [];

            foreach (var grouping in aggregate.Groupings.IsDefault ? [] : aggregate.Groupings)
            {
                ValidateAssignmentIdentity(grouping.Id, grouping.Target, ids, targets, aggregate.Id);
                ValidateExpression(grouping.Key, inputs, NodeLocation(aggregate.Id));
            }

            foreach (var assignment in aggregate.Aggregates.IsDefault ? [] : aggregate.Aggregates)
            {
                ValidateAssignmentIdentity(assignment.Id, assignment.Target, ids, targets, aggregate.Id);
                if (assignment.Value is not null)
                    ValidateExpression(assignment.Value, inputs, NodeLocation(aggregate.Id));
                if (assignment.Filter is not null)
                    ValidateExpression(assignment.Filter, inputs, NodeLocation(aggregate.Id));
            }
            return [aggregate.ResultBinding];
        }

        HashSet<ValueBindingId> ValidateOrder(OrderQueryNode order)
        {
            var bindings = CopyBindings(order.Input);
            foreach (var ordering in order.Orderings.IsDefault ? [] : order.Orderings)
                ValidateExpression(ordering.Key, bindings, NodeLocation(order.Id));
            return bindings;
        }

        HashSet<ValueBindingId> ValidatePage(PageQueryNode page)
        {
            var bindings = CopyBindings(page.Input);
            if (page.Page is KeysetPageDefinition keyset)
            {
                foreach (var expression in keyset.After)
                    ValidateBoundaryExpression(expression, NodeLocation(page.Id));

                if (nodes.TryGetValue(page.Input, out var input) && input is not OrderQueryNode)
                {
                    Add(
                        code: "relationQuery.page.keysetRequiresOrder",
                        message: $"Keyset page node '{page.Id.Value}' must consume an order node.",
                        location: NodeLocation(page.Id));
                }
                else if (input is OrderQueryNode order
                         && !keyset.After.IsDefaultOrEmpty
                         && keyset.After.Length != order.Orderings.Length)
                {
                    Add(
                        code: "relationQuery.page.keysetValueCountMismatch",
                        message: $"Keyset page node '{page.Id.Value}' supplies {keyset.After.Length} continuation values for {order.Orderings.Length} ordering expressions.",
                        location: NodeLocation(page.Id));
                }
            }
            return bindings;
        }

        void ValidateAssignments(
            IEnumerable<(QueryAssignmentId Id, FieldPath Target, Expr Expression)> assignments,
            IReadOnlySet<ValueBindingId> bindings,
            QueryNodeId nodeId)
        {
            HashSet<QueryAssignmentId> ids = [];
            HashSet<FieldPath> targets = [];
            foreach (var assignment in assignments)
            {
                ValidateAssignmentIdentity(assignment.Id, assignment.Target, ids, targets, nodeId);
                ValidateExpression(assignment.Expression, bindings, NodeLocation(nodeId));
            }
        }

        void ValidateAssignmentIdentity(
            QueryAssignmentId id,
            FieldPath target,
            ISet<QueryAssignmentId> ids,
            ISet<FieldPath> targets,
            QueryNodeId nodeId)
        {
            ValidateIdentifier(
                id.Value,
                code: "relationQuery.assignment.idMissing",
                message: $"Node '{nodeId.Value}' contains an assignment with an empty id.",
                location: NodeLocation(nodeId));
            ValidateFieldPath(target, NodeLocation(nodeId));

            if (!ids.Add(id))
            {
                Add(
                    code: "relationQuery.assignment.duplicateId",
                    message: $"Node '{nodeId.Value}' contains duplicate assignment id '{id.Value}'.",
                    location: NodeLocation(nodeId));
            }

            if (!targets.Add(target))
            {
                Add(
                    code: "relationQuery.assignment.duplicateTarget",
                    message: $"Node '{nodeId.Value}' assigns target '{target}' more than once.",
                    location: NodeLocation(nodeId));
            }
        }

        void ValidateExpression(Expr expression, IReadOnlySet<ValueBindingId> bindings, string location)
        {
            if (expression is null)
            {
                Add(
                    code: "relationQuery.expression.missing",
                    message: "A required relation/query expression is missing.",
                    location: location);
                return;
            }

            switch (expression)
            {
                case FieldExpr field:
                    ValidateFieldPath(field.Path, location);
                    if (field.Binding is { } binding && !bindings.Contains(binding))
                    {
                        Add(
                            code: "relationQuery.expression.bindingMissing",
                            message: $"Expression references binding '{binding.Value}' that is not visible at this node.",
                            location: location);
                    }
                    else if (field.Binding is null && bindings.Count != 1)
                    {
                        Add(
                            code: "relationQuery.expression.fieldBindingAmbiguous",
                            message: "An unbound field expression is only valid when exactly one value binding is visible.",
                            location: location);
                    }
                    break;
                case FieldRefExpr field:
                    ValidateFieldPath(field.Path, location);
                    if (bindings.Count != 1)
                    {
                        Add(
                            code: "relationQuery.expression.fieldBindingAmbiguous",
                            message: "An unbound typed field expression is only valid when exactly one value binding is visible.",
                            location: location);
                    }
                    ValidatePortableType(field.Type, $"{location}/type");
                    break;
                case CurrentItemExpr:
                    Add(
                        code: "relationQuery.expression.currentItemUnsupported",
                        message: "Canonical relation/query IR requires explicit value bindings instead of current-item expressions.",
                        location: location);
                    break;
                case ParameterExpr parameter when string.IsNullOrWhiteSpace(parameter.Parameter):
                    Add(
                        code: "relationQuery.expression.parameterIdMissing",
                        message: "A parameter expression must reference a non-empty parameter id.",
                        location: location);
                    break;
                case ParameterExpr parameter when !parameters.Contains(parameter.Parameter):
                    Add(
                        code: "relationQuery.expression.parameterMissing",
                        message: $"Expression references undeclared query parameter '{parameter.Parameter}'.",
                        location: location);
                    break;
                case LiteralExpr literal:
                    ValidatePortableType(literal.Type, $"{location}/type");
                    ValidatePortableObservationValue(literal.Value, $"{location}/value");
                    break;
                case ConstantExpr constant:
                    ValidatePortableObservationValue(constant.Value, $"{location}/value");
                    break;
                case UnaryExpr unary:
                    ValidateExpression(unary.Operand, bindings, location);
                    break;
                case BinaryExpr binary:
                    ValidateExpression(binary.Left, bindings, location);
                    ValidateExpression(binary.Right, bindings, location);
                    break;
                case ConditionalExpr conditional:
                    ValidatePortableType(conditional.ReturnType, $"{location}/returnType");
                    ValidateExpression(conditional.Test, bindings, location);
                    ValidateExpression(conditional.IfTrue, bindings, location);
                    ValidateExpression(conditional.IfFalse, bindings, location);
                    break;
                case CallExpr call:
                    ValidatePortableType(call.ReturnType, $"{location}/returnType");
                    foreach (var argument in call.Arguments)
                        ValidateExpression(argument, bindings, location);
                    break;
                case AggregateExpr aggregate:
                    Add(
                        code: "relationQuery.expression.aggregateUnsupported",
                        message: "Canonical relation/query IR represents cardinality-changing aggregation with aggregate query nodes.",
                        location: location);
                    ValidateExpression(aggregate.Source, bindings, location);
                    foreach (var group in aggregate.GroupBy)
                        ValidateExpression(group, bindings, location);
                    break;
            }
        }

        void ValidateBoundaryExpression(Expr expression, string location)
        {
            if (expression is null)
            {
                Add(
                    code: "relationQuery.expression.missing",
                    message: "A required relation/query expression is missing.",
                    location: location);
                return;
            }

            switch (expression)
            {
                case FieldExpr or FieldRefExpr or CurrentItemExpr or AggregateExpr:
                    Add(
                        code: "relationQuery.page.keysetBoundaryRowDependent",
                        message: "Keyset continuation values must be independent of the row being paged.",
                        location: location);
                    break;
                case ParameterExpr parameter when string.IsNullOrWhiteSpace(parameter.Parameter):
                    Add(
                        code: "relationQuery.expression.parameterIdMissing",
                        message: "A parameter expression must reference a non-empty parameter id.",
                        location: location);
                    break;
                case ParameterExpr parameter when !parameters.Contains(parameter.Parameter):
                    Add(
                        code: "relationQuery.expression.parameterMissing",
                        message: $"Expression references undeclared query parameter '{parameter.Parameter}'.",
                        location: location);
                    break;
                case LiteralExpr literal:
                    ValidatePortableType(literal.Type, $"{location}/type");
                    ValidatePortableObservationValue(literal.Value, $"{location}/value");
                    break;
                case ConstantExpr constant:
                    ValidatePortableObservationValue(constant.Value, $"{location}/value");
                    break;
                case UnaryExpr unary:
                    ValidateBoundaryExpression(unary.Operand, location);
                    break;
                case BinaryExpr binary:
                    ValidateBoundaryExpression(binary.Left, location);
                    ValidateBoundaryExpression(binary.Right, location);
                    break;
                case ConditionalExpr conditional:
                    ValidatePortableType(conditional.ReturnType, $"{location}/returnType");
                    ValidateBoundaryExpression(conditional.Test, location);
                    ValidateBoundaryExpression(conditional.IfTrue, location);
                    ValidateBoundaryExpression(conditional.IfFalse, location);
                    break;
                case CallExpr call:
                    ValidatePortableType(call.ReturnType, $"{location}/returnType");
                    foreach (var argument in call.Arguments)
                        ValidateBoundaryExpression(argument, location);
                    break;
            }
        }

        void ValidateDefinitionRoots()
        {
            switch (definition)
            {
                case RelationDefinition relation:
                    ValidateRelation(relation);
                    break;
                case QueryDefinition query:
                    ValidateQuery(query);
                    break;
            }
        }

        void ValidateRelation(RelationDefinition relation)
        {
            ValidateIdentifier(
                relation.Id?.Value,
                code: "relationQuery.relation.idMissing",
                message: "A relation definition must have a non-empty id.",
                location: "/definition/id");
            ValidateIdentifier(
                relation.Name?.Value,
                code: "relationQuery.relation.nameMissing",
                message: "A relation definition must have a non-empty name.",
                location: "/definition/name");
            ValidateIdentifier(
                relation.RootBinding.Value,
                code: "relationQuery.relation.rootBindingIdMissing",
                message: "A relation definition must have a non-empty root binding.",
                location: "/definition/rootBinding");

            var invariants = relation.Invariants.IsDefault ? [] : relation.Invariants;
            HashSet<string> invariantNames = new(StringComparer.Ordinal);
            for (var index = 0; index < invariants.Length; index++)
            {
                var invariant = invariants[index];
                var invariantLocation = $"/definition/invariants/{index}";
                if (invariant is null)
                {
                    Add(
                        code: "relationQuery.relation.invariantMissing",
                        message: "A relation invariant entry cannot be null.",
                        location: invariantLocation);
                }
                else if (string.IsNullOrWhiteSpace(invariant.Name))
                {
                    Add(
                        code: "relationQuery.relation.invariantNameMissing",
                        message: "A relation invariant must have a non-empty name.",
                        location: $"{invariantLocation}/name");
                }
                else if (!invariantNames.Add(invariant.Name))
                {
                    Add(
                        code: "relationQuery.relation.invariantDuplicateName",
                        message: $"Relation invariant name '{invariant.Name}' is declared more than once.",
                        location: $"{invariantLocation}/name");
                }
            }

            var rootIsSource = DefinitionNodes
                .OfType<SourceQueryNode>()
                .Any(source => source.Binding == relation.RootBinding);
            if (!rootIsSource)
            {
                Add(
                    code: "relationQuery.relation.rootBindingMissing",
                    message: $"Relation root binding '{relation.RootBinding.Value}' is not declared by a source node.",
                    location: "/definition/rootBinding");
            }

            if (relation.Output is null)
            {
                Add(
                    code: "relationQuery.relation.outputMissing",
                    message: "A relation definition must declare an output.",
                    location: "/definition/output");
                return;
            }

            ValidateIdentifier(
                relation.Output.Node.Value,
                code: "relationQuery.relation.outputNodeIdMissing",
                message: "A relation output must reference a non-empty node id.",
                location: "/definition/output/node");
            ValidateShape(
                relation.Output.Shape,
                context: "A relation output",
                location: "/definition/output/shape");

            if (!nodes.TryGetValue(relation.Output.Node, out var outputNode))
            {
                Add(
                    code: "relationQuery.relation.outputNodeMissing",
                    message: $"Relation output references unknown node '{relation.Output.Node.Value}'.",
                    location: "/definition/output/node");
                return;
            }

            var outputShape = outputNode switch
            {
                ProjectQueryNode project => project.ResultShape,
                AggregateQueryNode aggregate => aggregate.ResultShape,
                _ => (QualifiedShapeId?)null
            };
            if (outputShape is null)
            {
                Add(
                    code: "relationQuery.relation.outputNodeNotShaped",
                    message: $"Relation output node '{relation.Output.Node.Value}' must be a project or aggregate node.",
                    location: "/definition/output/node");
            }
            else if (outputShape.Value != relation.Output.Shape)
            {
                Add(
                    code: "relationQuery.relation.outputShapeMismatch",
                    message: $"Relation output shape '{relation.Output.Shape}' does not match node shape '{outputShape.Value}'.",
                    location: "/definition/output/shape");
            }

            var outputBindings = ResolveBindings(relation.Output.Node);
            if (relation.Output.Key is not null)
                ValidateExpression(relation.Output.Key, outputBindings, "/definition/output/key");

            for (var index = 0; index < invariants.Length; index++)
            {
                var invariant = invariants[index];
                if (invariant is not null)
                    ValidateExpression(invariant.Expression, outputBindings, $"/definition/invariants/{index}/expression");
            }
        }

        void ValidateQuery(QueryDefinition query)
        {
            ValidateIdentifier(
                query.Id.Value,
                code: "relationQuery.query.idMissing",
                message: "A query definition must have a non-empty id.",
                location: "/definition/id");
            ValidateIdentifier(
                query.Name.Value,
                code: "relationQuery.query.nameMissing",
                message: "A query definition must have a non-empty name.",
                location: "/definition/name");

            var results = query.Results.IsDefault ? [] : query.Results;
            if (results.IsDefaultOrEmpty)
            {
                Add(
                    code: "relationQuery.query.resultsEmpty",
                    message: "A query definition must declare at least one result.",
                    location: "/definition/results");
            }

            HashSet<QueryResultId> ids = [];
            foreach (var result in results)
            {
                ValidateIdentifier(
                    result.Id.Value,
                    code: "relationQuery.query.resultIdMissing",
                    message: "A query result must have a non-empty id.",
                    location: "/definition/results");
                ValidateIdentifier(
                    result.Input.Value,
                    code: "relationQuery.query.resultNodeIdMissing",
                    message: $"Query result '{result.Id.Value}' must reference a non-empty input node id.",
                    location: $"/definition/results/{result.Id.Value}/input");

                if (!ids.Add(result.Id))
                {
                    Add(
                        code: "relationQuery.query.resultDuplicateId",
                        message: $"Duplicate query result id '{result.Id.Value}'.",
                        location: $"/definition/results/{result.Id.Value}");
                }

                if (!nodes.TryGetValue(result.Input, out var input))
                {
                    Add(
                        code: "relationQuery.query.resultNodeMissing",
                        message: $"Query result '{result.Id.Value}' references unknown node '{result.Input.Value}'.",
                        location: $"/definition/results/{result.Id.Value}/input");
                    continue;
                }

                var resultBindings = ResolveBindings(result.Input);
                if (resultBindings.Count != 1)
                {
                    Add(
                        code: "relationQuery.query.resultBindingAmbiguous",
                        message: $"Query result '{result.Id.Value}' must resolve to exactly one shaped value binding.",
                        location: $"/definition/results/{result.Id.Value}/input");
                }

                var isAggregateResult = HasAggregateAncestry(result.Input, new HashSet<QueryNodeId>());
                if (result is AggregationQueryResultDefinition && !isAggregateResult)
                {
                    Add(
                        code: "relationQuery.query.aggregationResultNodeInvalid",
                        message: $"Aggregation result '{result.Id.Value}' must reference an aggregate-derived node.",
                        location: $"/definition/results/{result.Id.Value}/input");
                }
                else if (result is RowsQueryResultDefinition && isAggregateResult)
                {
                    Add(
                        code: "relationQuery.query.rowsResultNodeInvalid",
                        message: $"Rows result '{result.Id.Value}' cannot reference an aggregate-derived node.",
                        location: $"/definition/results/{result.Id.Value}/input");
                }
            }
        }

        void ValidateReachability()
        {
            HashSet<QueryNodeId> reachable = [];
            IEnumerable<QueryNodeId> roots = definition switch
            {
                RelationDefinition { Output: not null } relation => [relation.Output.Node],
                QueryDefinition query => (query.Results.IsDefault ? [] : query.Results)
                    .Select(static result => result.Input),
                _ => []
            };

            foreach (var root in roots)
                CollectReachable(root, reachable);

            foreach (var node in DefinitionNodes)
            {
                if (!reachable.Contains(node.Id))
                {
                    Add(
                        code: "relationQuery.node.unreachable",
                        message: $"Node '{node.Id.Value}' is not reachable from a declared relation output or query result.",
                        location: NodeLocation(node.Id));
                }
            }
        }

        void CollectReachable(QueryNodeId nodeId, ISet<QueryNodeId> reachable)
        {
            if (!reachable.Add(nodeId) || !nodes.TryGetValue(nodeId, out var node))
                return;
            foreach (var input in node.Inputs)
                CollectReachable(input, reachable);
        }

        bool HasAggregateAncestry(QueryNodeId nodeId, ISet<QueryNodeId> visited)
        {
            if (!visited.Add(nodeId) || !nodes.TryGetValue(nodeId, out var node))
                return false;

            return node is AggregateQueryNode
                   || node.Inputs.Any(input => HasAggregateAncestry(input, visited));
        }

        void ValidateBinding(ValueBindingId binding, QueryNodeId nodeId, string property)
        {
            ValidateIdentifier(
                binding.Value,
                code: "relationQuery.binding.idMissing",
                message: $"Node '{nodeId.Value}' must declare a non-empty {property} binding.",
                location: $"{NodeLocation(nodeId)}/{property}");
        }

        void ValidateShape(QualifiedShapeId shape, QueryNodeId nodeId, string property) =>
            ValidateShape(
                shape,
                context: $"Node '{nodeId.Value}'",
                location: $"{NodeLocation(nodeId)}/{property}");

        void ValidateShape(QualifiedShapeId shape, string context, string location)
        {
            ValidateIdentifier(
                shape.GraphId.Value,
                code: "relationQuery.shape.graphIdMissing",
                message: $"{context} must declare a non-empty shape graph id.",
                location: $"{location}/graphId");
            ValidateIdentifier(
                shape.ShapeId.Value,
                code: "relationQuery.shape.idMissing",
                message: $"{context} must declare a non-empty shape id.",
                location: $"{location}/shapeId");
        }

        void ValidateFieldPath(FieldPath path, string location)
        {
            if (path.Segments.IsDefaultOrEmpty)
            {
                Add(
                    code: "relationQuery.fieldPath.empty",
                    message: "A relation/query field path must contain at least one segment.",
                    location: location);
            }
        }

        void ValidatePortableType(TypeRef type, string location)
        {
            if (type is null)
            {
                Add(
                    code: "relationQuery.type.missing",
                    message: "A required semantic type reference is missing.",
                    location: location);
                return;
            }

            switch (type)
            {
                case OpaqueRuntimeTypeRef:
                    Add(
                        code: "relationQuery.type.opaqueRuntimeUnsupported",
                        message: "Canonical relation/query IR cannot contain a runtime-specific opaque type reference.",
                        location: location);
                    break;
                case NamedTypeRef named when string.IsNullOrWhiteSpace(named.TypeId.Value):
                    Add(
                        code: "relationQuery.type.namedIdMissing",
                        message: "A named type reference must have a non-empty type id.",
                        location: location);
                    break;
                case EntityReferenceTypeRef entity when string.IsNullOrWhiteSpace(entity.Entity.Value):
                    Add(
                        code: "relationQuery.type.entityNameMissing",
                        message: "An entity reference type must have a non-empty entity name.",
                        location: location);
                    break;
                case ArrayTypeRef { ElementType: null }:
                    Add(
                        code: "relationQuery.type.arrayElementMissing",
                        message: "An array type reference must declare an element type.",
                        location: location);
                    break;
                case ArrayTypeRef array:
                    ValidatePortableType(array.ElementType, $"{location}/elementType");
                    break;
                case ObjectTypeRef obj when obj.Fields.IsDefaultOrEmpty:
                    Add(
                        code: "relationQuery.type.objectFieldsEmpty",
                        message: "An inline object type must declare at least one field.",
                        location: location);
                    break;
                case ObjectTypeRef obj:
                    foreach (var field in obj.Fields)
                    {
                        if (string.IsNullOrWhiteSpace(field.Name))
                        {
                            Add(
                                code: "relationQuery.type.objectFieldNameMissing",
                                message: "An inline object field must have a non-empty name.",
                                location: location);
                        }
                        else if (field.Type is null)
                        {
                            Add(
                                code: "relationQuery.type.objectFieldTypeMissing",
                                message: $"Inline object field '{field.Name}' must declare a type.",
                                location: $"{location}/fields/{field.Name}/type");
                        }
                        else
                        {
                            ValidatePortableType(field.Type, $"{location}/fields/{field.Name}/type");
                        }
                    }
                    break;
            }
        }

        void ValidatePortableObservationValue(ObservationValue value, string location)
        {
            switch (value.Kind)
            {
                case ObservationValueKind.Undefined:
                case ObservationValueKind.Bytes:
                case ObservationValueKind.DateTimeOffset:
                case ObservationValueKind.DateOnly:
                case ObservationValueKind.TimeOnly:
                case ObservationValueKind.TimeSpan:
                    Add(
                        code: "relationQuery.value.kindUnsupported",
                        message: $"Observation value kind '{value.Kind}' does not have a lossless canonical relation/query JSON encoding.",
                        location: location);
                    break;
                case ObservationValueKind.Double when !double.IsFinite(value.Double):
                    Add(
                        code: "relationQuery.value.numberNonFinite",
                        message: "Canonical relation/query JSON cannot represent non-finite numeric values.",
                        location: location);
                    break;
                case ObservationValueKind.Object when value.Fields is not null:
                    foreach (var (property, child) in value.Fields)
                        ValidatePortableObservationValue(child, $"{location}/{property}");
                    break;
                case ObservationValueKind.Array when value.Array is not null:
                    for (var index = 0; index < value.Array.Length; index++)
                        ValidatePortableObservationValue(value.Array[index], $"{location}/{index}");
                    break;
            }
        }

        void ValidateIdentifier(string? value, string code, string message, string location)
        {
            if (string.IsNullOrWhiteSpace(value))
                Add(code, message, location);
        }

        HashSet<ValueBindingId> CopyBindings(QueryNodeId input) => [.. ResolveBindings(input)];

        void Add(string code, string message, string? location = null)
        {
            Diagnostics.Add(new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: message,
                Location: location));
        }

        static string NodeLocation(QueryNodeId nodeId) => $"/definition/body/nodes/{nodeId.Value}";
    }
}
