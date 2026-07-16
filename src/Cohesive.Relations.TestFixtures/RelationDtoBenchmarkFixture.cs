using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.TestFixtures;

/// <summary>
/// Canonical single-source and joined Load DTO semantics shared by executable tests and performance benchmarks.
/// </summary>
public static class RelationDtoBenchmarkFixture
{
    /// <summary>Load identity field.</summary>
    public const string LoadIdFieldName = "Id";

    /// <summary>Load customer-reference field.</summary>
    public const string LoadCustomerIdFieldName = "CustomerId";

    /// <summary>Load equipment-reference field.</summary>
    public const string LoadEquipmentIdFieldName = "EquipmentId";

    /// <summary>Load status field.</summary>
    public const string LoadStatusFieldName = "Status";

    /// <summary>Load amount field.</summary>
    public const string LoadAmountFieldName = "Amount";

    /// <summary>Customer identity field.</summary>
    public const string CustomerIdFieldName = "Id";

    /// <summary>Customer name field.</summary>
    public const string CustomerNameFieldName = "Name";

    /// <summary>Customer type field.</summary>
    public const string CustomerTypeFieldName = "Type";

    /// <summary>Equipment identity field.</summary>
    public const string EquipmentIdFieldName = "Id";

    /// <summary>Equipment number field.</summary>
    public const string EquipmentNumberFieldName = "Number";

    /// <summary>Equipment type field.</summary>
    public const string EquipmentTypeFieldName = "Type";

    /// <summary>Flattened output customer-name field.</summary>
    public const string SearchCustomerNameFieldName = "CustomerName";

    /// <summary>Flattened output customer-type field.</summary>
    public const string SearchCustomerTypeFieldName = "CustomerType";

    /// <summary>Flattened output equipment-number field.</summary>
    public const string SearchEquipmentNumberFieldName = "EquipmentNumber";

    /// <summary>Flattened output equipment-type field.</summary>
    public const string SearchEquipmentTypeFieldName = "EquipmentType";

    static readonly GraphId DomainGraphId = new("relations-benchmark-domain/v1");
    static readonly GraphId DtoGraphId = new("relations-benchmark-dto/v1");

    static readonly EntityTypeName LoadEntityType = new("BenchmarkLoad");
    static readonly EntityTypeName CustomerEntityType = new("BenchmarkCustomer");
    static readonly EntityTypeName EquipmentEntityType = new("BenchmarkEquipment");

    static readonly QualifiedShapeId LoadShapeId = Qualified(DomainGraphId, "Load");
    static readonly QualifiedShapeId CustomerShapeId = Qualified(DomainGraphId, "Customer");
    static readonly QualifiedShapeId EquipmentShapeId = Qualified(DomainGraphId, "Equipment");
    static readonly QualifiedShapeId LoadSummaryShapeId = Qualified(DtoGraphId, nameof(LoadSummaryDto));
    static readonly QualifiedShapeId LoadSearchShapeId = Qualified(DtoGraphId, nameof(LoadSearchDto));

    static readonly FieldPath LoadIdPath = FieldPath.FromField(LoadIdFieldName);
    static readonly FieldPath LoadCustomerIdPath = FieldPath.FromField(LoadCustomerIdFieldName);
    static readonly FieldPath LoadEquipmentIdPath = FieldPath.FromField(LoadEquipmentIdFieldName);
    static readonly FieldPath LoadStatusPath = FieldPath.FromField(LoadStatusFieldName);
    static readonly FieldPath LoadAmountPath = FieldPath.FromField(LoadAmountFieldName);
    static readonly FieldPath CustomerNamePath = FieldPath.FromField(CustomerNameFieldName);
    static readonly FieldPath CustomerTypePath = FieldPath.FromField(CustomerTypeFieldName);
    static readonly FieldPath EquipmentNumberPath = FieldPath.FromField(EquipmentNumberFieldName);
    static readonly FieldPath EquipmentTypePath = FieldPath.FromField(EquipmentTypeFieldName);

