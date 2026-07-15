using System.Diagnostics;
using System.Text.Json;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryPortableInt64JsonTests
{
    const long BeyondJavaScriptSafeInteger = 9_007_199_254_740_993L;

    [Theory]
    [InlineData(BeyondJavaScriptSafeInteger)]
    [InlineData(long.MaxValue)]
    public void PortableInt64Fields_UseCanonicalDecimalStringsAndRejectJsonNumbers(long value)
    {
        var options = RelationQueryJsonSerializer.CreateOptions();
        var boundary = new RelationQueryOperatingBoundary(
            new("boundary/max-page-size"),
            RelationQueryOperatingBoundaryKind.MaximumPageSize,
            value);
        var staticFact = new RelationQueryRealizationStaticFact(
            RelationQueryRealizationStaticFactKind.PageSize,
            value);
        var validation = new RelationQueryOperatingBoundaryValidation(
            boundary.Id,
            RelationQueryOperatingBoundaryValidationKind.StaticPlanFact,
            measuredValue: value);

        AssertCanonicalStringProperty(boundary, "limit", value, options);
        AssertCanonicalStringProperty(staticFact, "value", value, options);
        AssertCanonicalStringProperty(validation, "measuredValue", value, options);

        var boundaryJson = JsonSerializer.Serialize(boundary, options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RelationQueryOperatingBoundary>(
            boundaryJson.Replace(
                $"\"{value}\"",
                value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
            options));
    }

    [Theory]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("-0")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    public void PortableInt64Fields_RejectNonCanonicalOrOutOfRangeStrings(string value)
    {
        var options = RelationQueryJsonSerializer.CreateOptions();
        var boundary = new RelationQueryOperatingBoundary(
            new("boundary/max-page-size"),
            RelationQueryOperatingBoundaryKind.MaximumPageSize,
            limit: 1);
        var json = JsonSerializer.Serialize(boundary, options)
            .Replace("\"1\"", JsonSerializer.Serialize(value), StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RelationQueryOperatingBoundary>(json, options));
    }

    [Fact]
    public async Task InvalidReport_PreservesWideInt64AndFingerprintThroughJavaScriptRoundTrip()
    {
        var plan = PlanReference();
        var join = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Join);
        RelationQueryTargetCapabilityEvidenceId conflictId = new("evidence/conflict");
        var boundary = new RelationQueryOperatingBoundary(
            new("boundary/max-input-rows"),
            RelationQueryOperatingBoundaryKind.MaximumInputRows,
            BeyondJavaScriptSafeInteger);
        var profile = new RelationQueryTargetCapabilityProfile(
            new("target/test"),
            new("target/test/v1"),
            [plan.DefinitionSchemaVersion],
            [plan.CompilerProfile],
            [
                new(conflictId, join),
                new(
                    conflictId,
                    new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))
            ],
            [boundary]);
        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [new(new("requirement/join"), join)],
            profile,
            new(
                new("policy/test/v1"),
                "conventions/test/v1"));
        var options = RelationQueryJsonSerializer.CreateOptions();

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.DoesNotContain(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid
                && diagnostic.OperatingBoundary == boundary.Id);
        var json = JsonSerializer.Serialize(report, options);
        AssertBoundaryLimit(json, BeyondJavaScriptSafeInteger.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        var javascriptRoundTrip = await RoundTripThroughJavaScript(json);
        AssertBoundaryLimit(javascriptRoundTrip, BeyondJavaScriptSafeInteger.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        var roundTrip = JsonSerializer.Deserialize<RelationQueryRealizationReport>(javascriptRoundTrip, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(report.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(roundTrip.Fingerprint, RelationQueryRealizationFingerprinter.Compute(roundTrip));
        Assert.Equal(
            BeyondJavaScriptSafeInteger,
            Assert.Single(roundTrip.TargetProfile.OperatingBoundaries).Limit);
    }

    static void AssertCanonicalStringProperty<T>(
        T value,
        string propertyName,
        long expected,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, options));
        var property = document.RootElement.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, property.ValueKind);
        Assert.Equal(
            expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            property.GetString());
    }

    static void AssertBoundaryLimit(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        var limit = Assert.Single(document.RootElement
                .GetProperty("targetProfile")
                .GetProperty("operatingBoundaries")
                .EnumerateArray())
            .GetProperty("limit");
        Assert.Equal(JsonValueKind.String, limit.ValueKind);
        Assert.Equal(expected, limit.GetString());
    }

    static async Task<string> RoundTripThroughJavaScript(string json)
    {
        using var process = new Process
        {
            StartInfo = new("node")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add(
            "let source='';process.stdin.setEncoding('utf8');process.stdin.on('data',chunk=>source+=chunk);"
            + "process.stdin.on('end',()=>process.stdout.write(JSON.stringify(JSON.parse(source))));");

        Assert.True(process.Start());
        await process.StandardInput.WriteAsync(json);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 0, $"JavaScript JSON round trip failed: {error}");
        return output;
    }

    static RelationQueryCompiledPlanReference PlanReference()
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(result.IsSuccessful);
        return RelationQueryCompiledPlanReference.From(Assert.IsType<CompiledRelationQueryPlan>(result.Plan));
    }
}
