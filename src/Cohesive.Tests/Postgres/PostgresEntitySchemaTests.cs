using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresEntitySchemaTests
{
    public sealed record Parent(string Id, string Partition, string Note);
    public sealed record Child(string Id, string Partition, string ParentId, int Quantity);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_key_rejects_mismatched_identity_or_partition_collation(bool partition)
    {
        var domain = new DomainModelBuilder();
        var parent = domain.Entity<Parent>("schema/parent");
        var child = domain.Entity<Child>("schema/child");
        var parents = PostgresEntityRepositoryMapping.For(parent).Table("public", "parent")
            .Identity(x => x.Id, "id").Partition(x => x.Partition, "tenant").Column(x => x.Note, "note").Build();
        var children = PostgresEntityRepositoryMapping.For(child).Table("public", "child")
            .Identity(x => x.Id, "id").Partition(x => x.Partition, "tenant").Column(x => x.ParentId, "parent")
            .Column(x => x.Quantity, "quantity").Build();
        PostgresPersistenceBindings Define(bool align) => PostgresPersistenceBindings.Define(new("db"), b => b
            .Entity(parent, parents).Entity(child, children).Schema(configure: schema =>
            {
                schema.ForeignKey(child.References(x => x.ParentId, parent));
                if (partition)
                {
                    schema.Collation(child, x => x.Partition, "C");
                    if (align) schema.Collation(parent, x => x.Partition, "C");
                }
                else
                {
                    schema.Collation(child, x => x.ParentId, "C");
                    if (align) schema.Collation(parent, x => x.Id, "C");
                }
            }));
        var failure = Assert.Throws<ArgumentException>(() => Define(false));
        Assert.Contains("matching types and collations", failure.Message);
        Assert.Contains(partition ? "tenant" : "parent", failure.Message);
        Assert.NotNull(Define(true).ProductSchema);
    }

    [Fact]
    public void Checks_use_mapped_fields_and_capture_values_once()
    {
        var domain = new DomainModelBuilder();
        var entity = domain.Entity<Child>("schema/child");
        var mapping = PostgresEntityRepositoryMapping.For(entity).Table("public", "child")
            .Identity(x => x.Id, "id").Partition(x => x.Partition, "tenant").Column(x => x.ParentId, "parent")
            .Column(x => x.Quantity, "quantity").Build();
        var partition = "tenant'\\x";
        var bindings = PostgresPersistenceBindings.Define(new("db"), b => b.Entity(entity, mapping).Schema(configure: schema => schema
            .Check(entity, x => x.Partition, SqlBinaryOperator.Equal, partition)
            .Check(entity, x => x.Quantity, SqlBinaryOperator.GreaterThan, 0)));
        partition = "changed";
        Assert.Contains("CHECK (\"tenant\" = E'tenant''\\\\x'::text)", bindings.ProductSchema!.CreateSql);
        Assert.Contains("CHECK (\"quantity\" > 0)", bindings.ProductSchema.CreateSql);
        Assert.DoesNotContain(partition, bindings.ProductSchema.CreateSql);
        Assert.Throws<ArgumentException>(() => PostgresPersistenceBindings.Define(new("db"), b => b.Entity(entity, mapping)
            .Schema(configure: schema => schema.Check(entity, x => x.Quantity + 1, SqlBinaryOperator.Equal, 0))));
        Assert.Throws<ArgumentException>(() => PostgresPersistenceBindings.Define(new("db"), b => b.Entity(entity, mapping)
            .Schema(configure: schema => schema.Check(entity, x => x.Quantity, SqlBinaryOperator.Add, 0))));
    }

    [Fact]
    public void Scalar_metadata_covers_every_type_and_preserves_text_aliases()
    {
        foreach (var type in Enum.GetValues<PostgresRelationQueryScalarType>())
        {
            Assert.NotEmpty(PostgresRelationQueryScalarCatalog.SchemaTypeName(type));
            Assert.True(Enum.IsDefined(PostgresRelationQueryScalarCatalog.ToValueEncoding(type)));
            var native = PostgresRelationQueryScalarCatalog.ToNpgsqlDbType(type, false);
            Assert.Equal(native | NpgsqlTypes.NpgsqlDbType.Array, PostgresRelationQueryScalarCatalog.ToNpgsqlDbType(type, true));
        }
        foreach (uint oid in new uint[] { 25, 1042, 1043 })
            Assert.True(PostgresRelationQueryScalarCatalog.AcceptsPostgresType(PostgresRelationQueryScalarType.Text, oid));
        Assert.False(PostgresRelationQueryScalarCatalog.AcceptsPostgresType(PostgresRelationQueryScalarType.Int32, 25));
        Assert.False(PostgresRelationQueryScalarCatalog.TryFromSemanticType(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresRelationQueryScalarCatalog.SchemaTypeName((PostgresRelationQueryScalarType)999));
    }

    [Fact]
    public void Cyclic_foreign_keys_are_rejected_before_io()
    {
        var domain = new DomainModelBuilder();
        var a = domain.Entity<Child>("schema/a");
        var b = domain.Entity<Child>("schema/b");
        PostgresEntityRepositoryMapping Map(DomainEntity<Child> entity, string table) => PostgresEntityRepositoryMapping.For(entity)
            .Table("public", table).Identity(x => x.Id, "id").Partition(x => x.Partition, "partition")
            .Column(x => x.ParentId, "parent").Column(x => x.Quantity, "quantity").Build();
        Assert.Throws<ArgumentException>(() => PostgresPersistenceBindings.Define(new("db"), storage => storage
            .Entity(a, Map(a, "a")).Entity(b, Map(b, "b"))
            .Schema(configure: s => s.ForeignKey(a.References(x => x.ParentId, b)).ForeignKey(b.References(x => x.ParentId, a)))));
    }

    [Fact]
    public void Projection_is_deterministic_and_uses_mapped_names_nullability_and_explicit_references()
    {
        var domain = new DomainModelBuilder();
        var parent = domain.Entity<Parent>("schema/parent");
        var child = domain.Entity<Child>("schema/child");
        var reference = child.References(x => x.ParentId, parent);
        var parents = PostgresEntityRepositoryMapping.For(parent).Table("public", "z_parent")
            .Identity(x => x.Id, "parent_key").Partition(x => x.Partition, "tenant").Column(x => x.Note, "note").Build();
        var children = PostgresEntityRepositoryMapping.For(child).Table("public", "a_child")
            .Identity(x => x.Id, "child_key").Partition(x => x.Partition, "tenant").Column(x => x.ParentId, "owner")
            .Column(x => x.Quantity, "quantity").Build();
        PostgresEntitySchemaBuilder? retained = null;
        var first = PostgresPersistenceBindings.Define(new("db"), b => b.Schema(configure: s => { retained = s; s.ForeignKey(reference); })
            .Entity(child, children).Entity(parent, parents));
        var second = PostgresPersistenceBindings.Define(new("db"), b => b.Entity(parent, parents).Entity(child, children)
            .Schema(configure: s => s.ForeignKey(reference)));
        var sql = first.ProductSchema!.CreateSql;
        Assert.Equal(sql, second.ProductSchema!.CreateSql);
        Assert.Same(first.ProductSchema, first.ProductSchema);
        Assert.Contains("\"note\" text NOT NULL,", sql);
        Assert.Contains("\"quantity\" integer NOT NULL", sql);
        Assert.Contains("FOREIGN KEY (\"tenant\", \"owner\") REFERENCES \"public\".\"z_parent\" (\"tenant\", \"parent_key\")", sql);
        Assert.True(sql.IndexOf("CREATE TABLE IF NOT EXISTS \"public\".\"z_parent\"", StringComparison.Ordinal)
            < sql.IndexOf("CREATE TABLE IF NOT EXISTS \"public\".\"a_child\"", StringComparison.Ordinal));
        Assert.DoesNotContain("ALTER", sql);
        Assert.Throws<InvalidOperationException>(() => retained!.ForeignKey(reference));
    }

    [Fact]
    public void Foreign_key_to_unmapped_resource_is_rejected_offline()
    {
        var domain = new DomainModelBuilder();
        var parent = domain.Entity<Parent>("schema/parent");
        var child = domain.Entity<Child>("schema/child");
        var map = PostgresEntityRepositoryMapping.For(child).Table("public", "child")
            .Identity(x => x.Id, "id").Partition(x => x.Partition, "partition").Column(x => x.ParentId, "parent")
            .Column(x => x.Quantity, "quantity").Build();
        Assert.Throws<ArgumentException>(() => PostgresPersistenceBindings.Define(new("db"), b => b.Entity(child, map)
            .Schema(configure: s => s.ForeignKey(child.References(x => x.ParentId, parent)))));
        Assert.Throws<ArgumentException>(() => PostgresPersistenceBindings.Define(new("db"), b => b.Entity(child, map)
            .Schema(configure: s => s.Collation(child, x => x.Quantity, "C"))));
    }
}
