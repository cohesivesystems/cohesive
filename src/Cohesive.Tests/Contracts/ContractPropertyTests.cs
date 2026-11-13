using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Contracts;

public sealed class ContractPropertyTests
{
    static readonly ExecutionProvenance Origin = new(new("contract-tests", "1"), new("tests/contracts"), DocumentOrigin.Generated);

    [Fact]
    public void RolesReuseOneExactAssertionAndSetOrderDoesNotChangeMeaning()
    {
        var (predicate, context, source) = Source();
        var first = Declare(source);
        var second = Declare(source with
        {
            Classification = [ContractPredicateClassification.Invariant, ContractPredicateClassification.Safety],
            Scope = source.Scope with { Subjects = ["b", "a"] }
        });
        Assert.Equal(first.Reference, second.Reference);
        Valid(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(first), out var reopened));
        Valid(ExecutionDefinitionDocumentCatalog.TryCreate([predicate, context, reopened!], out var catalog));
        foreach (var role in Enum.GetValues<ContractRole>())
        {
            Valid(ContractPropertyDocuments.Inspect(new(role, first.Reference), catalog!, out var inspected));
            Assert.Equal(first.Reference, inspected!.Use.Property);
            Assert.Equal(role, inspected.Use.Role);
            Assert.Equal(ContractPropertyKinds.DataIntegrity, inspected.Definition.Kind);
        }
    }

    [Theory]
    [InlineData("invariant", "contract.property.classification")]
    [InlineData("duplicate-class", "contract.property.classification")]
    [InlineData("subjects", "contract.property.subjects")]
    [InlineData("boundary", "contract.property.scope")]
    [InlineData("predicate-kind", "contract.property.dependency")]
    [InlineData("parameters-kind", "contract.property.dependency")]
    public void MalformedAssertionsCannotPassInspection(string mutation, string code)
    {
        var (predicate, context, source) = Source();
        source = mutation switch
        {
            "invariant" => source with { Classification = [ContractPredicateClassification.Invariant] },
            "duplicate-class" => source with { Classification = [ContractPredicateClassification.Safety, ContractPredicateClassification.Safety] },
            "subjects" => source with { Scope = source.Scope with { Subjects = ["a", "a"] } },
            "boundary" => source with { Scope = source.Scope with { Boundary = "" } },
            "predicate-kind" => source with { Predicate = context.Reference },
            "parameters-kind" => source with { Parameters = predicate.Reference },
            _ => throw new ArgumentException(mutation)
        };
        var document = Declare(source);
        Valid(ExecutionDefinitionDocumentCatalog.TryCreate([predicate, context, document], out var catalog));
        var validation = ContractPropertyDocuments.Inspect(new(ContractRole.Requires, document.Reference), catalog!, out var inspected);
        Assert.Null(inspected);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void ChangedPredicateCannotSatisfyTheOriginalExactReference()
    {
        var (predicate, context, source) = Source();
        var changed = ContractPropertyDocuments.DeclarePredicate(predicate.Metadata.DefinitionId, predicate.Metadata.RevisionId,
            new { RequireDurableAcceptance = false }, Origin);
        var property = Declare(source);
        Valid(ExecutionDefinitionDocumentCatalog.TryCreate([changed, context, property], out var catalog));
        var validation = ContractPropertyDocuments.Inspect(new(ContractRole.Provides, property.Reference), catalog!, out var inspected);
        Assert.False(validation.IsValid);
        Assert.Null(inspected);
    }

    static (ExecutionDefinitionDocument Predicate, ExecutionDefinitionDocument Context, ContractPropertyDefinition Source) Source()
    {
        var predicate = ContractPropertyDocuments.DeclarePredicate(new("predicate"), new("v1"), new { RequireDurableAcceptance = true }, Origin);
        var context = ContractPropertyDocuments.DeclareContext(new("context"), new("v1"), new { Calendar = "fixture/v1" }, Origin);
        return (predicate, context, new(ContractPropertyKinds.DataIntegrity,
            [ContractPredicateClassification.Safety, ContractPredicateClassification.Invariant], predicate.Reference,
            new(["a", "b"], "instrument/session", "publication-commit", context.Reference, "valid-time"),
            context.Reference, context.Reference));
    }

    static ExecutionDefinitionDocument Declare(ContractPropertyDefinition source) =>
        ContractPropertyDocuments.Declare(new("property"), new("v1"), source, Origin);

    static void Valid(DocumentValidationResult result) =>
        Assert.True(result.IsValid, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
}
