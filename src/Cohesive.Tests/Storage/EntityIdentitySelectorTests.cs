using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Storage;

public sealed class EntityIdentitySelectorTests
{
    [Fact]
    public void Declared_json_field_selects_Sku_instead_of_Id() =>
        Assert.Equal("sku-1", EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>("sku")(new("wrong", "sku-1")));

    [Fact]
    public void Guid_entity_id_and_formattable_values_preserve_encoding()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString(), EntityRepositoryMappingExtensions.PrepareIdentitySelector<GuidRow>("Key")(new(id)));
        Assert.Equal("entity-1", EntityRepositoryMappingExtensions.PrepareIdentitySelector<EntityRow>()(new(new("entity-1"))));
        Assert.Equal("42", EntityRepositoryMappingExtensions.PrepareIdentitySelector<NumberRow>()(new(42)));
    }

    [Fact]
    public void Missing_or_ambiguous_identity_fails_during_preparation()
    {
        Assert.Throws<InvalidOperationException>(() => EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>("missing"));
        Assert.Throws<InvalidOperationException>(() => EntityRepositoryMappingExtensions.PrepareIdentitySelector<Ambiguous>("same"));
        Assert.Throws<InvalidOperationException>(() => EntityRepositoryMappingExtensions.PrepareIdentitySelector<NoIdentity>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sku")]
    public void Warm_selector_lookup_and_string_extraction_do_not_allocate_per_call(string? field)
    {
        var entity = new Stock("id", "sku");
        var prepared = EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>(field);
        for (var i = 0; i < 20_000; i++)
            _ = EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>(field)(entity);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
            _ = EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>(field)(entity);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(prepared, EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>(field));
        // A small fixed allowance tolerates runtime bookkeeping, but rejects even one byte per call.
        Assert.InRange(allocated, 0, 1024);
    }

    [Fact]
    public async Task Untyped_writes_and_batches_preserve_declared_identity_through_a_wrapper()
    {
        var inner = new RecordingRepository();
        IEntityRepository wrapped = new ForwardingRepository(inner);
        var context = OperationContext.Create();
        await wrapped.Upsert(context, new Stock("wrong", "single"));
        Assert.Equal("single", Assert.Single(inner.Ids));
        inner.IdentityReads = 0;
        await wrapped.UpsertBatch(context, new[] { new Stock("wrong", "first"), new Stock("wrong", "second") });
        Assert.Equal(new[] { "single", "first", "second" }, inner.Ids);
        Assert.Equal(1, inner.IdentityReads);
    }

    [Fact]
    public void A_wrapper_cannot_omit_identity_metadata()
    {
        var source = """
            using System.Threading.Tasks;
            using Cohesive.Storage;
            using Cohesive.Transitions.Model;
            using Cohesive.Model;
            using Cohesive.Prelude;
            public sealed class MissingIdentity : IEntityRepository
            {
                public EntityDefinition EntityDefinition => throw new System.NotSupportedException();
                public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => throw new System.NotSupportedException();
                public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => throw new System.NotSupportedException();
            }
            """;
        var platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = platform.Split(Path.PathSeparator).Append(typeof(IEntityRepository).Assembly.Location)
            .Append(typeof(EntityDefinition).Assembly.Location).Append(typeof(OperationContext).Assembly.Location)
            .Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var errors = CSharpCompilation.Create("MissingIdentity", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        var error = Assert.Single(errors);
        Assert.Equal("CS0535", error.Id);
        Assert.Contains("IdentityField", error.GetMessage());
    }

    sealed class ForwardingRepository(IEntityRepository inner) : IEntityRepository
    {
        public EntityDefinition EntityDefinition => inner.EntityDefinition;
        public string? IdentityField => inner.IdentityField;
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => inner.TryGet(context, id, options);
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => inner.Upsert(context, write);
    }

    sealed class RecordingRepository : IEntityRepository
    {
        public EntityDefinition EntityDefinition { get; } = ObjectEntityDefinition.For<Stock>(new("identity-test"));
        public int IdentityReads;
        public string IdentityField { get { IdentityReads++; return "sku"; } }
        public List<string> Ids { get; } = [];
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => throw new NotSupportedException();
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
        {
            Ids.Add(write.Entity.EntityId.Value);
            return Task.FromResult(new EntitySnapshot(write.Entity, write.Entity.EntityId.Value, new("test")));
        }
    }

    public sealed record Stock(string Id, [property: JsonPropertyName("sku")] string Sku);
    public sealed record GuidRow(Guid Key);
    public sealed record EntityRow(EntityId Id);
    public sealed record NumberRow(int Key);
    public sealed record NoIdentity(string Name);
    public sealed record Ambiguous([property: JsonPropertyName("same")] string First, [property: JsonPropertyName("same")] string Second);
}
