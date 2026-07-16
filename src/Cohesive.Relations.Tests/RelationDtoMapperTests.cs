using Cohesive.Relations.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.TestFixtures;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationDtoMapperTests
{
    [Fact]
    public void SharedSimpleFixture_ProducesOneCompleteCanonicalRelationRow()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var execution = scenario.Execution;

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, execution.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(execution.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, relation.State);
        Assert.Single(relation.Rows);
    }

    [Fact]
    public async Task FederatedFixture_ProducesFlattenedCanonicalRelationRows()
    {
        var scenario = await RelationDtoMapperTestFixture.ExecuteFederatedAsync();

        Assert.NotNull(scenario.Result.Interpretation);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, scenario.Result.Interpretation.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(scenario.Result.Interpretation.Relation);
        Assert.Equal(2, relation.Rows.Length);
        Assert.Equal("Customer One", relation.Rows[0].Value.GetProperty("CustomerName").String);
        Assert.Equal("TRUCK-001", relation.Rows[0].Value.GetProperty("EquipmentNumber").String);
    }

    [Theory]
    [InlineData(RelationDtoFixtureVariant.Complete, RelationQueryExecutionStatus.Succeeded)]
    [InlineData(RelationDtoFixtureVariant.MissingCustomer, RelationQueryExecutionStatus.Incomplete)]
    [InlineData(RelationDtoFixtureVariant.InvalidCustomerName, RelationQueryExecutionStatus.Failed)]
    public void SharedJoinedFixture_ExposesExpectedCanonicalStatus(
        RelationDtoFixtureVariant variant,
        RelationQueryExecutionStatus expected)
    {
        var scenario = RelationDtoBenchmarkFixture.CreateJoinedScenario(rowCount: 2, variant);

        Assert.Equal(expected, scenario.Execution.Status);
        Assert.Same(scenario.Evidence, scenario.Execution.Evidence);
    }

    [Fact]
    public void CompileAndMap_SimpleRecord_PreservesExactExecutionAndSourceRows()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 3);
        var mapper = Compile<LoadSummaryDto>(scenario.Plan);

        var result = mapper.Map(scenario.Execution);

        Assert.Equal(RelationDtoMappingStatus.Succeeded, result.Status);
        Assert.Same(scenario.Execution, result.Execution);
        Assert.Null(result.PhysicalExecution);
        Assert.Empty(result.FailedRows);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(scenario.Expected, result.Rows.Select(static row => row.Value));
        Assert.Equal(scenario.Execution.Relation!.Rows.Length, result.Rows.Length);
        for (var index = 0; index < result.Rows.Length; index++)
            Assert.Same(scenario.Execution.Relation.Rows[index], result.Rows[index].Source);
        Assert.All(mapper.Descriptor.Members, static member =>
            Assert.Equal(RelationDtoMemberBindingSource.ExactMemberName, member.BindingSource));
    }

    [Fact]
    public void CompileAndMap_JoinedRelation_MapsFlattenedEnrichment()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateJoinedScenario(rowCount: 4);
        var mapper = Compile<LoadSearchDto>(scenario.Plan);

        var result = mapper.Map(scenario.Execution);

        Assert.Equal(RelationDtoMappingStatus.Succeeded, result.Status);
        Assert.Equal(scenario.Expected, result.Rows.Select(static row => row.Value));
        Assert.Equal(scenario.Execution.Relation!.Rows.Length, result.Rows.Length);
        for (var index = 0; index < result.Rows.Length; index++)
            Assert.Same(scenario.Execution.Relation.Rows[index], result.Rows[index].Source);
        Assert.All(result.Rows, static row =>
        {
            Assert.NotEmpty(row.Value.CustomerName);
            Assert.NotEmpty(row.Value.EquipmentNumber);
        });
    }

    [Fact]
    public void MaterializationKernel_MapsCanonicalValuesWithoutCanonicalEnvelope()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateJoinedScenario(rowCount: 4);
        var kernel = Compile<LoadSearchDto>(scenario.Plan).MaterializationKernel;
        var rows = scenario.Execution.Relation!.Rows;
        var values = new LoadSearchDto[rows.Length];

        for (var index = 0; index < rows.Length; index++)
            values[index] = kernel(rows[index].Value);

        Assert.Equal(scenario.Expected, values);
    }

    [Fact]
    public void CompileAndMap_WritableDto_UsesInitMemberConstruction()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var mapper = Compile<WritableLoadSummaryDto>(scenario.Plan);

        var row = Assert.Single(mapper.Map(scenario.Execution).Rows).Value;

        Assert.Equal(scenario.Expected[0].Id, row.Id);
        Assert.Equal(scenario.Expected[0].Status, row.Status);
        Assert.Equal(scenario.Expected[0].Amount, row.Amount);
    }

    [Fact]
    public void CompileAndMap_OptionalUnmappedConstructorParameter_UsesDeclaredDefault()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var mapper = Compile<OptionalExtraLoadSummaryDto>(scenario.Plan);

        var row = Assert.Single(mapper.Map(scenario.Execution).Rows).Value;

        Assert.Equal(scenario.Expected[0].Id, row.Id);
        Assert.Equal(scenario.Expected[0].Status, row.Status);
        Assert.Equal(scenario.Expected[0].Amount, row.Amount);
        Assert.Equal("unmapped-default", row.Label);
    }

    [Fact]
    public void Compile_JsonNamePrecedesExactName_AndRemainsInspectable()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);

        var mapper = Compile<JsonLoadSummaryDto>(scenario.Plan);
        var mapped = Assert.Single(mapper.Map(scenario.Execution).Rows).Value;

        Assert.Equal(scenario.Expected[0].Id, mapped.LoadIdentifier);
        var id = Assert.Single(
            mapper.Descriptor.Members,
            static member => member.OutputField.Path == FieldPath.FromField("Id"));
        Assert.Equal(nameof(JsonLoadSummaryDto.LoadIdentifier), id.TargetMember);
        Assert.Equal(RelationDtoMemberBindingSource.SerializedName, id.BindingSource);
        Assert.NotNull(id.OutputReference);
    }

    [Fact]
    public void Compile_ExplicitBindingsPrecedeIncorrectSerializedNames()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var profile = new RelationDtoMapperProfile(
            "tests/explicit-load-summary/v1",
            [
                new(FieldPath.FromField("Id"), nameof(ExplicitLoadSummaryDto.LoadIdentifier)),
                new(FieldPath.FromField("Status"), nameof(ExplicitLoadSummaryDto.State)),
                new(FieldPath.FromField("Amount"), nameof(ExplicitLoadSummaryDto.Total))
            ],
            RelationDtoMemberConvention.ExplicitOnly);

        var mapper = Compile<ExplicitLoadSummaryDto>(scenario.Plan, profile);
        var mapped = Assert.Single(mapper.Map(scenario.Execution).Rows).Value;

        Assert.Equal(scenario.Expected[0].Id, mapped.LoadIdentifier);
        Assert.Equal(scenario.Expected[0].Status, mapped.State);
        Assert.Equal(scenario.Expected[0].Amount, mapped.Total);
        Assert.All(mapper.Descriptor.Members, static member =>
            Assert.Equal(RelationDtoMemberBindingSource.Explicit, member.BindingSource));
        Assert.Equal(profile.Id, mapper.Descriptor.ProfileId);
        Assert.Equal(profile.Fingerprint, mapper.Descriptor.ProfileFingerprint);
    }

    [Fact]
    public void Compile_InvalidTargetContracts_FailClosedWithAttributableDiagnostics()
    {
        var plan = RelationDtoBenchmarkFixture.SimplePlan;

        AssertCompileFailure<NoUsableConstructorLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.ConstructorUnavailable);
        AssertCompileFailure<AmbiguousConstructorLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.ConstructorUnavailable);
        AssertCompileFailure<MissingRequiredLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.RequiredTargetMemberUnmapped);
        AssertCompileFailure<IncompleteLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.OutputFieldUnmapped);
        AssertCompileFailure<IncompatibleLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.UnsupportedConversion);
        AssertCompileFailure<DuplicateSerializedBindingDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.AmbiguousMemberBinding);
        AssertCompileFailure<NestedLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.UnsupportedConversion);
        AssertCompileFailure<CollectionLoadSummaryDto>(
            plan,
            RelationDtoMapperDiagnosticCodes.UnsupportedConversion);
    }

    [Fact]
    public void Compile_QueryTerminal_IsRejectedWithoutCreatingASecondQueryMaterializer()
    {
        var queryCompilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        var queryPlan = Assert.IsType<CompiledRelationQueryPlan>(queryCompilation.Plan);

        var result = RelationDtoMapperCompiler.Default.Compile<LoadSummaryDto>(queryPlan);

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Mapper);
        Assert.Same(RelationQueryCompiledPlanReference.From(queryPlan), result.Descriptor.PlanReference);
        Assert.Equal(typeof(LoadSummaryDto), result.Descriptor.OutputType);
        Assert.NotEmpty(result.Descriptor.ProfileFingerprint);
        Assert.NotEmpty(result.Descriptor.OptionsFingerprint);
        Assert.NotEmpty(result.Descriptor.CompilationIdentity);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationDtoMapperDiagnosticCodes.UnsupportedTerminal);
        Assert.Equal(RelationDtoMapperDiagnosticPhase.Compilation, diagnostic.Phase);
    }

    [Fact]
    public void Map_MissingCustomer_PreservesIncompleteStatusGapsAndProvenanceWithoutPartialDtos()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateJoinedScenario(
            rowCount: 2,
            RelationDtoFixtureVariant.MissingCustomer);
        var mapper = Compile<LoadSearchDto>(scenario.Plan);

        var result = mapper.Map(
            scenario.Execution,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);

        Assert.Equal(RelationDtoMappingStatus.Incomplete, result.Status);
        Assert.Same(scenario.Execution, result.Execution);
        Assert.Same(scenario.Execution.RequirementGapAnalysis, result.Execution!.RequirementGapAnalysis);
        Assert.Empty(result.Rows);
        Assert.Equal(scenario.Execution.Relation!.Rows.Length, result.FailedRows.Length);
        Assert.NotEmpty(scenario.Execution.RequirementGapAnalysis.Gaps);
        Assert.All(result.FailedRows, failure =>
        {
            Assert.False(failure.Source.IsComplete);
            Assert.NotEmpty(failure.Source.UnresolvedGaps);
            Assert.Contains(
                failure.Diagnostics,
                static diagnostic => diagnostic.Code
                    == RelationDtoMapperDiagnosticCodes.RuntimeFieldConversionFailed);
        });
    }

    [Fact]
    public void Map_FailedCanonicalExecution_RemainsFailedWithoutInventingRows()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateJoinedScenario(
            rowCount: 1,
            RelationDtoFixtureVariant.InvalidCustomerName);
        var mapper = Compile<LoadSearchDto>(scenario.Plan);

        var result = mapper.Map(scenario.Execution);

        Assert.Equal(RelationDtoMappingStatus.Failed, result.Status);
        Assert.Same(scenario.Execution, result.Execution);
        Assert.Empty(result.Rows);
        Assert.Empty(result.FailedRows);
    }

    [Fact]
    public void Map_RowFailurePolicies_AreExplicitAndNeverExposePartialDtos()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 2);
        var malformed = RewriteFirstValue(
            scenario.Execution,
            static value => WithField(value, "Amount", ObservationValue.FromString("not-a-decimal")));
        var mapper = Compile<LoadSummaryDto>(scenario.Plan);

        var strict = mapper.Map(malformed, RelationDtoMappingFailurePolicy.Strict);
        var collected = mapper.Map(malformed, RelationDtoMappingFailurePolicy.CollectDiagnostics);
        var skipped = mapper.Map(malformed, RelationDtoMappingFailurePolicy.SkipInvalidRows);

        Assert.Equal(RelationDtoMappingStatus.Failed, strict.Status);
        Assert.Empty(strict.Rows);
        Assert.NotEmpty(strict.FailedRows);

        Assert.Equal(RelationDtoMappingStatus.Incomplete, collected.Status);
        Assert.Single(collected.Rows);
        Assert.Single(collected.FailedRows);
        Assert.Same(scenario.Execution.Relation!.Rows[1], collected.Rows[0].Source);

        Assert.Equal(RelationDtoMappingStatus.SucceededWithSkippedRows, skipped.Status);
        Assert.Single(skipped.Rows);
        Assert.Single(skipped.FailedRows);
        Assert.All(
            new[] { strict, collected, skipped },
            result => Assert.Contains(
                result.Diagnostics,
                static diagnostic => diagnostic.Code
                    == RelationDtoMapperDiagnosticCodes.RuntimeFieldConversionFailed
                    && diagnostic.Field == FieldPath.FromField("Amount")
                    && diagnostic.RowIndex == 0));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("wrong-kind")]
    public void Map_MalformedRequiredScalar_ProducesStructuredMemberDiagnostic(string variant)
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var malformed = RewriteFirstValue(
            scenario.Execution,
            value => variant switch
            {
                "missing" => WithoutField(value, "Amount"),
                "null" => WithField(value, "Amount", ObservationValue.Null),
                _ => WithField(value, "Amount", ObservationValue.FromBool(true))
            });
        var mapper = Compile<LoadSummaryDto>(scenario.Plan);

        var result = mapper.Map(malformed);

        Assert.Equal(RelationDtoMappingStatus.Failed, result.Status);
        var failure = Assert.Single(result.FailedRows);
        Assert.Same(malformed.Relation!.Rows[0], failure.Source);
        var diagnostic = Assert.Single(
            failure.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationDtoMapperDiagnosticCodes.RuntimeFieldConversionFailed);
        Assert.Equal(FieldPath.FromField("Amount"), diagnostic.Field);
        Assert.Equal(nameof(LoadSummaryDto.Amount), diagnostic.TargetMember);
        Assert.Equal(0, diagnostic.RowIndex);
        Assert.NotNull(diagnostic.Assignment);
        Assert.NotNull(diagnostic.Node);
    }

    [Fact]
    public void Map_PlanRelationAndShapeMismatchesFailClosed()
    {
        var simple = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var joined = RelationDtoBenchmarkFixture.CreateJoinedScenario(rowCount: 1);
        var mapper = Compile<LoadSummaryDto>(simple.Plan);

        var planMismatch = mapper.Map(joined.Execution);
        Assert.Equal(RelationDtoMappingStatus.Failed, planMismatch.Status);
        Assert.Contains(
            planMismatch.Diagnostics,
            static diagnostic => diagnostic.Code == RelationDtoMapperDiagnosticCodes.PlanMismatch);

        var relationMismatchExecution = RelationDtoMapperTestFixture.RewriteRelation(
            simple.Execution,
            static row => row,
            relation: new("different-relation"));
        var relationMismatch = mapper.Map(relationMismatchExecution);
        Assert.Contains(
            relationMismatch.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationDtoMapperDiagnosticCodes.RelationTerminalMismatch);

        var wrongShape = new QualifiedShapeId(new("different-graph"), new("DifferentShape"));
        var shapeMismatchExecution = RelationDtoMapperTestFixture.RewriteRelation(
            simple.Execution,
            row => RelationDtoMapperTestFixture.RewriteValue(row, row.Value, wrongShape),
            shape: wrongShape);
        var shapeMismatch = mapper.Map(shapeMismatchExecution);
        Assert.Contains(
            shapeMismatch.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationDtoMapperDiagnosticCodes.RelationTerminalMismatch);
    }

    [Fact]
    public void Map_EquivalentExactPlanReference_IsAcceptedAcrossCompilationInstances()
    {
        var compiled = RelationDtoMapperTestFixture.CreateNumericWideningScenario();
        var equivalent = RelationDtoMapperTestFixture.CreateNumericWideningScenario();
        Assert.NotSame(compiled.Execution.PlanReference, equivalent.Execution.PlanReference);
        var mapper = Compile<NumericWideningDto>(
            compiled.Plan,
            options: new(RelationDtoNumericConversionPolicy.LosslessWidening));

        var result = mapper.Map(equivalent.Execution);

        Assert.Equal(RelationDtoMappingStatus.Succeeded, result.Status);
        Assert.Equal(1m, Assert.Single(result.Rows).Value.LoadCount);
        Assert.Same(equivalent.Execution, result.Execution);
    }

    [Fact]
    public async Task Map_PhysicalExecutionOverload_PreservesExactPhysicalAndCanonicalResults()
    {
        var scenario = await RelationDtoMapperTestFixture.ExecuteFederatedAsync();
        var mapper = Compile<FederatedLoadSearchDto>(scenario.Plan);

        var result = mapper.Map(scenario.Result);

        Assert.Equal(RelationDtoMappingStatus.Succeeded, result.Status);
        Assert.Same(scenario.Result, result.PhysicalExecution);
        Assert.Same(scenario.Result.Interpretation, result.Execution);
        Assert.Equal(
            [
                new FederatedLoadSearchDto("load-1", "Customer One", "TRUCK-001"),
                new FederatedLoadSearchDto("load-2", "Customer Two", "TRAILER-002")
            ],
            result.Rows.Select(static row => row.Value));
    }

    [Fact]
    public void Map_PhysicalFailureWithoutInterpretation_RemainsAttributable()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        var mapper = Compile<LoadSummaryDto>(scenario.Plan);
        var physical = new Cohesive.Relations.Acquisition.RelationQueryPhysicalExecutionResult(
            Cohesive.Relations.Acquisition.RelationQueryPhysicalExecutionStatus.Failed,
            evidence: null,
            interpretation: null,
            diagnostics:
            [
                new(
                    Cohesive.Relations.Acquisition.RelationQueryPhysicalExecutionDiagnosticCodes.SourceReaderMissing,
                    DiagnosticSeverity.Error,
                    "Reader missing.")
            ]);

        var result = mapper.Map(physical);

        Assert.Equal(RelationDtoMappingStatus.Failed, result.Status);
        Assert.Same(physical, result.PhysicalExecution);
        Assert.Null(result.Execution);
        Assert.Empty(result.Rows);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationDtoMapperDiagnosticCodes.PhysicalInterpretationUnavailable);
    }

    [Fact]
    public async Task Compile_CacheIsConcurrentAndSeparatesProfileOptionsAndClrContract()
    {
        var plan = RelationDtoBenchmarkFixture.SimplePlan;
        var first = Compile<LoadSummaryDto>(plan);
        var second = Compile<LoadSummaryDto>(plan);
        Assert.Same(first, second);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => Compile<LoadSummaryDto>(plan)))
            .ToArray();
        var concurrent = await Task.WhenAll(tasks);
        Assert.All(concurrent, mapper => Assert.Same(first, mapper));

        var profile = new RelationDtoMapperProfile("tests/cache-profile/v2");
        var profiled = Compile<LoadSummaryDto>(plan, profile);
        var widened = Compile<LoadSummaryDto>(
            plan,
            options: new(RelationDtoNumericConversionPolicy.LosslessWidening));
        var writable = Compile<WritableLoadSummaryDto>(plan);

        Assert.NotEqual(first.Descriptor.CompilationIdentity, profiled.Descriptor.CompilationIdentity);
        Assert.NotEqual(first.Descriptor.CompilationIdentity, widened.Descriptor.CompilationIdentity);
        Assert.NotEqual(first.Descriptor.CompilationIdentity, writable.Descriptor.CompilationIdentity);
    }

    [Fact]
    public void Compile_ProfileFingerprintUsesStructurallyFramedTokens()
    {
        var first = new RelationDtoMapperProfile(
            "x",
            [new(FieldPath.FromField("F"), "M\n2")],
            RelationDtoMemberConvention.ExactMemberName);
        var formerlyColliding = new RelationDtoMapperProfile(
            "x\n1\nF\0M",
            memberConvention: RelationDtoMemberConvention.SerializedNameThenExactMemberName);

        Assert.NotEqual(first.Fingerprint, formerlyColliding.Fingerprint);
    }

    [Fact]
    public void CompileAndMap_LosslessNumericWidening_IsExplicitAndExecutable()
    {
        var scenario = RelationDtoMapperTestFixture.CreateNumericWideningScenario();

        var exact = RelationDtoMapperCompiler.Default.Compile<NumericWideningDto>(scenario.Plan);
        Assert.False(exact.IsSuccessful);
        Assert.Contains(
            exact.Diagnostics,
            static diagnostic => diagnostic.Code == RelationDtoMapperDiagnosticCodes.UnsupportedConversion
                && diagnostic.Field == LoadCustomerRelationFixture.AggregateLoadCountPath);

        var mapper = Compile<NumericWideningDto>(
            scenario.Plan,
            options: new(RelationDtoNumericConversionPolicy.LosslessWidening));
        var result = mapper.Map(scenario.Execution);

        Assert.Equal(RelationDtoMappingStatus.Succeeded, result.Status);
        var row = Assert.Single(result.Rows).Value;
        Assert.Equal("Available", row.CustomerName);
        Assert.Equal(12.5m, row.TotalAmount);
        Assert.Equal(1m, row.LoadCount);
    }

    [Fact]
    public void Map_OptionalFieldMissing_UsesNullableClrDestination()
    {
        var scenario = RelationDtoMapperTestFixture.CreateNumericWideningScenario();
        var execution = RewriteFirstValue(
            scenario.Execution,
            value => WithoutField(value, LoadCustomerRelationFixture.AggregateCustomerNameFieldName));
        var mapper = Compile<NumericWideningDto>(
            scenario.Plan,
            options: new(RelationDtoNumericConversionPolicy.LosslessWidening));

        var result = mapper.Map(execution);

        Assert.Equal(RelationDtoMappingStatus.Succeeded, result.Status);
        Assert.Null(Assert.Single(result.Rows).Value.CustomerName);
        Assert.Empty(result.FailedRows);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CompileAndMap_AreCultureInvariant()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;
            var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
            var profile = new RelationDtoMapperProfile("tests/turkish-culture/v1");

            var mapper = Compile<LoadSummaryDto>(scenario.Plan, profile);
            var mapped = Assert.Single(mapper.Map(scenario.Execution).Rows).Value;

            Assert.Equal(scenario.Expected[0], mapped);
            Assert.Equal(profile.Fingerprint, mapper.Descriptor.ProfileFingerprint);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    [Fact]
    public void Map_PreCanceledBatch_PropagatesCancellation()
    {
        var scenario = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 32);
        var mapper = Compile<LoadSummaryDto>(scenario.Plan);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => mapper.Map(
            scenario.Execution,
            cancellationToken: cancellation.Token));
    }

    static CompiledRelationDtoMapper<TOutput> Compile<TOutput>(
        Cohesive.Relations.Compilation.CompiledRelationQueryPlan plan,
        RelationDtoMapperProfile? profile = null,
        RelationDtoMapperCompilationOptions? options = null)
    {
        var result = RelationDtoMapperCompiler.Default.Compile<TOutput>(plan, profile, options);
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var mapper = Assert.IsType<CompiledRelationDtoMapper<TOutput>>(result.Mapper);
        Assert.Same(result.Descriptor, mapper.Descriptor.Compilation);
        return mapper;
    }

    static void AssertCompileFailure<TOutput>(
        Cohesive.Relations.Compilation.CompiledRelationQueryPlan plan,
        string expectedCode)
    {
        var result = RelationDtoMapperCompiler.Default.Compile<TOutput>(plan);

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Mapper);
        Assert.Same(RelationQueryCompiledPlanReference.From(plan), result.Descriptor.PlanReference);
        Assert.Equal(typeof(TOutput), result.Descriptor.OutputType);
        Assert.NotEmpty(result.Descriptor.ProfileFingerprint);
        Assert.NotEmpty(result.Descriptor.OptionsFingerprint);
        Assert.NotEmpty(result.Descriptor.CompilationIdentity);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == expectedCode);
        Assert.Equal(RelationDtoMapperDiagnosticPhase.Compilation, diagnostic.Phase);
        Assert.NotNull(diagnostic.Relation);
        Assert.NotNull(diagnostic.Shape);
    }

    static RelationQueryExecutionResult RewriteFirstValue(
        RelationQueryExecutionResult execution,
        Func<ObservationValue, ObservationValue> rewrite)
    {
        var first = true;
        return RelationDtoMapperTestFixture.RewriteRelation(
            execution,
            row =>
            {
                if (!first)
                    return row;
                first = false;
                return RelationDtoMapperTestFixture.RewriteValue(row, rewrite(row.Value));
            });
    }

    static ObservationValue WithField(
        ObservationValue value,
        string field,
        ObservationValue replacement)
    {
        var fields = value.Fields!.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        fields[field] = replacement;
        return ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(fields));
    }

    static ObservationValue WithoutField(ObservationValue value, string field)
    {
        var fields = value.Fields!.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        fields.Remove(field);
        return ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(fields));
    }
}

