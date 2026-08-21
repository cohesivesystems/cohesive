using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionLowererTests
{
    static readonly QualifiedShapeId LoadShape = new(new("expression-lowerer/v1"), new("Load"));
    static readonly QualifiedShapeId CustomerShape = new(new("expression-lowerer/v1"), new("Customer"));
    static readonly QualifiedShapeId EquipmentShape = new(new("expression-lowerer/v1"), new("Equipment"));

    [Fact]
    public void MultipleBindingsParameterMarkersAndCorrelatedAny_LowerWithoutClrArtifacts()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var customer = author.Source<Customer>(CustomerShape);
        var equipment = author.Source<Equipment>(EquipmentShape);
        var customerName = new ParameterMarker<string>(new("customer-name"));
        var stopLocation = new ParameterMarker<string>(new("stop-location"));
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, Customer, Equipment, bool>> predicate =
            (sourceLoad, sourceCustomer, sourceEquipment) =>
                sourceCustomer.Profile.Name == customerName.Value
                && sourceEquipment.Type == "Reefer"
                && sourceLoad.Stops.Any(stop =>
                    stop.Location.Code == stopLocation.Value
                    && stop.Sequence > 0);

        var result = lowerer.LowerValue(
            predicate,
            [load.Binding, customer.Binding, equipment.Binding],
            sourceReference: "filter/search");

        var lowered = result.RequireValue();
        Assert.Empty(result.Diagnostics);
        Assert.Equal(RelationQueryExpressionLowerer.Producer, lowered.Source.Producer);
        Assert.Equal("filter/search/body", lowered.Source.Reference);

        var fields = Descendants(lowered.Value).OfType<FieldExpr>().ToArray();
        Assert.Contains(
            fields,
            field => field.Binding == customer.Binding.Id
                     && field.Path == FieldPath.Parse("customer_profile.customer_name"));
        Assert.Contains(
            fields,
            field => field.Binding == equipment.Binding.Id
                     && field.Path == FieldPath.FromField("equipment_type"));
        Assert.Contains(
            fields,
            field => field.Binding == load.Binding.Id
                     && field.Path == FieldPath.FromField("load_stops"));
        Assert.Contains(
            fields,
            field => field.Binding is null
                     && field.Path == FieldPath.Parse("item.stop_location.location_code"));
        Assert.Contains(
            fields,
            field => field.Binding is null
                     && field.Path == FieldPath.Parse("item.stop_sequence"));

        var parameters = Descendants(lowered.Value).OfType<ParameterExpr>().ToArray();
        Assert.Equal(["customer-name", "stop-location"], parameters.Select(static parameter => parameter.Parameter));
        var any = Assert.Single(
            Descendants(lowered.Value).OfType<CallExpr>(),
            static call => call.Function == ExprFunctionNames.Any);
        Assert.Equal(2, any.Arguments.Length);
        AssertNoClrAuthoringArtifacts(lowered);
    }

    [Fact]
    public void NestedMemberInitializerAndRecordConstructor_LowerToFlattenedProjectionAssignments()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var customer = author.Source<Customer>(CustomerShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, Customer, LoadDocument>> projection =
            (sourceLoad, sourceCustomer) => new(
                sourceLoad.Id,
                new CustomerDocument
                {
                    Name = sourceCustomer.Profile.Name,
                    Type = sourceCustomer.Type
                });

        var result = lowerer.LowerProjection(
            projection,
            [load.Binding, customer.Binding],
            sourceReference: "projection/load-document");

        var lowered = result.RequireValue();
        Assert.Equal(3, lowered.Assignments.Length);
        Assert.Equal(
            ["document_id", "document_customer.customer_name", "document_customer.customer_type"],
            lowered.Assignments.Select(static assignment => assignment.Target.ToString()));
        Assert.Equal(
            FieldPath.FromField("load_id"),
            Assert.IsType<FieldExpr>(lowered.Assignments[0].Value).Path);
        Assert.Equal(
            FieldPath.Parse("customer_profile.customer_name"),
            Assert.IsType<FieldExpr>(lowered.Assignments[1].Value).Path);
        Assert.All(lowered.Assignments, static assignment => Assert.NotNull(assignment.AssignmentSource));
        Assert.All(lowered.Assignments, static assignment => Assert.NotNull(assignment.ValueSource));
        AssertNoClrAuthoringArtifacts(lowered);
    }

    [Fact]
    public void PortableFunctionsConditionalAndSafeBoxing_LowerToCanonicalExpressions()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, bool>> predicate = sourceLoad =>
            sourceLoad.Tags.Contains("priority")
            && sourceLoad.Status.EndsWith("Ready", StringComparison.Ordinal);
        Expression<Func<Load, object>> boxed = sourceLoad =>
            sourceLoad.Status == "Ready" ? sourceLoad.Id : "unknown";

        var loweredPredicate = lowerer.LowerValue(
            predicate,
            [load.Binding],
            sourceReference: "filter/functions").RequireValue();
        var loweredBoxed = lowerer.LowerValue(
            boxed,
            [load.Binding],
            sourceReference: "projection/conditional").RequireValue();

        Assert.Contains(
            Descendants(loweredPredicate.Value).OfType<CallExpr>(),
            static call => call.Function == ExprFunctionNames.Contains);
        Assert.Contains(
            Descendants(loweredPredicate.Value).OfType<CallExpr>(),
            static call => call.Function == ExprFunctionNames.EndsWith);
        Assert.IsType<ConditionalExpr>(loweredBoxed.Value);
    }

    [Fact]
    public void FrameworkDecimalOperator_LowersWithoutTreatingItAsArbitraryUserCode()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var minimumAmount = new ParameterMarker<decimal>(new("minimum-amount"));
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, bool>> predicate = sourceLoad => sourceLoad.Amount >= minimumAmount.Value;

        var lowered = lowerer.LowerValue(
            predicate,
            [load.Binding],
            sourceReference: "filter/minimum-amount").RequireValue();

        var comparison = Assert.IsType<BinaryExpr>(lowered.Value);
        Assert.Equal(BinaryOperator.Ge, comparison.Operator);
        Assert.Equal(
            FieldPath.FromField("load_amount"),
            Assert.IsType<FieldExpr>(comparison.Left).Path);
        Assert.Equal("minimum-amount", Assert.IsType<ParameterExpr>(comparison.Right).Parameter);
    }

    [Fact]
    public void UnsupportedCaptureAndMethod_ReturnActionableDiagnosticsWithoutExecutingUserCode()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        var captured = new ThrowingCapturedValue();
        Expression<Func<Load, bool>> capturedPredicate = sourceLoad => sourceLoad.Status == captured.Value;
        Expression<Func<Load, bool>> methodPredicate = sourceLoad => sourceLoad.Status.StartsWith("R");

        var captureResult = lowerer.LowerValue(
            capturedPredicate,
            [load.Binding],
            sourceReference: "filter/capture");
        var methodResult = lowerer.LowerValue(
            methodPredicate,
            [load.Binding],
            sourceReference: "filter/method");

        Assert.False(captureResult.IsSuccess);
        var capture = Assert.Single(captureResult.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.CapturedValueUnsupported, capture.Code);
        Assert.Equal(DiagnosticSeverity.Error, capture.Severity);
        Assert.Equal("body/right", capture.ExpressionPath);
        Assert.Equal("filter/capture", capture.SourceReference);
        Assert.NotNull(capture.Symbol);
        Assert.NotNull(capture.Suggestion);
        Assert.Equal(0, captured.ReadCount);

        Assert.False(methodResult.IsSuccess);
        var method = Assert.Single(methodResult.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.MethodUnsupported, method.Code);
        Assert.Equal("body", method.ExpressionPath);
        Assert.Contains(nameof(string.StartsWith), method.Symbol, StringComparison.Ordinal);
        var exception = Assert.Throws<RelationQueryExpressionAuthoringException>(methodResult.RequireValue);
        Assert.Equal(methodResult.Diagnostics, exception.Diagnostics);
    }

    [Fact]
    public void CultureSensitiveEndsWithAndLossyConversion_FailClosed()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, bool>> suffix = sourceLoad => sourceLoad.Status.EndsWith("Ready");
        Expression<Func<Load, long>> conversion = sourceLoad => sourceLoad.Sequence;

        var suffixResult = lowerer.LowerValue(
            suffix,
            [load.Binding],
            sourceReference: "filter/suffix");
        var conversionResult = lowerer.LowerValue(
            conversion,
            [load.Binding],
            sourceReference: "projection/conversion");

        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            Assert.Single(suffixResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.ConversionUnsupported,
            Assert.Single(conversionResult.Diagnostics).Code);
    }

    [Fact]
    public void HighPrecisionDecimalLiteral_LowersWithoutDoublePrecisionLoss()
    {
        const decimal Expected = 0.1234567890123456789012345678m;
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, decimal>> literal = _ => Expected;

        var lowered = lowerer.LowerValue(
            literal,
            [load.Binding],
            sourceReference: "literal/high-precision-decimal").RequireValue();

        var literalValue = Assert.IsType<LiteralExpr>(lowered.Value);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Decimal), literalValue.Type);
        Assert.Equal(ObservationValue.FromDecimal(Expected), literalValue.Value);
        Assert.Equal(ObservationValueKind.Decimal, literalValue.Value.Kind);
    }

    [Fact]
    public void IntegralLiterals_LowerWithTheirExactPortableClrTypes()
    {
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        (LambdaExpression Expression, ScalarTypeKind Kind)[] cases =
        [
            ((Expression<Func<int>>)(() => 1), ScalarTypeKind.Int32),
            ((Expression<Func<long>>)(() => 1L), ScalarTypeKind.Int64)
        ];

        foreach (var (expression, kind) in cases)
        {
            var lowered = lowerer.LowerValue(
                expression,
                sourceReference: $"literal/{kind}").RequireValue();

            var literalValue = Assert.IsType<LiteralExpr>(lowered.Value);
            Assert.Equal(new ScalarTypeRef(kind), literalValue.Type);
            Assert.Equal(ObservationValueKind.Int64, literalValue.Value.Kind);
            Assert.Equal(1, literalValue.Value.Int64);
        }
    }

    [Fact]
    public void DefinedEnumMemberLiteral_LowersWithItsPortableEnumType()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<NormalizationSource>();
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<NormalizationSource, NormalizationSourceKind>> literal = _ =>
            NormalizationSourceKind.HumanFeedback;
        Expression<Func<NormalizationSource, NormalizationSourceFlags>> unnamedCombination = _ =>
            NormalizationSourceFlags.Imported | NormalizationSourceFlags.Generated;
        Expression<Func<NormalizationSource, AmbiguousNormalizationSourceKind>> ambiguousAlias = _ =>
            AmbiguousNormalizationSourceKind.SchemaMapping;

        var lowered = lowerer.LowerValue(
            literal,
            [source.Binding],
            sourceReference: "literal/enum-member").RequireValue();
        var unsupported = lowerer.LowerValue(
            unnamedCombination,
            [source.Binding],
            sourceReference: "literal/unnamed-enum-combination");
        var ambiguous = lowerer.LowerValue(
            ambiguousAlias,
            [source.Binding],
            sourceReference: "literal/ambiguous-enum-alias");

        var enumLiteral = Assert.IsType<LiteralExpr>(lowered.Value);
        var enumType = Assert.IsType<EnumTypeRef>(enumLiteral.Type);
        Assert.Equal(nameof(NormalizationSourceKind), enumType.Name);
        Assert.Equal(
            ObservationValue.FromString(nameof(NormalizationSourceKind.HumanFeedback)),
            enumLiteral.Value);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.LiteralUnsupported,
            Assert.Single(unsupported.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.LiteralUnsupported,
            Assert.Single(ambiguous.Diagnostics).Code);
    }

    [Fact]
    public void ExactEnumComparisons_LowerFieldsAndNamedMembers()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, bool>> namedMember = source =>
            source.ProcessingStatus == LoadStatus.Complete;
        Expression<Func<Load, bool>> field = source =>
            source.ProcessingStatus != source.ExpectedProcessingStatus;

        var namedMemberResult = lowerer.LowerValue(
            namedMember,
            [load.Binding],
            sourceReference: "enum-comparison/named-member").RequireValue();
        var fieldResult = lowerer.LowerValue(
            field,
            [load.Binding],
            sourceReference: "enum-comparison/field").RequireValue();

        var namedMemberComparison = Assert.IsType<BinaryExpr>(namedMemberResult.Value);
        Assert.Equal(BinaryOperator.Eq, namedMemberComparison.Operator);
        Assert.Equal(
            FieldPath.FromField(nameof(Load.ProcessingStatus)),
            Assert.IsType<FieldExpr>(namedMemberComparison.Left).Path);
        var enumLiteral = Assert.IsType<LiteralExpr>(namedMemberComparison.Right);
        Assert.Equal(nameof(LoadStatus), Assert.IsType<EnumTypeRef>(enumLiteral.Type).Name);
        Assert.Equal(ObservationValue.FromString(nameof(LoadStatus.Complete)), enumLiteral.Value);

        var fieldComparison = Assert.IsType<BinaryExpr>(fieldResult.Value);
        Assert.Equal(BinaryOperator.Ne, fieldComparison.Operator);
        Assert.Equal(
            FieldPath.FromField(nameof(Load.ExpectedProcessingStatus)),
            Assert.IsType<FieldExpr>(fieldComparison.Right).Path);
    }

    [Fact]
    public void UndefinedAndAmbiguousEnumComparisons_FailClosed()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var source = author.Source<NormalizationSource>();
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, bool>> undefined = value =>
            value.ProcessingStatus == (LoadStatus)17;
        Expression<Func<NormalizationSource, bool>> unnamedFlags = value =>
            value.Flags == (NormalizationSourceFlags.Imported | NormalizationSourceFlags.Generated);
        Expression<Func<NormalizationSource, bool>> ambiguousAlias = value =>
            value.AmbiguousKind == AmbiguousNormalizationSourceKind.SchemaMapping;

        var results = new[]
        {
            lowerer.LowerValue(undefined, [load.Binding], "enum-comparison/undefined"),
            lowerer.LowerValue(unnamedFlags, [source.Binding], "enum-comparison/unnamed-flags"),
            lowerer.LowerValue(ambiguousAlias, [source.Binding], "enum-comparison/ambiguous-alias")
        };

        Assert.All(results, static result => Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.LiteralUnsupported,
            Assert.Single(result.Diagnostics).Code));
    }

    [Fact]
    public void DateTimeOffsetEqualsExact_LowersToCanonicalRepresentationEquality()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, bool>> exact = source =>
            source.OccurredAt.EqualsExact(source.ExpectedOccurredAt);

        var lowered = lowerer.LowerValue(
            exact,
            [load.Binding],
            sourceReference: "instant-comparison/exact").RequireValue();

        var comparison = Assert.IsType<BinaryExpr>(lowered.Value);
        Assert.Equal(BinaryOperator.Eq, comparison.Operator);
        Assert.Equal(
            FieldPath.FromField("load_occurred_at"),
            Assert.IsType<FieldExpr>(comparison.Left).Path);
        Assert.Equal(
            FieldPath.FromField(nameof(Load.ExpectedOccurredAt)),
            Assert.IsType<FieldExpr>(comparison.Right).Path);
    }

    [Fact]
    public void GuardedOptionalEvidence_ComposesExactInstantAndEnumComparisons()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<OptionalEvidence>();
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<OptionalEvidence, bool>> exact = input =>
            input.LastObservation != null
            && input.LastObservation.ObservedAt.EqualsExact(input.ExpectedObservation.ObservedAt)
            && input.LastObservation.Status == input.ExpectedObservation.Status;

        var lowered = lowerer.LowerValue(
            exact,
            [source.Binding],
            sourceReference: "optional-evidence/exact").RequireValue();

        Assert.Equal(3, Descendants(lowered.Value).OfType<BinaryExpr>().Count(
            static expression => expression.Operator is BinaryOperator.Eq or BinaryOperator.Ne));
        Assert.Equal(5, Descendants(lowered.Value).OfType<FieldExpr>().Count());
    }

    [Fact]
    public void AggregateNumericLiteralTypes_SurviveCanonicalJsonNormalization()
    {
        var author = RelationQuery.Expression();
        var rows = author.Source<LiteralAggregateRow>();
        var aggregate = author.Aggregate<SourceQueryNode, LiteralAggregateResult>(
            rows.Node,
            builder => builder
                .Value(
                    result => result.DecimalSum,
                    AggregateOperator.Sum,
                    (LiteralAggregateRow _) => 1m,
                    rows.Binding)
                .Value(
                    result => result.DecimalMinimum,
                    AggregateOperator.Min,
                    (LiteralAggregateRow _) => 1m,
                    rows.Binding)
                .Value(
                    result => result.IntegerSum,
                    AggregateOperator.Sum,
                    (LiteralAggregateRow _) => 1,
                    rows.Binding));
        var query = author.BuildQuery(
            new QueryId("typed-literal-aggregates"),
            new QueryName("TypedLiteralAggregates"),
            author.Aggregation(aggregate));

        var json = RelationQueryJsonSerializer.Serialize(query.CreateDocument(), indented: false);
        var roundTripped = RelationQueryJsonSerializer.Deserialize(json);
        var definition = Assert.IsType<QueryDefinition>(roundTripped.Definition);
        var aggregateNode = Assert.Single(definition.Body.Nodes.OfType<AggregateQueryNode>());
        var literals = aggregateNode.Aggregates
            .ToDictionary(
                static assignment => assignment.Target.ToString(),
                static assignment => Assert.IsType<LiteralExpr>(assignment.Value),
                StringComparer.Ordinal);

        Assert.Equal(
            new ScalarTypeRef(ScalarTypeKind.Decimal),
            literals[nameof(LiteralAggregateResult.DecimalSum)].Type);
        Assert.Equal(
            new ScalarTypeRef(ScalarTypeKind.Decimal),
            literals[nameof(LiteralAggregateResult.DecimalMinimum)].Type);
        Assert.Equal(
            new ScalarTypeRef(ScalarTypeKind.Int32),
            literals[nameof(LiteralAggregateResult.IntegerSum)].Type);
        Assert.All(
            literals.Values,
            static literalValue => Assert.Equal(ObservationValueKind.Int64, literalValue.Value.Kind));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            definition,
            author.ShapeDocuments.Select(static document => document.Graph));

        Assert.True(
            analysis.IsValid,
            string.Join(
                Environment.NewLine,
                analysis.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
    }

    [Fact]
    public void LiteralsWithoutCanonicalJsonEncoding_FailClosed()
    {
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        object[] unsupported =
        [
            ObservationValue.FromBytes(new byte[] { 1, 2, 3 }),
            ObservationValue.FromArray([ObservationValue.FromTimeSpan(TimeSpan.FromSeconds(1))]),
            DateTimeOffset.UnixEpoch,
            new DateOnly(2026, 7, 17),
            new TimeOnly(12, 34, 56),
            TimeSpan.FromMinutes(5),
            ObservationValue.Undefined,
            ObservationValue.FromDouble(double.PositiveInfinity)
        ];

        foreach (var value in unsupported)
        {
            var expression = Expression.Lambda(Expression.Constant(value, value.GetType()));
            var result = lowerer.LowerValue(expression, sourceReference: "literal/non-canonical");

            Assert.False(result.IsSuccess);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(RelationQueryExpressionDiagnosticCodes.LiteralUnsupported, diagnostic.Code);
            Assert.Equal("body", diagnostic.ExpressionPath);
        }
    }

    [Fact]
    public void Coalesce_LowersOnlyWhenCanonicalSemanticsAreExact()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, string>> exact = sourceLoad => (string?)null ?? sourceLoad.Status;
        Expression<Func<Load, string>> presenceDependent = sourceLoad => sourceLoad.Status ?? "unknown";
        var normalization = author.Source<NormalizationSource>();
        Expression<Func<NormalizationSource, string>> requiredNullable = source =>
            source.Metadata.CorrelationId ?? "unknown";

        var lowered = lowerer.LowerValue(
            exact,
            [load.Binding],
            sourceReference: "projection/exact-coalesce").RequireValue();
        var unsupported = lowerer.LowerValue(
            presenceDependent,
            [load.Binding],
            sourceReference: "projection/presence-coalesce");
        var loweredRequiredNullable = lowerer.LowerValue(
            requiredNullable,
            [normalization.Binding],
            sourceReference: "projection/required-nullable-coalesce").RequireValue();

        Assert.Equal(
            FieldPath.FromField("load_status"),
            Assert.IsType<FieldExpr>(lowered.Value).Path);
        var requiredNullableConditional = Assert.IsType<ConditionalExpr>(loweredRequiredNullable.Value);
        Assert.Equal(
            FieldPath.Parse($"{nameof(NormalizationSource.Metadata)}.{nameof(NormalizationMetadata.CorrelationId)}"),
            Assert.IsType<FieldExpr>(requiredNullableConditional.IfTrue).Path);
        var diagnostic = Assert.Single(unsupported.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.OperatorUnsupported, diagnostic.Code);
        Assert.Contains("presence/null test", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Suggestion);
    }

    [Fact]
    public void InexactCountEmptyAggregateAndScalarSequenceForms_FailClosed()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, int>> length = source => source.Stops.Length;
        Expression<Func<Load, int>> count = source => source.Stops.Count();
        Expression<Func<Load, int>> minimum = source => source.Numbers.Min();
        Expression<Func<Load, bool>> bytesAny = source => source.Payload.Any(value => value > 0);

        var results = new[]
        {
            lowerer.LowerValue(length, [load.Binding], "inexact/length"),
            lowerer.LowerValue(count, [load.Binding], "inexact/count"),
            lowerer.LowerValue(minimum, [load.Binding], "inexact/min"),
            lowerer.LowerValue(bytesAny, [load.Binding], "inexact/bytes-any")
        };

        Assert.All(results, static result => Assert.False(result.IsSuccess));
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            Assert.Single(results[0].Diagnostics).Code);
        Assert.All(
            results.Skip(1),
            static result => Assert.Equal(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                Assert.Single(result.Diagnostics).Code));
    }

    [Fact]
    public void EagerSelectMaterializationAndLongCount_LowerToCanonicalSequenceFunctions()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<NormalizationSource>();
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<NormalizationSource, NormalizationOutput>> projection = input => new()
        {
            Candidates = input.Candidates
                .Select(candidate => new NormalizationCandidateOutput
                {
                    Id = candidate.Id,
                    Selected = candidate.Id == input.SelectedCandidateId
                })
                .ToArray(),
            CandidateCount = input.Candidates.LongCount()
        };

        var lowered = lowerer.LowerProjection(
            projection,
            [source.Binding],
            "normalization/eager-sequence").RequireValue();

        var candidates = Assert.Single(
            lowered.Assignments,
            assignment => assignment.Target == FieldPath.FromField(nameof(NormalizationOutput.Candidates)));
        var select = Assert.IsType<CallExpr>(candidates.Value);
        Assert.Equal(ExprFunctionNames.Select, select.Function);
        Assert.Equal(2, select.Arguments.Length);
        Assert.Equal(
            FieldPath.FromField(nameof(NormalizationSource.Candidates)),
            Assert.IsType<FieldExpr>(select.Arguments[0]).Path);
        Assert.Equal(
            ExprFunctionNames.Object,
            Assert.IsType<CallExpr>(select.Arguments[1]).Function);

        var candidateCount = Assert.Single(
            lowered.Assignments,
            assignment => assignment.Target == FieldPath.FromField(nameof(NormalizationOutput.CandidateCount)));
        var count = Assert.IsType<CallExpr>(candidateCount.Value);
        Assert.Equal(ExprFunctionNames.Count, count.Function);
        Assert.Single(count.Arguments);
    }

    [Fact]
    public void GuardedNullableValue_LowersOnlyInsideTheProvenNonNullBranch()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<NormalizationSource>();
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<NormalizationSource, NormalizationOutput>> guarded = input => new()
        {
            Candidates = new NormalizationCandidateOutput[] { },
            CandidateCount = input.Candidates.LongCount(),
            Signals = input.Signals.HasValue
                ? new NormalizationSignals
                {
                    CandidateCount = input.Signals!.Value.CandidateCount != 0
                        ? input.Signals.Value.CandidateCount
                        : input.Candidates.LongCount(),
                    ModelVersion = input.Signals.Value.ModelVersion
                }
                : new NormalizationSignals
                {
                    CandidateCount = input.Candidates.LongCount(),
                    ModelVersion = null
                }
        };
        Expression<Func<NormalizationSource, long>> unguarded = input =>
            input.Signals!.Value.CandidateCount;

        var lowered = lowerer.LowerProjection(
            guarded,
            [source.Binding],
            "normalization/guarded-nullable").RequireValue();
        var unsupported = lowerer.LowerValue(
            unguarded,
            [source.Binding],
            "normalization/unguarded-nullable");

        var signals = Assert.Single(
            lowered.Assignments,
            assignment => assignment.Target == FieldPath.FromField(nameof(NormalizationOutput.Signals)));
        var conditional = Assert.IsType<ConditionalExpr>(signals.Value);
        var hasValue = Assert.IsType<BinaryExpr>(conditional.Test);
        Assert.Equal(BinaryOperator.Ne, hasValue.Operator);
        Assert.Equal(
            FieldPath.FromField(nameof(NormalizationSource.Signals)),
            Assert.IsType<FieldExpr>(hasValue.Left).Path);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            Assert.Single(unsupported.Diagnostics).Code);
    }

    [Fact]
    public void LazySelectWithoutExplicitMaterialization_RemainsRejected()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<NormalizationSource>();
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<NormalizationSource, IEnumerable<string>>> lazy = input =>
            input.Candidates.Select(candidate => candidate.Id);

        var result = lowerer.LowerValue(
            lazy,
            [source.Binding],
            "normalization/lazy-select");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.MethodUnsupported, diagnostic.Code);
        Assert.Contains("lazy", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullableReceiverAndNonCanonicalMembershipEquality_FailClosed()
    {
        var author = RelationQuery.Expression();
        var nullable = author.Source<NullableLoad>();
        var customEquality = author.Source<CustomEqualitySource>();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<NullableLoad, bool>> nullableReceiver = source => source.Customer!.Name == "Acme";
        Expression<Func<NullableLoad, bool>> nullableScalarReceiver = source => source.Customer!.Age == 0;
        Expression<Func<CustomEqualitySource, bool>> overloadedNull = source => source.Value != null;
        Expression<Func<Load, bool>> temporalMembership = source =>
            source.Instants.Contains(source.OccurredAt);

        var receiverResult = lowerer.LowerValue(
            nullableReceiver,
            [nullable.Binding],
            "inexact/nullable-receiver");
        var scalarReceiverResult = lowerer.LowerValue(
            nullableScalarReceiver,
            [nullable.Binding],
            "inexact/nullable-scalar-receiver");
        var overloadedNullResult = lowerer.LowerValue(
            overloadedNull,
            [customEquality.Binding],
            "inexact/overloaded-null-comparison");
        var membershipResult = lowerer.LowerValue(
            temporalMembership,
            [load.Binding],
            "inexact/temporal-membership");

        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            Assert.Single(receiverResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            Assert.Single(scalarReceiverResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
            Assert.Single(overloadedNullResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            Assert.Single(membershipResult.Diagnostics).Code);
    }

    [Fact]
    public void NullableSequenceItem_CustomComparerAndFloatingMembership_FailClosed()
    {
        var author = RelationQuery.Expression();
        var nullable = author.Source<NullableLoad>();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<NullableLoad, bool>> nullableItem = source =>
            source.Customers.Any(customer => customer!.Name == "Acme");
        Expression<Func<Load, bool>> comparerBearing = source =>
            Enumerable.Contains(source.ComparerTags, "priority");
        Expression<Func<Load, bool>> floatingPoint = source =>
            source.Measurements.Contains(0.1d);

        var nullableItemResult = lowerer.LowerValue(
            nullableItem,
            [nullable.Binding],
            "inexact/nullable-item");
        var comparerResult = lowerer.LowerValue(
            comparerBearing,
            [load.Binding],
            "inexact/custom-comparer");
        var floatingPointResult = lowerer.LowerValue(
            floatingPoint,
            [load.Binding],
            "inexact/floating-membership");

        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            Assert.Single(nullableItemResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            Assert.Single(comparerResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            Assert.Single(floatingPointResult.Diagnostics).Code);
    }

    [Fact]
    public void CarrierDependentScalarComparisons_FailClosed()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        var loadId = new ParameterMarker<Guid>(new("load-id"));
        var serviceDate = new ParameterMarker<DateOnly>(new("service-date"));
        var serviceTime = new ParameterMarker<TimeOnly>(new("service-time"));
        var status = new ParameterMarker<LoadStatus>(new("load-status"));
        Expression<Func<Load, bool>> guid = source => source.ExternalId == loadId.Value;
        Expression<Func<Load, bool>> date = source => source.ServiceDate == serviceDate.Value;
        Expression<Func<Load, bool>> time = source => source.ServiceTime == serviceTime.Value;
        Expression<Func<Load, bool>> enumeration = source => source.ProcessingStatus == status.Value;
        Expression<Func<Load, bool>> instantEquality = source => source.OccurredAt == source.ExpectedOccurredAt;

        var results = new[]
        {
            lowerer.LowerValue(guid, [load.Binding], "inexact/guid-equality"),
            lowerer.LowerValue(date, [load.Binding], "inexact/date-equality"),
            lowerer.LowerValue(time, [load.Binding], "inexact/time-equality"),
            lowerer.LowerValue(enumeration, [load.Binding], "inexact/enum-equality"),
            lowerer.LowerValue(instantEquality, [load.Binding], "inexact/instant-equality")
        };

        Assert.All(results, static result =>
        {
            Assert.False(result.IsSuccess);
            var code = Assert.Single(result.Diagnostics).Code;
            Assert.True(
                code is RelationQueryExpressionDiagnosticCodes.OperatorUnsupported
                    or RelationQueryExpressionDiagnosticCodes.ConversionUnsupported,
                $"Unexpected diagnostic code '{code}'.");
        });
    }

    [Fact]
    public void UnsafeObjectConstructionSetterAndArrayBounds_FailClosed()
    {
        var author = RelationQuery.Expression();
        var load = author.Source<Load>(LoadShape);
        var lowerer = new RelationQueryExpressionLowerer(ResolvePath);
        Expression<Func<Load, TransformingDto>> setter = source => new TransformingDto { Id = source.Id };
        Expression<Func<Load, DefaultedDto>> omittedDefault = source => new DefaultedDto { Id = source.Id };
        var sourceParameter = Expression.Parameter(typeof(Load), "source");
        var arrayBounds = Expression.Lambda<Func<Load, int[]>>(
            Expression.NewArrayBounds(typeof(int), Expression.Constant(2)),
            sourceParameter);

        var setterResult = lowerer.LowerProjection(setter, [load.Binding], "unsafe/setter");
        var constructorResult = lowerer.LowerProjection(
            omittedDefault,
            [load.Binding],
            "unsafe/constructor-default");
        var boundsResult = lowerer.LowerValue(arrayBounds, [load.Binding], "unsafe/array-bounds");

        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
            Assert.Single(setterResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
            Assert.Single(constructorResult.Diagnostics).Code);
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            Assert.Single(boundsResult.Diagnostics).Code);
    }

    static FieldPath ResolvePath(Type rootType, IReadOnlyList<PropertyInfo> members)
    {
        Assert.NotEmpty(members);
        Assert.True(
            members[0].DeclaringType?.IsAssignableFrom(rootType) == true
            || rootType.IsAssignableFrom(members[0].DeclaringType),
            $"Member '{members[0].Name}' is not rooted at '{rootType}'.");
        return new(
        [
            .. members.Select(member =>
                FieldPathSegment.ForField(
                    member.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name
                    ?? member.Name))
        ]);
    }

    static IEnumerable<Expr> Descendants(Expr root)
    {
        yield return root;
        IEnumerable<Expr> children = root switch
        {
            UnaryExpr unary => [unary.Operand],
            BinaryExpr binary => [binary.Left, binary.Right],
            ConditionalExpr conditional => [conditional.Test, conditional.IfTrue, conditional.IfFalse],
            CallExpr call => call.Arguments,
            _ => []
        };
        foreach (var child in children)
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    static void AssertNoClrAuthoringArtifacts(object root)
    {
        Assert.DoesNotContain(
            EnumerateObjectGraph(root),
            static value => value is Expression or MemberInfo or Type);
    }

    static IEnumerable<object> EnumerateObjectGraph(object root)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        return Enumerate(root, visited);

        static IEnumerable<object> Enumerate(object? value, ISet<object> visited)
        {
            if (value is null)
            {
                yield break;
            }

            yield return value;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string or decimal)
            {
                yield break;
            }

            if (!type.IsValueType && !visited.Add(value))
            {
                yield break;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    foreach (var nested in Enumerate(item, visited))
                    {
                        yield return nested;
                    }
                }
                yield break;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetMethod is null
                    || property.GetIndexParameters().Length != 0
                    || property.PropertyType.IsByRefLike)
                {
                    continue;
                }
                foreach (var nested in Enumerate(property.GetValue(value), visited))
                {
                    yield return nested;
                }
            }
        }
    }

    sealed class ParameterMarker<T>(QueryParameterId id) : IRelationQueryExpressionParameterMarker
    {
        public QueryParameterId ParameterId { get; } = id;

        public T Value => throw new InvalidOperationException("The marker must not be evaluated.");
    }

    sealed class ThrowingCapturedValue
    {
        public int ReadCount { get; private set; }

        public string Value
        {
            get
            {
                ReadCount++;
                throw new InvalidOperationException("Captured getter execution is forbidden.");
            }
        }
    }

    sealed class Load
    {
        [JsonPropertyName("load_id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("load_status")]
        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("load_sequence")]
        public int Sequence { get; init; }

        [JsonPropertyName("load_external_id")]
        public Guid ExternalId { get; init; }

        [JsonPropertyName("load_amount")]
        public decimal Amount { get; init; }

        public DateOnly ServiceDate { get; init; }

        public TimeOnly ServiceTime { get; init; }

        public LoadStatus ProcessingStatus { get; init; }

        public LoadStatus ExpectedProcessingStatus { get; init; }

        [JsonPropertyName("load_tags")]
        public string[] Tags { get; init; } = [];

        public HashSet<string> ComparerTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public double[] Measurements { get; init; } = [];

        [JsonPropertyName("load_stops")]
        public Stop[] Stops { get; init; } = [];

        [JsonPropertyName("load_numbers")]
        public int[] Numbers { get; init; } = [];

        [JsonPropertyName("load_payload")]
        public byte[] Payload { get; init; } = [];

        [JsonPropertyName("load_instants")]
        public DateTimeOffset[] Instants { get; init; } = [];

        [JsonPropertyName("load_occurred_at")]
        public DateTimeOffset OccurredAt { get; init; }

        public DateTimeOffset ExpectedOccurredAt { get; init; }
    }

    sealed class Stop
    {
        [JsonPropertyName("stop_location")]
        public Location Location { get; init; } = new();

        [JsonPropertyName("stop_sequence")]
        public int Sequence { get; init; }
    }

    sealed class Location
    {
        [JsonPropertyName("location_code")]
        public string Code { get; init; } = string.Empty;
    }

    sealed class Customer
    {
        [JsonPropertyName("customer_profile")]
        public CustomerProfile Profile { get; init; } = new();

        [JsonPropertyName("customer_type")]
        public string Type { get; init; } = string.Empty;
    }

    sealed class CustomerProfile
    {
        [JsonPropertyName("customer_name")]
        public string Name { get; init; } = string.Empty;
    }

    sealed class Equipment
    {
        [JsonPropertyName("equipment_type")]
        public string Type { get; init; } = string.Empty;
    }

    sealed record LoadDocument(
        [property: JsonPropertyName("document_id")] string Id,
        [property: JsonPropertyName("document_customer")] CustomerDocument Customer);

    sealed class CustomerDocument
    {
        [JsonPropertyName("customer_name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("customer_type")]
        public string Type { get; init; } = string.Empty;
    }

    sealed class NullableLoad
    {
        public NullableCustomer? Customer { get; init; }

        public NullableCustomer?[] Customers { get; init; } = [];
    }

    sealed class NullableCustomer
    {
        public string Name { get; init; } = string.Empty;

        public int Age { get; init; }
    }

    sealed class CustomEqualitySource
    {
        public CustomEqualityValue? Value { get; init; }
    }

    sealed class CustomEqualityValue
    {
        public string Id { get; init; } = string.Empty;

        public static bool operator ==(CustomEqualityValue? left, CustomEqualityValue? right) =>
            ReferenceEquals(left, right);

        public static bool operator !=(CustomEqualityValue? left, CustomEqualityValue? right) =>
            !ReferenceEquals(left, right);

        public override bool Equals(object? obj) => ReferenceEquals(this, obj);

        public override int GetHashCode() => base.GetHashCode();
    }

    sealed class NormalizationSource
    {
        public NormalizationCandidate[] Candidates { get; init; } = [];

        public string? SelectedCandidateId { get; init; }

        public NormalizationSourceFlags Flags { get; init; }

        public AmbiguousNormalizationSourceKind AmbiguousKind { get; init; }

        public NormalizationSignals? Signals { get; init; }

        public NormalizationMetadata Metadata { get; init; } = new();
    }

    sealed class NormalizationMetadata
    {
        public string? CorrelationId { get; init; }
    }

    sealed class NormalizationCandidate
    {
        public string Id { get; init; } = string.Empty;
    }

    sealed class NormalizationOutput
    {
        public NormalizationCandidateOutput[] Candidates { get; init; } = [];

        public long CandidateCount { get; init; }

        public NormalizationSignals? Signals { get; init; }
    }

    sealed class NormalizationCandidateOutput
    {
        public string Id { get; init; } = string.Empty;

        public bool Selected { get; init; }
    }

    readonly record struct NormalizationSignals
    {
        public long CandidateCount { get; init; }

        public string? ModelVersion { get; init; }
    }

    enum NormalizationSourceKind
    {
        SchemaMapping,
        HumanFeedback,
        Synthetic
    }

    [Flags]
    enum NormalizationSourceFlags
    {
        Imported = 1,
        Generated = 2
    }

    enum AmbiguousNormalizationSourceKind
    {
        SchemaMapping = 1,
        ImportedMapping = 1
    }

    enum LoadStatus : byte
    {
        Pending,
        Complete
    }

    sealed class OptionalEvidence
    {
        public OptionalObservation? LastObservation { get; init; }

        public OptionalObservation ExpectedObservation { get; init; } = new();
    }

    sealed class OptionalObservation
    {
        public DateTimeOffset ObservedAt { get; init; }

        public LoadStatus Status { get; init; }
    }

    sealed class TransformingDto
    {
        string id = string.Empty;

        public string Id
        {
            get => id;
            init => id = value.Trim();
        }
    }

    sealed class DefaultedDto
    {
        public string Id { get; init; } = string.Empty;

        public string Note { get; init; } = "default";
    }

    sealed class LiteralAggregateRow
    {
        public string Id { get; init; } = string.Empty;
    }

    sealed class LiteralAggregateResult
    {
        public decimal DecimalSum { get; init; }

        public decimal DecimalMinimum { get; init; }

        public int IntegerSum { get; init; }
    }
}
