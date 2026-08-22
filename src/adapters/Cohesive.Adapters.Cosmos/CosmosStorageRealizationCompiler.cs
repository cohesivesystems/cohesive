using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Realization;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Stable Cosmos storage-realization compilation diagnostic codes.</summary>
public static class CosmosStorageRealizationDiagnosticCodes
{
    /// <summary>The binding does not identify the exact canonical root container and aggregate keys.</summary>
    public const string RootBindingMismatch = "CSST100";

    /// <summary>A canonical owned collection has no exact embedded document-path mapping.</summary>
    public const string OwnedCollectionBindingMismatch = "CSST101";

    /// <summary>The structured collection evidence lacks a required semantic guarantee.</summary>
    public const string GuaranteeUnavailable = "CSST102";
}

/// <summary>Compiles canonical aggregate ownership into Cosmos embedded-document realization evidence.</summary>
public sealed class CosmosStorageRealizationCompiler
{
    const string CompilationStage = "cosmos-storage-realization-compilation";
    const string AdapterIdentity = "cohesive.adapters.cosmos";
    const string SingleDocumentAtomicityProfile =
        "cohesive.adapters.cosmos/storage-realization/single-document-atomicity/v1";
    const string RootDocumentChangeProfile =
        "cohesive.adapters.cosmos/storage-realization/root-document-change-identity/v1";

    /// <summary>
    /// Structured-collection semantic profile asserting that JSON array order is the canonical component-ordinal order.
    /// </summary>
    public const string CanonicalOrderedOwnedCollectionProfile =
        "cohesive.adapters.cosmos/storage-realization/ordered-owned-json-array/v1";

    /// <summary>Compiles one canonical storage structure against the official Cosmos binding authority.</summary>
    /// <param name="structure">Canonical aggregate structure to interpret.</param>
    /// <param name="rootPlacement">Exact placed root source represented by the Cosmos container.</param>
    /// <param name="storageBinding">Adapter-owned document and structured-collection mapping authority.</param>
    /// <param name="realizationId">Stable identity for the produced target realization.</param>
    /// <param name="provenance">Producer and source attribution for the target interpretation.</param>
    /// <returns>An exact realization document or deterministic binding diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    public StorageRealizationCompilationResult Compile(
        StorageStructureDefinition structure,
        RelationQuerySourcePlacementBinding rootPlacement,
        CosmosRelationQueryStorageBinding storageBinding,
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
        if (storageBinding.Target != CosmosRelationQueryTargetProfile.Target
            || storageBinding.TargetProfile != CosmosRelationQueryTargetProfile.ProfileId
            || storageBinding.Source != rootPlacement.Source
            || storageBinding.PlacementBinding != rootPlacement.Id
            || rootPlacement.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
            || rootPlacement.Shape != structure.RootShape)
        {
            diagnostics.Add(Error(
                CosmosStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The Cosmos binding does not identify the exact canonical aggregate-root placement.",
                "/storageBinding/placementBinding",
                structure.Id.Value,
                rootPlacement.Id.Value,
                storageBinding.PlacementBinding.Value));
            return StorageRealizationCompilationResult.Failure([.. diagnostics]);
        }

        ValidateRootKeys(structure, rootPlacement, storageBinding, diagnostics);
        var graph = structure.SemanticModel.Graph;
        var realizations = ImmutableArray.CreateBuilder<StorageOwnedCollectionRealization>(
            structure.OwnedCollections.Length);
        foreach (var canonical in structure.OwnedCollections)
        {
            var placedField = rootPlacement.Fields.SingleOrDefault(field =>
                field.SemanticPath == canonical.CollectionPath);
            CosmosRelationQueryFieldBinding? physical = null;
            if (placedField is not null)
            {
                physical = storageBinding.Fields.SingleOrDefault(field =>
                    field.Input == placedField.Input);
            }
            if (placedField is null || physical?.CollectionScope is not { } scope)
            {
                diagnostics.Add(Error(
                    CosmosStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                    $"Owned collection '{canonical.Id.Value}' has no Cosmos structured-array binding.",
                    "/storageBinding/fields",
                    canonical.Id.Value,
                    canonical.CollectionPath.ToString(),
                    physical?.DocumentPath.ToString() ?? "missing"));
                continue;
            }

            if (CosmosRelationQueryCollectionScopeContracts.GetGap(scope) is { } gap)
            {
                diagnostics.Add(Error(
                    CosmosStorageRealizationDiagnosticCodes.GuaranteeUnavailable,
                    gap.Message,
                    "/storageBinding/fields/" + Uri.EscapeDataString(physical.Input.Value) + "/collectionScope",
                    canonical.Id.Value,
                    gap.Resolution,
                    scope.SemanticProfile));
            }
            if (!string.Equals(
                    scope.SemanticProfile,
                    CanonicalOrderedOwnedCollectionProfile,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    CosmosStorageRealizationDiagnosticCodes.GuaranteeUnavailable,
                    $"Cosmos owned collection '{canonical.Id.Value}' does not attest that JSON array order equals canonical ordinal order.",
                    "/storageBinding/fields/" + Uri.EscapeDataString(physical.Input.Value) + "/collectionScope/semanticProfile",
                    canonical.Id.Value,
                    CanonicalOrderedOwnedCollectionProfile,
                    scope.SemanticProfile));
            }

