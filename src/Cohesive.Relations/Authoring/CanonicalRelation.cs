using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>Typed C# handle for one canonical exactly-one rooted Relation revision.</summary>
/// <remarks>
/// The persisted <see cref="Document"/> remains the sole durable semantic authority. CLR generic arguments,
/// contracts, exact references, and captured dependency evidence are typed projections used by authoring,
/// linking, and host registration. No expression tree, callback, or mutable authoring session is retained.
/// </remarks>
/// <typeparam name="TInput">CLR type projected into the rooted Relation input contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the singular Relation output contract.</typeparam>
public sealed class Relation<TInput, TResult>
    where TInput : notnull
    where TResult : notnull
{
    internal Relation(
        RelationQueryDocument document,
        ExecutionRevisionId revisionId,
        DocumentValidationResult validation,
        ImmutableArray<ShapeGraphDocument> shapeDocuments,
        RelationshipCatalogDocument relationshipCatalog,
        ValueContract inputContract,
        ValueContract resultContract)
    {
        Document = Guard.RequireNotNull(document);
        if (string.IsNullOrWhiteSpace(revisionId.Value))
            throw new ArgumentException("A typed Relation requires an exact revision identity.", nameof(revisionId));

        RevisionId = revisionId;
        Validation = Guard.RequireNotNull(validation);
        ShapeDocuments = shapeDocuments.IsDefault ? [] : shapeDocuments;
        RelationshipCatalog = Guard.RequireNotNull(relationshipCatalog);
        InputContract = Guard.RequireNotNull(inputContract);
        ResultContract = Guard.RequireNotNull(resultContract);
    }

    /// <summary>Canonical persisted Relation document and sole durable semantic authority.</summary>
    public RelationQueryDocument Document { get; }

    /// <summary>Exact semantic revision used when this Relation is linked from another execution definition.</summary>
    public ExecutionRevisionId RevisionId { get; }

    /// <summary>Canonical validation produced by the expression-authoring terminal.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether canonical Relation validation succeeded.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Canonical Relation definition projected from <see cref="Document"/>.</summary>
    public RelationDefinition Definition => (RelationDefinition)Document.Definition;

    /// <summary>Exact identity, revision, and semantic fingerprint of this Relation revision.</summary>
    public ExecutionDefinitionReference Reference => new(
        new(Definition.Id.Value),
        RevisionId,
        new(
            Document.DefinitionFingerprint.Algorithm,
            Document.DefinitionFingerprint.Canonicalization,
            Document.DefinitionFingerprint.Value));

    /// <summary>Exact shape documents captured when the canonical Relation terminal was committed.</summary>
    public ImmutableArray<ShapeGraphDocument> ShapeDocuments { get; }

    /// <summary>Exact relationship catalog captured when the canonical Relation terminal was committed.</summary>
    public RelationshipCatalogDocument RelationshipCatalog { get; }

    /// <summary>
    /// Portable structural CLR input contract proven against the graph-qualified Relation root and
    /// <typeparamref name="TInput"/>.
    /// </summary>
    public ValueContract InputContract { get; }

    /// <summary>
    /// Portable structural CLR result contract proven against the graph-qualified Relation output and
    /// <typeparamref name="TResult"/>.
    /// </summary>
    public ValueContract ResultContract { get; }
}
