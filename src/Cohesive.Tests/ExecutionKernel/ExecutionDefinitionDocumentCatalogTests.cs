using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionDocumentCatalogTests
{
    [Fact]
    public void TryCreate_OrdersDocumentsAndResolvesOnlyCompleteExactReferences()
    {
        var betaRevisionTwo = Document("definition/beta", "revision/2", "beta-two");
        var alphaRevisionTwo = Document("definition/alpha", "revision/2", "alpha-two");
        var alphaRevisionOne = Document("definition/alpha", "revision/1", "alpha-one");

        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(
            [betaRevisionTwo, alphaRevisionTwo, alphaRevisionOne],
            out var catalog);

        Assert.True(validation.IsValid);
        Assert.NotNull(catalog);
        Assert.Equal(3, catalog.Count);
        Assert.Collection(
            catalog.Documents,
            document => Assert.Same(alphaRevisionOne, document),
            document => Assert.Same(alphaRevisionTwo, document),
            document => Assert.Same(betaRevisionTwo, document));
        Assert.True(catalog.TryResolve(Reference(alphaRevisionOne), out var resolved));
        Assert.Same(alphaRevisionOne, resolved);
        Assert.False(catalog.TryResolve(
            Reference(Document("definition/alpha", "revision/3", "alpha-three")),
            out _));
    }

    [Fact]
    public void TryCreate_RejectsDocumentWhoseFingerprintDoesNotProveItsSemanticContent()
    {
        var document = Document("definition/alpha", "revision/1", "alpha-one");
        var other = Document("definition/alpha", "revision/1", "different-content");
        var invalid = new ExecutionDefinitionDocument(
            document.Kind,
            new(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.SchemaVersion,
                other.Metadata.Fingerprint,
                document.Metadata.Provenance,
                document.Metadata.DisplayName,
                document.Metadata.Description,
                document.Metadata.SourceMap,
                document.Metadata.Diagnostics),
            document.Definition,
            document.Extensions);

        var validation = ExecutionDefinitionDocumentCatalog.TryCreate([invalid], out var catalog);

        Assert.Null(catalog);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ExecutionDefinitionDiagnosticCodes.FingerprintMismatch, diagnostic.Code);
        Assert.Equal("/definitions/0/metadata/fingerprint/value", diagnostic.Location);
    }

    [Fact]
    public void TryCreate_RejectsNullDocument()
    {
        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(
            new ExecutionDefinitionDocument[] { null! },
            out var catalog);

        Assert.Null(catalog);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ExecutionDefinitionDiagnosticCodes.CatalogDocumentInvalid, diagnostic.Code);
        Assert.Equal("/definitions/0", diagnostic.Location);
    }

    [Fact]
    public void TryCreate_RejectsDuplicateExactRevision()
    {
        var document = Document("definition/alpha", "revision/1", "alpha-one");

        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(
            [document, document],
            out var catalog);

        Assert.Null(catalog);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ExecutionDefinitionDiagnosticCodes.CatalogRevisionDuplicate, diagnostic.Code);
    }

    [Fact]
    public void TryCreate_RejectsConflictingFingerprintsForOneRevision()
    {
        var first = Document("definition/alpha", "revision/1", "alpha-one");
        var conflicting = Document("definition/alpha", "revision/1", "different-content");

        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(
            [first, conflicting],
            out var catalog);

        Assert.Null(catalog);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ExecutionDefinitionDiagnosticCodes.CatalogRevisionDuplicate, diagnostic.Code);
    }

    [Fact]
    public void ValidateReference_DistinguishesIdentityRevisionAndFingerprintFailures()
    {
        var document = Document("definition/alpha", "revision/1", "alpha-one");
        var catalogValidation = ExecutionDefinitionDocumentCatalog.TryCreate([document], out var catalog);
        Assert.True(catalogValidation.IsValid);
        Assert.NotNull(catalog);

        var unknownIdentity = catalog.ValidateReference(
            Reference(Document("definition/missing", "revision/1", "missing")),
            "/definition",
            out _);
        var unknownRevision = catalog.ValidateReference(
            Reference(Document("definition/alpha", "revision/2", "alpha-two")),
            "/definition",
            out _);
        var fingerprintMismatch = catalog.ValidateReference(
            Reference(Document("definition/alpha", "revision/1", "different-content")),
            "/definition",
            out _);
        var exact = catalog.ValidateReference(
            Reference(document),
            "/definition",
            out var resolved);

        AssertDiagnostic(
            unknownIdentity,
            ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown,
            "/definition/definitionId");
        AssertDiagnostic(
            unknownRevision,
            ExecutionDefinitionDiagnosticCodes.RevisionUnsupported,
            "/definition/revisionId");
        AssertDiagnostic(
            fingerprintMismatch,
            ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible,
            "/definition/fingerprint");
        Assert.True(exact.IsValid);
        Assert.Same(document, resolved);
    }

    static void AssertDiagnostic(
        DocumentValidationResult validation,
        string code,
        string location)
    {
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(location, diagnostic.Location);
    }

    static ExecutionDefinitionDocument Document(
        string definitionId,
        string revisionId,
        string value) =>
        ExecutionDefinitionDocument.Create(
            new("test.definition"),
            new(definitionId),
            new(revisionId),
            new TestDefinition(value),
            new(
                new("catalog-tests", "1.0"),
                new("tests/execution-definition-catalog", new([definitionId, revisionId])),
                DocumentOrigin.User));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);

    sealed record TestDefinition(string Value);
}
