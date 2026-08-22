using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Physical parent-root key stored by one decomposed PostgreSQL component table.</summary>
public sealed record PostgresRelationQueryOwnedCollectionParentBinding
{
    /// <summary>Creates parent-root correlation evidence.</summary>
    /// <param name="semanticPath">Canonical root identity path represented by the component column.</param>
    /// <param name="columnName">Physical non-null parent-root column.</param>
    /// <param name="scalarType">Physical parent-root scalar type.</param>
    /// <param name="textSemantics">Exact text equality evidence, or <see langword="null"/> for non-text values.</param>
    /// <param name="numericDomain">Finite CLR-decimal evidence for a numeric parent key.</param>
    /// <param name="temporalDomain">Finite canonical temporal evidence for a temporal parent key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path, column, or scalar-domain requirement is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scalarType"/> is unsupported.</exception>
    public PostgresRelationQueryOwnedCollectionParentBinding(
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A PostgreSQL owned component requires a parent-root semantic path.",
                nameof(semanticPath));
        }

        PostgresRelationQueryFieldBinding.RequireValueSemantics(
            scalarType,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited,
            textSemantics,
            PostgresRelationQueryOrderingCapability.None);
        PostgresRelationQueryIdentityBinding.RequireKeyDomainEvidence(
            scalarType,
            numericDomain,
            temporalDomain);
        if (scalarType == PostgresRelationQueryScalarType.Text
            && textSemantics?.Equality != PostgresRelationQueryTextEqualitySemantics.Ordinal)
        {
            throw new ArgumentException(
                "A text parent-root key requires exact ordinal equality evidence.",
                nameof(textSemantics));
        }

        SemanticPath = semanticPath;
        ColumnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        ScalarType = scalarType;
        TextSemantics = textSemantics;
        NumericDomain = numericDomain;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Canonical root identity path represented by the component column.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Physical non-null parent-root column.</summary>
    public string ColumnName { get; }

    /// <summary>Physical parent-root scalar type.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }

    /// <summary>Exact text equality evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Finite canonical temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }
}

/// <summary>One canonical component field mapped to a PostgreSQL component-table column.</summary>
public sealed record PostgresRelationQueryOwnedCollectionElementFieldBinding
{
    /// <summary>Creates a component-field column binding.</summary>
    /// <param name="semanticPath">Direct component-relative field path.</param>
    /// <param name="columnName">Physical component-table column.</param>
    /// <param name="scalarType">Physical scalar type.</param>
    /// <param name="missingValueEncoding">Physical representation of semantic missing.</param>
    /// <param name="nullValueEncoding">Physical representation of semantic explicit null.</param>
    /// <param name="textSemantics">Text equality and ordering evidence when applicable.</param>
    /// <param name="ordering">Exact ordering evidence supplied by the column.</param>
    /// <param name="numericDomain">Finite CLR-decimal evidence for numeric values.</param>
    /// <param name="temporalDomain">Finite canonical temporal evidence for temporal values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path, column, value encoding, or scalar-domain requirement is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    public PostgresRelationQueryOwnedCollectionElementFieldBinding(
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryMissingValueEncoding missingValueEncoding,
        PostgresRelationQueryNullValueEncoding nullValueEncoding,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryOrderingCapability ordering = PostgresRelationQueryOrderingCapability.None,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (semanticPath.Segments.Length != 1
            || semanticPath.Segments[0].Kind != SegmentKind.Field
            || string.IsNullOrWhiteSpace(semanticPath.Segments[0].Segment))
        {
            throw new ArgumentException(
                "The PostgreSQL owned-component closure requires one direct semantic field path.",
                nameof(semanticPath));
        }

        PostgresRelationQueryFieldBinding.RequireValueSemantics(
            scalarType,
            missingValueEncoding,
            nullValueEncoding,
            textSemantics,
            ordering);
        PostgresRelationQueryIdentityBinding.RequireKeyDomainEvidence(
            scalarType,
            numericDomain,
            temporalDomain);
        SemanticPath = semanticPath;
        ColumnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        ScalarType = scalarType;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        TextSemantics = textSemantics;
        Ordering = ordering;
        NumericDomain = numericDomain;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Direct component-relative semantic field path.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Physical component-table column.</summary>
    public string ColumnName { get; }

