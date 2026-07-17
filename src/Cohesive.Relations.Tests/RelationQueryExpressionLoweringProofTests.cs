using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionLoweringProofTests
{
    static readonly QualifiedShapeId LoadShape = new(new("expression-proof/v1"), new("Load"));

    [Fact]
    public void DirectMember_LowersToBindingQualifiedCanonicalFieldAndRetainsSourceReference()
    {
        var core = new RelationQueryAuthoringCore();
        var source = core.Source(
            LoadShape,
            nodeId: new("loads"),
            bindingId: new("load"));
        var lowerer = new RelationQueryExpressionLoweringProof(ResolvePath);

        var lowered = lowerer.LowerValue(
            source.Binding,
            (Load load) => load.Id,
            sourceReference: "member/load-id");

        var field = Assert.IsType<FieldExpr>(lowered.Value);
        Assert.Equal(source.Binding.Id, field.Binding);
        Assert.Equal(FieldPath.FromField("load_id"), field.Path);
        Assert.Equal(RelationQueryExpressionLoweringProof.Producer, lowered.ValueSource.Producer);
        Assert.Equal("member/load-id/body", lowered.ValueSource.Reference);
    }

    [Fact]
    public void BinaryPredicate_LowersCanonicalOperatorMembersAndLiteral()
    {
        var core = new RelationQueryAuthoringCore();
        var source = core.Source(LoadShape);
        var lowerer = new RelationQueryExpressionLoweringProof(ResolvePath);

        var lowered = lowerer.LowerPredicate(
            source.Binding,
            (Load load) => load.Status == "Ready",
            sourceReference: "filter/ready-loads");

        var binary = Assert.IsType<BinaryExpr>(lowered.Value);
        Assert.Equal(BinaryOperator.Eq, binary.Operator);
        var field = Assert.IsType<FieldExpr>(binary.Left);
        Assert.Equal(source.Binding.Id, field.Binding);
        Assert.Equal(FieldPath.FromField("load_status"), field.Path);
        Assert.Equal(
            ObservationValue.FromString("Ready"),
            Assert.IsType<ConstantExpr>(binary.Right).Value);
        Assert.Equal("filter/ready-loads", lowered.NodeSource.Reference);
        Assert.Equal("filter/ready-loads/body", lowered.PredicateSource.Reference);
    }

    [Fact]
    public void ObjectInitializer_LowersToStructuralProjectionInputsWithPerSiteProvenance()
    {
        var core = new RelationQueryAuthoringCore();
        var source = core.Source(LoadShape);
        var lowerer = new RelationQueryExpressionLoweringProof(ResolvePath);

        var lowered = lowerer.LowerProjection(
            source.Binding,
            (Load load) => new LoadSearchProjection
            {
                Id = load.Id,
                Status = load.Status
            },
            sourceReference: "projection/load-search");

        Assert.Equal(2, lowered.Assignments.Length);
        Assert.Equal(FieldPath.FromField("document_id"), lowered.Assignments[0].Target);
        Assert.Equal(FieldPath.FromField("document_status"), lowered.Assignments[1].Target);
        Assert.Equal(
            FieldPath.FromField("load_id"),
            Assert.IsType<FieldExpr>(lowered.Assignments[0].Value).Path);
        Assert.Equal(
            FieldPath.FromField("load_status"),
            Assert.IsType<FieldExpr>(lowered.Assignments[1].Value).Path);
        Assert.Equal(
            "projection/load-search/body/bindings/0",
            lowered.Assignments[0].AssignmentSource?.Reference);
        Assert.Equal(
            "projection/load-search/body/bindings/0/expression",
            lowered.Assignments[0].ValueSource?.Reference);
        Assert.Equal("projection/load-search", lowered.NodeSource.Reference);
        Assert.Equal("projection/load-search/body", lowered.BindingSource.Reference);

        Assert.DoesNotContain(
            EnumerateObjectGraph(lowered),
            static value => value is Expression or MemberInfo or Type);
    }

    [Fact]
    public void ExpressionProducer_BuildsOnlyThroughStructuralCoreAndMatchesHandAuthoredCanonicalIr()
    {
        var lowerer = new RelationQueryExpressionLoweringProof(ResolvePath);
        var resultShape = new QualifiedShapeId(
            new("expression-proof/v1"),
            new("LoadSearchProjection"));
        var authored = lowerer.BuildQuery(
            new("ready-load-search"),
            new("ReadyLoadSearch"),
            LoadShape,
            resultShape,
            (Load load) => load.Status == "Ready",
            (Load load) => new LoadSearchProjection
            {
                Id = load.Id,
                Status = load.Status
            },
            sourceReference: "query");

        var sourceId = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.SourceNode,
            1);
        var sourceBinding = RelationQueryAuthoringIdentityConvention.CreateBindingId(sourceId, "source");
        var filterId = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.FilterNode,
            1);
        var projectionId = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.ProjectNode,
            1);
        var projectionBinding = RelationQueryAuthoringIdentityConvention.CreateBindingId(projectionId, "result");
        var rowsId = RelationQueryAuthoringIdentityConvention.CreateResultId(
            RelationQueryWireNames.RowsResult,
            1);
        var expectedAssignmentIds = new[]
        {
            RelationQueryAuthoringIdentityConvention.CreateAssignmentId(projectionId, "projection", 1),
            RelationQueryAuthoringIdentityConvention.CreateAssignmentId(projectionId, "projection", 2)
        };
        Cohesive.Relations.IR.QueryDefinition expected = new(
            new("ready-load-search"),
            new("ReadyLoadSearch"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, sourceBinding, LoadShape),
                new FilterQueryNode(
                    filterId,
                    sourceId,
                    new BinaryExpr(
                        BinaryOperator.Eq,
                        Expr.Field(sourceBinding, FieldPath.FromField("load_status")),
                        Expr.Const("Ready"))),
                new ProjectQueryNode(
                    projectionId,
                    filterId,
                    projectionBinding,
                    resultShape,
                    [
                        new(
                            expectedAssignmentIds[0],
                            FieldPath.FromField("document_id"),
                            Expr.Field(sourceBinding, FieldPath.FromField("load_id"))),
                        new(
                            expectedAssignmentIds[1],
                            FieldPath.FromField("document_status"),
                            Expr.Field(sourceBinding, FieldPath.FromField("load_status")))
                    ])
            ]),
            [new RowsQueryResultDefinition(rowsId, projectionId)]);

        var actualDocument = authored.CreateDocument();
        var expectedDocument = RelationQueryDocument.FromDefinition(expected);
        Assert.True(authored.Validation.IsValid);
        Assert.Equal(expectedDocument.DefinitionFingerprint, actualDocument.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(expectedDocument, indented: false),
            RelationQueryJsonSerializer.Serialize(actualDocument, indented: false));

        var actualProject = Assert.Single(authored.Definition.Body.Nodes.OfType<ProjectQueryNode>());
        Assert.Contains(
            authored.Provenance.Sources,
            decision => decision.Kind == RelationQueryAuthoringDecisionKind.Binding
                        && decision.Target == sourceBinding.Value
                        && decision.Source.Reference == "query/source/binding");
        Assert.Contains(
            authored.Provenance.Sources,
            decision => decision.Kind == RelationQueryAuthoringDecisionKind.Expression
                        && decision.Target == filterId.Value
                        && decision.Role == "predicate"
                        && decision.Source.Reference == "query/filter/body");
        Assert.Contains(
            authored.Provenance.Sources,
            decision => decision.Kind == RelationQueryAuthoringDecisionKind.Binding
                        && decision.Target == projectionBinding.Value
                        && decision.Source.Reference == "query/projection/body");
        for (var index = 0; index < actualProject.Assignments.Length; index++)
        {
            var assignment = actualProject.Assignments[index];
            Assert.Contains(
                authored.Provenance.Sources,
                decision => decision.Kind == RelationQueryAuthoringDecisionKind.Assignment
                            && decision.Target == assignment.Id.Value
                            && decision.Source.Reference == $"query/projection/body/bindings/{index}");
            Assert.Contains(
                authored.Provenance.Sources,
                decision => decision.Kind == RelationQueryAuthoringDecisionKind.Expression
                            && decision.Target == assignment.Id.Value
                            && decision.Role == "value"
                            && decision.Source.Reference == $"query/projection/body/bindings/{index}/expression");
        }

        Assert.DoesNotContain(
            EnumerateObjectGraph(authored.Definition),
            static value => value is Expression or MemberInfo or Type);
    }

    [Fact]
    public void UnsupportedExpression_IsRejectedWithoutEvaluatingCapturedState()
    {
        var core = new RelationQueryAuthoringCore();
        var source = core.Source(LoadShape);
        var lowerer = new RelationQueryExpressionLoweringProof(ResolvePath);
        var captured = new ThrowingCapturedValue();

        var method = Assert.Throws<RelationQueryExpressionLoweringProofException>(() =>
            lowerer.LowerPredicate(
                source.Binding,
                (Load load) => load.Status.StartsWith("R"),
                sourceReference: "filter/method"));
        var capture = Assert.Throws<RelationQueryExpressionLoweringProofException>(() =>
            lowerer.LowerPredicate(
                source.Binding,
                (Load load) => load.Status == captured.Value,
                sourceReference: "filter/capture"));

        Assert.Equal("body", method.ExpressionPath);
        Assert.Equal("body/right", capture.ExpressionPath);
        Assert.Equal(0, captured.ReadCount);
    }

    [Fact]
    public void ProjectionConstructorArguments_AreRejectedInsteadOfSilentlyDiscarded()
    {
        var core = new RelationQueryAuthoringCore();
        var source = core.Source(LoadShape);
        var lowerer = new RelationQueryExpressionLoweringProof(ResolvePath);

        var exception = Assert.Throws<RelationQueryExpressionLoweringProofException>(() =>
            lowerer.LowerProjection(
                source.Binding,
                (Load load) => new ConstructorProjection(load.Id)
                {
                    Status = load.Status
                },
                sourceReference: "projection/constructor"));

        Assert.Equal("body/new", exception.ExpressionPath);
    }

    static FieldPath ResolvePath(MemberInfo member)
    {
        var serializedName = member.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name;
        return FieldPath.FromField(serializedName ?? member.Name);
    }

    static IEnumerable<object> EnumerateObjectGraph(object root)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        return Enumerate(root, visited);

        static IEnumerable<object> Enumerate(object? value, ISet<object> visited)
        {
            if (value is null)
                yield break;

            yield return value;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string or decimal)
                yield break;
            if (!type.IsValueType && !visited.Add(value))
                yield break;

            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    foreach (var nested in Enumerate(item, visited))
                        yield return nested;
                }

                yield break;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetMethod is null
                    || property.GetIndexParameters().Length != 0
                    || property.PropertyType.IsByRefLike)
                    continue;

                foreach (var nested in Enumerate(property.GetValue(value), visited))
                    yield return nested;
            }
        }
    }

    sealed class Load
    {
        [JsonPropertyName("load_id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("load_status")]
        public string Status { get; init; } = string.Empty;
    }

    sealed class LoadSearchProjection
    {
        [JsonPropertyName("document_id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("document_status")]
        public string Status { get; init; } = string.Empty;
    }

    sealed class ConstructorProjection(string id)
    {
        public string Id { get; } = id;

        public string Status { get; init; } = string.Empty;
    }

    sealed class ThrowingCapturedValue
    {
        public int ReadCount { get; private set; }

        public string Value
        {
            get
            {
                ReadCount++;
                throw new InvalidOperationException("The proof must not evaluate captured state.");
            }
        }
    }
}
