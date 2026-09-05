using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Sql;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cohesive.Tests.Sql;

public sealed class SqlAdapterExtensibilityTests
{
    [Fact]
    public void SharedSql_GrantsFriendAccessOnlyToTests()
    {
        var friends = typeof(SqlDialect).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName).Order().ToArray();
        Assert.Equal(["Cohesive.Adapters.SQLite.Tests", "Cohesive.Tests"], friends);
    }

    [Fact]
    public void ExternalAdapter_CompilesAndRunsUsingOnlyPublicContracts()
    {
        // This assembly has no friend access, even though the host test assembly does.
        const string source = """
            using System;
            using System.Linq;
            using System.Collections.Immutable;
            using Cohesive.Adapters.Sql;

            public sealed class ExternalDialect : SqlDialect
            {
                public override string Name => "external/v1";
                public override void ValidateIdentifier(SqlIdentifier identifier)
                {
                    if (SqlUtf8.GetByteCount(identifier.Value, nameof(identifier)) > 128)
                        throw new ArgumentException("Identifier exceeds the target byte limit.");
                }
                public override void ValidateParameter(object? value) { }
                public override string FunctionName(SqlFunction function) => throw new NotSupportedException();
                public override string FunctionName(SqlAggregateFunction function) => throw new NotSupportedException();
                public override void Require(SqlFeature feature) { }
                public override void WriteIntrinsic(
                    string intrinsic, ImmutableArray<SqlExpression> arguments, SqlExpressionWriter writer)
                {
                    if (intrinsic != "external.custom/v1")
                    {
                        base.WriteIntrinsic(intrinsic, arguments, writer);
                        return;
                    }
                    if (arguments.Length != 2) throw new ArgumentException("Expected two operands.");
                    writer.WriteIdentifier(new SqlIdentifier("custom\"function"));
                    writer.WriteSyntax("(");
                    writer.WriteExpression(arguments[0]);
                    writer.WriteSyntax(", ");
                    writer.WriteExpression(arguments[1]);
                    writer.WriteSyntax(")");
                }

                public static SqlCommandTemplate NativeQuery()
                {
                    var inner = new SqlSelectBuilder()
                        .Select(SqlExpression.Column("keys", "item"), "item").BuildQuery();
                    return SqlSelectBuilder.FromArray("keys", "keys", "item")
                        .Select(SqlExpression.Column("joined", "item"), "item")
                        .CrossJoinLateral(inner, "joined")
                        .Where(SqlExpression.EqualAny(SqlExpression.Column("joined", "item"), "keys"))
                        .BuildTemplate(new ExternalDialect());
                }

                public static string[] ParameterSlots()
                {
                    var slots = new SqlParameterSlots<string>();
                    slots.AddConstant(position => "@p" + position);
                    slots.GetOrAddRuntime("value", position => "@p" + position);
                    slots.GetOrAddRuntime("value", _ => throw new Exception("Must reuse the slot."));
                    return slots.Snapshot().ToArray();
                }

                public static string TableName() => new SqlQualifiedTable("my\"schema", "table")
                    .ToSql(new ExternalDialect());
            }
            """;
        var platformAssemblies = Assert.IsType<string>(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
        var references = platformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SqlDialect).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            assemblyName: "External.SqlAdapter.PublicContractProbe",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        var type = assembly.GetType("ExternalDialect", throwOnError: true)!;
        var dialect = Assert.IsAssignableFrom<SqlDialect>(Activator.CreateInstance(type));

        const string hostileValue = "'); DROP TABLE secrets; -- $999";
        var runtime = SqlExpression.RuntimeParameter("value");
        var operands = new[] { SqlExpression.Constant(hostileValue), runtime };
        var nested = SqlExpression.Intrinsic("external.custom/v1", operands);
        operands[0] = SqlExpression.Constant("changed after authoring");
        var query = new SqlSelectBuilder().Select(
            SqlExpression.Intrinsic("external.custom/v1", nested, runtime), "result").BuildQuery();
        var template = query.ToCommandTemplate(dialect);
        Assert.Equal(
            "SELECT \"custom\"\"function\"(\"custom\"\"function\"($1, $2), $2) AS \"result\"",
            template.Text);
        Assert.Equal(hostileValue, template.Parameters[0].ConstantValue);
        Assert.Equal("value", template.Parameters[1].Binding);
        Assert.Equal(2, template.Parameters.Length);
        Assert.Equal(JsonSerializer.Serialize(template), JsonSerializer.Serialize(query.ToCommandTemplate(dialect)));

        var rehydrated = JsonSerializer.Deserialize<SqlCommandTemplate>(JsonSerializer.Serialize(template))!;
        var statement = rehydrated.Bind(dialect, new Dictionary<string, object?> { ["value"] = "actual" });
        Assert.Equal(new object?[] { hostileValue, "actual" }, statement.Parameters.Select(parameter => parameter.Value));
        Assert.Throws<ArgumentException>(() => rehydrated.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["value"] = "actual" }));

        var native = Assert.IsType<SqlCommandTemplate>(type.GetMethod("NativeQuery")!.Invoke(null, null));
        Assert.Contains("unnest($1)", native.Text);
        Assert.Contains("CROSS JOIN LATERAL", native.Text);
        Assert.Contains(" = ANY($1)", native.Text);
        Assert.Equal("keys", Assert.Single(native.Parameters).Binding);
        Assert.Equal(new[] { "@p0", "@p1" }, Assert.IsType<string[]>(type.GetMethod("ParameterSlots")!.Invoke(null, null)));
        Assert.Equal("\"my\"\"schema\".\"table\"", type.GetMethod("TableName")!.Invoke(null, null));

        Assert.Throws<ArgumentException>(() => new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic("external.custom/v1"), "bad").BuildTemplate(dialect));
        var unsupported = Assert.Throws<SqlConstructionException>(() => new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic("other.construct/v1"), "bad").BuildTemplate(dialect));
        Assert.Equal("external/v1", unsupported.Dialect);
        Assert.Equal("other.construct/v1", unsupported.Construct);
        Assert.Equal("sql.unsupported-construct", unsupported.Code);
    }

    [Fact]
    public void Intrinsic_RejectsInvalidInputAndUnsupportedTargets()
    {
        Assert.Throws<ArgumentNullException>(() => SqlExpression.Intrinsic(null!));
        Assert.Throws<ArgumentException>(() => SqlExpression.Intrinsic(" "));
        Assert.Throws<ArgumentNullException>(() => SqlExpression.Intrinsic("custom", null!));
        Assert.Throws<ArgumentNullException>(() => SqlExpression.Intrinsic("custom", [null!]));
        Assert.Throws<ArgumentException>(() => new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic, SqlExpression.Constant(1)), "now")
            .BuildTemplate(PostgresSqlDialect.Instance));
        Assert.Throws<SqlConstructionException>(() => new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic("unknown/v1"), "bad").BuildTemplate(PostgresSqlDialect.Instance));
    }

    [Fact]
    public void DefaultWriter_CannotBeUsedOutsideConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => default(SqlExpressionWriter).WriteSyntax("SELECT"));
        Assert.Throws<InvalidOperationException>(() => default(SqlExpressionWriter).WriteExpression(SqlExpression.Constant(1)));
        Assert.Throws<InvalidOperationException>(() => default(SqlExpressionWriter).WriteIdentifier(new SqlIdentifier("column")));
    }
}
