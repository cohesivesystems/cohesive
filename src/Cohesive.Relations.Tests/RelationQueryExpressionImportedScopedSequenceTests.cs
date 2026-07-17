using System.Reflection;
using System.Linq.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionImportedScopedSequenceTests
{
    [Fact]
    public void Expand_PrefersCollectionSourceMappingOverUnrelatedImportedItemRoot()
    {
        var customerType = ClrShapeIdentityConvention.GetTypeId(typeof(Customer));
        var customerGraphId = new GraphId("imported/customer-root-a/v1");
        var customerShapeId = new ShapeId("customer.a");
        var customerDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            customerGraphId,
            [
                new Shape(
                    customerShapeId,
                    [new FieldDefinition(new("display_name_a"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]));

        var loadGraphId = new GraphId("imported/load-nested-b/v1");
        var loadShapeId = new ShapeId("load.b");
        var loadDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            loadGraphId,
            [
                new Shape(
                    loadShapeId,
                    [
                        new FieldDefinition(
                            new("customers_b"),
                            new NamedTypeRef(customerType),
                            cardinality: FieldCardinality.Many)
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    customerType,
                    [new StructuralField(new("display_name_b"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]));
        var customerName = Property<Customer>(nameof(Customer.Name));
        var context = new RelationQueryClrAuthoringContext();
        _ = context.Shape<Customer>(
            customerDocument,
            new(customerGraphId, customerShapeId),
            new Dictionary<PropertyInfo, FieldPath>
            {
                [customerName] = FieldPath.FromField("display_name_a")
            });
        var importedLoad = context.Shape<ImportedLoad>(
            loadDocument,
            new(loadGraphId, loadShapeId),
            new Dictionary<PropertyInfo, FieldPath>
            {
                [Property<ImportedLoad>(nameof(ImportedLoad.Customers))] = FieldPath.FromField("customers_b"),
                [customerName] = FieldPath.FromField("display_name_b")
            });
        var author = RelationQuery.Expression(context);
        var loads = author.Source(importedLoad);

        var expanded = author.Expand(
            loads.Node,
            (ImportedLoad load) => load.Customers,
            loads.Binding);
        var projected = author.Project(
            expanded.Node,
            (Customer customer) => new CustomerProjection { Name = customer.Name },
            expanded.Binding);
        var query = author.BuildQuery(
            new("imported-expand-provenance"),
            new("ImportedExpandProvenance"),
            author.Rows(projected));

        var projection = Assert.Single(query.Definition.Body.Nodes.OfType<ProjectQueryNode>());
        var assignment = Assert.Single(projection.Assignments);
        var source = Assert.IsType<FieldExpr>(assignment.Value);
        Assert.Equal(FieldPath.FromField("display_name_b"), source.Path);
        Assert.DoesNotContain("display_name_a", source.Path.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Any_PreservesImportedMappingAcrossConditionalCollectionsFromOneBinding()
    {
        var graphId = new GraphId("imported/conditional-customers/v1");
        var shapeId = new ShapeId("conditional-load");
        var customerType = ClrShapeIdentityConvention.GetTypeId(typeof(Customer));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    shapeId,
                    [
                        new FieldDefinition(new("use_primary"), new ScalarTypeRef(ScalarTypeKind.Bool)),
                        new FieldDefinition(new("primary"), new NamedTypeRef(customerType), cardinality: FieldCardinality.Many),
                        new FieldDefinition(new("backup"), new NamedTypeRef(customerType), cardinality: FieldCardinality.Many)
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    customerType,
                    [new StructuralField(new("display_name"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]));
        var context = new RelationQueryClrAuthoringContext();
        var imported = context.Shape<ConditionalLoad>(
            document,
            new(graphId, shapeId),
            new Dictionary<PropertyInfo, FieldPath>
            {
                [Property<ConditionalLoad>(nameof(ConditionalLoad.UsePrimary))] = FieldPath.FromField("use_primary"),
                [Property<ConditionalLoad>(nameof(ConditionalLoad.PrimaryCustomers))] = FieldPath.FromField("primary"),
                [Property<ConditionalLoad>(nameof(ConditionalLoad.BackupCustomers))] = FieldPath.FromField("backup"),
                [Property<Customer>(nameof(Customer.Name))] = FieldPath.FromField("display_name")
            });
        var author = RelationQuery.Expression(context);
        var loads = author.Source(imported);
        var otherLoads = author.Source(imported, sourceReference: "conditional/other");

        var filtered = author.Filter(
            loads.Node,
            (ConditionalLoad load) =>
                (load.UsePrimary ? load.PrimaryCustomers : load.BackupCustomers)
                .Any(customer => customer.Name == "Acme"),
            loads.Binding);
        var joined = author.Join(
            loads.Node,
            otherLoads.Node,
            JoinKind.Inner,
            (ConditionalLoad left, ConditionalLoad right) => left.UsePrimary == right.UsePrimary,
            loads.Binding,
            otherLoads.Binding);
        Expression<Func<ConditionalLoad, ConditionalLoad, IEnumerable<Customer>>> ambiguous =
            (left, right) => left.UsePrimary ? left.PrimaryCustomers : right.PrimaryCustomers;
        var provenanceFailure = Assert.Throws<RelationQueryExpressionAuthoringException>(() =>
            author.Expand<JoinQueryNode, Customer>(
                joined,
                ambiguous,
                [loads.Binding, otherLoads.Binding],
                sourceReference: "conditional/retry"));
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.MemberPathUnavailable,
            Assert.Single(provenanceFailure.Diagnostics).Code);
        var expanded = author.Expand<JoinQueryNode, Customer>(
            joined,
            (Expression<Func<ConditionalLoad, IEnumerable<Customer>>>)
                (load => load.PrimaryCustomers),
            [loads.Binding],
            sourceReference: "conditional/retry");
        var projected = author.Project(
            expanded.Node,
            (Customer customer) => new CustomerProjection { Name = customer.Name },
            expanded.Binding);
        var query = author.BuildQuery(
            new("conditional-imported-any"),
            new("ConditionalImportedAny"),
            author.Rows(filtered, loads.Binding, id: "filtered"),
            author.Rows(projected, id: "expanded"));

        var filter = Assert.Single(query.Definition.Body.Nodes.OfType<FilterQueryNode>());
        var any = Assert.IsType<CallExpr>(filter.Predicate);
        var collection = Assert.IsType<ConditionalExpr>(any.Arguments[0]);
        Assert.Equal(FieldPath.FromField("primary"), Assert.IsType<FieldExpr>(collection.IfTrue).Path);
        Assert.Equal(FieldPath.FromField("backup"), Assert.IsType<FieldExpr>(collection.IfFalse).Path);
        var comparison = Assert.IsType<BinaryExpr>(any.Arguments[1]);
        Assert.Equal(
            FieldPath.Parse("item.display_name"),
            Assert.IsType<FieldExpr>(comparison.Left).Path);
    }

    [Fact]
    public void Any_PreservesImportedItemMappingWithoutContaminatingConventionalRoot()
    {
        var graphId = new GraphId("imported/scoped-customers/v1");
        var shapeId = new ShapeId("load.wire");
        var qualified = new QualifiedShapeId(graphId, shapeId);
        var customerType = ClrShapeIdentityConvention.GetTypeId(typeof(Customer));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    shapeId,
                    [
                        new FieldDefinition(
                            new("customers"),
                            new NamedTypeRef(customerType),
                            cardinality: FieldCardinality.Many)
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    customerType,
                    [new StructuralField(new("display_name"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]));
        var customers = Property<ImportedLoad>(nameof(ImportedLoad.Customers));
        var customerName = Property<Customer>(nameof(Customer.Name));
        var context = new RelationQueryClrAuthoringContext();
        var importedLoad = context.Shape<ImportedLoad>(
            document,
            qualified,
            new Dictionary<PropertyInfo, FieldPath>
            {
                [customers] = FieldPath.FromField("customers"),
                [customerName] = FieldPath.FromField("display_name")
            });
        var author = RelationQuery.Expression(context);
        var loads = author.Source(importedLoad);
        var conventionalCustomers = author.Source<Customer>();

        var importedFilter = author.Filter(
            loads.Node,
            (ImportedLoad load) => load.Customers.Any(customer => customer.Name == "Imported"),
            loads.Binding);
        var conventionalFilter = author.Filter(
            conventionalCustomers.Node,
            (Customer customer) => customer.Name == "Conventional",
            conventionalCustomers.Binding);
        var query = author.BuildQuery(
            new("imported-scoped-any"),
            new("ImportedScopedAny"),
            author.Rows(importedFilter, loads.Binding, id: "imported"),
            author.Rows(conventionalFilter, conventionalCustomers.Binding, id: "conventional"));

        Assert.True(
            query.Validation.IsValid,
            string.Join(Environment.NewLine, query.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var importedNode = Assert.Single(
            query.Definition.Body.Nodes.OfType<FilterQueryNode>(),
            node => node.Id == importedFilter.Id);
        var any = Assert.IsType<CallExpr>(importedNode.Predicate);
        Assert.Equal(ExprFunctionNames.Any, any.Function);
        var collection = Assert.IsType<FieldExpr>(any.Arguments[0]);
        Assert.Equal(loads.Binding.Id, collection.Binding);
        Assert.Equal(FieldPath.FromField("customers"), collection.Path);
        var itemComparison = Assert.IsType<BinaryExpr>(any.Arguments[1]);
        var itemName = Assert.IsType<FieldExpr>(itemComparison.Left);
        Assert.Null(itemName.Binding);
        Assert.Equal(FieldPath.Parse("item.display_name"), itemName.Path);

        var conventionalNode = Assert.Single(
            query.Definition.Body.Nodes.OfType<FilterQueryNode>(),
            node => node.Id == conventionalFilter.Id);
        var conventionalComparison = Assert.IsType<BinaryExpr>(conventionalNode.Predicate);
        var conventionalName = Assert.IsType<FieldExpr>(conventionalComparison.Left);
        Assert.Equal(conventionalCustomers.Binding.Id, conventionalName.Binding);
        Assert.Equal(FieldPath.FromField(nameof(Customer.Name)), conventionalName.Path);
    }

    static PropertyInfo Property<T>(string name) =>
        typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Property '{typeof(T)}.{name}' was not found.");

    sealed record ImportedLoad(IReadOnlyList<Customer> Customers);

    sealed record ConditionalLoad(
        bool UsePrimary,
        IReadOnlyList<Customer> PrimaryCustomers,
        IReadOnlyList<Customer> BackupCustomers);

    sealed record Customer(string Name);

    sealed class CustomerProjection
    {
        public string Name { get; init; } = string.Empty;
    }
}
