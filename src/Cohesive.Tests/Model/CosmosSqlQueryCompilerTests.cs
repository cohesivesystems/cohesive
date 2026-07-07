using Cohesive.Adapters.Cosmos;

namespace Cohesive.Tests.Model;

public sealed class CosmosSqlQueryCompilerTests
{
    [Fact]
    public void Compile_EqualityRangeAndNegation_ProducesParameterizedSql()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new And<FieldPredicate>(
            [
                new FieldPredicate(FieldPath.FromField("status"), new ExactValuePredicate("open")),
                new FieldPredicate(FieldPath.FromField("score"), NumberRangeValuePredicate.GreaterThanOrEqual(10)),
                new Not<FieldPredicate>(new FieldPredicate(FieldPath.FromField("deletedAt"), new ExistsValuePredicate()))
            ]));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE (c[\"status\"] = @p0 AND c[\"score\"] >= @p1 AND NOT (IS_DEFINED(c[\"deletedAt\"])))",
            compiled.Text);
        Assert.Equal("open", Assert.IsType<string>(compiled.Parameters["@p0"]));
        Assert.Equal(10d, Assert.IsType<double>(compiled.Parameters["@p1"]));
    }

    [Fact]
    public void Compile_SetMembership_ExpandsToEqualityDisjunction()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new FieldPredicate(
                FieldPath.FromField("status"),
                new InValuePredicate(["open", "paused"])));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE (c[\"status\"] = @p0 OR c[\"status\"] = @p1)",
            compiled.Text);
        Assert.Equal("open", compiled.Parameters["@p0"]);
        Assert.Equal("paused", compiled.Parameters["@p1"]);
    }

    [Fact]
    public void Compile_CaseSensitiveStringPredicate_OmitsIgnoreCaseArgument()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new FieldPredicate(
                FieldPath.FromField("name"),
                new PrefixValuePredicate("Al")));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE STARTSWITH(c[\"name\"], @p0)",
            compiled.Text);
        Assert.Equal("Al", compiled.Parameters["@p0"]);
    }

    [Fact]
    public void Compile_CaseInsensitiveStringPredicates_UseIgnoreCaseArgument()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new And<FieldPredicate>(
            [
                new FieldPredicate(
                    FieldPath.FromField("name"),
                    new PrefixValuePredicate("al", CaseSensitive: false)),
                new FieldPredicate(
                    FieldPath.FromField("name"),
                    new SuffixValuePredicate("ta", CaseSensitive: false)),
                new FieldPredicate(
                    FieldPath.FromField("name"),
                    new ContainsValuePredicate("phab", CaseSensitive: false))
            ]));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE (STARTSWITH(c[\"name\"], @p0, true) AND ENDSWITH(c[\"name\"], @p1, true) AND CONTAINS(c[\"name\"], @p2, true))",
            compiled.Text);
        Assert.Equal("al", compiled.Parameters["@p0"]);
        Assert.Equal("ta", compiled.Parameters["@p1"]);
        Assert.Equal("phab", compiled.Parameters["@p2"]);
    }

    [Fact]
    public void Compile_ScopedPredicate_UsesExistsSubquery()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new FieldPredicate(
                FieldPath.FromField("status"),
                new ExactValuePredicate("open")),
            Scope: FieldPath.FromField("items"));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE EXISTS (SELECT VALUE scope0 FROM scope0 IN c[\"items\"] WHERE scope0[\"status\"] = @p0)",
            compiled.Text);
        Assert.Equal("open", compiled.Parameters["@p0"]);
    }

    [Fact]
    public void Compile_AnyValuePredicate_UsesScalarExistsSubquery()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new FieldPredicate(
                FieldPath.FromField("tags"),
                new AnyValuePredicate(new ExactValuePredicate("canonical", CaseSensitive: false))));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE EXISTS (SELECT VALUE any0 FROM any0 IN c[\"tags\"] WHERE STRINGEQUALS(any0, @p0, true))",
            compiled.Text);
        Assert.Equal("canonical", compiled.Parameters["@p0"]);
    }

    [Fact]
    public void Compile_AnyFieldPredicate_UsesExistsSubquery()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new FieldPredicate(
                FieldPath.FromField("items"),
                new AnyFieldPredicate(new FieldPredicate(
                    FieldPath.FromField("status"),
                    new ExactValuePredicate("open")))));

        var compiled = compiler.Compile(query);

        Assert.Equal(
            "SELECT * FROM c WHERE EXISTS (SELECT VALUE any0 FROM any0 IN c[\"items\"] WHERE any0[\"status\"] = @p0)",
            compiled.Text);
        Assert.Equal("open", compiled.Parameters["@p0"]);
    }

    [Fact]
    public void CompileSupported_FullTextPredicate_ThrowsForUnsupportedCapability()
    {
        var compiler = new CosmosSqlQueryCompiler();
        var query = new EntityPredicate(
            Predicate: new FieldPredicate(
                FieldPath.FromField("content"),
                new FullTextValuePredicate("urgent")));

        var error = Assert.Throws<NotSupportedException>(() => compiler.CompileSupported(query));

        Assert.Contains(nameof(QueryCapability.FullText), error.Message);
    }
}
