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
            return MissingBodyValidation();

        var bindingFlow = RelationQueryBindingFlowAnalyzer.Analyze(definition);
        return RelationQueryExpressionAnalyzer.AnalyzeWithBindingFlow(definition, bindingFlow).Validation;
    }

    /// <summary>
    /// Validates a definition using a previously computed canonical binding-flow analysis.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to validate.</param>
    /// <param name="bindingFlow">Binding flow computed for <paramref name="definition"/>.</param>
    /// <returns>Structured semantic validation diagnostics.</returns>
    internal static DocumentValidationResult ValidateStructureWithBindingFlow(
        RelationQueryDefinition definition,
        RelationQueryBindingFlowAnalysis bindingFlow)
    {
        if (definition.Body is null)
            return MissingBodyValidation();

        ValidationContext context = new(definition, bindingFlow);
        context.Validate();
        return DocumentValidationResult.FromDiagnostics(context.Diagnostics);
    }

    static DocumentValidationResult MissingBodyValidation() =>
        DocumentValidationResult.FromDiagnostics([
            new(Code: "relationQuery.body.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relation/query definition must contain a logical query body.",
                Location: "/definition/body")
        ]);

    sealed class ValidationContext(
        RelationQueryDefinition definition,
        RelationQueryBindingFlowAnalysis bindingFlow)
    {
        readonly Dictionary<QueryNodeId, LogicalQueryNode> nodes = [];
        public List<DocumentValidationDiagnostic> Diagnostics { get; } = [];

        ImmutableArray<LogicalQueryNode> RawDefinitionNodes =>
            definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes;

        ImmutableArray<LogicalQueryNode> DefinitionNodes =>
            [.. RawDefinitionNodes.Where(static node => node is not null)];

        ImmutableArray<QueryParameterDefinition> RawDefinitionParameters =>
            definition.Body.Parameters.IsDefault ? [] : definition.Body.Parameters;

        ImmutableArray<QueryParameterDefinition> DefinitionParameters =>
            [.. RawDefinitionParameters.Where(static parameter => parameter is not null)];

        public void Validate()
        {
            ReportNullEntries(
                RawDefinitionNodes,
                "relationQuery.node.entryMissing",
                "A logical query node entry cannot be null.",
                "/definition/body/nodes");
            ReportNullEntries(
                RawDefinitionParameters,
                "relationQuery.parameter.entryMissing",
                "A query parameter entry cannot be null.",
                "/definition/body/parameters");
            if (DefinitionNodes.IsDefaultOrEmpty)
            {
                Add(code: "relationQuery.body.nodesEmpty",
                    message: "A logical query body must contain at least one node.",
                    location: "/definition/body/nodes");
            }

            IndexParameters();
            IndexNodes();
            ValidateNodeReferences();
            Diagnostics.AddRange(bindingFlow.StructuralDiagnostics);
            ValidateNodeExpressions();
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

                if (!Enum.IsDefined(parameter.Presence))
                {
                    Add(
                        code: "relationQuery.parameter.presenceInvalid",
                        message: $"Query parameter '{parameter.Id.Value}' declares unsupported presence '{parameter.Presence}'.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}/presence");
                }

                var defaultKindIsValid = Enum.IsDefined(parameter.DefaultKind);
                if (!defaultKindIsValid)
                {
                    Add(
                        code: "relationQuery.parameter.defaultKindInvalid",
                        message: $"Query parameter '{parameter.Id.Value}' declares unsupported default kind '{parameter.DefaultKind}'.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}/defaultKind");
                }

                if (defaultKindIsValid
                    && parameter.Presence == FieldPresence.Required
                    && parameter.DefaultKind == QueryParameterDefaultKind.Value)
                {
                    Add(
                        code: "relationQuery.parameter.requiredHasDefault",
                        message: $"Required query parameter '{parameter.Id.Value}' cannot declare a default value.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}/defaultKind");
                }

                if (defaultKindIsValid
                    && parameter.DefaultKind == QueryParameterDefaultKind.None
                    && parameter.DefaultValue is not null)
                {
                    Add(
                        code: "relationQuery.parameter.defaultUnexpected",
                        message: $"Query parameter '{parameter.Id.Value}' contains a default value but does not declare one.",
                        location: $"/definition/body/parameters/{parameter.Id.Value}/defaultValue");
                }

                if (parameter.DefaultValue is { } persistedDefaultValue)
                {
                    ValidatePortableObservationValue(
                        persistedDefaultValue,
                        $"/definition/body/parameters/{parameter.Id.Value}/defaultValue");
                }

                if (defaultKindIsValid && parameter.DefaultKind == QueryParameterDefaultKind.Value)
                {
                    var effectiveDefaultValue = parameter.DefaultValue ?? ObservationValue.Null;
                    if (parameter.Type is not null
                        && Enum.IsDefined(parameter.Presence)
                        && !parameter.EffectiveValueContract.IsSatisfiedByConstant(effectiveDefaultValue))
                    {
                        Add(
                            code: "relationQuery.parameter.defaultTypeMismatch",
                            message: $"Query parameter '{parameter.Id.Value}' has a default value that does not satisfy its declared type.",
                            location: $"/definition/body/parameters/{parameter.Id.Value}/defaultValue");
                    }
                }
            }

            foreach (var duplicate in DefinitionParameters
                         .GroupBy(static parameter => parameter.Id.Value, StringComparer.Ordinal)
                         .Where(static group => group.Count() > 1)
                         .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                Add(
                    code: "relationQuery.parameter.duplicateId",
                    message: $"Duplicate query parameter id '{duplicate.Key}'.",
                    location: $"/definition/body/parameters/{duplicate.Key}");
            }
        }

        void IndexNodes()
        {
            foreach (var node in DefinitionNodes)
                ValidateNodeLocal(node);

            foreach (var group in DefinitionNodes
                         .GroupBy(static node => node.Id)
                         .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
            {
                var candidates = group.Take(2).ToArray();
                if (candidates.Length > 1)
                {
                    Add(code: "relationQuery.node.duplicateId",
                        message: $"Duplicate logical query node id '{group.Key.Value}'.",
                        location: NodeLocation(group.Key));
                    continue;
                }

                nodes.Add(group.Key, candidates[0]);
            }

            foreach (var duplicate in nodes.Values
                         .OfType<SourceQueryNode>()
                         .GroupBy(static source => source.Binding)
                         .Where(static group => group.Count() > 1)
                         .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
            {
                var location = duplicate
                    .Select(static source => source.Id)
                    .OrderBy(static id => id.Value, StringComparer.Ordinal)
                    .First();
                Add(
                    code: "relationQuery.binding.duplicateSource",
                    message: $"Source binding '{duplicate.Key.Value}' is declared by more than one source node.",
                    location: NodeLocation(location));
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
                case JoinQueryNode join when !Enum.IsDefined(join.Kind):
                    Add(
                        code: "relationQuery.join.kindInvalid",
                        message: $"Join node '{join.Id.Value}' declares unsupported join kind '{join.Kind}'.",
                        location: $"{NodeLocation(join.Id)}/kind");
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
                    else if (project.Assignments.Any(static assignment => assignment is null))
                    {
                        Add(
                            code: "relationQuery.project.assignmentMissing",
                            message: $"Projection node '{project.Id.Value}' contains a missing assignment entry.",
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
                    else if (aggregate.Aggregates.Any(static assignment => assignment is null))
                    {
                        Add(
                            code: "relationQuery.aggregate.assignmentMissing",
                            message: $"Aggregate node '{aggregate.Id.Value}' contains a missing aggregate assignment entry.",
                            location: $"{NodeLocation(aggregate.Id)}/aggregates");
                    }
                    if (!aggregate.Groupings.IsDefault
                        && aggregate.Groupings.Any(static grouping => grouping is null))
                    {
                        Add(
                            code: "relationQuery.aggregate.groupingMissing",
                            message: $"Aggregate node '{aggregate.Id.Value}' contains a missing grouping entry.",
                            location: $"{NodeLocation(aggregate.Id)}/groupings");
                    }

                    foreach (var assignment in aggregate.Aggregates.IsDefault
                                 ? []
                                 : aggregate.Aggregates.Where(static assignment => assignment is not null))
                    {
                        if (!Enum.IsDefined(assignment.Operation))
                        {
                            Add(
                                code: "relationQuery.aggregate.operationInvalid",
                                message: $"Aggregate assignment '{assignment.Id.Value}' declares an unsupported operation value.",
                                location: $"{NodeLocation(aggregate.Id)}/aggregates/{assignment.Id.Value}/operation");
                        }
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
                case OrderQueryNode order when order.Orderings.Any(static ordering => ordering is null):
                    Add(
                        code: "relationQuery.order.orderingMissing",
                        message: $"Order node '{order.Id.Value}' contains a missing ordering entry.",
                        location: $"{NodeLocation(order.Id)}/orderings");
                    break;
                case OrderQueryNode order:
                    for (var index = 0; index < order.Orderings.Length; index++)
                    {
                        var ordering = order.Orderings[index];
                        if (!Enum.IsDefined(ordering.Direction))
                        {
                            Add(
                                code: "relationQuery.order.directionInvalid",
                                message: $"Order node '{order.Id.Value}' contains an unsupported sort direction.",
                                location: $"{NodeLocation(order.Id)}/orderings/{index}/direction");
                        }
                        if (!Enum.IsDefined(ordering.NullPlacement))
                        {
                            Add(
                                code: "relationQuery.order.nullPlacementInvalid",
                                message: $"Order node '{order.Id.Value}' contains an unsupported null placement.",
                                location: $"{NodeLocation(order.Id)}/orderings/{index}/nullPlacement");
                        }
                    }
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

        HashSet<ValueBindingId> ResolveBindings(QueryNodeId nodeId) =>
            [.. bindingFlow.GetOutput(nodeId).Bindings.Keys];

        void ValidateNodeExpressions()
        {
            foreach (var node in DefinitionNodes.OrderBy(static node => node.Id.Value, StringComparer.Ordinal))
            {
                switch (node)
                {
                    case FilterQueryNode filter:
                        ValidateExpressionPortability(filter.Predicate, $"{NodeLocation(filter.Id)}/predicate");
                        break;
                    case JoinQueryNode join:
                        ValidateExpressionPortability(join.Predicate, $"{NodeLocation(join.Id)}/predicate");
                        break;
                    case ExpandCollectionQueryNode expansion:
                        ValidateExpressionPortability(expansion.Collection, $"{NodeLocation(expansion.Id)}/collection");
                        break;
                    case ProjectQueryNode project:
                        ValidateAssignments(
                            (project.Assignments.IsDefault ? [] : project.Assignments)
                            .Where(static assignment => assignment is not null)
                            .Select(static assignment => (assignment.Id, assignment.Target, assignment.Value)),
                            project.Id);
                        break;
                    case DistinctQueryNode distinct:
                        var distinctKeys = distinct.Keys.IsDefault ? [] : distinct.Keys;
                        for (var index = 0; index < distinctKeys.Length; index++)
                        {
                            ValidateExpressionPortability(
                                distinctKeys[index],
                                $"{NodeLocation(distinct.Id)}/keys/{index}");
                        }
                        break;
                    case AggregateQueryNode aggregate:
                        ValidateAggregateExpressions(aggregate);
                        break;
                    case OrderQueryNode order:
                        var orderings = order.Orderings.IsDefault ? [] : order.Orderings;
                        for (var index = 0; index < orderings.Length; index++)
                        {
                            if (orderings[index] is not { } ordering)
                                continue;
                            ValidateExpressionPortability(
                                ordering.Key,
                                $"{NodeLocation(order.Id)}/orderings/{index}/key");
                        }
                        break;
                    case PageQueryNode page:
                        ValidatePageExpressions(page);
                        break;
                }
            }
        }

        void ValidateAggregateExpressions(AggregateQueryNode aggregate)
        {
            HashSet<QueryAssignmentId> ids = [];
            HashSet<FieldPath> targets = [];

            foreach (var grouping in (aggregate.Groupings.IsDefault ? [] : aggregate.Groupings)
                         .Where(static grouping => grouping is not null)
                         .OrderBy(static grouping => grouping.Id.Value, StringComparer.Ordinal))
            {
                ValidateAssignmentIdentity(grouping.Id, grouping.Target, ids, targets, aggregate.Id);
                ValidateExpressionPortability(
                    grouping.Key,
                    $"{NodeLocation(aggregate.Id)}/groupings/{grouping.Id.Value}/key");
            }

            foreach (var assignment in (aggregate.Aggregates.IsDefault ? [] : aggregate.Aggregates)
                         .Where(static assignment => assignment is not null)
                         .OrderBy(static assignment => assignment.Id.Value, StringComparer.Ordinal))
            {
                ValidateAssignmentIdentity(assignment.Id, assignment.Target, ids, targets, aggregate.Id);
                if (assignment.Value is not null)
                {
                    ValidateExpressionPortability(
                        assignment.Value,
                        $"{NodeLocation(aggregate.Id)}/aggregates/{assignment.Id.Value}/value");
                }
                if (assignment.Filter is not null)
                {
                    ValidateExpressionPortability(
                        assignment.Filter,
                        $"{NodeLocation(aggregate.Id)}/aggregates/{assignment.Id.Value}/filter");
                }
            }
        }

        void ValidatePageExpressions(PageQueryNode page)
        {
            if (page.Page is not KeysetPageDefinition keyset)
                return;

            var after = keyset.After.IsDefault ? [] : keyset.After;
            for (var index = 0; index < after.Length; index++)
            {
                ValidateExpressionPortability(
                    after[index],
                    $"{NodeLocation(page.Id)}/page/after/{index}");
            }

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

        void ValidateAssignments(
            IEnumerable<(QueryAssignmentId Id, FieldPath Target, Expr Expression)> assignments,
            QueryNodeId nodeId)
        {
            HashSet<QueryAssignmentId> ids = [];
            HashSet<FieldPath> targets = [];
            foreach (var assignment in assignments)
            {
                ValidateAssignmentIdentity(assignment.Id, assignment.Target, ids, targets, nodeId);
                ValidateExpressionPortability(
                    assignment.Expression,
                    $"{NodeLocation(nodeId)}/assignments/{assignment.Id.Value}/value");
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
            var targetIsValid = ValidateFieldPath(target, NodeLocation(nodeId));

            if (!ids.Add(id))
            {
                Add(
                    code: "relationQuery.assignment.duplicateId",
                    message: $"Node '{nodeId.Value}' contains duplicate assignment id '{id.Value}'.",
                    location: NodeLocation(nodeId));
            }

            if (targetIsValid && !targets.Add(target))
            {
                Add(
                    code: "relationQuery.assignment.duplicateTarget",
                    message: $"Node '{nodeId.Value}' assigns target '{SafePath(target)}' more than once.",
                    location: NodeLocation(nodeId));
            }
        }

        void ValidateExpressionPortability(Expr expression, string location)
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
                    break;
                case FieldRefExpr field:
                    ValidateFieldPath(field.Path, location);
                    ValidatePortableType(field.Type, $"{location}/type");
                    break;
                case CurrentItemExpr:
                case ParameterExpr:
                    break;
                case LiteralExpr literal:
                    ValidatePortableType(literal.Type, $"{location}/type");
                    ValidatePortableObservationValue(literal.Value, $"{location}/value");
                    break;
                case ConstantExpr constant:
                    ValidatePortableObservationValue(constant.Value, $"{location}/value");
                    break;
                case UnaryExpr unary:
                    ValidateExpressionPortability(unary.Operand, $"{location}/operand");
                    break;
                case BinaryExpr binary:
                    ValidateExpressionPortability(binary.Left, $"{location}/left");
                    ValidateExpressionPortability(binary.Right, $"{location}/right");
                    break;
                case ConditionalExpr conditional:
                    if (!IsUnspecifiedResultType(conditional.ReturnType))
                        ValidatePortableType(conditional.ReturnType, $"{location}/returnType");
                    ValidateExpressionPortability(conditional.Test, $"{location}/test");
                    ValidateExpressionPortability(conditional.IfTrue, $"{location}/ifTrue");
                    ValidateExpressionPortability(conditional.IfFalse, $"{location}/ifFalse");
                    break;
                case CallExpr call:
                    if (!IsUnspecifiedResultType(call.ReturnType))
                        ValidatePortableType(call.ReturnType, $"{location}/returnType");
                    var arguments = call.Arguments.IsDefault ? [] : call.Arguments;
                    for (var index = 0; index < arguments.Length; index++)
                        ValidateExpressionPortability(arguments[index], $"{location}/arguments/{index}");
                    break;
                case AggregateExpr aggregate:
                    ValidatePortableType(aggregate.ReturnType, $"{location}/returnType");
                    ValidateExpressionPortability(aggregate.Source, $"{location}/source");
                    var groupings = aggregate.GroupBy.IsDefault ? [] : aggregate.GroupBy;
                    for (var index = 0; index < groupings.Length; index++)
                        ValidateExpressionPortability(groupings[index], $"{location}/groupBy/{index}");
                    break;
            }
        }

        static bool IsUnspecifiedResultType(TypeRef? type) =>
            type is OpaqueRuntimeTypeRef { RuntimeType: "unknown" };

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
            if (!Enum.IsDefined(relation.Output.Mode))
            {
                Add(
                    code: "relationQuery.relation.outputModeInvalid",
                    message: $"Relation output declares unsupported cardinality mode '{relation.Output.Mode}'.",
                    location: "/definition/output/mode");
            }

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
                Add(code: "relationQuery.relation.outputNodeNotShaped",
                    message: $"Relation output node '{relation.Output.Node.Value}' must be a project or aggregate node.",
                    location: "/definition/output/node");
            }
            else if (outputShape.Value != relation.Output.Shape)
            {
                Add(code: "relationQuery.relation.outputShapeMismatch",
                    message: $"Relation output shape '{relation.Output.Shape}' does not match node shape '{outputShape.Value}'.",
                    location: "/definition/output/shape");
            }

            if (relation.Output.Key is not null)
                ValidateExpressionPortability(relation.Output.Key, "/definition/output/key");

            for (var index = 0; index < invariants.Length; index++)
            {
                var invariant = invariants[index];
                if (invariant is not null)
                    ValidateExpressionPortability(
                        invariant.Expression,
                        $"/definition/invariants/{index}/expression");
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
            for (var index = 0; index < results.Length; index++)
            {
                var result = results[index];
                if (result is null)
                {
                    Add(
                        code: "relationQuery.query.resultMissing",
                        message: "A query result entry cannot be null.",
                        location: $"/definition/results/{index}");
                    continue;
                }

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
                    Add(code: "relationQuery.query.resultDuplicateId",
                        message: $"Duplicate query result id '{result.Id.Value}'.",
                        location: $"/definition/results/{result.Id.Value}");
                }

                if (!nodes.TryGetValue(result.Input, out var input))
                {
                    Add(code: "relationQuery.query.resultNodeMissing",
                        message: $"Query result '{result.Id.Value}' references unknown node '{result.Input.Value}'.",
                        location: $"/definition/results/{result.Id.Value}/input");
                    continue;
                }

                var resultBindings = ResolveBindings(result.Input);
                if (resultBindings.Count != 1)
                {
                    Add(code: "relationQuery.query.resultBindingAmbiguous",
                        message: $"Query result '{result.Id.Value}' must resolve to exactly one shaped value binding.",
                        location: $"/definition/results/{result.Id.Value}/input");
                }

                var isAggregateResult = HasAggregateAncestry(result.Input, new HashSet<QueryNodeId>());
                if (result is AggregationQueryResultDefinition && !isAggregateResult)
                {
                    Add(code: "relationQuery.query.aggregationResultNodeInvalid",
                        message: $"Aggregation result '{result.Id.Value}' must reference an aggregate-derived node.",
                        location: $"/definition/results/{result.Id.Value}/input");
                }
                else if (result is RowsQueryResultDefinition && isAggregateResult)
                {
                    Add(code: "relationQuery.query.rowsResultNodeInvalid",
                        message: $"Rows result '{result.Id.Value}' cannot reference an aggregate-derived node.",
                        location: $"/definition/results/{result.Id.Value}/input");
                }
            }
        }

        void ValidateReachability()
        {
            HashSet<QueryNodeId> reachable = [];
            var roots = definition switch
            {
                RelationDefinition { Output: not null } relation => [relation.Output.Node],
                QueryDefinition query => (query.Results.IsDefault ? [] : query.Results)
                    .Where(static result => result is not null)
                    .Select(static result => result.Input),
                _ => []
            };

            foreach (var root in roots)
                CollectReachable(root, reachable);

            foreach (var node in DefinitionNodes)
            {
                if (!reachable.Contains(node.Id))
                {
                    Add(code: "relationQuery.node.unreachable",
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

        void ReportNullEntries<T>(
            ImmutableArray<T> values,
            string code,
            string message,
            string location)
            where T : class
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index] is null)
                    Add(code, message, $"{location}/{index}");
            }
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

        bool ValidateFieldPath(FieldPath path, string location)
        {
            if (path.Segments.IsDefaultOrEmpty)
            {
                Add(code: "relationQuery.fieldPath.empty",
                    message: "A relation/query field path must contain at least one segment.",
                    location: location);
                return false;
            }

            if (path.Segments.Any(static segment => segment.Kind switch
                {
                    SegmentKind.Field => string.IsNullOrWhiteSpace(segment.Segment),
                    SegmentKind.Element => segment.Segment is not null,
                    _ => true
                }))
            {
                Add(
                    code: "relationQuery.fieldPath.segmentInvalid",
                    message: "A relation/query field path contains an invalid segment.",
                    location: location);
                return false;
            }

            return true;
        }

        static string SafePath(FieldPath path) => path.Segments.IsDefaultOrEmpty
            ? "<invalid>"
            : string.Join("/", path.Segments.Select(static segment => segment.Kind == SegmentKind.Element
                ? "[]"
                : segment.Segment ?? "<field>"));

        void ValidatePortableType(TypeRef type, string location)
        {
            if (type is null)
            {
                Add(code: "relationQuery.type.missing",
                    message: "A required semantic type reference is missing.",
                    location: location);
                return;
            }

            switch (type)
            {
                case OpaqueRuntimeTypeRef:
                    Add(code: "relationQuery.type.opaqueRuntimeUnsupported",
                        message: "Canonical relation/query IR cannot contain a runtime-specific opaque type reference.",
                        location: location);
                    break;
                case NamedTypeRef named when string.IsNullOrWhiteSpace(named.TypeId.Value):
                    Add(code: "relationQuery.type.namedIdMissing",
                        message: "A named type reference must have a non-empty type id.",
                        location: location);
                    break;
                case EntityReferenceTypeRef entity when string.IsNullOrWhiteSpace(entity.Entity.Value):
                    Add(code: "relationQuery.type.entityNameMissing",
                        message: "An entity reference type must have a non-empty entity name.",
                        location: location);
                    break;
                case ArrayTypeRef { ElementType: null }:
                    Add(code: "relationQuery.type.arrayElementMissing",
                        message: "An array type reference must declare an element type.",
                        location: location);
                    break;
                case ArrayTypeRef array:
                    ValidatePortableType(array.ElementType, $"{location}/elementType");
                    break;
                case ObjectTypeRef obj when obj.Fields.IsDefaultOrEmpty:
                    Add(code: "relationQuery.type.objectFieldsEmpty",
                        message: "An inline object type must declare at least one field.",
                        location: location);
                    break;
                case ObjectTypeRef obj:
                    foreach (var field in obj.Fields)
                    {
                        if (string.IsNullOrWhiteSpace(field.Name))
                        {
                            Add(code: "relationQuery.type.objectFieldNameMissing",
                                message: "An inline object field must have a non-empty name.",
                                location: location);
                        }
                        else if (field.Type is null)
                        {
                            Add(code: "relationQuery.type.objectFieldTypeMissing",
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
                    Add(code: "relationQuery.value.kindUnsupported",
                        message: $"Observation value kind '{value.Kind}' does not have a lossless canonical relation/query JSON encoding.",
                        location: location);
                    break;
                case ObservationValueKind.Double when !double.IsFinite(value.Double):
                    Add(code: "relationQuery.value.numberNonFinite",
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