    static readonly FieldPath SearchIdPath = FieldPath.FromField(LoadIdFieldName);
    static readonly FieldPath SearchCustomerIdPath = FieldPath.FromField(LoadCustomerIdFieldName);
    static readonly FieldPath SearchCustomerNamePath = FieldPath.FromField(SearchCustomerNameFieldName);
    static readonly FieldPath SearchCustomerTypePath = FieldPath.FromField(SearchCustomerTypeFieldName);
    static readonly FieldPath SearchEquipmentIdPath = FieldPath.FromField(LoadEquipmentIdFieldName);
    static readonly FieldPath SearchEquipmentNumberPath = FieldPath.FromField(SearchEquipmentNumberFieldName);
    static readonly FieldPath SearchEquipmentTypePath = FieldPath.FromField(SearchEquipmentTypeFieldName);
    static readonly FieldPath SearchStatusPath = FieldPath.FromField(LoadStatusFieldName);
    static readonly FieldPath SearchAmountPath = FieldPath.FromField(LoadAmountFieldName);

    static readonly ValueBindingId LoadBinding = new("load");
    static readonly ValueBindingId CustomerBinding = new("customer");
    static readonly ValueBindingId EquipmentBinding = new("equipment");
    static readonly ValueBindingId SummaryBinding = new("summary");
    static readonly ValueBindingId SearchBinding = new("search");

    static readonly QueryNodeId LoadSourceNodeId = new("loads");
    static readonly QueryNodeId CustomerTraversalNodeId = new("load-customer");
    static readonly QueryNodeId EquipmentTraversalNodeId = new("load-equipment");
    static readonly QueryNodeId SimpleProjectionNodeId = new("project-load-summary");
    static readonly QueryNodeId JoinedProjectionNodeId = new("project-load-search");

    static readonly RelationshipId LoadCustomerRelationshipId = new("Load.Customer");
    static readonly RelationshipId LoadEquipmentRelationshipId = new("Load.Equipment");

    static readonly ShapeGraphDocument DomainShapeGraphDocument = CreateDomainShapeGraphDocument();
    static readonly ShapeGraphDocument DtoShapeGraphDocument = CreateDtoShapeGraphDocument();
    static readonly ImmutableArray<ShapeGraphDocument> ShapeGraphDocuments =
        [DomainShapeGraphDocument, DtoShapeGraphDocument];
    static readonly RelationshipCatalogDocument RelationshipCatalogDocument =
        Cohesive.Relations.Serialization.RelationshipCatalogDocument.FromCatalog(
            new RelationshipCatalog(
            [
                new(
                    LoadCustomerRelationshipId,
                    LoadShapeId,
                    LoadCustomerIdPath,
                    CustomerShapeId,
                    ObservationIdentityRelationshipTargetKey.Instance),
                new(
                    LoadEquipmentRelationshipId,
                    LoadShapeId,
                    LoadEquipmentIdPath,
                    EquipmentShapeId,
                    ObservationIdentityRelationshipTargetKey.Instance)
            ]));

    static readonly RelationQueryDocument SimpleDocument = CreateSimpleDocument();
    static readonly RelationQueryDocument JoinedDocument = CreateJoinedDocument();

    /// <summary>Canonical compiled single-source Load-to-summary relation plan.</summary>
    public static CompiledRelationQueryPlan SimplePlan { get; } = Compile(SimpleDocument);

    /// <summary>Canonical compiled joined Load, Customer, and Equipment relation plan.</summary>
    public static CompiledRelationQueryPlan JoinedPlan { get; } = Compile(JoinedDocument);

    /// <summary>Creates deterministic successful inputs for the single-source DTO relation.</summary>
    /// <param name="rowCount">Number of root Load rows to create.</param>
    /// <returns>A reusable canonical execution and equivalent observation inputs.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount"/> is not positive.</exception>
    public static RelationDtoFixtureScenario<LoadSummaryDto> CreateSimpleScenario(int rowCount)
    {
        var rows = CreateRows(rowCount);
        var evidence = CreateEvidence(SimplePlan, rows, RelationDtoFixtureVariant.Complete);
        var execution = Execute(SimplePlan, evidence);
        return new(
            SimplePlan,
            evidence,
            execution,
            ToObservations(execution),
            [.. rows.Select(static row => new LoadSummaryDto(row.LoadId, row.Status, row.Amount))]);
    }