    /// <summary>Physical scalar type.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }

    /// <summary>Physical representation of semantic missing.</summary>
    public PostgresRelationQueryMissingValueEncoding MissingValueEncoding { get; }

    /// <summary>Physical representation of semantic explicit null.</summary>
    public PostgresRelationQueryNullValueEncoding NullValueEncoding { get; }

    /// <summary>Text equality and ordering evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Exact ordering evidence supplied by the column.</summary>
    public PostgresRelationQueryOrderingCapability Ordering { get; }

    /// <summary>Finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Finite canonical temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }
}

/// <summary>
/// Adapter-owned physical mapping from one canonical owned collection to a decomposed PostgreSQL component table.
/// </summary>
public sealed record PostgresRelationQueryOwnedCollectionBinding
{
    /// <summary>Creates a decomposed owned-collection table binding.</summary>
    /// <param name="collection">Canonical owned-collection identity.</param>
    /// <param name="rootPlacementBinding">Root placement whose page is bounded before component acquisition.</param>
    /// <param name="collectionInput">Exact compiled root collection-field input.</param>
    /// <param name="collectionPath">Canonical root-relative collection path.</param>
    /// <param name="componentType">Canonical named structural component type.</param>
    /// <param name="schemaName">Physical PostgreSQL schema name.</param>
    /// <param name="tableName">Physical component table name.</param>
    /// <param name="parentRoot">Component column resolving the canonical parent root.</param>
    /// <param name="partition">Inherited tenant/partition column on the component table.</param>
    /// <param name="localIdentityPath">Canonical component-local identity path.</param>
    /// <param name="ordinalPath">Canonical component ordering path.</param>
    /// <param name="fields">Complete direct component field-column mappings.</param>
    /// <param name="validatedParentForeignKeyName">Validated foreign key from component records to roots.</param>
    /// <param name="validatedAggregateIdentityName">
    /// Validated unique constraint over partition, parent root, and component-local identity.
    /// </param>
    /// <param name="atomicityEvidenceReference">Authority proving root and component writes share one transaction.</param>
    /// <param name="changeCaptureEvidenceReference">
    /// Authority proving component changes retain the parent root key used for impact routing.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference or string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, path, physical name, field mapping, or evidence reference is invalid.</exception>
    public PostgresRelationQueryOwnedCollectionBinding(
        StorageOwnedCollectionId collection,
        RelationQuerySourcePlacementBindingId rootPlacementBinding,
        RelationQueryInputId collectionInput,
        FieldPath collectionPath,
        TypeId componentType,
        string schemaName,
        string tableName,
        PostgresRelationQueryOwnedCollectionParentBinding parentRoot,
        PostgresRelationQueryPartitionBinding partition,
        FieldPath localIdentityPath,
        FieldPath ordinalPath,
        ImmutableArray<PostgresRelationQueryOwnedCollectionElementFieldBinding> fields,
        string validatedParentForeignKeyName,
        string validatedAggregateIdentityName,
        string atomicityEvidenceReference,
        string changeCaptureEvidenceReference)
    {
        if (string.IsNullOrWhiteSpace(collection.Value)
            || string.IsNullOrWhiteSpace(rootPlacementBinding.Value)
            || string.IsNullOrWhiteSpace(collectionInput.Value)
            || string.IsNullOrWhiteSpace(componentType.Value))
        {
            throw new ArgumentException(
                "A PostgreSQL owned-collection binding requires non-default semantic identities.",
                nameof(collection));
        }
        if (collectionPath.Segments.IsDefaultOrEmpty
            || localIdentityPath.Segments.IsDefaultOrEmpty
            || ordinalPath.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A PostgreSQL owned-collection binding requires collection, local identity, and ordinal paths.",
                nameof(collectionPath));
        }