            if (graph.TryGetType(canonical.ComponentType, out var componentType)
                && componentType is TypeDefinition.Structural component)
            {
                ValidateComponentFields(canonical, component, scope, diagnostics);
            }
            realizations.Add(new StorageEmbeddedOwnedCollectionRealization(
                collection: canonical.Id,
                bindingEvidenceReferences:
                [
                    BindingFingerprintEvidence(storageBinding),
                    CollectionBindingEvidence(storageBinding, physical)
                ],
                acquisitionEvidenceReference: scope.SemanticProfile,
                atomicityEvidenceReference: SingleDocumentAtomicityProfile,
                changeCaptureEvidenceReference: RootDocumentChangeProfile));
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
        CosmosRelationQueryStorageBinding storageBinding,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var identityMismatch = rootPlacement.Identity is not { } identity
            || identity.SemanticPath != structure.RootIdentityPath
            || !string.Equals(
                identity.SourceSelector,
                storageBinding.IdentityPath.ToString(),
                StringComparison.Ordinal);
        if (identityMismatch)
        {
            diagnostics.Add(Error(
                CosmosStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The Cosmos document identity path does not preserve the canonical root identity selector.",
                "/storageBinding/identityPath",
                structure.Id.Value,
                structure.RootIdentityPath.ToString(),
                storageBinding.IdentityPath.ToString()));
        }

        var partitionMismatch = rootPlacement.Partition is not { } partition
            || storageBinding.PartitionPath is not { } partitionPath
            || !string.Equals(
                partition.SourceSelector,
                partitionPath.ToString(),
                StringComparison.Ordinal);
        if (partitionMismatch)
        {
            diagnostics.Add(Error(
                CosmosStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The Cosmos partition path does not preserve the canonical inherited tenant selector.",
                "/storageBinding/partitionPath",
                structure.Id.Value,
                structure.PartitionPath.ToString(),
                storageBinding.PartitionPath?.ToString() ?? "missing"));
        }

        var partitionField = rootPlacement.Fields.SingleOrDefault(field =>
            field.SemanticPath == structure.PartitionPath);
        if (partitionField is not null
            && storageBinding.Fields.SingleOrDefault(field => field.Input == partitionField.Input) is { } physical
            && storageBinding.PartitionPath != physical.DocumentPath)
        {
            diagnostics.Add(Error(
                CosmosStorageRealizationDiagnosticCodes.RootBindingMismatch,
                "The Cosmos semantic partition field maps to a different document path than the physical partition key.",
                "/storageBinding/partitionPath",
                structure.Id.Value,
                physical.DocumentPath.ToString(),
                storageBinding.PartitionPath?.ToString() ?? "missing"));
        }
    }

    static void ValidateComponentFields(
        StorageOwnedCollectionDefinition canonical,
        TypeDefinition.Structural component,
        CosmosRelationQueryCollectionScopeEvidence scope,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (scope.ChildFields.Length != component.Fields.Length)
        {
            diagnostics.Add(Error(
                CosmosStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                $"Cosmos structured collection '{canonical.Id.Value}' must map every canonical component field exactly once.",
                "/storageBinding/collectionScope/childFields",
                canonical.Id.Value,
                component.Fields.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                scope.ChildFields.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var field in component.Fields)
        {
            var path = FieldPath.FromField(field.Name.Value);
            var child = scope.ChildFields.SingleOrDefault(candidate => candidate.ElementPath == path);
            if (child is null
                || field.Cardinality != FieldCardinality.Single
                || field.Presence != FieldPresence.Required
                || field.Nullability != FieldNullability.NonNullable
                || child.MissingValueBehavior
                    != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion
                || child.NullValueBehavior
                    != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion
                || !CosmosRelationQueryCollectionScopeContracts.TryGetValueDomain(
                    field.Type,
                    out var expectedDomain)
                || child.ValueDomain != expectedDomain)
            {
                diagnostics.Add(Error(
                    CosmosStorageRealizationDiagnosticCodes.OwnedCollectionBindingMismatch,
                    $"Cosmos collection child '{path}' does not preserve its canonical required scalar domain.",
                    "/storageBinding/collectionScope/childFields/" + Uri.EscapeDataString(path.ToString()),
                    canonical.Id.Value,
                    field.Type.ToString() ?? "unknown",
                    child?.ValueDomain.ToString() ?? "missing"));
            }
        }

        var localIdentity = scope.ChildFields.SingleOrDefault(field =>
            field.ElementPath == canonical.LocalIdentityPath);
        if (localIdentity is null
            || !localIdentity.SemanticCapabilities.HasFlag(
                CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality))
        {
            diagnostics.Add(Error(
                CosmosStorageRealizationDiagnosticCodes.GuaranteeUnavailable,
                $"Cosmos component identity '{canonical.LocalIdentityPath}' lacks exact equality evidence.",
                "/storageBinding/collectionScope/localIdentity",
                canonical.Id.Value,
                CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality.ToString(),
                localIdentity?.SemanticCapabilities.ToString() ?? "missing"));
        }
    }

    static string BindingFingerprintEvidence(CosmosRelationQueryStorageBinding binding) => string.Concat(
        "cohesive.adapters.cosmos/storage-binding/",
        Uri.EscapeDataString(binding.Fingerprint.Algorithm),
        "/",
        Uri.EscapeDataString(binding.Fingerprint.Canonicalization),
        "/",
        binding.Fingerprint.Value);

    static string CollectionBindingEvidence(
        CosmosRelationQueryStorageBinding binding,
        CosmosRelationQueryFieldBinding collection) => string.Concat(
        BindingFingerprintEvidence(binding),
        "/field/",
        Uri.EscapeDataString(collection.Input.Value),
        "/path/",
        Uri.EscapeDataString(collection.DocumentPath.ToString()));

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
            resolutionOptions: ["Correct the Cosmos binding or select a target that preserves the canonical aggregate semantics."],
            expected: expected,
            observed: observed));
}