    /// <summary>Creates deterministic inputs for the joined DTO relation.</summary>
    /// <param name="rowCount">Number of root Load rows to create.</param>
    /// <param name="variant">Successful or diagnostic data variation.</param>
    /// <returns>A reusable canonical execution and equivalent observation inputs.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rowCount"/> is not positive or <paramref name="variant"/> is unsupported.
    /// </exception>
    public static RelationDtoFixtureScenario<LoadSearchDto> CreateJoinedScenario(
        int rowCount,
        RelationDtoFixtureVariant variant = RelationDtoFixtureVariant.Complete)
    {
        if (!Enum.IsDefined(variant))
            throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unsupported DTO fixture variant.");

        var rows = CreateRows(rowCount);
        var evidence = CreateEvidence(JoinedPlan, rows, variant);
        var execution = Execute(JoinedPlan, evidence);
        return new(
            JoinedPlan,
            evidence,
            execution,
            ToObservations(execution),
            [.. rows.Select(static row => new LoadSearchDto(
                row.LoadId,
                row.CustomerId,
                row.CustomerName,
                row.CustomerType,
                row.EquipmentId,
                row.EquipmentNumber,
                row.EquipmentType,
                row.Status,
                row.Amount))]);
    }

    static ImmutableArray<FixtureRow> CreateRows(int rowCount)
    {
        if (rowCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "A scenario requires at least one row.");

        var rows = ImmutableArray.CreateBuilder<FixtureRow>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(new(
                Key: i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LoadId: $"load-{i:D6}",
                CustomerId: $"customer-{i % 97:D4}",
                CustomerName: $"Customer {i % 97:D4}",
                CustomerType: i % 2 == 0 ? "Preferred" : "Standard",
                EquipmentId: $"equipment-{i % 193:D4}",
                EquipmentNumber: $"TR-{i % 193:D5}",
                EquipmentType: i % 3 == 0 ? "Reefer" : "DryVan",
                Status: i % 4 == 0 ? "InTransit" : "Available",
                Amount: 1000m + i * 1.25m));
        }

