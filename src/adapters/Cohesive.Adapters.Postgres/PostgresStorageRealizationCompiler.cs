using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Stable PostgreSQL storage-realization compilation diagnostic codes.</summary>
public static class PostgresStorageRealizationDiagnosticCodes
{
    /// <summary>The binding does not identify the canonical root placement and aggregate key fields exactly.</summary>
    public const string RootBindingMismatch = "PGST100";

    /// <summary>A canonical owned collection has no exact decomposed component-table mapping.</summary>
    public const string OwnedCollectionBindingMismatch = "PGST101";

    /// <summary>The physical binding lacks a capability or guarantee required by the canonical structure.</summary>
    public const string GuaranteeUnavailable = "PGST102";
}

/// <summary>
/// Compiles canonical aggregate ownership into PostgreSQL decomposed-table realization evidence.
/// </summary>
public sealed class PostgresStorageRealizationCompiler
{
    const string CompilationStage = "postgres-storage-realization-compilation";
    const string AdapterIdentity = "cohesive.adapters.postgres";
    const string RootPageAcquisitionProfile =
        "cohesive.adapters.postgres/storage-realization/root-page-before-component-join/v1";

    /// <summary>Compiles one canonical storage structure against the official PostgreSQL binding authority.</summary>
    /// <param name="structure">Canonical aggregate structure to interpret.</param>
    /// <param name="rootPlacement">Exact placed root source represented by the PostgreSQL root table.</param>
    /// <param name="storageBinding">Adapter-owned physical root and component mapping authority.</param>
    /// <param name="realizationId">Stable identity for the produced target realization.</param>
    /// <param name="provenance">Producer and source attribution for the target interpretation.</param>
    /// <returns>An exact realization document or deterministic binding diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    public StorageRealizationCompilationResult Compile(
        StorageStructureDefinition structure,
        RelationQuerySourcePlacementBinding rootPlacement,
        PostgresRelationQueryStorageBinding storageBinding,
        StorageTargetRealizationId realizationId,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(rootPlacement);
        ArgumentNullException.ThrowIfNull(storageBinding);
        ArgumentNullException.ThrowIfNull(provenance);

        var structureValidation = StorageRealizationValidator.ValidateStructure(structure);
        if (!structureValidation.IsValid)
        {
            return StorageRealizationCompilationResult.Failure(structureValidation.Diagnostics);
        }

