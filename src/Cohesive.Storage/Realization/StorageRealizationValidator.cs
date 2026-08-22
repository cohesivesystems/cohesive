using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Realization;

/// <summary>Stable machine-readable Storage Realization validation diagnostic codes.</summary>
public static class StorageRealizationDiagnosticCodes
{
    /// <summary>The retained shape graph or root shape is absent or invalid.</summary>
    public const string SemanticModelInvalid = "storage.realization.semanticModel.invalid";

    /// <summary>A root identity or partition path is absent or not a required scalar.</summary>
    public const string RootFieldInvalid = "storage.realization.rootField.invalid";

    /// <summary>An owned collection does not resolve to the declared required named structural sequence.</summary>
    public const string OwnedCollectionInvalid = "storage.realization.ownedCollection.invalid";

    /// <summary>A component-local identity is absent or not a required scalar.</summary>
    public const string ComponentIdentityInvalid = "storage.realization.componentIdentity.invalid";

    /// <summary>A component ordinal is absent or not a required integral scalar.</summary>
    public const string ComponentOrdinalInvalid = "storage.realization.componentOrdinal.invalid";

    /// <summary>The target realization references a different semantic structure fingerprint.</summary>
    public const string StructureLinkMismatch = "storage.realization.structureLink.mismatch";

    /// <summary>A canonical owned collection has no target realization.</summary>
    public const string CollectionRealizationMissing = "storage.realization.collection.missing";

    /// <summary>A target realization refers to an unknown canonical owned collection.</summary>
    public const string CollectionRealizationUnknown = "storage.realization.collection.unknown";

    /// <summary>A persisted structure or target fingerprint does not match canonical content.</summary>
    public const string FingerprintMismatch = "storage.realization.fingerprint.mismatch";

    /// <summary>The persisted document schema version is unsupported.</summary>
    public const string SchemaVersionUnsupported = "storage.realization.schemaVersion.unsupported";
}

/// <summary>Validates canonical aggregate ownership and one linked target-specific storage realization.</summary>
public static class StorageRealizationValidator
{
    const string ValidationStage = "storage-realization-validation";

