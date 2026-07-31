using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Deterministic fingerprint of one exact target-operation intent, excluding replaceable ownership fences.</summary>
public sealed record MaterializationTargetIntentFingerprint
{
    /// <summary>Creates a target-intent fingerprint.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization profile identity.</param>
    /// <param name="value">Lower-case hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is empty or contains ill-formed Unicode, or <paramref name="value"/> is not lower-case hexadecimal.</exception>
    [JsonConstructor]
    public MaterializationTargetIntentFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = MaterializationContract.RequireUnicodeIdentity(algorithm, nameof(algorithm));
        Canonicalization = MaterializationContract.RequireUnicodeIdentity(canonicalization, nameof(canonicalization));
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));
        if (value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A target-intent fingerprint value must be lower-case hexadecimal.",
                nameof(value));
        }
    }

    /// <summary>Stable digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lower-case hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>One-pass canonical analysis of a bounded target-write batch.</summary>
public sealed record MaterializationTargetBatchIntent
{
    internal MaterializationTargetBatchIntent(
        MaterializationTargetIntentFingerprint fingerprint,
        long canonicalByteCount,
        int itemCount,
        bool hasUpserts,
        bool hasDeletes)
    {
        Fingerprint = fingerprint;
        CanonicalByteCount = canonicalByteCount;
        ItemCount = itemCount;
        HasUpserts = hasUpserts;
        HasDeletes = hasDeletes;
    }

    /// <summary>Gets the canonical batch-intent fingerprint.</summary>
    public MaterializationTargetIntentFingerprint Fingerprint { get; }

    /// <summary>Gets the exact canonical UTF-8 size used for the target write-byte limit.</summary>
    public long CanonicalByteCount { get; }

    /// <summary>Gets the number of requested item mutations.</summary>
    public int ItemCount { get; }

    /// <summary>Gets whether the batch contains at least one upsert.</summary>
    public bool HasUpserts { get; }

    /// <summary>Gets whether the batch contains at least one delete.</summary>
    public bool HasDeletes { get; }
}

/// <summary>Computes canonical target-operation intent fingerprints shared by every materialization adapter.</summary>
/// <remarks>
/// Worker and promotion fences establish replaceable ownership and are deliberately excluded from operation intent.
/// Consequently, an exact retry may advance its applicable fence and still replay the original semantic result.
/// Every other field represented by the operation-specific intent is fingerprinted. Callers must keep operation-id
/// namespaces distinct because the operation kind is supplied by the typed overload rather than embedded in the
/// digest payload.
/// </remarks>
public static class MaterializationTargetIntentFingerprinter
{
    const int BatchMutationValueEnclosingDepth = 3;
    static readonly JsonSerializerOptions FingerprintOptions = StrictDocumentJson.CreateOptions();

    /// <summary>Digest algorithm used by target-intent fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by target-intent fingerprints.</summary>
    public const string Canonicalization = "cohesive-materialization-target-intent/v1-c14n/v1";

