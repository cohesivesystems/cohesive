using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;

namespace Cohesive.Relations.TestFixtures;

/// <summary>Deterministic values and scalable physical inputs for the canonical federated Load fixture.</summary>
static class FederatedLoadConformanceData
{
    public const string CustomerType = "Priority";
    public const string EquipmentType = "Tractor";
    public const string LoadStatus = "Open";

    public static FederatedLoadSearchRow Expected(int ordinal) => new()
    {
        Id = LoadIdentity(ordinal),
        CustomerName = CustomerName(CustomerOrdinal(ordinal, distinctCount: 1)),
        EquipmentNumber = EquipmentNumber(EquipmentOrdinal(ordinal, distinctCount: 1))
    };

    public static RelationQueryRuntimeEvidence CreateReferenceEvidence(
        CompiledRelationQueryPlan plan,
        bool includeCustomer = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var source = plan.InputContract.Sources.Single();
        var customerTraversal = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadCustomerRelationshipId);
        var equipmentTraversal = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadEquipmentRelationshipId);
        var load = Occurrence("reference/load/1", source.Binding, source.Shape, LoadIdentity(1));
        var customer = Occurrence(
            "reference/customer/1",
            customerTraversal.Result,
            customerTraversal.ResultShape,
            CustomerIdentity(1));
        var equipment = Occurrence(
            "reference/equipment/1",
            equipmentTraversal.Result,
            equipmentTraversal.ResultShape,
            EquipmentIdentity(1));
        var fields = ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            RelationQueryObservationOccurrence owner = input.Binding == source.Binding
                ? load
                : input.Binding == customerTraversal.Result
                    ? customer
                    : input.Binding == equipmentTraversal.Result
                        ? equipment
                        : throw new InvalidOperationException(
                            $"Unexpected federated fixture binding '{input.Binding.Value}'.");
            if (!includeCustomer && owner == customer)
                continue;
            fields.Add(new(
                input.Id,
                owner.Id,
                RelationQueryFieldEvidenceState.Value,
                Value(input.Binding, input.Field.Path, source, customerTraversal, equipmentTraversal)));
        }

        return new(
            new(includeCustomer
                ? "conformance/federated/reference/complete"
                : "conformance/federated/reference/missing-customer"),
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
                    customerTraversal.Input.Id,
                    load.Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    includeCustomer ? [customer] : [],
                    RelationQueryEvidenceCompleteness.Complete,
                    evidenceReference: includeCustomer
                        ? "conformance/federated/customer-found"
                        : "conformance/federated/customer-not-found"),
                new(
                    equipmentTraversal.Input.Id,
                    load.Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    [equipment],
                    RelationQueryEvidenceCompleteness.Complete,
                    evidenceReference: "conformance/federated/equipment-found")
            ],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(plan));
    }

    public static PhysicalScenario CreatePhysicalScenario(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        int rootCount = 1,
        int distinctCustomerCount = 1,
        int distinctEquipmentCount = 1,
        bool includeFirstCustomer = true,
        bool recordRequests = true)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distinctCustomerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distinctEquipmentCount);

        var source = compilation.Plan.InputContract.Sources.Single();
        if (source.Role != RelationQuerySourceInputRole.RelationRoot)
        {
            throw new ArgumentException(
                "A supplied physical scenario requires a relation-root source rather than an enumerated query source.",
                nameof(compilation));
        }
        var sourcePlacement = compilation.Placement.Bindings.Single(binding => binding.Input == source.Input.Id);
        var sourceFields = source.Fields.ToDictionary(
            static field => field.Input.Field.Path,
            field =>
            {
                var placement = sourcePlacement.Fields.Single(candidate => candidate.Input == field.Input.Id);
                return new RelationQuerySourceReadField(
                    field.Input.Id,
                    field.Input.Field.Path,
                    placement.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput);
            });
        var loads = CreateLoadRows(rootCount, distinctCustomerCount, distinctEquipmentCount);
        var supplied = new RelationQuerySuppliedSourceInput(
            source.Input.Id,
            RelationQueryEvidenceCompleteness.Complete,
            [
                .. loads.Select(row => new RelationQuerySourceReadObservation(
                    row.Identity,
                    source.Shape,
                    [
                        .. sourceFields.Select(pair => row.Fields[pair.Key].ToResult(pair.Value))
                    ]))
            ],
            "conformance/federated/supplied-loads");
        var customerSource = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var equipmentSource = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        var customerRows = CreateCustomerRows(distinctCustomerCount);
        if (!includeFirstCustomer)
        {
            customerRows = [.. customerRows.Where(static row => row.Identity != CustomerIdentity(1))];
        }
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customerSource.Id, customerSource.ExecutionDomain, customerSource.TargetProfile),
            customerRows,
            recordRequests: recordRequests);
        var equipmentReader = new DeterministicRelationQuerySourceReader(
            new(equipmentSource.Id, equipmentSource.ExecutionDomain, equipmentSource.TargetProfile),
            CreateEquipmentRows(distinctEquipmentCount),
            recordRequests: recordRequests);
        return new(
            supplied,
            customerReader,
            equipmentReader,
            CreateExpectedRows(rootCount, distinctCustomerCount, distinctEquipmentCount));
    }

    public static EnumeratedPhysicalScenario CreateEnumeratedPhysicalScenario(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        int rootCount = 1,
        int distinctCustomerCount = 1,
        int distinctEquipmentCount = 1,
        Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult>? customerResultFactory = null,
        Action<RelationQuerySourceReadRequest>? afterLoadRead = null,
        bool recordRequests = true)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distinctCustomerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distinctEquipmentCount);
        if (compilation.Plan.InputContract.Sources.Any(static source =>
                source.Role == RelationQuerySourceInputRole.RelationRoot))
        {
            throw new ArgumentException(
                "An enumerated physical scenario requires a query source rather than a supplied relation root.",
                nameof(compilation));
        }
        var loadSource = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customerSource = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var equipmentSource = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        var loads = new DeterministicRelationQuerySourceReader(
            new(loadSource.Id, loadSource.ExecutionDomain, loadSource.TargetProfile),
            CreateLoadRows(rootCount, distinctCustomerCount, distinctEquipmentCount),
            afterRead: afterLoadRead,
            recordRequests: recordRequests);
        var customers = new DeterministicRelationQuerySourceReader(
            new(customerSource.Id, customerSource.ExecutionDomain, customerSource.TargetProfile),
            CreateCustomerRows(distinctCustomerCount),
            customerResultFactory,
            recordRequests: recordRequests);
        var equipment = new DeterministicRelationQuerySourceReader(
            new(equipmentSource.Id, equipmentSource.ExecutionDomain, equipmentSource.TargetProfile),
            CreateEquipmentRows(distinctEquipmentCount),
            recordRequests: recordRequests);
        return new(
            loads,
            customers,
            equipment,
            CreateExpectedRows(rootCount, distinctCustomerCount, distinctEquipmentCount));
    }

    public static ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> CreateLoadRows(
        int count,
        int distinctCustomerCount,
        int distinctEquipmentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distinctCustomerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(distinctEquipmentCount);
        return
        [
            .. Enumerable.Range(1, count).Select(ordinal =>
                DeterministicRelationQuerySourceReader.SourceRow.Create(
                    LoadIdentity(ordinal),
                    (FederatedLoadRelationFixture.LoadIdPath, ObservationValue.FromString(LoadIdentity(ordinal))),
                    (
                        FederatedLoadRelationFixture.LoadCustomerIdPath,
                        ObservationValue.FromString(CustomerIdentity(CustomerOrdinal(ordinal, distinctCustomerCount)))),
                    (
                        FederatedLoadRelationFixture.LoadEquipmentIdPath,
                        ObservationValue.FromString(EquipmentIdentity(EquipmentOrdinal(ordinal, distinctEquipmentCount)))),
                    (FederatedLoadRelationFixture.LoadStatusPath, ObservationValue.FromString(LoadStatus)),
                    (FederatedLoadRelationFixture.LoadAmountPath, ObservationValue.FromDecimal(ordinal))))
        ];
    }

    public static ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> CreateCustomerRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return
        [
            .. Enumerable.Range(1, count).Select(ordinal =>
                DeterministicRelationQuerySourceReader.SourceRow.Create(
                    CustomerIdentity(ordinal),
                    (
                        FederatedLoadRelationFixture.CustomerIdPath,
                        ObservationValue.FromString(CustomerIdentity(ordinal))),
                    (
                        FederatedLoadRelationFixture.CustomerNamePath,
                        ObservationValue.FromString(CustomerName(ordinal))),
                    (
                        FederatedLoadRelationFixture.CustomerTypePath,
                        ObservationValue.FromString(CustomerType))))
        ];
    }

    public static ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> CreateEquipmentRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return
        [
            .. Enumerable.Range(1, count).Select(ordinal =>
                DeterministicRelationQuerySourceReader.SourceRow.Create(
                    EquipmentIdentity(ordinal),
                    (
                        FederatedLoadRelationFixture.EquipmentIdPath,
                        ObservationValue.FromString(EquipmentIdentity(ordinal))),
                    (
                        FederatedLoadRelationFixture.EquipmentNumberPath,
                        ObservationValue.FromString(EquipmentNumber(ordinal))),
                    (
                        FederatedLoadRelationFixture.EquipmentTypePath,
                        ObservationValue.FromString(EquipmentType))))
        ];
    }

    static RelationQueryObservationOccurrence Occurrence(
        string id,
        ValueBindingId binding,
        QualifiedShapeId shape,
        string observationIdentity) => new(new(id), binding, shape, observationIdentity);

    static ImmutableArray<FederatedLoadSearchRow> CreateExpectedRows(
        int rootCount,
        int distinctCustomerCount,
        int distinctEquipmentCount) =>
    [
        .. Enumerable.Range(1, rootCount).Select(ordinal => new FederatedLoadSearchRow
        {
            Id = LoadIdentity(ordinal),
            CustomerName = CustomerName(CustomerOrdinal(ordinal, distinctCustomerCount)),
            EquipmentNumber = EquipmentNumber(EquipmentOrdinal(ordinal, distinctEquipmentCount))
        }).OrderBy(static row => row.Id, StringComparer.Ordinal)
    ];

    static ObservationValue Value(
        ValueBindingId binding,
        FieldPath path,
        RelationQuerySourceInputContract source,
        RelationQueryTraversalInputContract customer,
        RelationQueryTraversalInputContract equipment) =>
        (binding == source.Binding, binding == customer.Result, binding == equipment.Result, path.ToString()) switch
        {
            (true, false, false, FederatedLoadRelationFixture.LoadIdFieldName) =>
                ObservationValue.FromString(LoadIdentity(1)),
            (true, false, false, FederatedLoadRelationFixture.LoadCustomerIdFieldName) =>
                ObservationValue.FromString(CustomerIdentity(1)),
            (true, false, false, FederatedLoadRelationFixture.LoadEquipmentIdFieldName) =>
                ObservationValue.FromString(EquipmentIdentity(1)),
            (false, true, false, FederatedLoadRelationFixture.CustomerNameFieldName) =>
                ObservationValue.FromString(CustomerName(1)),
            (false, false, true, FederatedLoadRelationFixture.EquipmentNumberFieldName) =>
                ObservationValue.FromString(EquipmentNumber(1)),
            _ => throw new InvalidOperationException($"Unexpected federated fixture field '{binding.Value}.{path}'.")
        };

    static int CustomerOrdinal(int loadOrdinal, int distinctCount) => ((loadOrdinal - 1) % distinctCount) + 1;

    static int EquipmentOrdinal(int loadOrdinal, int distinctCount) => ((loadOrdinal - 1) % distinctCount) + 1;

    static string LoadIdentity(int ordinal) => $"load-{ordinal}";

    static string CustomerIdentity(int ordinal) => $"customer-{ordinal}";

    static string EquipmentIdentity(int ordinal) => $"equipment-{ordinal}";

    static string CustomerName(int ordinal) => $"Customer {ordinal}";

    static string EquipmentNumber(int ordinal) => $"TRUCK-{ordinal:D3}";

    internal sealed record PhysicalScenario(
        RelationQuerySuppliedSourceInput SuppliedLoads,
        DeterministicRelationQuerySourceReader Customers,
        DeterministicRelationQuerySourceReader Equipment,
        ImmutableArray<FederatedLoadSearchRow> Expected)
    {
        public ImmutableArray<IRelationQuerySourceReader> Readers => [Customers, Equipment];
    }

    internal sealed record EnumeratedPhysicalScenario(
        DeterministicRelationQuerySourceReader Loads,
        DeterministicRelationQuerySourceReader Customers,
        DeterministicRelationQuerySourceReader Equipment,
        ImmutableArray<FederatedLoadSearchRow> Expected)
    {
        public ImmutableArray<IRelationQuerySourceReader> Readers => [Loads, Customers, Equipment];
    }
}
