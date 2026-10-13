using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class EntityFieldSetConventionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Short_entity_sets_use_canonical_field_names_independent_of_order(bool reverse)
    {
        var authored = TransitionAuthoring.Create<Fields, bool, bool>(Fields.Instance.Definition.Shape, Metadata(), t =>
        {
            if (reverse) t.Set(x => x.Enabled, (_, input) => input);
            t.Set(x => x.Label, "ready");
            if (!reverse) t.Set(x => x.Enabled, (_, input) => input);
            t.Return(new("result"), TransitionOutcomeDisposition.Applied, true);
        });
        Assert.True(authored.IsValid, string.Join("\n", authored.Validation.Diagnostics));
        var updates = authored.Definition.Body.Steps.OfType<UpdateTransitionNode>().ToArray();
        Assert.Equal("set/Label", Assert.Single(updates, x => x.Path.ToString() == "Label").Id.Value);
        Assert.Equal("set/Enabled", Assert.Single(updates, x => x.Path.ToString() == "Enabled").Id.Value);
    }

    [Fact]
    public void Null_sets_keep_null_distinct_from_removal_for_reference_and_value_fields()
    {
        var authored = TransitionAuthoring.Create<Fields, bool, bool>(Fields.Instance.Definition.Shape, Metadata(), t => t
            .SetNull(x => x.Label)
            .SetNull(x => x.Count)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, true));
        Assert.True(authored.IsValid, string.Join("\n", authored.Validation.Diagnostics));
        var updates = authored.Definition.Body.Steps.OfType<UpdateTransitionNode>().ToArray();
        Assert.Equal(["set/Label", "set/Count"], updates.Select(x => x.Id.Value));
        foreach (var update in updates)
            Assert.Equal(Expr.Const((string?)null), Assert.IsType<SetTransitionPatch>(update.Operation).Value);
    }

    [Fact]
    public void Repeated_field_updates_require_explicit_ids()
    {
        var duplicate = TransitionAuthoring.Create<Fields, bool, bool>(Fields.Instance.Definition.Shape, Metadata(), t => t
            .Set(x => x.Enabled, true).Set(x => x.Enabled, false)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, true));
        Assert.False(duplicate.IsValid);
        var distinct = TransitionAuthoring.Create<Fields, bool, bool>(Fields.Instance.Definition.Shape, Metadata(), t => t
            .Set(new("enable"), x => x.Enabled, true).Set(new("disable"), x => x.Enabled, false)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, true));
        Assert.True(distinct.IsValid, string.Join("\n", distinct.Validation.Diagnostics));
    }

    [Fact]
    public void Compiler_binds_short_calls_to_entity_field_overloads()
    {
        var compilation = Compile("b.Set(e => e.Label, \"ready\"); b.Set(e => e.Enabled, (_, input) => input);");
        Assert.Empty(compilation.GetDiagnostics().Where(x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        foreach (var call in compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(call).Symbol);
            Assert.Contains("Field<", method.Parameters[0].Type.ToDisplayString());
        }
    }

    [Fact]
    public void Compiler_rejects_null_for_nonnullable_value_fields()
    {
        var errors = Compile("b.SetNull(e => e.Enabled);").GetDiagnostics().Where(x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.Contains(errors, x => x.Id == "CS0452");
    }

    [Fact]
    public void Compiler_rejects_incompatible_entity_field_values()
    {
        var errors = Compile("b.Set(e => e.Enabled, \"wrong\");").GetDiagnostics()
            .Where(x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.NotEmpty(errors);
    }

    static CSharpCompilation Compile(string calls)
    {
        var source = $$"""
            using Cohesive.Transitions.Authoring;
            public sealed class E : Entity<E>
            {
                public Field<string?> Label { get; } = null!;
                public Field<bool> Enabled { get; } = null!;
            }
            public class Probe
            {
                public static void Author(TransitionSequenceBuilder<E, bool, bool> b) { {{calls}} }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(TransitionAuthoring).Assembly.Location).Append(typeof(ObservationValue).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(x => MetadataReference.CreateFromFile(x));
        return CSharpCompilation.Create("EntityFieldProbe", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    static TransitionAuthoringMetadata Metadata() => new(new("field-convention"), new("1"), new("body"),
        new(new(TransitionAuthoring.Producer), new("tests/field-convention"), DocumentOrigin.Generated));

    sealed class Fields : Entity<Fields>
    {
        public Fields()
        {
            Label = MutableField<string?>(nameof(Label));
            Enabled = MutableField<bool>(nameof(Enabled));
            Count = MutableField<int?>(nameof(Count));
        }
        public Field<string?> Label { get; }
        public Field<bool> Enabled { get; }
        public Field<int?> Count { get; }
    }
}