    /// <summary>Validates one complete persisted Storage Realization document.</summary>
    /// <param name="document">Document to validate.</param>
    /// <returns>Deterministically ordered semantic, linkage, schema, and fingerprint diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(StorageRealizationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (!string.Equals(
                document.SchemaVersion,
                StorageRealizationDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                StorageRealizationDiagnosticCodes.SchemaVersionUnsupported,
                $"Unsupported Storage Realization schema version '{document.SchemaVersion}'.",
                "/schemaVersion",
                document.Structure.Id.Value,
                StorageRealizationDocument.CurrentSchemaVersion,
                document.SchemaVersion));
        }

        diagnostics.AddRange(Validate(document.Structure, document.Realization).Diagnostics);
        var expectedStructure = StorageRealizationFingerprinter.ComputeStructure(document.Structure);
        if (!Equals(expectedStructure, document.StructureFingerprint))
        {
            diagnostics.Add(Error(
                StorageRealizationDiagnosticCodes.FingerprintMismatch,
                "The persisted storage-structure fingerprint does not match canonical semantic content.",
                "/structureFingerprint",
                document.Structure.Id.Value,
                expectedStructure.Value,
                document.StructureFingerprint.Value));
        }

        var expectedRealization = StorageRealizationFingerprinter.ComputeTarget(document.Realization);
        if (!Equals(expectedRealization, document.RealizationFingerprint))
        {
            diagnostics.Add(Error(
                StorageRealizationDiagnosticCodes.FingerprintMismatch,
                "The persisted target-realization fingerprint does not match canonical realization content.",
                "/realizationFingerprint",
                document.Realization.Id.Value,
                expectedRealization.Value,
                document.RealizationFingerprint.Value));
        }

        return Result(diagnostics);
    }

    /// <summary>Validates canonical semantic structure and exact target realization linkage.</summary>
    /// <param name="structure">Canonical semantic storage structure.</param>
    /// <param name="realization">Target-specific interpretation to validate.</param>
    /// <returns>Deterministically ordered semantic and realization diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(
        StorageStructureDefinition structure,
        StorageTargetRealization realization)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(realization);
        List<DocumentValidationDiagnostic> diagnostics = [.. ValidateStructure(structure).Diagnostics];
        var expectedStructure = StorageRealizationFingerprinter.ComputeStructure(structure);
        if (!Equals(expectedStructure, realization.StructureFingerprint))
        {
            diagnostics.Add(Error(
                StorageRealizationDiagnosticCodes.StructureLinkMismatch,
                "The target realization references a different canonical storage-structure fingerprint.",
                "/realization/structureFingerprint",
                realization.Id.Value,
                expectedStructure.Value,
                realization.StructureFingerprint.Value));
        }

        var semanticCollections = structure.OwnedCollections.ToDictionary(static item => item.Id);
        foreach (var collection in structure.OwnedCollections)
        {
            if (!realization.OwnedCollections.Any(candidate => candidate.Collection == collection.Id))
            {
                diagnostics.Add(Error(
                    StorageRealizationDiagnosticCodes.CollectionRealizationMissing,
                    $"Owned collection '{collection.Id.Value}' has no target realization.",
                    "/realization/ownedCollections",
                    realization.Id.Value,
                    collection.Id.Value,
                    "absent"));
            }
        }

        for (var index = 0; index < realization.OwnedCollections.Length; index++)
        {
            var collection = realization.OwnedCollections[index];
            if (!semanticCollections.ContainsKey(collection.Collection))
            {
                diagnostics.Add(Error(
                    StorageRealizationDiagnosticCodes.CollectionRealizationUnknown,
                    $"Target realization refers to unknown owned collection '{collection.Collection.Value}'.",
                    $"/realization/ownedCollections/{index}/collection",
                    realization.Id.Value,
                    string.Join(",", structure.OwnedCollections.Select(static item => item.Id.Value)),
                    collection.Collection.Value));
            }
        }

        return Result(diagnostics);
    }

    /// <summary>Validates root, ownership, local identity, ordinal, and inherited partition semantics.</summary>
    /// <param name="structure">Canonical semantic storage structure.</param>
    /// <returns>Deterministically ordered semantic diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="structure"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult ValidateStructure(StorageStructureDefinition structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var graph = structure.SemanticModel.Graph;
        if (!string.Equals(
                structure.SemanticModel.SchemaVersion,
                ShapeGraphDocument.CurrentSchemaVersion,
                StringComparison.Ordinal)
            || graph.HasErrors
            || !graph.TryGetShape(structure.RootShape, out var root))
        {
            diagnostics.Add(Error(
                StorageRealizationDiagnosticCodes.SemanticModelInvalid,
                $"Semantic model does not contain a valid root shape '{structure.RootShape}'.",
                "/structure/rootShape",
                structure.Id.Value,
                structure.RootShape.ToString(),
                "missing-or-invalid"));
            return Result(diagnostics);
        }

        ValidateRequiredScalarRootField(
            root,
            structure.RootIdentityPath,
            "/structure/rootIdentityPath",
            "root identity",
            structure,
            diagnostics);
        ValidateRequiredScalarRootField(
            root,
            structure.PartitionPath,
            "/structure/partitionPath",
            "partition",
            structure,
            diagnostics);

        HashSet<FieldPath> collectionPaths = [];
        for (var index = 0; index < structure.OwnedCollections.Length; index++)
        {
            var collection = structure.OwnedCollections[index];
            var location = $"/structure/ownedCollections/{index}";
            if (!collectionPaths.Add(collection.CollectionPath)
                || !TryDirectField(root, collection.CollectionPath, out var field)
                || field.Cardinality != FieldCardinality.Many
                || field.Presence != FieldPresence.Required
                || field.Nullability != FieldNullability.NonNullable
                || field.Type is not NamedTypeRef named
                || named.TypeId != collection.ComponentType
                || !graph.TryGetType(collection.ComponentType, out var type)
                || type is not TypeDefinition.Structural component)
            {
                diagnostics.Add(Error(
                    StorageRealizationDiagnosticCodes.OwnedCollectionInvalid,
                    $"Owned collection '{collection.Id.Value}' must resolve to one required non-null many-valued named structural field.",
                    location + "/collectionPath",
                    collection.Id.Value,
                    collection.ComponentType.Value,
                    collection.CollectionPath.ToString()));
                continue;
            }

            ValidateRequiredComponentField(
                component,
                collection.LocalIdentityPath,
                location + "/localIdentityPath",
                collection,
                diagnostics,
                ordinal: false);
            ValidateRequiredComponentField(
                component,
                collection.OrdinalPath,
                location + "/ordinalPath",
                collection,
                diagnostics,
                ordinal: true);
            if (collection.LocalIdentityPath == collection.OrdinalPath)
            {
                diagnostics.Add(Error(
                    StorageRealizationDiagnosticCodes.ComponentOrdinalInvalid,
                    $"Owned collection '{collection.Id.Value}' must use distinct local identity and ordinal paths.",
                    location + "/ordinalPath",
                    collection.Id.Value,
                    "distinct-from-local-identity",
                    collection.OrdinalPath.ToString()));
            }
        }

        return Result(diagnostics);
    }

    static void ValidateRequiredScalarRootField(
        Shape root,
        FieldPath path,
        string location,
        string description,
        StorageStructureDefinition structure,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (!TryDirectField(root, path, out var field)
            || field.Cardinality != FieldCardinality.Single
            || field.Presence != FieldPresence.Required
            || field.Nullability != FieldNullability.NonNullable
            || !IsScalar(field.Type))
        {
            diagnostics.Add(Error(
                StorageRealizationDiagnosticCodes.RootFieldInvalid,
                $"Canonical {description} path '{path}' must resolve to one required non-null scalar root field.",
                location,
                structure.Id.Value,
                "required-non-null-scalar",
                path.ToString()));
        }
    }

    static void ValidateRequiredComponentField(
        TypeDefinition.Structural component,
        FieldPath path,
        string location,
        StorageOwnedCollectionDefinition collection,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        bool ordinal)
    {
        var valid = TryDirectField(component, path, out var field)
                    && field.Cardinality == FieldCardinality.Single
                    && field.Presence == FieldPresence.Required
                    && field.Nullability == FieldNullability.NonNullable
                    && (ordinal
                        ? field.Type is ScalarTypeRef { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 }
                        : IsScalar(field.Type));
        if (valid)
            return;

        diagnostics.Add(Error(
            ordinal
                ? StorageRealizationDiagnosticCodes.ComponentOrdinalInvalid
                : StorageRealizationDiagnosticCodes.ComponentIdentityInvalid,
            ordinal
                ? $"Owned collection '{collection.Id.Value}' ordinal must be one required non-null Int32 or Int64 field."
                : $"Owned collection '{collection.Id.Value}' local identity must be one required non-null scalar field.",
            location,
            collection.Id.Value,
            ordinal ? "required-non-null-integral" : "required-non-null-scalar",
            path.ToString()));
    }

    static bool TryDirectField(Shape shape, FieldPath path, out FieldDefinition field)
    {
        field = null!;
        if (path.Segments.Length != 1
            || !path.Segments[0].TryGetFieldIdentity(out var identity)
            || !shape.TryGetField(identity, out var resolved))
        {
            return false;
        }

        field = resolved;
        return true;
    }

    static bool TryDirectField(
        TypeDefinition.Structural type,
        FieldPath path,
        out StructuralField field)
    {
        field = null!;
        if (path.Segments.Length != 1
            || !path.Segments[0].TryGetFieldIdentity(out var identity)
            || !type.TryGetField(identity, out var resolved))
        {
            return false;
        }

        field = resolved;
        return true;
    }

    static bool IsScalar(TypeRef type) => type is ScalarTypeRef or EnumTypeRef or EntityReferenceTypeRef;

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
            stage: ValidationStage,
            subject: subject,
            resolutionOptions: ["Correct the canonical structure or select a realization that preserves it exactly."],
            expected: expected,
            observed: observed));

    static DocumentValidationResult Result(IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        var normalized = diagnostics
            .Distinct()
            .Order(DocumentValidationDiagnosticComparer.Ordinal)
            .ToImmutableArray();
        return normalized.IsDefaultOrEmpty ? DocumentValidationResult.Valid : new(normalized);
    }
}