        List<DocumentValidationDiagnostic> diagnostics = [];
        var rootTable = storageBinding.Tables.SingleOrDefault(table =>
            table.PlacementBinding == rootPlacement.Id);
        if (storageBinding.Target != PostgresRelationQueryTargetProfile.Target
            || storageBinding.TargetProfile != PostgresRelationQueryTargetProfile.ProfileId
            || rootTable is null
            || rootPlacement.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
            || rootPlacement.Shape != structure.RootShape
            || rootTable.Shape != structure.RootShape
            || rootTable.Source != rootPlacement.Source
            || rootTable.Input != rootPlacement.Input)
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The PostgreSQL binding does not identify the exact canonical aggregate-root placement.",
                "/storageBinding/tables",
                structure.Id.Value,
                rootPlacement.Id.Value,
                rootTable?.PlacementBinding.Value ?? "missing"));
            return StorageRealizationCompilationResult.Failure([.. diagnostics]);
        }

        ValidateRootKeys(structure, rootPlacement, rootTable, diagnostics);
        var graph = structure.SemanticModel.Graph;
        var realizations = ImmutableArray.CreateBuilder<StorageOwnedCollectionRealization>(
            structure.OwnedCollections.Length);
        foreach (var canonical in structure.OwnedCollections)
        {
            var physical = storageBinding.OwnedCollections.SingleOrDefault(candidate =>
                candidate.Collection == canonical.Id);
            if (physical is null)
            {
                diagnostics.Add(Error(
                    PostgresStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                    $"Owned collection '{canonical.Id.Value}' has no PostgreSQL component-table mapping.",
                    "/storageBinding/ownedCollections",
                    canonical.Id.Value,
                    canonical.CollectionPath.ToString(),
                    "missing"));
                continue;
            }

            ValidateOwnedCollection(
                structure,
                rootPlacement,
                rootTable,
                canonical,
                physical,
                diagnostics);
            if (!graph.TryGetType(canonical.ComponentType, out var componentType)
                || componentType is not TypeDefinition.Structural component)
            {
                continue;
            }
            ValidateComponentFields(canonical, component, physical, diagnostics);
            realizations.Add(new StorageDecomposedOwnedCollectionRealization(
                collection: canonical.Id,
                bindingEvidenceReferences:
                [
                    BindingFingerprintEvidence(storageBinding),
                    ComponentBindingEvidence(storageBinding, physical)
                ],
                acquisitionEvidenceReference: RootPageAcquisitionProfile,
                atomicityEvidenceReference: physical.AtomicityEvidenceReference,
                changeCaptureEvidenceReference: physical.ChangeCaptureEvidenceReference));
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return StorageRealizationCompilationResult.Failure([.. diagnostics]);
        }

        var target = new StorageTargetRealization(
            id: realizationId,
            structureFingerprint: StorageRealizationFingerprinter.ComputeStructure(structure),
            target: new(
                adapter: AdapterIdentity,
                capabilityProfile: storageBinding.TargetProfile.Value),
            ownedCollections: realizations.MoveToImmutable(),
            provenance: provenance);
        return StorageRealizationCompilationResult.Success(
            StorageRealizationDocument.FromDefinitions(structure, target));
    }

    static void ValidateRootKeys(
        StorageStructureDefinition structure,
        RelationQuerySourcePlacementBinding rootPlacement,
        PostgresRelationQueryTableBinding rootTable,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var graph = structure.SemanticModel.Graph;
        graph.TryGetShape(structure.RootShape, out var root);
        var identityField = Resolve(root!, structure.RootIdentityPath);
        var partitionField = Resolve(root!, structure.PartitionPath);
        var identityMismatch = rootTable.Identity is not { } identity
            || rootPlacement.Identity?.SemanticPath != structure.RootIdentityPath
            || identity.SemanticPath != structure.RootIdentityPath
            || identityField is null
            || PostgresRelationQueryBindingSemanticValidator.GetValueSemanticsMismatch(
                ValueContract.FromField(identityField),
                identity.ScalarType,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited,
                identity.NumericDomain,
                identity.TemporalDomain) is not null;
        if (identityMismatch)
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The PostgreSQL root identity does not preserve the canonical identity path and scalar domain.",
                "/storageBinding/root/identity",
                structure.Id.Value,
                structure.RootIdentityPath.ToString(),
                rootTable.Identity?.SemanticPath.ToString() ?? "missing"));
        }

        var partitionMismatch = rootTable.Partition is not { } partition
            || rootPlacement.Partition is not { } placedPartition
            || partition.SemanticPath != structure.PartitionPath
            || !string.Equals(
                partition.SourceSelector,
                placedPartition.SourceSelector,
                StringComparison.Ordinal)
            || partitionField is null
            || PostgresRelationQueryBindingSemanticValidator.GetValueSemanticsMismatch(
                ValueContract.FromField(partitionField),
                partition.ScalarType,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited,
                partition.NumericDomain,
                partition.TemporalDomain) is not null;
        if (partitionMismatch)
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The PostgreSQL root partition does not preserve the canonical inherited partition path and selector.",
                "/storageBinding/root/partition",
                structure.Id.Value,
                structure.PartitionPath.ToString(),
                rootTable.Partition?.SemanticPath.ToString() ?? "missing"));
        }
    }

    static void ValidateOwnedCollection(
        StorageStructureDefinition structure,
        RelationQuerySourcePlacementBinding rootPlacement,
        PostgresRelationQueryTableBinding rootTable,
        StorageOwnedCollectionDefinition canonical,
        PostgresRelationQueryOwnedCollectionBinding physical,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var placedField = rootPlacement.Fields.SingleOrDefault(field =>
            field.Input == physical.CollectionInput);
        if (physical.RootPlacementBinding != rootPlacement.Id
            || physical.CollectionPath != canonical.CollectionPath
            || physical.ComponentType != canonical.ComponentType
            || physical.LocalIdentityPath != canonical.LocalIdentityPath
            || physical.OrdinalPath != canonical.OrdinalPath
            || placedField?.SemanticPath != canonical.CollectionPath)
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                $"PostgreSQL component mapping '{physical.Collection.Value}' does not preserve the canonical collection identity, path, or component type.",
                "/storageBinding/ownedCollections/" + Uri.EscapeDataString(canonical.Id.Value),
                canonical.Id.Value,
                canonical.CollectionPath.ToString(),
                physical.CollectionPath.ToString()));
        }

        var rootIdentity = rootTable.Identity!;
        var rootPartition = rootTable.Partition!;
        if (physical.ParentRoot.SemanticPath != structure.RootIdentityPath
            || physical.ParentRoot.ScalarType != rootIdentity.ScalarType
            || !Equals(physical.ParentRoot.TextSemantics, rootIdentity.TextSemantics)
            || physical.Partition.SemanticPath != structure.PartitionPath
            || physical.Partition.ScalarType != rootPartition.ScalarType
            || !Equals(physical.Partition.TextSemantics, rootPartition.TextSemantics)
            || !string.Equals(
                physical.Partition.SourceSelector,
                rootPartition.SourceSelector,
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.GuaranteeUnavailable,
                $"PostgreSQL component table '{physical.SchemaName}.{physical.TableName}' does not inherit the exact root identity and tenant partition domains.",
                "/storageBinding/ownedCollections/" + Uri.EscapeDataString(canonical.Id.Value) + "/keys",
                canonical.Id.Value,
                "exact-parent-and-partition-domains",
                "mismatch"));
        }
    }

    static void ValidateComponentFields(
        StorageOwnedCollectionDefinition canonical,
        TypeDefinition.Structural component,
        PostgresRelationQueryOwnedCollectionBinding physical,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (physical.Fields.Length != component.Fields.Length)
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                $"PostgreSQL component mapping '{physical.Collection.Value}' must cover every canonical component field exactly once.",
                "/storageBinding/ownedCollections/" + Uri.EscapeDataString(canonical.Id.Value) + "/fields",
                canonical.Id.Value,
                component.Fields.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                physical.Fields.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var field in component.Fields)
        {
            var path = FieldPath.FromField(field.Name.Value);
            var binding = physical.Fields.SingleOrDefault(candidate => candidate.SemanticPath == path);
            var contract = new ValueContract(
                type: field.Type,
                cardinality: field.Cardinality,
                presence: field.Presence,
                nullability: field.Nullability);
            if (binding is null
                || PostgresRelationQueryBindingSemanticValidator.GetValueSemanticsMismatch(
                    contract,
                    binding.ScalarType,
                    binding.MissingValueEncoding,
                    binding.NullValueEncoding,
                    binding.NumericDomain,
                    binding.TemporalDomain) is not null)
            {
                diagnostics.Add(Error(
                    PostgresStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                    $"PostgreSQL component field '{path}' does not preserve its canonical scalar contract.",
                    "/storageBinding/ownedCollections/" + Uri.EscapeDataString(canonical.Id.Value) + "/fields/" + Uri.EscapeDataString(path.ToString()),
                    canonical.Id.Value,
                    contract.ToString(),
                    binding?.ScalarType.ToString() ?? "missing"));
            }
        }

        var ordinal = physical.ResolveField(canonical.OrdinalPath);
        if (!ordinal.Ordering.HasFlag(PostgresRelationQueryOrderingCapability.Exact))
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.GuaranteeUnavailable,
                $"PostgreSQL component ordinal '{canonical.OrdinalPath}' lacks exact canonical ordering evidence.",
                "/storageBinding/ownedCollections/" + Uri.EscapeDataString(canonical.Id.Value) + "/ordinal",
                canonical.Id.Value,
                PostgresRelationQueryOrderingCapability.Exact.ToString(),
                ordinal.Ordering.ToString()));
        }

        var localIdentity = physical.ResolveField(canonical.LocalIdentityPath);
        if (localIdentity.ScalarType == PostgresRelationQueryScalarType.Text
            && localIdentity.TextSemantics?.Equality
                != PostgresRelationQueryTextEqualitySemantics.Ordinal)
        {
            diagnostics.Add(Error(
                PostgresStorageRealizationDiagnosticCodes.GuaranteeUnavailable,
                $"PostgreSQL component identity '{canonical.LocalIdentityPath}' lacks exact ordinal equality evidence.",
                "/storageBinding/ownedCollections/" + Uri.EscapeDataString(canonical.Id.Value) + "/localIdentity",
                canonical.Id.Value,
                PostgresRelationQueryTextEqualitySemantics.Ordinal.ToString(),
                localIdentity.TextSemantics?.Equality.ToString() ?? "missing"));
        }
    }

    static FieldDefinition? Resolve(Shape shape, FieldPath path)
    {
        if (path.Segments.Length != 1
            || !path.Segments[0].TryGetFieldIdentity(out var identity))
        {
            return null;
        }
        return shape.TryGetField(identity, out var field) ? field : null;
    }

    static string BindingFingerprintEvidence(PostgresRelationQueryStorageBinding binding) => string.Concat(
        "cohesive.adapters.postgres/storage-binding/",
        Uri.EscapeDataString(binding.Fingerprint.Algorithm),
        "/",
        Uri.EscapeDataString(binding.Fingerprint.Canonicalization),
        "/",
        binding.Fingerprint.Value);

    static string ComponentBindingEvidence(
        PostgresRelationQueryStorageBinding binding,
        PostgresRelationQueryOwnedCollectionBinding collection) => string.Concat(
        BindingFingerprintEvidence(binding),
        "/owned-collection/",
        Uri.EscapeDataString(collection.Collection.Value),
        "/table/",
        Uri.EscapeDataString(collection.SchemaName),
        "/",
        Uri.EscapeDataString(collection.TableName));

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string subject,
        string expected,
        string observed) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        location,
        Evidence: new(
            stage: CompilationStage,
            subject: subject,
            resolutionOptions: ["Correct the PostgreSQL binding or select a target that preserves the canonical aggregate semantics."],
            expected: expected,
            observed: observed));
}