        return rows.MoveToImmutable();
    }

    static RelationQueryRuntimeEvidence CreateEvidence(
        CompiledRelationQueryPlan plan,
        ImmutableArray<FixtureRow> rows,
        RelationDtoFixtureVariant variant)
    {
        Dictionary<string, RelationQueryObservationOccurrence> loads = new(StringComparer.Ordinal);
        Dictionary<string, RelationQueryObservationOccurrence> customers = new(StringComparer.Ordinal);
        Dictionary<string, RelationQueryObservationOccurrence> equipment = new(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            loads.Add(row.Key, Occurrence("load", row.Key, LoadBinding, LoadShapeId, row.LoadId));
            if (variant != RelationDtoFixtureVariant.MissingCustomer)
            {
                customers.Add(
                    row.Key,
                    Occurrence("customer", row.Key, CustomerBinding, CustomerShapeId, row.CustomerId));
            }
            equipment.Add(
                row.Key,
                Occurrence("equipment", row.Key, EquipmentBinding, EquipmentShapeId, row.EquipmentId));
        }

        var source = plan.RequirementGraph.Inputs
            .OfType<RelationQuerySourceSetInput>()
            .Single(input => input.Binding == LoadBinding);
        ImmutableArray<RelationQuerySourceEvidence> sources =
        [
            new(
                source.Id,
                RelationQuerySourceEvidenceState.Provided,
                [.. loads.Values])
        ];

        var fields = ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            foreach (var row in rows)
            {
                if (input.Binding == LoadBinding)
                {
                    fields.Add(ValueField(input, loads[row.Key], LoadValue(row, input.Field.Path)));
                    continue;
                }

                if (input.Binding == CustomerBinding)
                {
                    if (variant == RelationDtoFixtureVariant.MissingCustomer)
                        continue;

                    var value = input.Field.Path == CustomerNamePath
                        && variant == RelationDtoFixtureVariant.InvalidCustomerName
                        ? ObservationValue.FromInt64(42)
                        : CustomerValue(row, input.Field.Path);
                    fields.Add(ValueField(input, customers[row.Key], value));
                    continue;
                }

                if (input.Binding == EquipmentBinding)
                {
                    fields.Add(ValueField(input, equipment[row.Key], EquipmentValue(row, input.Field.Path)));
                    continue;
                }

                throw new InvalidOperationException($"Unsupported fixture binding '{input.Binding.Value}'.");
            }
        }

        var traversals = ImmutableArray.CreateBuilder<RelationQueryTraversalEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>())
        {
            foreach (var row in rows)
            {
                ImmutableArray<RelationQueryObservationOccurrence> results = input.Relationship == LoadCustomerRelationshipId
                    ? variant == RelationDtoFixtureVariant.MissingCustomer
                        ? []
                        : [customers[row.Key]]
                    : input.Relationship == LoadEquipmentRelationshipId
                        ? [equipment[row.Key]]
                        : throw new InvalidOperationException(
                            $"Unsupported fixture relationship '{input.Relationship.Value}'.");
                traversals.Add(new(
                    input.Id,
                    loads[row.Key].Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    results,
                    RelationQueryEvidenceCompleteness.Complete));
            }
        }

        ImmutableArray<RelationQueryCapabilityEvidence> capabilities =
        [
            .. plan.RequirementGraph.Inputs
                .OfType<RelationQueryCapabilityInput>()
                .Select(static input => new RelationQueryCapabilityEvidence(
                    input.Id,
                    RelationQueryCapabilityEvidenceState.Available,
                    "test-fixtures/in-memory"))
        ];

        var definitionId = plan.Definition is IRRelationDefinition relation
            ? relation.Id.Value
            : "query";
        return new(
            new($"test-fixtures/{definitionId}/{variant}/{rows.Length}"),
            plan,
            sources: sources,
            fields: fields.ToImmutable(),
            traversals: traversals.ToImmutable(),
            capabilities: capabilities);
    }

    static RelationQueryFieldEvidence ValueField(
        RelationQueryFieldInput input,
        RelationQueryObservationOccurrence owner,
        ObservationValue value) =>
        new(input.Id, owner.Id, RelationQueryFieldEvidenceState.Value, value);

    static ObservationValue LoadValue(FixtureRow row, FieldPath path)
    {
        if (path == LoadIdPath)
            return ObservationValue.FromString(row.LoadId);
        if (path == LoadCustomerIdPath)
            return ObservationValue.FromString(row.CustomerId);
        if (path == LoadEquipmentIdPath)
            return ObservationValue.FromString(row.EquipmentId);
        if (path == LoadStatusPath)
            return ObservationValue.FromString(row.Status);
        if (path == LoadAmountPath)
            return ObservationValue.FromDecimal(row.Amount);
        throw new InvalidOperationException($"Unsupported Load fixture field '{path}'.");
    }

    static ObservationValue CustomerValue(FixtureRow row, FieldPath path)
    {
        if (path == CustomerNamePath)
            return ObservationValue.FromString(row.CustomerName);
        if (path == CustomerTypePath)
            return ObservationValue.FromString(row.CustomerType);
        throw new InvalidOperationException($"Unsupported Customer fixture field '{path}'.");
    }

    static ObservationValue EquipmentValue(FixtureRow row, FieldPath path)
    {
        if (path == EquipmentNumberPath)
            return ObservationValue.FromString(row.EquipmentNumber);
        if (path == EquipmentTypePath)
            return ObservationValue.FromString(row.EquipmentType);
        throw new InvalidOperationException($"Unsupported Equipment fixture field '{path}'.");
    }

    static RelationQueryObservationOccurrence Occurrence(
        string kind,
        string key,
        ValueBindingId binding,
        QualifiedShapeId shape,
        string identity) =>
        new(new($"{kind}/{key}"), binding, shape, identity);

    static RelationQueryExecutionResult Execute(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence) =>
        RelationQueryInMemoryInterpreter.Default.Execute(new(plan, evidence));

    static ImmutableArray<Observation> ToObservations(RelationQueryExecutionResult execution)
    {
        var relation = execution.Relation;
        if (relation is null)
            return [];
        var observations = ImmutableArray.CreateBuilder<Observation>(relation.Rows.Length);
        foreach (var row in relation.Rows)
        {
            observations.Add(new(
                row.Shape.ShapeId,
                row.Identity?.GetRequiredString()
                    ?? row.Root?.ObservationIdentity
                    ?? row.Root?.Id.Value
                    ?? throw new InvalidOperationException("A fixture output row requires an identity."),
                row.Value.Fields
                    ?? throw new InvalidOperationException("A fixture output row requires an object value.")));
        }
        return observations.MoveToImmutable();
    }

    static CompiledRelationQueryPlan Compile(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            ShapeGraphDocuments,
            RelationshipCatalogDocument));
        if (!result.IsSuccessful || result.Plan is null)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        }
        return result.Plan;
    }

    static RelationQueryDocument CreateSimpleDocument()
    {
        IRRelationDefinition definition = new(
            new("benchmark-load-summary"),
            new("BenchmarkLoadSummary"),
            new(
            [
                new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
                new ProjectQueryNode(
                    SimpleProjectionNodeId,
                    LoadSourceNodeId,
                    SummaryBinding,
                    LoadSummaryShapeId,
                    [
                        Assignment("summary-id", SearchIdPath, LoadBinding, LoadIdPath),
                        Assignment("summary-status", SearchStatusPath, LoadBinding, LoadStatusPath),
                        Assignment("summary-amount", SearchAmountPath, LoadBinding, LoadAmountPath)
                    ])
            ]),
            LoadBinding,
            new(
                SimpleProjectionNodeId,
                LoadSummaryShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(SummaryBinding, SearchIdPath)));
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateJoinedDocument()
    {
        IRRelationDefinition definition = new(
            new("benchmark-load-search"),
            new("BenchmarkLoadSearch"),
            new(
            [
                new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
                new TraverseRelationshipQueryNode(
                    CustomerTraversalNodeId,
                    LoadSourceNodeId,
                    LoadBinding,
                    LoadCustomerRelationshipId,
                    RelationshipTraversalDirection.Forward,
                    CustomerBinding,
                    JoinKind.Left,
                    QueryInputRequirement.Required),
                new TraverseRelationshipQueryNode(
                    EquipmentTraversalNodeId,
                    CustomerTraversalNodeId,
                    LoadBinding,
                    LoadEquipmentRelationshipId,
                    RelationshipTraversalDirection.Forward,
                    EquipmentBinding,
                    JoinKind.Left,
                    QueryInputRequirement.Required),
                new ProjectQueryNode(
                    JoinedProjectionNodeId,
                    EquipmentTraversalNodeId,
                    SearchBinding,
                    LoadSearchShapeId,
                    [
                        Assignment("search-id", SearchIdPath, LoadBinding, LoadIdPath),
                        Assignment("search-customer-id", SearchCustomerIdPath, LoadBinding, LoadCustomerIdPath),
                        Assignment("search-customer-name", SearchCustomerNamePath, CustomerBinding, CustomerNamePath),
                        Assignment("search-customer-type", SearchCustomerTypePath, CustomerBinding, CustomerTypePath),
                        Assignment("search-equipment-id", SearchEquipmentIdPath, LoadBinding, LoadEquipmentIdPath),
                        Assignment("search-equipment-number", SearchEquipmentNumberPath, EquipmentBinding, EquipmentNumberPath),
                        Assignment("search-equipment-type", SearchEquipmentTypePath, EquipmentBinding, EquipmentTypePath),
                        Assignment("search-status", SearchStatusPath, LoadBinding, LoadStatusPath),
                        Assignment("search-amount", SearchAmountPath, LoadBinding, LoadAmountPath)
                    ])
            ]),
            LoadBinding,
            new(
                JoinedProjectionNodeId,
                LoadSearchShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(SearchBinding, SearchIdPath)));
        return RelationQueryDocument.FromDefinition(definition);
    }

    static ProjectionAssignment Assignment(
        string id,
        FieldPath target,
        ValueBindingId source,
        FieldPath path) =>
        new(new(id), target, Expr.Field(source, path));

    static ShapeGraphDocument CreateDomainShapeGraphDocument()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var load = new Shape(
            LoadShapeId.ShapeId,
            [
                new FieldDefinition(new(LoadIdFieldName), stringType, role: FieldRole.Identity),
                new FieldDefinition(
                    new(LoadCustomerIdFieldName),
                    new EntityReferenceTypeRef(CustomerEntityType),
                    role: FieldRole.Reference),
                new FieldDefinition(
                    new(LoadEquipmentIdFieldName),
                    new EntityReferenceTypeRef(EquipmentEntityType),
                    role: FieldRole.Reference),
                new FieldDefinition(new(LoadStatusFieldName), stringType),
                new FieldDefinition(new(LoadAmountFieldName), new ScalarTypeRef(ScalarTypeKind.Decimal))
            ],
            role: ShapeRoles.Entity).WithEntityType(LoadEntityType);
        var customer = new Shape(
            CustomerShapeId.ShapeId,
            [
                new FieldDefinition(new(CustomerIdFieldName), stringType, role: FieldRole.Identity),
                new FieldDefinition(new(CustomerNameFieldName), stringType),
                new FieldDefinition(new(CustomerTypeFieldName), stringType)
            ],
            role: ShapeRoles.Entity).WithEntityType(CustomerEntityType);
        var equipment = new Shape(
            EquipmentShapeId.ShapeId,
            [
                new FieldDefinition(new(EquipmentIdFieldName), stringType, role: FieldRole.Identity),
                new FieldDefinition(new(EquipmentNumberFieldName), stringType),
                new FieldDefinition(new(EquipmentTypeFieldName), stringType)
            ],
            role: ShapeRoles.Entity).WithEntityType(EquipmentEntityType);
        return ShapeGraphDocument.FromGraph(new(DomainGraphId, [load, customer, equipment]));
    }

    static ShapeGraphDocument CreateDtoShapeGraphDocument()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var customerReferenceType = new EntityReferenceTypeRef(CustomerEntityType);
        var equipmentReferenceType = new EntityReferenceTypeRef(EquipmentEntityType);
        var summary = new Shape(
            LoadSummaryShapeId.ShapeId,
            [
                new FieldDefinition(new(LoadIdFieldName), stringType, role: FieldRole.Identity),
                new FieldDefinition(new(LoadStatusFieldName), stringType),
                new FieldDefinition(new(LoadAmountFieldName), new ScalarTypeRef(ScalarTypeKind.Decimal))
            ],
            role: ShapeRoles.Dto);
        var search = new Shape(
            LoadSearchShapeId.ShapeId,
            [
                new FieldDefinition(new(LoadIdFieldName), stringType, role: FieldRole.Identity),
                new FieldDefinition(new(LoadCustomerIdFieldName), customerReferenceType),
                new FieldDefinition(new(SearchCustomerNameFieldName), stringType),
                new FieldDefinition(new(SearchCustomerTypeFieldName), stringType),
                new FieldDefinition(new(LoadEquipmentIdFieldName), equipmentReferenceType),
                new FieldDefinition(new(SearchEquipmentNumberFieldName), stringType),
                new FieldDefinition(new(SearchEquipmentTypeFieldName), stringType),
                new FieldDefinition(new(LoadStatusFieldName), stringType),
                new FieldDefinition(new(LoadAmountFieldName), new ScalarTypeRef(ScalarTypeKind.Decimal))
            ],
            role: ShapeRoles.Dto);
        return ShapeGraphDocument.FromGraph(new(DtoGraphId, [summary, search]));
    }

    static QualifiedShapeId Qualified(GraphId graph, string shape) => new(graph, new(shape));

    sealed record FixtureRow(
        string Key,
        string LoadId,
        string CustomerId,
        string CustomerName,
        string CustomerType,
        string EquipmentId,
        string EquipmentNumber,
        string EquipmentType,
        string Status,
        decimal Amount);
}
