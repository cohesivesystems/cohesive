using Cohesive.Adapters.Sql;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresRelationQueryArtifactContractTests
{
    static readonly RelationQueryFieldReference ValueField = new(
        new QualifiedShapeId(new GraphId("tests/postgres/artifact"), new ShapeId("Row")),
        FieldPath.FromField("value"));

    [Fact]
    public void ResultMetadata_RequiresExactEncodingAndSafePostgresAlias()
    {
        var contract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));

        _ = new PostgresRelationQueryResultFieldBinding(
            "value",
            ValueField,
            contract,
            PostgresRelationQueryValueEncoding.Text);
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryResultFieldBinding(
            "value",
            ValueField,
            contract,
            PostgresRelationQueryValueEncoding.Numeric));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryResultFieldBinding(
            new string('a', PostgresSqlDialect.StandardMaxUtf8ByteLength + 1),
            ValueField,
            contract,
            PostgresRelationQueryValueEncoding.Text));
    }

    [Fact]
    public void SelectedFieldMetadata_RequiresSafePostgresColumnIdentifier()
    {
        _ = new PostgresRelationQuerySelectedField(
            new RelationQueryInputId("field:value"),
            ValueField,
            new RelationQuerySourcePlacementBindingId("placement:row"),
            "value");

        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySelectedField(
            new RelationQueryInputId("field:value"),
            ValueField,
            new RelationQuerySourcePlacementBindingId("placement:row"),
            new string('a', PostgresSqlDialect.StandardMaxUtf8ByteLength + 1)));
    }

    [Fact]
    public void RuntimeOrderingDomainMetadata_IsTextOnlyAndDefinesExactAsciiBoundary()
    {
        var domain = new PostgresRelationQueryTextOrderingDomainEvidence(
            "ck_runtime_text_ascii",
            "tests/postgres/runtime-text-domain/v1");
        var textContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var supplied = new PostgresRelationQuerySuppliedFieldBinding(
            1,
            new("field:value"),
            ValueField,
            textContract,
            PostgresRelationQueryValueEncoding.Text,
            domain);

        Assert.Same(domain, supplied.OrderingDomain);
        Assert.True(domain.IsSatisfiedBy(string.Empty));
        Assert.True(domain.IsSatisfiedBy("AZaz09-._~"));
        Assert.False(domain.IsSatisfiedBy("café"));
        Assert.False(domain.IsSatisfiedBy("\0"));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySuppliedFieldBinding(
            1,
            new("field:value"),
            ValueField,
            new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int32)),
            PostgresRelationQueryValueEncoding.Int32,
            domain));
    }
}
