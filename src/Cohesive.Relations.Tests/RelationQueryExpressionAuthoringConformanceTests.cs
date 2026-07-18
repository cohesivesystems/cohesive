using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionAuthoringConformanceTests
{
    [Fact]
    public void ExpressionAuthoredRelation_CompilesExecutesAndMapsWithCanonicalSemanticsAndProvenance()
    {
        var scenario = AuthorRelation(registerOutputFirst: false);
        var relation = scenario.Relation;

        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));
        var project = Assert.Single(relation.Definition.Body.Nodes.OfType<ProjectQueryNode>());
        Assert.Equal(
            ["id", "label", "adjustedAmount", "priority"],
            project.Assignments.Select(static assignment => assignment.Target.ToString()));
        var label = Assert.Single(project.Assignments, static assignment => assignment.Target.Matches("label"));
        var labelConditional = Assert.IsType<ConditionalExpr>(label.Value);
        Assert.Contains(
            Descendants(labelConditional).OfType<CallExpr>(),
            static call => call.Function == ExprFunctionNames.Concat);
        var amount = Assert.Single(
            project.Assignments,
            static assignment => assignment.Target.Matches("adjustedAmount"));
        Assert.Equal(BinaryOperator.Add, Assert.IsType<BinaryExpr>(amount.Value).Operator);
        var priority = Assert.Single(project.Assignments, static assignment => assignment.Target.Matches("priority"));
        Assert.Equal(ExprFunctionNames.Contains, Assert.IsType<CallExpr>(priority.Value).Function);

        var key = Assert.IsType<FieldExpr>(relation.Definition.Output.Key);
        Assert.Equal(project.ResultBinding, key.Binding);
        Assert.Equal(FieldPath.FromField("id"), key.Path);
        var invariant = Assert.Single(relation.Definition.Invariants);
        Assert.Equal("positive-adjusted-amount", invariant.Name);
        var negated = Assert.IsType<UnaryExpr>(invariant.Expression);
        Assert.Equal(UnaryOperator.Not, negated.Operator);
        Assert.Equal(BinaryOperator.Le, Assert.IsType<BinaryExpr>(negated.Operand).Operator);

        Assert.Contains(
            relation.Provenance.Sources,
            decision => decision.Kind == RelationQueryAuthoringDecisionKind.Expression
                        && decision.Role == "output/key"
                        && decision.Source.Producer == RelationQueryExpressionLowerer.Producer
                        && decision.Source.Reference == "conformance/relation/key/body");
        Assert.Contains(
            relation.Provenance.Sources,
            decision => decision.Kind == RelationQueryAuthoringDecisionKind.Assignment
                        && decision.Source.Reference.StartsWith(
                            "conformance/project/body/bindings/",
                            StringComparison.Ordinal));
        Assert.Contains(
            relation.Provenance.Sources,
            decision => decision.Kind == RelationQueryAuthoringDecisionKind.Expression
                        && decision.Role == "invariants/0/expression"
                        && decision.Source.Reference == "conformance/invariant/body");
        AssertNoClrAuthoringArtifacts(relation.Definition);

        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            scenario.Author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(new(
            plan,
            CreateEvidence(plan)));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, execution.Status);
        Assert.Empty(execution.Diagnostics);
        var row = Assert.Single(Assert.IsType<RelationQueryRelationResult>(execution.Relation).Rows);
        Assert.Equal(ObservationValue.FromString("load-1"), row.Identity);
        Assert.Equal(ObservationValue.FromString("load-1"), row.Value.GetProperty("id"));
        Assert.Equal(ObservationValue.FromString("ready-Ready"), row.Value.GetProperty("label"));
        Assert.Equal(ObservationValue.FromDouble(12.5), row.Value.GetProperty("adjustedAmount"));
        Assert.Equal(ObservationValue.FromBool(true), row.Value.GetProperty("priority"));

        var mapperCompilation = RelationDtoMapperCompiler.Default.Compile<LoadIndexDto>(plan);
        Assert.True(mapperCompilation.IsSuccessful, Format(mapperCompilation.Diagnostics));
        var mapper = Assert.IsType<CompiledRelationDtoMapper<LoadIndexDto>>(mapperCompilation.Mapper);
        var mapped = Assert.Single(mapper.Map(execution).Rows).Value;
        Assert.Equal("load-1", mapped.Id);
        Assert.Equal("ready-Ready", mapped.Label);
        Assert.Equal(12.5m, mapped.AdjustedAmount);
        Assert.True(mapped.Priority);
    }

    [Fact]
    public void CanonicalArtifacts_AreStableAcrossCultureAndClrRegistrationOrder()
    {
        var conventional = WithCulture(
            new CultureInfo("en-US"),
            () => AuthorRelation(registerOutputFirst: false));
        var reversed = WithCulture(
            new CultureInfo("tr-TR"),
            () => AuthorRelation(registerOutputFirst: true));
        var conventionalDocument = conventional.Relation.CreateDocument();
        var reversedDocument = reversed.Relation.CreateDocument();

        Assert.Equal(conventionalDocument.DefinitionFingerprint, reversedDocument.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(conventionalDocument, indented: false),
            RelationQueryJsonSerializer.Serialize(reversedDocument, indented: false));
        Assert.Equal(
            ProvenanceSnapshot(conventional.Relation.Provenance),
            ProvenanceSnapshot(reversed.Relation.Provenance));
        Assert.Equal(
            ShapeSnapshot(conventional.Author.ShapeDocuments),
            ShapeSnapshot(reversed.Author.ShapeDocuments));
    }

    [Fact]
    public void ConventionAuthoredJoinedRelation_IsStableAcrossCultureAndClrRegistrationOrder()
    {
        var conventional = WithCulture(
            new CultureInfo("en-US"),
            () => AuthorJoinedRelation(registerOutputFirst: false));
        var reversed = WithCulture(
            new CultureInfo("tr-TR"),
            () => AuthorJoinedRelation(registerOutputFirst: true));

        Assert.Equal(conventional.Relationship, reversed.Relationship);
        Assert.Equal(conventional.Catalog.CatalogFingerprint, reversed.Catalog.CatalogFingerprint);
        Assert.Equal(
            RelationshipCatalogJsonSerializer.Serialize(conventional.Catalog, indented: false),
            RelationshipCatalogJsonSerializer.Serialize(reversed.Catalog, indented: false));
        Assert.Equal(
            conventional.Relation.CreateDocument().DefinitionFingerprint,
            reversed.Relation.CreateDocument().DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(conventional.Relation.CreateDocument(), indented: false),
            RelationQueryJsonSerializer.Serialize(reversed.Relation.CreateDocument(), indented: false));
        Assert.Equal(
            ProvenanceSnapshot(conventional.Relation.Provenance),
            ProvenanceSnapshot(reversed.Relation.Provenance));
        Assert.Equal(
            ShapeSnapshot(conventional.Author.ShapeDocuments),
            ShapeSnapshot(reversed.Author.ShapeDocuments));
    }

    [Fact]
    public void ExpressionAuthoredJoinedRelation_CompilesTraversesExecutesAndMapsFlattenedDto()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<JoinedLoad>();
        var customers = author.Traverse<JoinedLoad, JoinedCustomer>(
            loads,
            load => load.CustomerId);
        var projected = author.Project(
            customers,
            (JoinedLoad load, JoinedCustomer customer) => new JoinedLoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type
            });
        var relation = projected.BuildRelation((JoinedLoadSearchDto document) => document.Id);

        var catalog = author.CreateRelationshipCatalogDocument();
        var loadCustomer = Assert.Single(catalog.Catalog.Relationships);

        Assert.Equal(RelationshipIdConvention.Create(loadCustomer), loadCustomer.Id);
        Assert.Equal(
            RelationQueryExpressionRelationConvention.CreateId(
                loads.Binding.Shape!.Value,
                projected.Binding.Shape!.Value),
            relation.Definition.Id);
        Assert.Equal(
            RelationQueryExpressionRelationConvention.CreateName(typeof(JoinedLoadSearchDto)),
            relation.Definition.Name);
        var relationIdentity = Assert.Single(
            relation.Provenance.Identities,
            static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Relation);
        Assert.Equal(RelationQueryAuthoringIdentityOrigin.Convention, relationIdentity.Origin);
        Assert.Equal(RelationQueryExpressionRelationConvention.Version, relationIdentity.Convention);
        Assert.Equal($"relation/{relation.Definition.Id.Value}", relationIdentity.Source?.Reference);
        Assert.All(
            relation.Provenance.Configuration,
            static decision =>
            {
                Assert.Equal(RelationQueryAuthoringValueOrigin.Convention, decision.Origin);
                Assert.Equal(RelationQueryExpressionRelationConvention.Version, decision.Convention);
            });
        Assert.Contains(
            relation.Provenance.Configuration,
            decision => decision.Target == relation.Definition.Id.Value
                        && decision.Setting == RelationQueryExpressionRelationConvention.NameSetting
                        && decision.Value == relation.Definition.Name.Value);
        Assert.Contains(
            relation.Provenance.Configuration,
            decision => decision.Target == relation.Definition.Id.Value
                        && decision.Setting == RelationQueryExpressionRelationConvention.SourceReferenceSetting
                        && decision.Value == $"relation/{relation.Definition.Id.Value}");
        Assert.Contains(
            relation.Provenance.Configuration,
            decision => decision.Target == relation.Definition.Id.Value
                        && decision.Setting == RelationQueryExpressionRelationConvention.RootBindingSetting
                        && decision.Value == loads.Binding.Id.Value);
        Assert.Contains(
            relation.Provenance.Sources,
            source => source.Kind == RelationQueryAuthoringDecisionKind.Node
                      && source.Target == loads.Node.Id.Value
                      && source.Source.Reference == $"source/{typeof(JoinedLoad).FullName}");
        Assert.Contains(
            relation.Provenance.Sources,
            source => source.Kind == RelationQueryAuthoringDecisionKind.Node
                      && source.Target == customers.Node.Id.Value
                      && source.Source.Reference == $"traverse/{loadCustomer.Id.Value}");
        Assert.Contains(
            relation.Provenance.Sources,
            source => source.Kind == RelationQueryAuthoringDecisionKind.Node
                      && source.Target == projected.Node.Id.Value
                      && source.Source.Reference == $"project/{typeof(JoinedLoadSearchDto).FullName}");
        Assert.Contains(
            relation.Provenance.Sources,
            source => source.Kind == RelationQueryAuthoringDecisionKind.Terminal
                      && source.Target == relation.Definition.Id.Value
                      && source.Source.Reference == $"relation/{relation.Definition.Id.Value}");

        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            author.ShapeDocuments,
            catalog));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var relationshipInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        Assert.Equal(loadCustomer.Id, relationshipInput.Relationship);
        Assert.Equal(loads.Binding.Id, relationshipInput.From);
        Assert.Equal(customers.Binding.Id, relationshipInput.Result);

        var execution = RelationQueryInMemoryInterpreter.Default.Execute(new(
            plan,
            CreateJoinedEvidence(plan)));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, execution.Status);
        Assert.Empty(execution.Diagnostics);
        var row = Assert.Single(Assert.IsType<RelationQueryRelationResult>(execution.Relation).Rows);
        Assert.Equal(ObservationValue.FromString("load-joined-1"), row.Identity);
        Assert.Equal(ObservationValue.FromString("load-joined-1"), row.Value.GetProperty("id"));
        Assert.Equal(ObservationValue.FromString("customer-joined-1"), row.Value.GetProperty("customerId"));
        Assert.Equal(ObservationValue.FromString("Acme"), row.Value.GetProperty("customerName"));
        Assert.Equal(ObservationValue.FromString("Preferred"), row.Value.GetProperty("customerType"));
        Assert.Equal(2, row.InputOccurrences.Length);

        var mapperCompilation = RelationDtoMapperCompiler.Default.Compile<JoinedLoadSearchDto>(plan);
        Assert.True(mapperCompilation.IsSuccessful, Format(mapperCompilation.Diagnostics));
        var mapper = Assert.IsType<CompiledRelationDtoMapper<JoinedLoadSearchDto>>(mapperCompilation.Mapper);
        var mapped = Assert.Single(mapper.Map(execution).Rows).Value;
        Assert.Equal("load-joined-1", mapped.Id);
        Assert.Equal("customer-joined-1", mapped.CustomerId);
        Assert.Equal("Acme", mapped.CustomerName);
        Assert.Equal("Preferred", mapped.CustomerType);
    }

    [Fact]
    public void Rows_RequiresSingleBindingProjectionAndFailedAttemptDoesNotConsumeResultIdentity()
    {
        var author = RelationQuery.Expression();
        var loadCustomer = author.Relationship<JoinedLoad, JoinedCustomer>(
            load => load.CustomerId,
            new RelationshipId("conformance/rows-load-customer"));
        var loads = author.Source<JoinedLoad>();
        var customers = author.Traverse(loads.Node, loads.Binding, loadCustomer);

        var exception = Assert.Throws<ArgumentException>(() =>
            author.Rows(customers.Node, customers.Binding, id: "rows"));
        Assert.Contains("Project", exception.Message, StringComparison.Ordinal);

        var projected = author.Project(
            customers.Node,
            (JoinedCustomer customer) => new JoinedCustomerRow { Name = customer.Name },
            customers.Binding);
        var query = author.BuildQuery(
            new QueryId("single-binding-rows"),
            new QueryName("SingleBindingRows"),
            author.Rows(projected, id: "rows"));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            RelationshipCatalogDocument.FromCatalog(new RelationshipCatalog([loadCustomer.Definition]))));

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
    }

    [Fact]
    public void RelationOutput_RequiresSingleBindingProjectionAndProjectedRetryCompiles()
    {
        var author = RelationQuery.Expression();
        var left = author.Source<JoinedLoad>(sourceReference: "self/left");
        var right = author.Source<JoinedLoad>(sourceReference: "self/right");
        var joined = author.Join(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (JoinedLoad l, JoinedLoad r) => l.Id == r.Id,
            left.Binding,
            right.Binding);

        var exception = Assert.Throws<ArgumentException>(() => author.BuildRelation(
            new RelationId("ambiguous-self-join"),
            new RelationName("AmbiguousSelfJoin"),
            left.Binding,
            joined,
            left.Binding));
        Assert.Contains("Project", exception.Message, StringComparison.Ordinal);

        var projected = author.Project(
            joined,
            (JoinedLoad load) => new JoinedLoadRow { Id = load.Id },
            left.Binding);
        var relation = author.BuildRelation(
            new RelationId("projected-self-join"),
            new RelationName("ProjectedSelfJoin"),
            left.Binding,
            projected.Node,
            projected.Binding,
            (JoinedLoadRow row) => row.Id);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            author.ShapeDocuments));

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
    }

    static AuthoredScenario AuthorRelation(bool registerOutputFirst)
    {
        var context = new RelationQueryClrAuthoringContext();
        if (registerOutputFirst)
        {
            _ = context.Shape<LoadIndexDto>();
            _ = context.Shape<Load>();
        }
        else
        {
            _ = context.Shape<Load>();
            _ = context.Shape<LoadIndexDto>();
        }

        var author = RelationQuery.Expression(context);
        var loads = author.Source<Load>(sourceReference: "conformance/source");
        var projected = author.Project(
            loads.Node,
            (Load load) => new LoadIndexDto
            {
                Id = load.Id,
                Label = load.Status == "Ready" ? "ready-" + load.Status : "other",
                AdjustedAmount = load.Amount + 2m,
                Priority = load.Tags.Contains("priority")
            },
            loads.Binding,
            sourceReference: "conformance/project");
        var relation = author.BuildRelation(
            new RelationId("load-index-conformance"),
            new RelationName("LoadIndexConformance"),
            loads.Binding,
            projected.Node,
            projected.Binding,
            (LoadIndexDto document) => document.Id,
            invariants:
            [
                new(
                    "positive-adjusted-amount",
                    document => !(document.AdjustedAmount <= 0m),
                    "Adjusted amount must be positive.",
                    sourceReference: "conformance/invariant")
            ],
            sourceReference: "conformance/relation");
        return new(author, relation);
    }

    static JoinedAuthoredScenario AuthorJoinedRelation(bool registerOutputFirst)
    {
        var context = new RelationQueryClrAuthoringContext();
        if (registerOutputFirst)
        {
            _ = context.Shape<JoinedLoadSearchDto>();
            _ = context.Shape<JoinedCustomer>();
            _ = context.Shape<JoinedLoad>();
        }
        else
        {
            _ = context.Shape<JoinedLoad>();
            _ = context.Shape<JoinedCustomer>();
            _ = context.Shape<JoinedLoadSearchDto>();
        }

        var author = RelationQuery.Expression(context);
        var loads = author.Source<JoinedLoad>();
        var customers = author.Traverse<JoinedLoad, JoinedCustomer>(loads, load => load.CustomerId);
        var projected = author.Project(
            customers,
            (JoinedLoad load, JoinedCustomer customer) => new JoinedLoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type
            });
        var relation = projected.BuildRelation((JoinedLoadSearchDto document) => document.Id);
        var catalog = author.CreateRelationshipCatalogDocument();
        return new(author, Assert.Single(catalog.Catalog.Relationships), catalog, relation);
    }

    sealed class JoinedCustomerRow
    {
        public string Name { get; init; } = string.Empty;
    }

    sealed class JoinedLoadRow
    {
        public string Id { get; init; } = string.Empty;
    }

    static RelationQueryRuntimeEvidence CreateEvidence(CompiledRelationQueryPlan plan)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        var occurrence = new RelationQueryObservationOccurrence(
            new("load/1"),
            source.Binding,
            source.Shape,
            observationIdentity: "source-load-1");
        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>(source.Fields.Length);
        foreach (var field in source.Fields)
        {
            fields.Add(new(
                field.Input.Id,
                occurrence.Id,
                RelationQueryFieldEvidenceState.Value,
                FieldValue(field.Input.Field.Path)));
        }

        return new(
            new RelationQueryEvaluationId("conformance/evaluation"),
            plan,
            sources:
            [
                new(
                    source.Input.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [occurrence])
            ],
            fields: fields.ToImmutable(),
            capabilities:
            [
                .. plan.RequirementGraph.Inputs
                    .OfType<RelationQueryCapabilityInput>()
                    .Select(static input => new RelationQueryCapabilityEvidence(
                        input.Id,
                        RelationQueryCapabilityEvidenceState.Available))
            ]);
    }

    static RelationQueryRuntimeEvidence CreateJoinedEvidence(CompiledRelationQueryPlan plan)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        var relationship = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        var load = new RelationQueryObservationOccurrence(
            new("joined/load/1"),
            source.Binding,
            source.Shape,
            observationIdentity: "load-joined-1");
        var customer = new RelationQueryObservationOccurrence(
            new("joined/customer/1"),
            relationship.Result,
            relationship.ResultShape,
            observationIdentity: "customer-joined-1");
        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            var owner = input.Binding == source.Binding
                ? load
                : input.Binding == relationship.Result
                    ? customer
                    : throw new InvalidOperationException(
                        $"Unexpected joined binding '{input.Binding.Value}'.");
            fields.Add(new(
                input.Id,
                owner.Id,
                RelationQueryFieldEvidenceState.Value,
                JoinedFieldValue(input.Binding, input.Field.Path, source.Binding, relationship.Result)));
        }

        return new(
            new RelationQueryEvaluationId("conformance/joined/evaluation"),
            plan,
            sources:
            [
                new(
                    source.Input.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [load])
            ],
            fields: fields.ToImmutable(),
            traversals:
            [
                new(
                    relationship.Id,
                    load.Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    [customer],
                    RelationQueryEvidenceCompleteness.Complete)
            ],
            capabilities:
            [
                .. plan.RequirementGraph.Inputs
                    .OfType<RelationQueryCapabilityInput>()
                    .Select(static input => new RelationQueryCapabilityEvidence(
                        input.Id,
                        RelationQueryCapabilityEvidenceState.Available))
            ]);
    }

    static ObservationValue JoinedFieldValue(
        ValueBindingId binding,
        FieldPath path,
        ValueBindingId load,
        ValueBindingId customer) => (binding == load, binding == customer, path.ToString()) switch
        {
            (true, false, "id") => ObservationValue.FromString("load-joined-1"),
            (true, false, "customerId") => ObservationValue.FromString("customer-joined-1"),
            (false, true, "name") => ObservationValue.FromString("Acme"),
            (false, true, "type") => ObservationValue.FromString("Preferred"),
            _ => throw new InvalidOperationException(
                $"Unexpected joined field input '{binding.Value}.{path}'.")
        };

    static ObservationValue FieldValue(FieldPath path) => path.ToString() switch
    {
        "id" => ObservationValue.FromString("load-1"),
        "status" => ObservationValue.FromString("Ready"),
        "amount" => ObservationValue.FromDouble(10.5),
        "tags" => ObservationValue.FromArray(
        [
            ObservationValue.FromString("priority"),
            ObservationValue.FromString("expedite")
        ]),
        _ => throw new InvalidOperationException($"Unexpected compiled source field '{path}'.")
    };

    static T WithCulture<T>(CultureInfo culture, Func<T> action)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    static string ShapeSnapshot(ImmutableArray<ShapeGraphDocument> documents) =>
        string.Join(
            "\n",
            documents.SelectMany(static document =>
                document.Graph.Shapes.Select(shape =>
                    $"{document.Graph.Id.Value}/{shape.Id.Value}:" + string.Join(
                        ",",
                        shape.Fields.Select(field => $"{field.Name.Value}={field.Type}")))));

    static string ProvenanceSnapshot(RelationQueryAuthoringManifest provenance) =>
        string.Join(
            "\n",
            provenance.Identities.Select(static decision =>
                    $"identity:{decision.Kind}:{decision.Value}:{decision.Origin}:{decision.Convention}")
                .Concat(provenance.Sources.Select(static decision =>
                    $"source:{decision.Kind}:{decision.Target}:{decision.Role}:" +
                    $"{decision.Source.Producer}:{decision.Source.Reference}"))
                .Concat(provenance.Configuration.Select(static decision =>
                    $"configuration:{decision.Target}:{decision.Setting}:{decision.Value}:" +
                    $"{decision.Origin}:{decision.Convention}:{decision.Source?.Producer}:" +
                    $"{decision.Source?.Reference}")));

    static IEnumerable<Expr> Descendants(Expr root)
    {
        yield return root;
        IEnumerable<Expr> children = root switch
        {
            UnaryExpr unary => [unary.Operand],
            BinaryExpr binary => [binary.Left, binary.Right],
            ConditionalExpr conditional => [conditional.Test, conditional.IfTrue, conditional.IfFalse],
            CallExpr call => call.Arguments,
            _ => []
        };
        foreach (var child in children)
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    static void AssertNoClrAuthoringArtifacts(object root)
    {
        Assert.DoesNotContain(
            EnumerateObjectGraph(root),
            static value => value is Expression or MemberInfo or Type or Delegate);
    }

    static IEnumerable<object> EnumerateObjectGraph(object root)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        Stack<object?> pending = new();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (current is null
                || current is string
                || current.GetType().IsPrimitive
                || current.GetType().IsEnum)
            {
                continue;
            }
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;
            if (current is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    pending.Push(item);
                }

                continue;
            }

            foreach (var property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetMethod is not null
                    && property.GetIndexParameters().Length == 0
                    && !property.PropertyType.IsByRefLike)
                {
                    pending.Push(property.GetValue(current));
                }
            }
        }
    }

    static string Format<T>(IEnumerable<T> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics);

    sealed record AuthoredScenario(
        RelationQueryExpressionAuthoring Author,
        RelationQueryAuthoringResult<Cohesive.Relations.IR.RelationDefinition> Relation);

    sealed record JoinedAuthoredScenario(
        RelationQueryExpressionAuthoring Author,
        RelationshipDefinition Relationship,
        RelationshipCatalogDocument Catalog,
        RelationQueryAuthoringResult<Cohesive.Relations.IR.RelationDefinition> Relation);

    public sealed class Load
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }

        [JsonPropertyName("tags")]
        public string[] Tags { get; init; } = [];
    }

    public sealed class LoadIndexDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; init; } = string.Empty;

        [JsonPropertyName("adjustedAmount")]
        public decimal AdjustedAmount { get; init; }

        [JsonPropertyName("priority")]
        public bool Priority { get; init; }
    }

    public sealed class JoinedLoad
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("customerId")]
        public string CustomerId { get; init; } = string.Empty;
    }

    public sealed class JoinedCustomer
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;
    }

    public sealed class JoinedLoadSearchDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("customerId")]
        public string CustomerId { get; init; } = string.Empty;

        [JsonPropertyName("customerName")]
        public string CustomerName { get; init; } = string.Empty;

        [JsonPropertyName("customerType")]
        public string CustomerType { get; init; } = string.Empty;
    }
}
