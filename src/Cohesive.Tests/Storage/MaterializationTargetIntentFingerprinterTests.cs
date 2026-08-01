using System.Buffers;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationTargetIntentFingerprinterTests
{
    static readonly DateTimeOffset Epoch = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly MaterializationGenerationId GenerationId = new("generation/fingerprint");
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        "sha256",
        "cohesive-materialization-definition/v1-c14n/v1",
        "0123456789abcdef");

    [Fact]
    public void Compute_ExcludesOnlyReplaceableOwnershipFences()
    {
        MaterializationBeginGenerationRequest begin = new(
            new("materialization/fingerprint"),
            GenerationId,
            DefinitionFingerprint,
            new("1"),
            Epoch);
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.Compute(begin),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationBeginGenerationRequest(
                begin.MaterializationId,
                begin.GenerationId,
                begin.DefinitionFingerprint,
                new("2"),
                begin.CreatedAtUtc)));
        Assert.NotEqual(
            MaterializationTargetIntentFingerprinter.Compute(begin),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationBeginGenerationRequest(
                begin.MaterializationId,
                begin.GenerationId,
                begin.DefinitionFingerprint,
                begin.WorkerFence,
                begin.CreatedAtUtc.AddTicks(1))));

        MaterializationApplyBatchRequest batch = Batch(new("1"));
        var analyzed = MaterializationTargetIntentFingerprinter.AnalyzeBatch(batch);
        var takeover = MaterializationTargetIntentFingerprinter.AnalyzeBatch(Batch(new("2")));
        Assert.Equal(analyzed.Fingerprint, takeover.Fingerprint);
        Assert.Equal(analyzed.CanonicalByteCount, takeover.CanonicalByteCount);
        Assert.NotEqual(
            analyzed.Fingerprint,
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationApplyBatchRequest(
                batch.BatchId,
                batch.GenerationId,
                batch.WorkerFence,
                [new MaterializationUpsert(
                    new("item/fingerprint"),
                    new("mutation/fingerprint"),
                    new("1"),
                    ObservationValue.FromString("changed"))])));

        MaterializationSealGenerationRequest seal = new(
            new("seal/fingerprint"),
            GenerationId,
            new("2"),
            new("1"),
            Epoch.AddMinutes(1));
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.Compute(seal),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationSealGenerationRequest(
                seal.SealId,
                seal.GenerationId,
                seal.ExpectedRevision,
                new("2"),
                seal.SealedAtUtc)));
        Assert.NotEqual(
            MaterializationTargetIntentFingerprinter.Compute(seal),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationSealGenerationRequest(
                seal.SealId,
                seal.GenerationId,
                new("3"),
                seal.WorkerFence,
                seal.SealedAtUtc)));

        MaterializationValidateGenerationRequest validation = new(
            new("validation/fingerprint"),
            GenerationId,
            new("3"),
            new("seal/content-fingerprint"),
            expectedVisibleItemCount: 1,
            "tests/validator-v1",
            new("1"),
            Epoch.AddMinutes(2));
        MaterializationValidateGenerationRequest validationTakeover = new(
            validation.ValidationId,
            validation.GenerationId,
            validation.ExpectedRevision,
            validation.ExpectedSealFingerprint,
            validation.ExpectedVisibleItemCount,
            validation.Validator,
            new("2"),
            validation.ValidatedAtUtc);
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.Compute(validation),
            MaterializationTargetIntentFingerprinter.Compute(validationTakeover));
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.ComputeValidationResult(
                validation,
                DocumentValidationResult.Valid),
            MaterializationTargetIntentFingerprinter.ComputeValidationResult(
                validationTakeover,
                DocumentValidationResult.Valid));
        Assert.NotEqual(
            MaterializationTargetIntentFingerprinter.Compute(validation),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationValidateGenerationRequest(
                validation.ValidationId,
                validation.GenerationId,
                validation.ExpectedRevision,
                validation.ExpectedSealFingerprint,
                validation.ExpectedVisibleItemCount,
                "tests/validator-v2",
                validation.WorkerFence,
                validation.ValidatedAtUtc)));

        MaterializationPromoteGenerationRequest promotion = new(
            new("promotion/fingerprint"),
            GenerationId,
            new("4"),
            new("validation/content-fingerprint"),
            expectedActiveGenerationId: null,
            MaterializationTargetRevision.Initial,
            new("1"),
            new("1"),
            Epoch.AddMinutes(3));
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.Compute(promotion),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationPromoteGenerationRequest(
                promotion.PromotionId,
                promotion.GenerationId,
                promotion.ExpectedGenerationRevision,
                promotion.ValidationFingerprint,
                promotion.ExpectedActiveGenerationId,
                promotion.ExpectedTargetRevision,
                new("2"),
                new("2"),
                promotion.PromotedAtUtc)));
        Assert.NotEqual(
            MaterializationTargetIntentFingerprinter.Compute(promotion),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationPromoteGenerationRequest(
                promotion.PromotionId,
                promotion.GenerationId,
                promotion.ExpectedGenerationRevision,
                promotion.ValidationFingerprint,
                promotion.ExpectedActiveGenerationId,
                new("1"),
                promotion.GenerationWorkerFence,
                promotion.PromotionFence,
                promotion.PromotedAtUtc)));

        MaterializationRetireGenerationRequest retirement = new(
            new("retirement/fingerprint"),
            GenerationId,
            new("5"),
            new("1"),
            Epoch.AddMinutes(4));
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.Compute(retirement),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationRetireGenerationRequest(
                retirement.RetirementId,
                retirement.GenerationId,
                retirement.ExpectedRevision,
                new("2"),
                retirement.RetiredAtUtc)));
        Assert.NotEqual(
            MaterializationTargetIntentFingerprinter.Compute(retirement),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationRetireGenerationRequest(
                retirement.RetirementId,
                retirement.GenerationId,
                retirement.ExpectedRevision,
                retirement.WorkerFence,
                retirement.RetiredAtUtc.AddTicks(1))));

        MaterializationCleanupGenerationRequest cleanup = new(
            new("cleanup/fingerprint"),
            GenerationId,
            new("6"),
            new("1"),
            Epoch.AddMinutes(5));
        Assert.Equal(
            MaterializationTargetIntentFingerprinter.Compute(cleanup),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationCleanupGenerationRequest(
                cleanup.CleanupId,
                cleanup.GenerationId,
                cleanup.ExpectedRevision,
                new("2"),
                cleanup.CleanedAtUtc)));
        Assert.NotEqual(
            MaterializationTargetIntentFingerprinter.Compute(cleanup),
            MaterializationTargetIntentFingerprinter.Compute(new MaterializationCleanupGenerationRequest(
                cleanup.CleanupId,
                cleanup.GenerationId,
                new("7"),
                cleanup.WorkerFence,
                cleanup.CleanedAtUtc)));
    }

    [Fact]
    public void AnalyzeBatch_ProducesVersionedGoldenFingerprintAndCanonicalBounds()
    {
        var batch = Batch(new("1"));

        var intent = MaterializationTargetIntentFingerprinter.AnalyzeBatch(batch);

        Assert.Equal(MaterializationTargetIntentFingerprinter.Algorithm, intent.Fingerprint.Algorithm);
        Assert.Equal(MaterializationTargetIntentFingerprinter.Canonicalization, intent.Fingerprint.Canonicalization);
        Assert.Equal("086eef0ace6cfffc1b2b01aba1756a887cf3c367359210c3d41bb6b784b250a9", intent.Fingerprint.Value);
        Assert.Equal(197, intent.CanonicalByteCount);
        Assert.Equal(1, intent.ItemCount);
        Assert.True(intent.HasUpserts);
        Assert.False(intent.HasDeletes);
        Assert.Equal(intent.Fingerprint, MaterializationTargetIntentFingerprinter.Compute(batch));
    }

    [Fact]
    public void TryAnalyzeBatch_SaturatesStructuralOversizeWithoutMaterializingCanonicalJson()
    {
        var values = ImmutableArray.CreateRange(
            Enumerable.Repeat(ObservationValue.Null, 10_000));
        MaterializationApplyBatchRequest request = new(
            new("batch/structural-limit"),
            GenerationId,
            new("1"),
            [
                new MaterializationUpsert(
                    new("item/structural-limit"),
                    new("mutation/structural-limit"),
                    new("1"),
                    ObservationValue.FromImmutableArray(values))
            ]);

        var accepted = MaterializationTargetIntentFingerprinter.TryAnalyzeBatch(
            request,
            maximumCanonicalBytes: 256,
            out var intent,
            out var observedCanonicalBytes);

        Assert.False(accepted);
        Assert.Null(intent);
        Assert.Equal(257, observedCanonicalBytes);
    }

    [Fact]
    public void TryAnalyzeBatch_SaturatesOneLargeStringWithoutRequestingATokenSizedBuffer()
    {
        MaterializationApplyBatchRequest request = new(
            new("batch/large-string-limit"),
            GenerationId,
            new("1"),
            [
                new MaterializationUpsert(
                    new("item/large-string-limit"),
                    new("mutation/large-string-limit"),
                    new("1"),
                    ObservationValue.FromString(new string('<', 1_000_000)))
            ]);

        var accepted = MaterializationTargetIntentFingerprinter.TryAnalyzeBatch(
            request,
            maximumCanonicalBytes: 256,
            out var intent,
            out var observedCanonicalBytes);

        Assert.False(accepted);
        Assert.Null(intent);
        Assert.Equal(257, observedCanonicalBytes);
    }

    [Fact]
    public void StreamingCanonicalObservationWriter_MatchesCanonicalUtf8JsonAuthority()
    {
        ObservationValue value = ObservationValue.FromObject(
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["z"] = ObservationValue.FromArray([
                    ObservationValue.FromString("<>&\"\\\n\u2028é😀"),
                    ObservationValue.FromDouble(-0d),
                    ObservationValue.FromDouble(1.2345678901234567e100),
                    ObservationValue.FromDecimal(1.2300m),
                    ObservationValue.FromBytes(new byte[] { 0, 1, 2, 253, 254, 255 })
                ]),
                ["a"] = ObservationValue.FromBool(true)
            });
        ArrayBufferWriter<byte> expected = new();
        using (Utf8JsonWriter writer = new(expected, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            CanonicalJsonWriter.WriteCanonicalObservationValue(
                writer,
                value,
                ObservationBytesJsonEncoding.Base64String);
        }
        ArrayBufferWriter<byte> actual = new();

        CanonicalJsonWriter.WriteCanonicalObservationValue(
            actual,
            value,
            ObservationBytesJsonEncoding.Base64String);

        Assert.True(expected.WrittenSpan.SequenceEqual(actual.WrittenSpan));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1_000)]
    public void StreamingCanonicalObservationWriter_MatchesAuthorityAtSupportedDepth(int containerDepth)
    {
        var value = NestedArrays(containerDepth);
        ArrayBufferWriter<byte> expected = new();
        using (Utf8JsonWriter writer = new(expected, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteNestedArrays(writer, containerDepth);
        }
        ArrayBufferWriter<byte> actual = new();

        CanonicalJsonWriter.WriteCanonicalObservationValue(
            actual,
            value,
            ObservationBytesJsonEncoding.Throw);

        Assert.True(expected.WrittenSpan.SequenceEqual(actual.WrittenSpan));
    }

    [Fact]
    public void StreamingCanonicalObservationWriter_RejectsExcessiveDepthLikeAuthority()
    {
        var value = NestedArrays(containerDepth: 50_000);
        ArrayBufferWriter<byte> actual = new();

        Assert.Throws<InvalidOperationException>(() =>
        {
            ArrayBufferWriter<byte> expected = new();
            using Utf8JsonWriter writer = new(expected, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            WriteNestedArrays(writer, containerDepth: 1_001);
        });
        Assert.Throws<InvalidOperationException>(() =>
            CanonicalJsonWriter.WriteCanonicalObservationValue(
                actual,
                value,
                ObservationBytesJsonEncoding.Throw));
    }

    [Fact]
    public void AnalyzeBatch_AccountsForItsEnclosingCanonicalDocumentDepth()
    {
        var accepted = BatchWithValue(NestedArrays(containerDepth: 997));
        var excessive = BatchWithValue(NestedArrays(containerDepth: 998));

        _ = MaterializationTargetIntentFingerprinter.AnalyzeBatch(accepted);
        Assert.Throws<InvalidOperationException>(() =>
            MaterializationTargetIntentFingerprinter.AnalyzeBatch(excessive));
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdfff)]
    public void MaterializationIdentities_RejectIllFormedUtf16BeforeFingerprinting(int codeUnit)
    {
        var value = new string((char)codeUnit, 1);
        Assert.Throws<ArgumentException>(() => new MaterializationGenerationId(value));
        Assert.Throws<ArgumentException>(() => new MaterializationBatchId(value));
        Assert.Throws<ArgumentException>(() => new MaterializationItemMutationId(value));
        Assert.Throws<ArgumentException>(() => new MaterializationTargetId(value));
        Assert.Throws<ArgumentException>(() => new MaterializationId(value));
    }

    [Fact]
    public void BatchLimits_RequireOneSufficientRealizationForEveryApplicableCapability()
    {
        var upsert = MaterializationTargetIntentFingerprinter.AnalyzeBatch(Batch(new("1")));
        MaterializationApplyBatchRequest mixedRequest = new(
            new("batch/mixed"),
            GenerationId,
            new("1"),
            [
                new MaterializationUpsert(
                    new("item/upsert"),
                    new("mutation/upsert"),
                    new("1"),
                    ObservationValue.FromString("value")),
                new MaterializationDelete(
                    new("item/delete"),
                    new("mutation/delete"),
                    new("1"))
            ]);
        var mixed = MaterializationTargetIntentFingerprinter.AnalyzeBatch(mixedRequest);
        var upsertOnly = Profile(
            writeItems: 2,
            writeBytes: Math.Max(upsert.CanonicalByteCount, mixed.CanonicalByteCount),
            includeDelete: false);
        var complete = Profile(
            writeItems: 2,
            writeBytes: Math.Max(upsert.CanonicalByteCount, mixed.CanonicalByteCount),
            includeDelete: true);
        var narrow = Profile(
            writeItems: 1,
            writeBytes: Math.Max(upsert.CanonicalByteCount, mixed.CanonicalByteCount),
            includeDelete: true);

        Assert.True(MaterializationTargetBatchLimits.Supports(upsertOnly, upsert));
        Assert.False(MaterializationTargetBatchLimits.Supports(upsertOnly, mixed));
        Assert.True(MaterializationTargetBatchLimits.Supports(complete, mixed));
        Assert.False(MaterializationTargetBatchLimits.Supports(narrow, mixed));
    }

    [Fact]
    public void SealFingerprint_NormalizesItemOrderAndDistinguishesNullUpsertFromTombstone()
    {
        MaterializationSealContentEntry first = new(
            new("item/a"),
            new("1"),
            new("mutation/a"),
            MaterializationItemMutationKind.Upsert,
            ObservationValue.Null);
        MaterializationSealContentEntry second = new(
            new("item/b"),
            new("2"),
            new("mutation/b"),
            MaterializationItemMutationKind.Delete,
            value: null);

        var forward = MaterializationSealFingerprinter.Compute([first, second]);
        var reversed = MaterializationSealFingerprinter.Compute([second, first]);
        var changedKind = MaterializationSealFingerprinter.Compute([
            new(
                first.ItemId,
                first.Version,
                first.MutationId,
                MaterializationItemMutationKind.Delete,
                value: null),
            second
        ]);

        Assert.Equal(forward, reversed);
        Assert.NotEqual(forward, changedKind);
        Assert.StartsWith("sha256-v1:", forward.Value, StringComparison.Ordinal);
        using (MaterializationSealFingerprintAccumulator accumulator = new())
        {
            accumulator.Append(first);
            accumulator.Append(second);
            Assert.Equal(forward, accumulator.Complete());
            Assert.Throws<InvalidOperationException>(() => accumulator.Complete());
        }
        using (MaterializationSealFingerprintAccumulator accumulator = new())
        {
            accumulator.Append(second);
            Assert.Throws<ArgumentException>(() => accumulator.Append(first));
        }
        Assert.Throws<ArgumentException>(() => MaterializationSealFingerprinter.Compute([first, first]));
        Assert.Throws<ArgumentException>(() => new MaterializationSealContentEntry(
            first.ItemId,
            first.Version,
            first.MutationId,
            MaterializationItemMutationKind.Upsert,
            value: null));
        Assert.Throws<ArgumentException>(() => new MaterializationSealContentEntry(
            first.ItemId,
            first.Version,
            first.MutationId,
            MaterializationItemMutationKind.Delete,
            ObservationValue.Null));
    }

    [Fact]
    public void SealFingerprint_UsesPortableUnicodeScalarOrderAcrossPlanes()
    {
        MaterializationSealContentEntry basicMultilingualPlane = new(
            new("\uE000"),
            new("1"),
            new("mutation/bmp"),
            MaterializationItemMutationKind.Upsert,
            ObservationValue.FromString("bmp"));
        MaterializationSealContentEntry supplementaryPlane = new(
            new("\U00010000"),
            new("1"),
            new("mutation/supplementary"),
            MaterializationItemMutationKind.Upsert,
            ObservationValue.FromString("supplementary"));

        Assert.True(string.CompareOrdinal(basicMultilingualPlane.ItemId.Value, supplementaryPlane.ItemId.Value) > 0);
        Assert.True(MaterializationSealContentOrder.Compare(
            basicMultilingualPlane.ItemId,
            supplementaryPlane.ItemId) < 0);
        Assert.Equal(
            MaterializationSealFingerprinter.Compute([basicMultilingualPlane, supplementaryPlane]),
            MaterializationSealFingerprinter.Compute([supplementaryPlane, basicMultilingualPlane]));
        using MaterializationSealFingerprintAccumulator accumulator = new();
        accumulator.Append(basicMultilingualPlane);
        accumulator.Append(supplementaryPlane);
        _ = accumulator.Complete();
    }

    [Fact]
    public void Fingerprint_RejectsNonCanonicalDigestValues()
    {
        Assert.Throws<ArgumentException>(() => new MaterializationTargetIntentFingerprint(
            "sha256",
            "target/v1",
            "ABCDEF"));
        Assert.Throws<ArgumentException>(() => new MaterializationTargetIntentFingerprint(
            "sha256",
            "target/v1",
            "not-hex"));
    }

    static MaterializationApplyBatchRequest Batch(MaterializationWorkerFence fence) => new(
        new("batch/fingerprint"),
        GenerationId,
        fence,
        [new MaterializationUpsert(
            new("item/fingerprint"),
            new("mutation/fingerprint"),
            new("1"),
            ObservationValue.FromString("value"))]);

    static MaterializationApplyBatchRequest BatchWithValue(ObservationValue value) => new(
        new("batch/depth"),
        GenerationId,
        new("1"),
        [new MaterializationUpsert(
            new("item/depth"),
            new("mutation/depth"),
            new("1"),
            value)]);

    static ObservationValue NestedArrays(int containerDepth)
    {
        var value = ObservationValue.Null;
        for (var depth = 0; depth < containerDepth; depth++)
        {
            value = ObservationValue.FromImmutableArray([value]);
        }
        return value;
    }

    static void WriteNestedArrays(Utf8JsonWriter writer, int containerDepth)
    {
        for (var depth = 0; depth < containerDepth; depth++)
        {
            writer.WriteStartArray();
        }
        writer.WriteNullValue();
        for (var depth = 0; depth < containerDepth; depth++)
        {
            writer.WriteEndArray();
        }
    }

    static MaterializationCapabilityProfile Profile(
        long writeItems,
        long writeBytes,
        bool includeDelete)
    {
        static MaterializationCapabilityEvidence Evidence(
            string id,
            MaterializationCapabilityKind capability,
            long writeItems,
            long writeBytes) => new(
                new(id),
                capability,
                CapabilityRealizationKind.Native,
                capability == MaterializationCapabilityKind.TargetPerItemOutcomes
                    ? [MaterializationGuaranteeKind.ExactPerItemOutcome]
                    : [
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                [
                    new(MaterializationLimitKind.WriteItems, writeItems),
                    new(MaterializationLimitKind.WriteBytes, writeBytes)
                ],
                ["tests/materialization-target-intent/v1"]);

        ImmutableArray<MaterializationCapabilityEvidence>.Builder evidence =
            ImmutableArray.CreateBuilder<MaterializationCapabilityEvidence>(includeDelete ? 3 : 2);
        evidence.Add(Evidence(
            "outcomes",
            MaterializationCapabilityKind.TargetPerItemOutcomes,
            writeItems,
            writeBytes));
        evidence.Add(Evidence(
            "upsert",
            MaterializationCapabilityKind.TargetBulkUpsert,
            writeItems,
            writeBytes));
        if (includeDelete)
        {
            evidence.Add(Evidence(
                "delete",
                MaterializationCapabilityKind.TargetBulkDelete,
                writeItems,
                writeBytes));
        }

        return new(
            new("profile/target-intent-v1"),
            MaterializationEndpointRole.Target,
            "target/intent",
            evidence.MoveToImmutable());
    }
}