    /// <summary>Computes the exact begin-generation intent, excluding its replaceable worker fence.</summary>
    /// <param name="request">Begin-generation request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationBeginGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeCanonical(new BeginFingerprintInput(
            request.MaterializationId.Value,
            request.GenerationId.Value,
            request.DefinitionFingerprint,
            request.CreatedAtUtc));
    }

    /// <summary>Computes one item-mutation intent.</summary>
    /// <param name="mutation">Item mutation to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationItemMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return ComputeCanonical(MutationFingerprintInput.From(mutation));
    }

    /// <summary>Computes the exact batch intent, excluding its replaceable worker fence.</summary>
    /// <param name="request">Batch request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationApplyBatchRequest request) =>
        AnalyzeBatch(request).Fingerprint;

    /// <summary>Analyzes one batch once for idempotency fingerprinting and canonical write-bound enforcement.</summary>
    /// <param name="request">Batch request to analyze.</param>
    /// <returns>Fingerprint, exact canonical byte count, item count, and applicable mutation kinds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetBatchIntent AnalyzeBatch(MaterializationApplyBatchRequest request)
    {
        if (!TryAnalyzeBatch(
                request,
                long.MaxValue,
                out var intent,
                out _))
        {
            throw new OverflowException("The canonical materialization batch exceeds the supported 64-bit byte count.");
        }
        return intent;
    }

    /// <summary>Attempts one bounded streaming canonical count and fingerprint pass over a target-write batch.</summary>
    /// <param name="request">Batch whose exact canonical intent is analyzed.</param>
    /// <param name="maximumCanonicalBytes">Positive canonical-byte policy limit.</param>
    /// <param name="intent">Complete intent when the canonical representation fits; otherwise <see langword="null"/>.</param>
    /// <param name="observedCanonicalByteCount">
    /// Exact canonical byte count when the representation fits; otherwise the saturated value
    /// <paramref name="maximumCanonicalBytes"/> plus one.
    /// </param>
    /// <returns><see langword="true"/> when the exact intent fits and was fingerprinted; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Canonical bytes are streamed into a bounded hashing sink and are never materialized as one output buffer.
    /// The supplied immutable request remains caller-owned and is traversed without copying its mutation graph.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumCanonicalBytes"/> is not positive.</exception>
    /// <exception cref="JsonException">An enum or value has no declared canonical JSON encoding.</exception>
    /// <exception cref="InvalidOperationException">An observation value has no permitted canonical JSON encoding.</exception>
    public static bool TryAnalyzeBatch(
        MaterializationApplyBatchRequest request,
        long maximumCanonicalBytes,
        [NotNullWhen(true)] out MaterializationTargetBatchIntent? intent,
        out long observedCanonicalByteCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maximumCanonicalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCanonicalBytes),
                maximumCanonicalBytes,
                "A canonical batch-byte limit must be positive.");
        }

        var hasUpserts = false;
        var hasDeletes = false;
        using CappedHashBufferWriter output = new(maximumCanonicalBytes);
        output.Write("{\"batchId\":"u8);
        WriteString(request.BatchId.Value);
        output.Write(",\"generationId\":"u8);
        WriteString(request.GenerationId.Value);
        output.Write(",\"mutations\":["u8);
        for (var index = 0; index < request.Mutations.Length; index++)
        {
            if (index > 0)
                output.Write(","u8);
            var mutation = request.Mutations[index];
            output.Write("{\"itemId\":"u8);
            WriteString(mutation.ItemId.Value);
            output.Write(",\"kind\":"u8);
            WriteString(mutation.Kind.ToString());
            output.Write(",\"mutationId\":"u8);
            WriteString(mutation.MutationId.Value);
            output.Write(",\"value\":"u8);
            if (mutation is MaterializationUpsert upsert)
            {
                hasUpserts = true;
                CanonicalJsonWriter.WriteCanonicalObservationValue(
                    output,
                    upsert.Value,
                    ObservationBytesJsonEncoding.Throw,
                    BatchMutationValueEnclosingDepth);
            }
            else
            {
                hasDeletes = true;
                output.Write("null"u8);
            }
            output.Write(",\"version\":"u8);
            WriteString(mutation.Version.Value);
            output.Write("}"u8);
        }
        output.Write("]}"u8);

        observedCanonicalByteCount = output.CanonicalByteCount;
        if (output.Exceeded)
        {
            intent = null;
            return false;
        }

        intent = new(
            new(Algorithm, Canonicalization, Convert.ToHexStringLower(output.GetHash())),
            observedCanonicalByteCount,
            request.Mutations.Length,
            hasUpserts,
            hasDeletes);
        return true;

        void WriteString(string value) =>
            CanonicalJsonWriter.WriteCanonicalObservationValue(
                output,
                ObservationValue.FromString(value),
                ObservationBytesJsonEncoding.Throw);
    }

    /// <summary>Computes the exact seal intent, excluding its replaceable worker fence.</summary>
    /// <param name="request">Seal request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationSealGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeCanonical(SealOperationFingerprintInput.From(request));
    }

    /// <summary>Computes the exact validation intent, excluding its replaceable worker fence.</summary>
    /// <param name="request">Validation request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationValidateGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeCanonical(ValidationOperationFingerprintInput.From(request));
    }

    /// <summary>Computes a validation receipt fingerprint from exact intent and normalized observed diagnostics.</summary>
    /// <param name="request">Validation request whose intent was evaluated.</param>
    /// <param name="validation">Portable observed validation result.</param>
    /// <returns>A deterministic validation receipt fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="validation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="validation"/> contains incomplete diagnostics.</exception>
    /// <exception cref="JsonException">The request or normalized diagnostics cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A request or diagnostic value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The request or normalized diagnostics have no canonical JSON representation.</exception>
    public static MaterializationValidationFingerprint ComputeValidationResult(
        MaterializationValidateGenerationRequest request,
        DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = MaterializationContract.NormalizeValidation(validation, nameof(validation));
        var value = ComputeLegacyCanonicalValue(new ValidationFingerprintInput(
            ValidationOperationFingerprintInput.From(request),
            normalized));
        return new(value);
    }

    /// <summary>Computes the exact promotion intent, excluding both replaceable ownership fences.</summary>
    /// <param name="request">Promotion request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationPromoteGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeCanonical(PromotionOperationFingerprintInput.From(request));
    }

    /// <summary>Computes the exact retirement intent, excluding its replaceable worker fence.</summary>
    /// <param name="request">Retirement request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationRetireGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeCanonical(RetirementOperationFingerprintInput.From(request));
    }

    /// <summary>Computes the exact cleanup intent, excluding its replaceable worker fence.</summary>
    /// <param name="request">Cleanup request to fingerprint.</param>
    /// <returns>The deterministic target-intent fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The intent cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An intent value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The intent has no canonical JSON representation.</exception>
    public static MaterializationTargetIntentFingerprint Compute(MaterializationCleanupGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeCanonical(CleanupOperationFingerprintInput.From(request));
    }

    static string ComputeLegacyCanonicalValue<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var canonical = StrictDocumentJson.GetCanonicalBytes(value, FingerprintOptions);
        return $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(canonical))}";
    }

    static MaterializationTargetIntentFingerprint ComputeCanonical<T>(T value) where T : class
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(value, FingerprintOptions);
        return ComputeCanonicalBytes(canonical);
    }

    static MaterializationTargetIntentFingerprint ComputeCanonicalBytes(ReadOnlySpan<byte> canonical) =>
        new(Algorithm, Canonicalization, Convert.ToHexStringLower(SHA256.HashData(canonical)));

    sealed record BeginFingerprintInput(
        string MaterializationId,
        string GenerationId,
        ExecutionDefinitionFingerprint DefinitionFingerprint,
        DateTimeOffset CreatedAtUtc);

    sealed record SealOperationFingerprintInput(
        string SealId,
        string GenerationId,
        string ExpectedRevision,
        DateTimeOffset SealedAtUtc)
    {
        internal static SealOperationFingerprintInput From(MaterializationSealGenerationRequest request) =>
            new(request.SealId.Value, request.GenerationId.Value, request.ExpectedRevision.Value, request.SealedAtUtc);
    }

    sealed record ValidationOperationFingerprintInput(
        string ValidationId,
        string GenerationId,
        string ExpectedRevision,
        string ExpectedSealFingerprint,
        long? ExpectedVisibleItemCount,
        string Validator,
        DateTimeOffset ValidatedAtUtc)
    {
        internal static ValidationOperationFingerprintInput From(MaterializationValidateGenerationRequest request) =>
            new(
                request.ValidationId.Value,
                request.GenerationId.Value,
                request.ExpectedRevision.Value,
                request.ExpectedSealFingerprint.Value,
                request.ExpectedVisibleItemCount,
                request.Validator,
                request.ValidatedAtUtc);
    }

    sealed record PromotionOperationFingerprintInput(
        string PromotionId,
        string GenerationId,
        string ExpectedGenerationRevision,
        string ValidationFingerprint,
        string? ExpectedActiveGenerationId,
        string ExpectedTargetRevision,
        DateTimeOffset PromotedAtUtc)
    {
        internal static PromotionOperationFingerprintInput From(MaterializationPromoteGenerationRequest request) =>
            new(
                request.PromotionId.Value,
                request.GenerationId.Value,
                request.ExpectedGenerationRevision.Value,
                request.ValidationFingerprint.Value,
                request.ExpectedActiveGenerationId?.Value,
                request.ExpectedTargetRevision.Value,
                request.PromotedAtUtc);
    }

    sealed record RetirementOperationFingerprintInput(
        string RetirementId,
        string GenerationId,
        string ExpectedRevision,
        DateTimeOffset RetiredAtUtc)
    {
        internal static RetirementOperationFingerprintInput From(MaterializationRetireGenerationRequest request) =>
            new(request.RetirementId.Value, request.GenerationId.Value, request.ExpectedRevision.Value, request.RetiredAtUtc);
    }

    sealed record CleanupOperationFingerprintInput(
        string CleanupId,
        string GenerationId,
        string ExpectedRevision,
        DateTimeOffset CleanedAtUtc)
    {
        internal static CleanupOperationFingerprintInput From(MaterializationCleanupGenerationRequest request) =>
            new(request.CleanupId.Value, request.GenerationId.Value, request.ExpectedRevision.Value, request.CleanedAtUtc);
    }

    sealed record MutationFingerprintInput(
        MaterializationItemMutationKind Kind,
        string ItemId,
        string MutationId,
        string Version,
        ObservationValue? Value)
    {
        internal static MutationFingerprintInput From(MaterializationItemMutation mutation) => new(
            mutation.Kind,
            mutation.ItemId.Value,
            mutation.MutationId.Value,
            mutation.Version.Value,
            mutation is MaterializationUpsert upsert ? upsert.Value : null);
    }

    sealed record ValidationFingerprintInput(
        ValidationOperationFingerprintInput Request,
        DocumentValidationResult Validation);

    sealed class CappedHashBufferWriter : IBufferWriter<byte>, IDisposable
    {
        const int DefaultBufferSize = 4 * 1024;
        readonly long maximumBytes;
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        bool disposed;

        internal CappedHashBufferWriter(long maximumBytes) => this.maximumBytes = maximumBytes;

        internal bool Exceeded { get; private set; }

        internal long CanonicalByteCount { get; private set; }

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (count < 0 || count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count), count, "The committed byte count is outside the supplied buffer.");
            if (Exceeded)
                return;
            if (count > maximumBytes - CanonicalByteCount)
            {
                Exceeded = true;
                CanonicalByteCount = checked(maximumBytes + 1);
                return;
            }

            hash.AppendData(buffer.AsSpan(0, count));
            CanonicalByteCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return buffer;
        }

        internal void Write(ReadOnlySpan<byte> value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (!value.IsEmpty)
            {
                var count = Math.Min(value.Length, buffer.Length);
                value[..count].CopyTo(buffer);
                Advance(count);
                value = value[count..];
            }
        }

        internal byte[] GetHash()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Exceeded)
                throw new InvalidOperationException("An exceeded canonical stream has no complete fingerprint.");
            return hash.GetHashAndReset();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            hash.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = [];
        }

        void EnsureBuffer(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "A buffer size hint cannot be negative.");
            sizeHint = Math.Max(1, sizeHint);
            if (buffer.Length >= sizeHint)
                return;

            var replacement = ArrayPool<byte>.Shared.Rent(sizeHint);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = replacement;
        }
    }
}