        var normalizedFields = fields.IsDefault ? [] : fields;
        if (normalizedFields.IsDefaultOrEmpty || normalizedFields.Any(static field => field is null))
        {
            throw new ArgumentException(
                "A PostgreSQL owned-collection binding requires non-null component field mappings.",
                nameof(fields));
        }
        if (normalizedFields.GroupBy(static field => field.SemanticPath).Any(static group => group.Count() > 1)
            || normalizedFields.GroupBy(static field => field.ColumnName, StringComparer.Ordinal)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A PostgreSQL owned-collection binding cannot repeat a component path or column.",
                nameof(fields));
        }
        if (!normalizedFields.Any(field => field.SemanticPath == localIdentityPath)
            || !normalizedFields.Any(field => field.SemanticPath == ordinalPath))
        {
            throw new ArgumentException(
                "A PostgreSQL owned-collection binding must map its local identity and ordinal paths.",
                nameof(fields));
        }

        Collection = collection;
        RootPlacementBinding = rootPlacementBinding;
        CollectionInput = collectionInput;
        CollectionPath = collectionPath;
        ComponentType = componentType;
        SchemaName = PostgresRelationQueryStorageBinding.RequireIdentifier(schemaName, nameof(schemaName));
        TableName = PostgresRelationQueryStorageBinding.RequireIdentifier(tableName, nameof(tableName));
        ParentRoot = Guard.RequireNotNull(parentRoot);
        Partition = Guard.RequireNotNull(partition);
        LocalIdentityPath = localIdentityPath;
        OrdinalPath = ordinalPath;
        Fields =
        [
            .. normalizedFields.OrderBy(
                static field => field.SemanticPath.ToString(),
                StringComparer.Ordinal)
        ];
        ValidatedParentForeignKeyName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedParentForeignKeyName,
            nameof(validatedParentForeignKeyName));
        ValidatedAggregateIdentityName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedAggregateIdentityName,
            nameof(validatedAggregateIdentityName));
        AtomicityEvidenceReference = Guard.RequireNotNullOrWhiteSpace(atomicityEvidenceReference);
        ChangeCaptureEvidenceReference = Guard.RequireNotNullOrWhiteSpace(changeCaptureEvidenceReference);
    }

    /// <summary>Canonical owned-collection identity.</summary>
    public StorageOwnedCollectionId Collection { get; }

    /// <summary>Root placement whose page is bounded before component acquisition.</summary>
    public RelationQuerySourcePlacementBindingId RootPlacementBinding { get; }

    /// <summary>Exact compiled root collection-field input.</summary>
    public RelationQueryInputId CollectionInput { get; }

    /// <summary>Canonical root-relative collection path.</summary>
    public FieldPath CollectionPath { get; }

    /// <summary>Canonical named structural component type.</summary>
    public TypeId ComponentType { get; }

    /// <summary>Physical PostgreSQL schema name.</summary>
    public string SchemaName { get; }

    /// <summary>Physical component table name.</summary>
    public string TableName { get; }

    /// <summary>Component column resolving the canonical parent root.</summary>
    public PostgresRelationQueryOwnedCollectionParentBinding ParentRoot { get; }

    /// <summary>Inherited tenant/partition column on the component table.</summary>
    public PostgresRelationQueryPartitionBinding Partition { get; }

    /// <summary>Canonical component-local identity path.</summary>
    public FieldPath LocalIdentityPath { get; }

    /// <summary>Canonical component ordering path.</summary>
    public FieldPath OrdinalPath { get; }

    /// <summary>Complete component field mappings in deterministic semantic-path order.</summary>
    public ImmutableArray<PostgresRelationQueryOwnedCollectionElementFieldBinding> Fields { get; }

    /// <summary>Validated foreign key from component records to roots.</summary>
    public string ValidatedParentForeignKeyName { get; }

    /// <summary>Validated aggregate-local component identity constraint.</summary>
    public string ValidatedAggregateIdentityName { get; }

    /// <summary>Authority proving root and component writes share one transaction.</summary>
    public string AtomicityEvidenceReference { get; }

    /// <summary>Authority proving component changes retain their parent root identity.</summary>
    public string ChangeCaptureEvidenceReference { get; }

    /// <summary>Resolves one component field by canonical component-relative path.</summary>
    /// <param name="semanticPath">Direct component-relative semantic path.</param>
    /// <returns>The exact physical component field binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="semanticPath"/> is not mapped.</exception>
    public PostgresRelationQueryOwnedCollectionElementFieldBinding ResolveField(FieldPath semanticPath) =>
        Fields.SingleOrDefault(field => field.SemanticPath == semanticPath)
        ?? throw new KeyNotFoundException(
            $"PostgreSQL owned collection '{Collection.Value}' has no component field '{semanticPath}'.");
}
