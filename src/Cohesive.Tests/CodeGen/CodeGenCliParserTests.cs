using Cohesive.CodeGen.Cli;

namespace Cohesive.Tests.CodeGen;

public sealed class CodeGenCliParserTests
{
    [Fact]
    public void TryParse_RecognizesApiEmitKind()
    {
        var parsed = CodeGenCliParser.TryParse(
            [
                "--contracts", "/tmp/contracts.dll",
                "--out", "/tmp/generated",
                "--emit", "shapes,apis,openapi,graphql,constants",
                "--module", "sample"
            ],
            out var options,
            out var error,
            out var showHelp);

        Assert.True(parsed);
        Assert.False(showHelp);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Contains(CodeGenEmitKind.Shapes, options!.EmitKinds);
        Assert.Contains(CodeGenEmitKind.Apis, options.EmitKinds);
        Assert.Contains(CodeGenEmitKind.OpenApi, options.EmitKinds);
        Assert.Contains(CodeGenEmitKind.GraphQL, options.EmitKinds);
        Assert.Contains(CodeGenEmitKind.Constants, options.EmitKinds);
    }

    [Fact]
    public void TryParse_RecognizesExternalShapeModules()
    {
        var parsed = CodeGenCliParser.TryParse(
            [
                "--contracts", "/tmp/contracts.dll",
                "--out", "/tmp/generated",
                "--emit", "shapes",
                "--module", "sample",
                "--external-shapes", "Cohesive.Presentation=./cohesive.shapes.generated",
                "--external-shapes", "Cohesive.Model=./cohesive.shapes.generated"
            ],
            out var options,
            out var error,
            out var showHelp);

        Assert.True(parsed);
        Assert.False(showHelp);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Collection(
            options!.ExternalTypeScriptShapeModules,
            presentation =>
            {
                Assert.Equal("clr:type:Cohesive.Presentation.", presentation.TypeIdPrefix);
                Assert.Equal("clr:shape:Cohesive.Presentation.", presentation.ShapeIdPrefix);
                Assert.Equal("./cohesive.shapes.generated", presentation.ImportPath);
            },
            model =>
            {
                Assert.Equal("clr:type:Cohesive.Model.", model.TypeIdPrefix);
                Assert.Equal("clr:shape:Cohesive.Model.", model.ShapeIdPrefix);
                Assert.Equal("./cohesive.shapes.generated", model.ImportPath);
            });
    }

    [Fact]
    public void TryParse_RecognizesCanonicalJsonShapeProjection()
    {
        var parsed = CodeGenCliParser.TryParse(
            [
                "--contracts", "/tmp/contracts.dll",
                "--out", "/tmp/generated",
                "--emit", "shapes",
                "--module", "sample",
                "--shape-projection", "canonical-json"
            ],
            out var options,
            out var error,
            out var showHelp);

        Assert.True(parsed);
        Assert.False(showHelp);
        Assert.Null(error);
        Assert.Equal(ContractShapeProjection.CanonicalJson, options!.ShapeProjection);
    }
}