/// <summary>Shared enforcement of canonical target-write batch bounds against attributable capability evidence.</summary>
public static class MaterializationTargetBatchLimits
{
    /// <summary>Determines whether every applicable target capability covers one analyzed batch.</summary>
    /// <param name="profile">Exact target capability profile.</param>
    /// <param name="intent">Canonical batch analysis produced by <see cref="MaterializationTargetIntentFingerprinter"/>.</param>
    /// <returns><see langword="true"/> when per-item outcomes and every represented mutation kind cover the complete item-and-byte pair.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> or <paramref name="intent"/> is <see langword="null"/>.</exception>
    public static bool Supports(
        MaterializationCapabilityProfile profile,
        MaterializationTargetBatchIntent intent)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(intent);
        return Supports(MaterializationCapabilityKind.TargetPerItemOutcomes)
            && (!intent.HasUpserts || Supports(MaterializationCapabilityKind.TargetBulkUpsert))
            && (!intent.HasDeletes || Supports(MaterializationCapabilityKind.TargetBulkDelete));

        bool Supports(MaterializationCapabilityKind capability) =>
            MaterializationCapabilityLimits.SupportsBounds(
                profile,
                capability,
                MaterializationLimitKind.WriteItems,
                intent.ItemCount,
                MaterializationLimitKind.WriteBytes,
                intent.CanonicalByteCount);
    }
}
