using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

[MemoryDiagnoser]
public class ShapeGraphMetadataValidationBenchmarks
{
    ShapeGraphDocument document = null!;

    [Params(128, 4096)]
    public int Fields { get; set; }

    [Params(false, true)]
    public bool Named { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.String);
        document = Named
            ? ShapeGraphDocument.FromGraph(new(new("graph"), [new(new("shape"), [new(new("value"), new NamedTypeRef(new("type /é")))])],
                [new TypeDefinition.Structural(new("type /é"), [..Enumerable.Range(0, Fields).Select(i => new StructuralField(new("field /é" + i), scalar))])]))
            : ShapeGraphDocument.FromGraph(new(new("graph"), [new(new("shape /é"),
                [..Enumerable.Range(0, Fields).Select(i => new FieldDefinition(new("field /é" + i), scalar))]) ]));
        if (!Validate().IsValid) throw new InvalidOperationException("Invalid validation fixture.");
    }

    [Benchmark]
    public DocumentValidationResult Validate() => ShapeGraphDocumentSemanticValidator.Validate(document);
}