sealed class WritableLoadSummaryDto
{
    public required string Id { get; init; }

    public required string Status { get; init; }

    public decimal Amount { get; init; }
}

sealed record OptionalExtraLoadSummaryDto(
    string Id,
    string Status,
    decimal Amount,
    string? Label = "unmapped-default");

sealed record JsonLoadSummaryDto(
    [property: JsonPropertyName("Id")] string LoadIdentifier,
    string Status,
    decimal Amount);

sealed record ExplicitLoadSummaryDto(
    [property: JsonPropertyName("WrongId")] string LoadIdentifier,
    [property: JsonPropertyName("WrongStatus")] string State,
    [property: JsonPropertyName("WrongAmount")] decimal Total);

sealed class MissingRequiredLoadSummaryDto
{
    public required string Id { get; init; }

    public required string Status { get; init; }

    public decimal Amount { get; init; }

    public required string RequiredButAbsent { get; init; }
}

sealed record IncompatibleLoadSummaryDto(string Id, string Status, DateOnly Amount);

sealed record IncompleteLoadSummaryDto(string Id, string Status);

sealed record DuplicateSerializedBindingDto(
    [property: JsonPropertyName("Id")] string First,
    [property: JsonPropertyName("Id")] string Second,
    string Status,
    decimal Amount);

sealed record NestedLoadSummaryDto(string Id, NestedStatus Status, decimal Amount);

sealed record NestedStatus(string Value);

sealed record CollectionLoadSummaryDto(string Id, IReadOnlyList<string> Status, decimal Amount);

sealed record NumericWideningDto(string? CustomerName, decimal TotalAmount, decimal LoadCount);

sealed class NoUsableConstructorLoadSummaryDto
{
    NoUsableConstructorLoadSummaryDto(string id, string status, decimal amount)
    {
        Id = id;
        Status = status;
        Amount = amount;
    }

    public string Id { get; }

    public string Status { get; }

    public decimal Amount { get; }
}

sealed class AmbiguousConstructorLoadSummaryDto
{
    public AmbiguousConstructorLoadSummaryDto(string id, string status, decimal amount)
    {
        Id = id;
        Status = status;
        Amount = amount;
    }

    public AmbiguousConstructorLoadSummaryDto(string id, string status, decimal? amount)
    {
        Id = id;
        Status = status;
        Amount = amount;
    }

    public string Id { get; }

    public string Status { get; }

    public decimal? Amount { get; }
}
